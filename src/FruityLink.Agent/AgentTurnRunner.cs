using FruityLink.Core.Configuration;
using FruityLink.Llm.Diagnostics;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;

namespace FruityLink.Agent;

/// <summary>The outcome of one agent turn.</summary>
/// <param name="Thought">The model's reasoning (from the folded <c>&lt;think&gt;</c> block), or empty.</param>
/// <param name="Text">The visible answer text, marker-free. When the turn was capped it carries a
/// trailing "[stopped: tool-round limit…]" notice so the user knows how to resume.</param>
/// <param name="WasCapped">True when the turn ended early on a tool-loop limit — our auto-invoke
/// round cap, or SK's own internal attempt limit (detected by uninvoked tool calls on the reply).</param>
internal sealed record TurnResult(string Thought, string Text, bool WasCapped);

/// <summary>
/// Runs ONE complete agent turn against a prepared kernel: append the user message, send a
/// non-streaming chat completion with the shared execution settings (tools auto-invoked by SK),
/// keep the history consistent (rollback on failure, reply dedup, think-marker stripping,
/// tool-pairing repair on a capped turn), and split the reply into (thought, text).
///
/// <para>This is the single implementation of the turn pipeline previously copied — with drift —
/// across <c>FlAgent</c>, <c>ChatBridgeService</c>, and <c>SubAgentService</c> (the sub-agent copy
/// had lost Temperature and rollback). Those types are now thin adapters over this runner.</para>
///
/// <para>The runner itself is stateless; per-turn state lives in the caller's
/// <see cref="ChatHistory"/> and in the kernel's <see cref="AutoInvokeIterationFilter"/> (reset per
/// turn via <see cref="AutoInvokeIterationFilter.BeginTurn"/>). Callers must serialize turns per
/// kernel/history pair (FlAgent's gate, ChatBridge's single loop, SubAgent's per-call kernel).</para>
/// </summary>
internal sealed class AgentTurnRunner
{
    /// <summary>Default auto-invoke round cap: bounds a runaway loop (flaky backends can re-call
    /// tools forever); 12 rounds is ample for legitimate multi-step actions.</summary>
    internal const int DefaultMaxRounds = 12;

    /// <summary>Extra HTTP requests allowed past the round cap before the per-turn budget aborts
    /// the send. A normal capped turn spends one request per round; the slack absorbs an overshoot
    /// round or two from mixed batches whose LAST call fails validation (the cap filter never sees
    /// those entries). Rounds whose calls ALL fail validation never reach the filter at all — the
    /// budget (<see cref="LlmTurnBudget"/>) is what stops that runaway near the cap instead of
    /// SK's internal 128-attempt limit. Retries of one request count once (budget sits outermost).</summary>
    internal const int RequestBudgetSlack = 4;

    /// <summary>Appended to the surfaced text of a capped turn (NOT stored in history — the
    /// synthetic tool results carry the in-context signal) so the user knows why the agent stopped
    /// and how to resume.</summary>
    internal const string CapNotice = "[stopped: tool-round limit — say continue to resume]";

    /// <summary>Synthetic tool-result content paired to any tool_call id a terminated loop left
    /// unanswered, so strict backends don't 400 every later request over an orphaned call. Worded
    /// neutrally: the orphan can come from OUR round cap or from SK's internal attempt limit.</summary>
    internal const string SkippedToolResult = "skipped: tool loop terminated";

