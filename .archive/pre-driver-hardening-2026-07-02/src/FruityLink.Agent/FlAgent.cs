using System.Runtime.CompilerServices;
using System.Text;
using FruityLink.Core.Abstractions;
using FruityLink.Core.Domain;
using FruityLink.Llm;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;

namespace FruityLink.Agent;

/// <summary>
/// The conversational agent. Builds a Semantic Kernel from the user's selected backend,
/// registers the FL tool plugins, and streams tool-using replies. Maintains conversation
/// history so it can be snapshotted/restored by the version tree.
/// </summary>
public sealed class FlAgent(
    IChatKernelFactory kernelFactory,
    ISettingsStore settingsStore,
    ISecretStore secretStore,
    FlPluginSet plugins,
    ToolCallFilter toolFilter)
{
    private readonly ChatHistory _history = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Kernel? _kernel;
    private IChatCompletionService? _chat;

    /// <summary>True once a backend has been configured.</summary>
    public bool IsConfigured => _kernel is not null;

    /// <summary>Raised after each tool the agent invokes, for live UI display.</summary>
    public event Action<ToolCallInfo>? ToolInvoked
    {
        add => toolFilter.Invoked += value;
        remove => toolFilter.Invoked -= value;
    }

    /// <summary>
    /// Builds (or rebuilds) the kernel from current settings and registers all tool plugins.
    /// Call again after the user changes the backend.
    /// </summary>
    public async Task ConfigureAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try { await ConfigureCoreAsync(ct).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    private async Task ConfigureCoreAsync(CancellationToken ct)
    {
        var app = await settingsStore.LoadAsync(ct).ConfigureAwait(false);
        var llm = app.LlmOrDefault;
        string? apiKey = llm.ApiKeyRef is null ? null : await secretStore.GetAsync(llm.ApiKeyRef, ct).ConfigureAwait(false);

        var kernel = kernelFactory.CreateKernel(llm, apiKey);
        kernel.FunctionInvocationFilters.Add(toolFilter);
        // Bound a runaway auto-invoke loop (flaky backends can re-call tools forever); 12 rounds is
        // ample for legitimate multi-step actions.
        kernel.AutoFunctionInvocationFilters.Add(new AutoInvokeIterationFilter(maxRounds: 12));
        foreach (var (name, instance) in plugins.All)
            kernel.Plugins.AddFromObject(instance, name);

        _kernel = kernel;
        _chat = kernel.GetRequiredService<IChatCompletionService>();

        if (_history.Count == 0)
            _history.AddSystemMessage(SystemPrompts.BuildDefault());
    }

    /// <summary>
    /// Sends the user's message and streams the assistant's reply as <see cref="AgentDelta"/>s
    /// (visible text and, where the model exposes it, reasoning "thoughts"). Tools are invoked
    /// automatically and surfaced via <see cref="ToolInvoked"/>.
    /// </summary>
    public async IAsyncEnumerable<AgentDelta> StreamAsync(
        string userInput, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_kernel is null || _chat is null)
                await ConfigureCoreAsync(ct).ConfigureAwait(false);

            // Snapshot history BEFORE this turn so a failed/cancelled send can be rolled back cleanly.
            int historyMark = _history.Count;
            _history.AddUserMessage(userInput);

            var settings = new OpenAIPromptExecutionSettings
            {
                // Headroom so a reasoning turn can't spend its whole budget on <think> and then get cut
                // off mid tool-call JSON (truncated arguments are unrepairable). Matches SubAgentService
                // (8192). The iteration filter — not MaxTokens — is what bounds a runaway loop now.
                MaxTokens = 8192,

                // Low temperature for stable, deterministic tool-call argument JSON: fewer
                // self-correction/repair loops, so fewer round-trips. Quality knob, not a latency one.
                Temperature = 0.25,

                // Let the model request several independent tool calls in ONE response (e.g. add a
                // channel + author its notes) so a multi-step action costs one LLM round-trip instead
                // of many — the main perceived-latency win. SK still auto-invokes them SEQUENTIALLY
                // (AllowConcurrentInvocation left at its default false), so the single-client FL bridge
                // is never called concurrently.
                FunctionChoiceBehavior = FunctionChoiceBehavior.Auto(
                    options: new FunctionChoiceBehaviorOptions { AllowParallelCalls = true }),

                // Keep GLM's thinking ON (it is by default) but size it for a BRIEF plan-before-tools
                // rather than the deepest "max". reasoning_effort is the portable knob over the Ollama
                // /v1 OpenAI-compatible endpoint; passed via ExtensionData so SK serializes it into the
                // request body. The reasoning itself is captured downstream (LlmToolCallRepairHandler
                // folds message.reasoning_content into <think>…</think>) and split out in StreamAsync.
                ExtensionData = new Dictionary<string, object> { ["reasoning_effort"] = "medium" },
            };

            // Non-streaming on purpose: the backend returns complete tool-call arguments in a single
            // JSON body. Streaming makes some OpenAI-compatible proxies resend the full arguments in
            // each chunk, which SK concatenates into invalid JSON (e.g. "{}{}") and rejects. Tool
            // calls and reasoning are still surfaced; only the final text arrives all at once.
            ChatMessageContent reply;
            try
            {
                reply = await _chat!
                    .GetChatMessageContentAsync(_history, settings, _kernel, ct)
                    .ConfigureAwait(false);
            }
            catch
            {
                // SK edits _history in place as it auto-invokes tools. If the turn throws or is
                // cancelled mid-loop, history can be left with an assistant tool_calls message whose
                // ids have no matching tool results — which 400s EVERY later request on strict backends
                // (a permanently wedged conversation). Roll the whole failed turn back to the mark.
                TruncateHistoryTo(historyMark);
                throw;
            }

            // The backend's reasoning rides in the content channel as <think>…</think> (folded there
            // by LlmToolCallRepairHandler, because SK's typed OpenAI deserialization drops the raw
            // reasoning_content/reasoning fields). Split it back out: a Thought delta for the reasoning,
            // a clean Text delta for the answer. Store the STRIPPED text in history so the marker never
            // pollutes context or any later tool-call parsing.
            string content = reply.Content ?? string.Empty;
            (string thought, string text) = SplitThink(content);

            EnsureInHistory(reply, text);

            // Strip <think> from EVERY assistant message SK appended this turn, not just the final one:
            // a multi-round tool turn appends one assistant message per round, each carrying the folded
            // reasoning block. Left in, they re-inflate tokens and feed weak models their own prior
            // reasoning as if it were answer content on the next turn.
            StripThinkFromTurn(historyMark);

            if (!string.IsNullOrEmpty(thought))
                yield return new AgentDelta(AgentDeltaKind.Thought, thought);

            if (!string.IsNullOrEmpty(text))
                yield return new AgentDelta(AgentDeltaKind.Text, text);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Appends the final assistant reply to history unless SK's auto-invoke already did, storing the
    /// <paramref name="strippedText"/> (reasoning <c>&lt;think&gt;</c> markers removed) as its content so
    /// the marker never pollutes history or any later tool-call parsing. When SK already appended the
    /// reply (with the marker), strips that copy in place.
    /// </summary>
    private void EnsureInHistory(ChatMessageContent reply, string strippedText)
    {
        ChatMessageContent? last = _history.Count > 0 ? _history[^1] : null;
        bool alreadyThere = last is not null
            && last.Role == AuthorRole.Assistant
            && string.Equals(last.Content ?? string.Empty, reply.Content ?? string.Empty, StringComparison.Ordinal);

        if (alreadyThere)
        {
            // SK already appended the reply with the <think> marker still in it — strip it in place.
            last!.Content = strippedText;
        }
        else
        {
            reply.Content = strippedText;
            _history.Add(reply);
        }
    }

    /// <summary>Removes trailing messages so <see cref="_history"/> is back to <paramref name="mark"/>
    /// entries — used to discard a failed turn's half-applied SK edits.</summary>
    private void TruncateHistoryTo(int mark)
    {
        while (_history.Count > mark)
            _history.RemoveAt(_history.Count - 1);
    }

    /// <summary>Strips any <c>&lt;think&gt;…&lt;/think&gt;</c> block from every assistant message added
    /// at or after <paramref name="fromIndex"/> (all rounds of the just-completed turn).</summary>
    private void StripThinkFromTurn(int fromIndex)
    {
        for (int i = Math.Max(fromIndex, 0); i < _history.Count; i++)
        {
            ChatMessageContent m = _history[i];
            if (m.Role != AuthorRole.Assistant) continue;
            string c = m.Content ?? string.Empty;
            if (c.IndexOf("<think>", StringComparison.OrdinalIgnoreCase) < 0) continue;
            m.Content = SplitThink(c).Text;
        }
    }

    /// <summary>
    /// Splits a reply's content into (reasoning, answer) by pulling out a leading
    /// <c>&lt;think&gt;…&lt;/think&gt;</c> block (folded in by the HTTP handler from the backend's
    /// reasoning_content/reasoning field, or emitted inline by some backends). When no block is present
    /// the whole content is returned as the answer with an empty thought.
    /// </summary>
    internal static (string Thought, string Text) SplitThink(string content)
    {
        const string open = "<think>";
        const string close = "</think>";
        int a = content.IndexOf(open, StringComparison.OrdinalIgnoreCase);
        int b = content.IndexOf(close, StringComparison.OrdinalIgnoreCase);
        if (a < 0 || b < 0 || b < a) return (string.Empty, content);

        string thought = content.Substring(a + open.Length, b - (a + open.Length)).Trim();
        string text = content.Remove(a, (b + close.Length) - a).Trim();
        return (thought, text);
    }

    /// <summary>
    /// Resets the conversation, optionally seeding it from a restored session (used when
    /// switching to a version-tree branch). Re-adds the system prompt first.
    /// </summary>
    public void ResetConversation(IEnumerable<ChatMessage>? seed = null)
    {
        _history.Clear();
        _history.AddSystemMessage(SystemPrompts.BuildDefault());
        if (seed is null) return;

        foreach (var message in seed)
        {
            switch (message.Role)
            {
                case ChatRole.User:
                    _history.AddUserMessage(message.Content);
                    break;
                case ChatRole.Assistant:
                    _history.AddAssistantMessage(message.Content);
                    break;
                case ChatRole.System:
                    _history.AddSystemMessage(message.Content);
                    break;
                // Tool messages are replayed implicitly via assistant content; skip.
            }
        }
    }
}
