using FruityLink.Core.Abstractions;
using FruityLink.Core.Configuration;
using FruityLink.Llm;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;

namespace FruityLink.Agent;

/// <summary>
/// Drives the in-FL chat tab: a background loop polls the injected bridge for messages the user
/// submits in FL's "FruityLink AI" browser tab, runs them through a dedicated conversational agent
/// (its own history; same glm backend + full FL tool set as the main agent, so it actually automates
/// FL), and pushes the reply back into the tab's display. Comms reuse the existing pipe
/// (chattab_open / chat_poll / chat_say). Separate conversation from the WPF chat by design.
/// </summary>
public sealed class ChatBridgeService(
    IChatKernelFactory kernelFactory,
    ISettingsStore settingsStore,
    ISecretStore secretStore,
    FlPluginSet plugins,
    ToolCallFilter toolFilter,
    INativeFlControl fl)
{
    private readonly ChatHistory _history = new();
    private Kernel? _kernel;
    private IChatCompletionService? _chat;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    private static readonly string LogPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "fruitylink-chatloop.log");
    private static void Log(string m) { try { System.IO.File.AppendAllText(LogPath, m + System.Environment.NewLine); } catch { /* diagnostic only */ } }

    /// <summary>Start the background poll loop (idempotent). Safe to call before FL/the bridge is up.</summary>
    public void Start()
    {
        if (_loop is not null) return;
        Log("Start() invoked");
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => LoopAsync(_cts.Token));
    }

    public async Task StopAsync()
    {
        if (_cts is null) return;
        _cts.Cancel();
        try { if (_loop is not null) await _loop.ConfigureAwait(false); } catch { /* shutdown */ }
        _cts = null;
        _loop = null;
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        Log("LoopAsync entered");
        bool tabOpened = false;
        bool? lastAvail = null;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                bool avail = await fl.IsAvailableAsync(ct).ConfigureAwait(false);
                if (avail != lastAvail) { Log($"available={avail}"); lastAvail = avail; }
                if (!avail)
                {
                    tabOpened = false;                 // bridge gone; re-open when it returns
                    await Task.Delay(2000, ct).ConfigureAwait(false);
                    continue;
                }
                if (!tabOpened)
                {
                    Log("opening chat tab");
                    await fl.OpenChatTabAsync(ct).ConfigureAwait(false);
                    // Visible proof the loop is alive + polling (and a hint for the user).
                    await fl.ChatSayAsync("FruityLink AI ready — type a request and click Send.", ct).ConfigureAwait(false);
                    Log("chat tab opened + marker sent");
                    tabOpened = true;
                }

                string input = await fl.ChatPollAsync(ct).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(input))
                {
                    await Task.Delay(500, ct).ConfigureAwait(false);
                    continue;
                }

                await fl.ChatSayAsync("you: " + input, ct).ConfigureAwait(false);
                string reply = await RunTurnAsync(input, ct).ConfigureAwait(false);
                await fl.ChatSayAsync(reply, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Log("ERROR: " + ex);
                try { await fl.ChatSayAsync("[error] " + ex.Message, CancellationToken.None).ConfigureAwait(false); } catch { /* best effort */ }
                try { await Task.Delay(1500, ct).ConfigureAwait(false); } catch { break; }
            }
        }
    }

    private async Task<string> RunTurnAsync(string input, CancellationToken ct)
    {
        if (_kernel is null || _chat is null)
            await ConfigureAsync(ct).ConfigureAwait(false);

        // Snapshot BEFORE this turn so a failed/cancelled send can be rolled back cleanly (this loop
        // swallows exceptions and continues, so a poisoned history would otherwise wedge the tab for
        // the rest of the FL session).
        int historyMark = _history.Count;
        _history.AddUserMessage(input);

        var settings = new OpenAIPromptExecutionSettings
        {
            MaxTokens = 8192,    // headroom so reasoning can't truncate tool-call JSON (matches FlAgent)
            Temperature = 0.25,  // low temp for stable tool-call JSON (matches FlAgent)
            // AllowConcurrentInvocation left default (false): SK auto-invokes a batched turn's calls
            // sequentially, so the single-client FL bridge is never hit concurrently.
            FunctionChoiceBehavior = FunctionChoiceBehavior.Auto(
                options: new FunctionChoiceBehaviorOptions { AllowParallelCalls = true }),
            // Keep GLM thinking ON but brief (portable Ollama /v1 knob); matches FlAgent. Reasoning is
            // captured by LlmToolCallRepairHandler folding reasoning_content into <think>…</think>.
            ExtensionData = new Dictionary<string, object> { ["reasoning_effort"] = "medium" },
        };

        ChatMessageContent reply;
        try
        {
            reply = await _chat!
                .GetChatMessageContentAsync(_history, settings, _kernel!, ct)
                .ConfigureAwait(false);
        }
        catch
        {
            // Roll back the half-applied turn so orphaned tool_calls can't poison every later request.
            while (_history.Count > historyMark)
                _history.RemoveAt(_history.Count - 1);
            throw;
        }

        // Ensure the final reply is in history exactly once: SK's auto-invoke may or may not have
        // already appended it, so add it only when it isn't the last message (avoids a duplicate).
        string text = FlAgent.SplitThink(reply.Content ?? string.Empty).Text;
        ChatMessageContent? last = _history.Count > 0 ? _history[^1] : null;
        bool alreadyThere = last is not null
            && last.Role == AuthorRole.Assistant
            && string.Equals(last.Content ?? string.Empty, reply.Content ?? string.Empty, StringComparison.Ordinal);
        if (alreadyThere)
            last!.Content = text;
        else
        {
            reply.Content = text;
            _history.Add(reply);
        }

        // Drop any <think>…</think> reasoning the HTTP handler folded into content: the in-FL chat tab
        // shows plain text only (no thoughts pane) and the marker must not pollute this history. Do it
        // for every assistant message this turn appended (one per tool-call round), not just the last.
        for (int i = historyMark; i < _history.Count; i++)
        {
            if (_history[i].Role != AuthorRole.Assistant) continue;
            string c = _history[i].Content ?? string.Empty;
            if (c.IndexOf("<think>", StringComparison.OrdinalIgnoreCase) >= 0)
                _history[i].Content = FlAgent.SplitThink(c).Text;
        }

        return string.IsNullOrWhiteSpace(text) ? "(done)" : text;
    }

    private async Task ConfigureAsync(CancellationToken ct)
    {
        AppSettings app = await settingsStore.LoadAsync(ct).ConfigureAwait(false);
        LlmSettings llm = app.LlmOrDefault;
        string? apiKey = llm.ApiKeyRef is null ? null : await secretStore.GetAsync(llm.ApiKeyRef, ct).ConfigureAwait(false);

        Kernel kernel = kernelFactory.CreateKernel(llm, apiKey);
        kernel.FunctionInvocationFilters.Add(toolFilter);
        kernel.AutoFunctionInvocationFilters.Add(new AutoInvokeIterationFilter(maxRounds: 12));
        foreach ((string name, object instance) in plugins.All)
            kernel.Plugins.AddFromObject(instance, name);

        _kernel = kernel;
        _chat = kernel.GetRequiredService<IChatCompletionService>();
        if (_history.Count == 0)
            _history.AddSystemMessage(SystemPrompts.BuildDefault());
    }
}