    /// <summary>
    /// Runs one turn: <paramref name="userInput"/> in, <see cref="TurnResult"/> out, with
    /// <paramref name="history"/> updated in place. On failure the turn's half-applied assistant/tool
    /// edits are rolled back, but the user's message and the ENTIRE prior conversation are kept — so a
    /// transient backend blip (e.g. a 5xx after retries are exhausted) never costs the user their
    /// context, and the next "try again" turn resumes with full history.
    /// </summary>
    /// <param name="kernel">Kernel with plugins + filters registered (see <see cref="AgentKernelBuilder"/>).</param>
    /// <param name="chat">The kernel's chat completion service.</param>
    /// <param name="history">Conversation the turn appends to. Owned by the caller.</param>
    /// <param name="userInput">The user's message for this turn.</param>
    /// <param name="llm">Account settings for per-connection knobs (<see cref="AccountSettings.AllowParallelToolCalls"/>);
    /// null falls back to the defaults.</param>
    /// <param name="ct">Cancels the turn; the turn's partial tool edits are rolled back, but the
    /// user's message and the prior conversation are kept (see the catch below).</param>
    public async Task<TurnResult> RunTurnAsync(
        Kernel kernel,
        IChatCompletionService chat,
        ChatHistory history,
        string userInput,
        AccountSettings? llm = null,
        CancellationToken ct = default)
    {
        // Subset BEFORE the user message lands: the selector reads the history tail as "the
        // previous turn" (its wasn't-defined fallback) and the user message separately.
        IReadOnlyList<KernelFunction>? subset = ToolSubsetSelector.SelectForTurn(kernel, userInput, history);

        // Snapshot history BEFORE this turn so a failed/cancelled send can be rolled back cleanly.
        int historyMark = history.Count;
        history.AddUserMessage(userInput);

        // The cap filter is long-lived on the kernel; reset its per-turn signal so a previous
        // turn's cap can't leak into this one.
        AutoInvokeIterationFilter? capFilter =
            kernel.AutoFunctionInvocationFilters.OfType<AutoInvokeIterationFilter>().FirstOrDefault();
        capFilter?.BeginTurn();

        OpenAIPromptExecutionSettings settings =
            CreateExecutionSettings(subset, llm?.AllowParallelToolCalls ?? true);

        // Budget the turn's HTTP round-trips as a backstop for the filter-blind runaway: rounds
        // whose tool calls ALL fail validation ("wasn't defined") bypass the cap filter entirely,
        // so without this the loop only stops at SK's internal 128-attempt limit — one
        // growing-history request per round. The scope is ambient (AsyncLocal): it covers SK's
        // internal loop for THIS turn only, and a sub-agent turn spawned inside a tool call opens
        // its own scope. Tripping it throws from the HTTP layer → the catch below rolls the whole
        // turn back, so no orphaned tool_call ids survive.
        using IDisposable requestBudget =
            LlmTurnBudget.Begin((capFilter?.MaxRounds ?? DefaultMaxRounds) + RequestBudgetSlack);

        // Non-streaming on purpose: the backend returns complete tool-call arguments in a single
        // JSON body. Streaming makes some OpenAI-compatible proxies resend the full arguments in
        // each chunk, which SK concatenates into invalid JSON (e.g. "{}{}") and rejects. Tool
        // calls and reasoning are still surfaced; only the final text arrives all at once.
        ChatMessageContent reply;
        try
        {
            reply = await chat
                .GetChatMessageContentAsync(history, settings, kernel, ct)
                .ConfigureAwait(false);
        }
        catch
        {
            // SK edits history in place as it auto-invokes tools. If the turn throws or is
            // cancelled mid-loop, history can be left with an assistant tool_calls message whose
            // ids have no matching tool results — which 400s EVERY later request on strict backends
            // (a permanently wedged conversation).
            //
            // Roll back ONLY this turn's half-applied assistant/tool edits — everything AFTER the
            // user message — while KEEPING the user's message (at historyMark) and the ENTIRE prior
            // conversation. A lone unanswered user message carries no orphaned tool_call ids, so it
            // never wedges a strict backend; keeping it means a transient failure (e.g. a 5xx after
            // retries are exhausted) does NOT discard the user's request or their context, so the
            // next "try again" turn resumes with the full history instead of an empty conversation.
            // Truncating all the way to historyMark (which dropped the user's message) was the
            // context-loss bug this fixes.
            TruncateHistoryTo(history, historyMark + 1);
            throw;
        }

        // The backend's reasoning rides in the content channel as <think>…</think> (folded there
        // by LlmToolCallRepairHandler, because SK's typed OpenAI deserialization drops the raw
        // reasoning_content/reasoning fields). Split it back out: a Thought for the reasoning,
        // a clean Text for the answer. Store the STRIPPED text in history so the marker never
        // pollutes context or any later tool-call parsing.
        (string thought, string text) = ThinkTagParser.Split(reply.Content ?? string.Empty);

        EnsureInHistory(history, reply, text);
        StripThinkFromTurn(history, historyMark);

        // A reply still CARRYING FunctionCallContent means the loop ended WITHOUT invoking those
        // calls: SK exhausts its internal auto-invoke attempt limit (128) by returning the
        // assistant message with its uninvoked tool calls — no exception, and possibly without the
        // cap filter ever firing, because calls that fail validation ("wasn't defined") bypass
        // auto-function-invocation filters entirely.
        bool uninvokedCalls = reply.Items.OfType<FunctionCallContent>()
            .Any(c => c.Id is { Length: > 0 });

        // ALWAYS repair tool pairing (a cheap O(turn) scan, no-op when pairing is complete): an
        // assistant tool_calls message whose ids lack matching tool results — whether from our
        // round cap or SK's own limit — 400s EVERY later request on strict backends, permanently
        // wedging the conversation.
        RepairToolPairing(history, historyMark);

        bool capped = (capFilter?.WasCapped ?? false) || uninvokedCalls;
        if (capped)
            text = string.IsNullOrEmpty(text) ? CapNotice : text + "\n\n" + CapNotice;

        return new TurnResult(thought, text, capped);
    }

    /// <summary>
    /// The ONE execution-settings factory shared by every agent (main, in-FL tab, sub-agents) —
    /// previously three drifting copies.
    /// </summary>
    /// <param name="advertisedFunctions">Per-turn tool subset, or null = full surface.</param>
    /// <param name="allowParallelToolCalls">Per-backend toggle: some backends (GLM via certain
    /// gateways, LM Studio builds) mis-serialize parallel tool_calls; turning this off trades
    /// round-trips for parseability.</param>
    private static OpenAIPromptExecutionSettings CreateExecutionSettings(
        IReadOnlyList<KernelFunction>? advertisedFunctions, bool allowParallelToolCalls)
    {
        return new OpenAIPromptExecutionSettings
        {
            // Headroom so a reasoning turn can't spend its whole budget on <think> and then get cut
            // off mid tool-call JSON (truncated arguments are unrepairable). The iteration filter —
            // not MaxTokens — is what bounds a runaway loop.
            MaxTokens = 8192,

            // Low temperature for stable, deterministic tool-call argument JSON: fewer
            // self-correction/repair loops, so fewer round-trips. Quality knob, not a latency one.
            Temperature = 0.25,

            // Let the model request several independent tool calls in ONE response (e.g. add a
            // channel + author its notes) so a multi-step action costs one LLM round-trip instead
            // of many — the main perceived-latency win. SK still auto-invokes a response's calls
            // SEQUENTIALLY (AllowConcurrentInvocation left at its default false). Note the
            // "single-client FL bridge" invariant does NOT live here: sub-agents run their own
            // kernels in parallel, so concurrent bridge calls DO happen and are serialized by the
            // bridge client itself (one pipe, one request at a time).
            // functions: null advertises the kernel's full tool surface; a non-null subset shrinks
            // what THIS request advertises without touching what is registered/invocable.
            FunctionChoiceBehavior = FunctionChoiceBehavior.Auto(
                functions: advertisedFunctions,
                options: new FunctionChoiceBehaviorOptions { AllowParallelCalls = allowParallelToolCalls }),

            // Keep GLM's thinking ON (it is by default) but size it for a BRIEF plan-before-tools
            // rather than the deepest "max". reasoning_effort is the portable knob over the Ollama
            // /v1 OpenAI-compatible endpoint; passed via ExtensionData so SK serializes it into the
            // request body. The reasoning itself is captured downstream (LlmToolCallRepairHandler
            // folds message.reasoning_content into <think>…</think>) and split out by the runner.
            ExtensionData = new Dictionary<string, object> { ["reasoning_effort"] = "medium" },
        };
    }

    /// <summary>
    /// Appends the final assistant reply to history unless SK's auto-invoke already did, storing the
    /// <paramref name="strippedText"/> (reasoning <c>&lt;think&gt;</c> markers removed) as its content so
    /// the marker never pollutes history or any later tool-call parsing. When SK already appended the
    /// reply (with the marker), strips that copy in place. "Already appended" means the SAME message
    /// instance or an identical NON-EMPTY content — two distinct empty-content replies (common when a
    /// weak model ends a turn on a bare tool call) must not collide, or one of them silently vanishes
    /// from history.
    /// </summary>
    private static void EnsureInHistory(ChatHistory history, ChatMessageContent reply, string strippedText)
    {
        ChatMessageContent? last = history.Count > 0 ? history[^1] : null;
        string replyContent = reply.Content ?? string.Empty;
        bool alreadyThere = last is not null
            && (ReferenceEquals(last, reply)
                || (last.Role == AuthorRole.Assistant
                    && replyContent.Length > 0
                    && string.Equals(last.Content ?? string.Empty, replyContent, StringComparison.Ordinal)));

        if (alreadyThere)
        {
            // SK already appended the reply with the <think> marker still in it — strip it in place.
            last!.Content = strippedText;
        }
        else
        {
            reply.Content = strippedText;
            history.Add(reply);
        }
    }

    /// <summary>Removes trailing messages so <paramref name="history"/> is back to
    /// <paramref name="mark"/> entries — used to discard a failed turn's half-applied SK edits.</summary>
    private static void TruncateHistoryTo(ChatHistory history, int mark)
    {
        while (history.Count > mark)
            history.RemoveAt(history.Count - 1);
    }

    /// <summary>
    /// Strips any <c>&lt;think&gt;…&lt;/think&gt;</c> block from every assistant message added at or
    /// after <paramref name="fromIndex"/> (all rounds of the just-completed turn) — a multi-round
    /// tool turn appends one assistant message per round, each carrying the folded reasoning block.
    /// Left in, they re-inflate tokens and feed weak models their own prior reasoning as if it were
    /// answer content on the next turn.
    /// </summary>
    private static void StripThinkFromTurn(ChatHistory history, int fromIndex)
    {
        for (int i = Math.Max(fromIndex, 0); i < history.Count; i++)
        {
            ChatMessageContent m = history[i];
            if (m.Role != AuthorRole.Assistant) continue;
            string content = m.Content ?? string.Empty;
            if (!ThinkTagParser.ContainsMarker(content)) continue;
            m.Content = ThinkTagParser.Split(content).Text;
        }
    }

    /// <summary>
    /// Pairs up this turn's assistant tool_calls with tool results and appends a synthetic
    /// "<see cref="SkippedToolResult"/>" result for any call id left unanswered. Runs after EVERY
    /// turn (no-op when pairing is complete): terminating the auto-invoke loop — our round cap OR
    /// SK's internal attempt limit — can leave an assistant message's calls without results, and
    /// strict OpenAI-compatible backends reject the ENTIRE conversation on the next
    /// request when a tool_call id has no tool message — this keeps such a conversation usable.
    /// Synthetic results are inserted directly after each assistant message's existing result block
    /// so the strict adjacent-pairing shape is preserved.
    /// </summary>
    private static void RepairToolPairing(ChatHistory history, int fromIndex)
    {
        // Collect every answered call id in the turn first (results can trail the assistant
        // message by several tool messages).
        var answered = new HashSet<string>(StringComparer.Ordinal);
        for (int i = Math.Max(fromIndex, 0); i < history.Count; i++)
        {
            foreach (FunctionResultContent result in history[i].Items.OfType<FunctionResultContent>())
                if (result.CallId is { Length: > 0 } id)
                    answered.Add(id);
        }

        // Walk assistant messages last-to-first so insertions don't shift unprocessed indices.
        for (int i = history.Count - 1; i >= Math.Max(fromIndex, 0); i--)
        {
            ChatMessageContent m = history[i];
            if (m.Role != AuthorRole.Assistant) continue;

            var orphans = m.Items.OfType<FunctionCallContent>()
                .Where(c => c.Id is { Length: > 0 } && !answered.Contains(c.Id!))
                .ToList();
            if (orphans.Count == 0) continue;

            // Insert after the assistant message's contiguous tool-result block.
            int insertAt = i + 1;
            while (insertAt < history.Count && history[insertAt].Role == AuthorRole.Tool)
                insertAt++;

            foreach (FunctionCallContent call in orphans)
            {
                // The FunctionCallContent-based ctor copies name/plugin/call id so the synthetic
                // result serializes exactly like a real one for the orphaned tool_call id.
                var synthetic = new FunctionResultContent(call, SkippedToolResult);
                history.Insert(insertAt++, synthetic.ToChatMessage());
                answered.Add(call.Id!);
            }
        }
    }
}
