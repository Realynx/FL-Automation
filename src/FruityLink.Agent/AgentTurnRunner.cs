using System.Text;
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
/// Runs ONE complete agent turn against a prepared kernel: append the user message, send a chat
/// completion with the shared execution settings (tools auto-invoked by SK) — STREAMED by default
/// when the caller supplies a delta sink, buffered otherwise or when
/// <see cref="AccountSettings.StreamResponses"/> is off — keep the history consistent (rollback on
/// failure, reply dedup, think-marker stripping, tool-pairing repair on a capped turn), and split
/// the reply into (thought, text).
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
    /// <summary>Default auto-invoke round cap: bounds a RUNAWAY loop (flaky backends can re-call
    /// tools forever), not honest work — real multi-step jobs (chop + arrange + route a session)
    /// chain dozens of rounds, and hitting the old cap of 12 mid-job was a recurring complaint.
    /// Overridable per install via <see cref="AccountSettings.MaxToolRoundsPerTurn"/>.</summary>
    internal const int DefaultMaxRounds = 40;

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
    /// context, and the next "try again" turn resumes with full history. When a STREAMED turn dies
    /// after real answer text already arrived, that partial text is additionally recorded as the
    /// assistant turn (see <see cref="RunStreamedAttemptAsync"/>) so a follow-up "continue" has it
    /// in context.
    /// </summary>
    /// <param name="kernel">Kernel with plugins + filters registered (see <see cref="AgentKernelBuilder"/>).</param>
    /// <param name="chat">The kernel's chat completion service.</param>
    /// <param name="history">Conversation the turn appends to. Owned by the caller.</param>
    /// <param name="userInput">The user's message for this turn.</param>
    /// <param name="llm">Account settings for per-connection knobs
    /// (<see cref="AccountSettings.AllowParallelToolCalls"/>, <see cref="AccountSettings.StreamResponses"/>);
    /// null falls back to the defaults.</param>
    /// <param name="onDelta">Receives every surfaced piece of the turn exactly once, streaming or
    /// not: incremental thought/text deltas while a streamed reply arrives, or the whole thought +
    /// whole text after a buffered one. Null = no delta delivery (turn-level callers use the
    /// returned <see cref="TurnResult"/>) and FORCES the buffered path — streaming with nobody
    /// consuming the tokens buys latency risk for nothing. May be invoked from a background
    /// (non-UI) thread; the caller marshals.</param>
    /// <param name="ct">Cancels the turn; the turn's partial tool edits are rolled back, but the
    /// user's message, the prior conversation, and any substantial streamed partial text are kept
    /// (see the attempt catches below).</param>
    public async Task<TurnResult> RunTurnAsync(
        Kernel kernel,
        IChatCompletionService chat,
        ChatHistory history,
        string userInput,
        AccountSettings? llm = null,
        Action<AgentDelta>? onDelta = null,
        CancellationToken ct = default)
    {
        // Subset BEFORE the user message lands: the selector reads the history tail as "the
        // previous turn" (its wasn't-defined fallback) and the user message separately.
        IReadOnlyList<KernelFunction>? subset = ToolSubsetSelector.SelectForTurn(kernel, userInput, history);

        // Snapshot history BEFORE this turn so a failed/cancelled send can be rolled back cleanly.
        int historyMark = history.Count;
        history.AddUserMessage(userInput);

        // The cap filter is long-lived on the kernel; each attempt below resets its per-turn
        // signal so a previous turn's (or attempt's) cap can't leak into this one.
        AutoInvokeIterationFilter? capFilter =
            kernel.AutoFunctionInvocationFilters.OfType<AutoInvokeIterationFilter>().FirstOrDefault();

        OpenAIPromptExecutionSettings settings =
            CreateExecutionSettings(subset, llm?.AllowParallelToolCalls ?? true);

        // Streaming is the default (live tokens in the bubble) but only when the caller actually
        // consumes deltas: sink-less turn callers (sub-agents, the in-FL bridge tab) keep the
        // buffered path, where the wire-layer tool-call repair still protects them. The
        // StreamResponses settings.json escape hatch forces buffered for backends whose streamed
        // tool calls arrive corrupted.
        bool stream = onDelta is not null && (llm?.StreamResponses ?? true);

        if (stream)
        {
            var sink = new DeltaSink(onDelta!);
            try
            {
                capFilter?.BeginTurn();
                return await RunStreamedAttemptAsync(
                        kernel, chat, history, historyMark, capFilter, settings, sink, ct)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested
                                       && !sink.AnyText
                                       && LooksLikeStreamedToolCallCorruption(ex))
            {
                // The wire-layer repair handler CANNOT fix streamed bodies (it hard-skips
                // text/event-stream), so a proxy that resends/corrupts streamed tool-call
                // arguments fails here in a JSON-parse shape. Retry ONCE in buffered mode, where
                // the repair works — but only while nothing visible has streamed out, or the
                // retry's answer would duplicate text already in the user's bubble. The streamed
                // attempt already rolled its edits back; drop any recorded sub-visible partial
                // too, so the retry starts from a clean lone user message.
                TruncateHistoryTo(history, historyMark + 1);
            }
        }

        capFilter?.BeginTurn();
        return await RunBufferedAttemptAsync(
                kernel, chat, history, historyMark, capFilter, settings, onDelta, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// One buffered (non-streaming) send: the backend returns complete tool-call arguments in a
    /// single JSON body, which is what the wire-layer repair handler can fix. Streaming makes some
    /// OpenAI-compatible proxies resend the full arguments in each chunk, which SK concatenates
    /// into invalid JSON (e.g. "{}{}") and rejects — hence this path stays the fallback for the
    /// streamed attempt and the whole path for StreamResponses=false. A caller-supplied delta sink
    /// still gets the surfaced pieces (whole thought, whole text) so delta delivery is ONE
    /// contract regardless of mode.
    /// </summary>
    private static async Task<TurnResult> RunBufferedAttemptAsync(
        Kernel kernel,
        IChatCompletionService chat,
        ChatHistory history,
        int historyMark,
        AutoInvokeIterationFilter? capFilter,
        OpenAIPromptExecutionSettings settings,
        Action<AgentDelta>? onDelta,
        CancellationToken ct)
    {
        // Budget the attempt's HTTP round-trips as a backstop for the filter-blind runaway: rounds
        // whose tool calls ALL fail validation ("wasn't defined") bypass the cap filter entirely,
        // so without this the loop only stops at SK's internal 128-attempt limit — one
        // growing-history request per round. The scope is ambient (AsyncLocal): it covers SK's
        // internal loop for THIS attempt only, and a sub-agent turn spawned inside a tool call
        // opens its own scope. Tripping it throws from the HTTP layer → the catch below rolls the
        // whole turn back, so no orphaned tool_call ids survive.
        using IDisposable requestBudget =
            LlmTurnBudget.Begin((capFilter?.MaxRounds ?? DefaultMaxRounds) + RequestBudgetSlack);

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

        TurnResult result = FinishTurn(history, historyMark, capFilter, reply);

        // Buffered mode with a sink (StreamResponses off, or the fallback retry): deliver the
        // surfaced pieces through the same channel a streamed turn uses, all at once.
        if (onDelta is not null)
        {
            if (result.Thought.Length > 0) onDelta(new AgentDelta(AgentDeltaKind.Thought, result.Thought));
            if (result.Text.Length > 0) onDelta(new AgentDelta(AgentDeltaKind.Text, result.Text));
        }

        return result;
    }

    /// <summary>
    /// One streamed send: tokens are pushed into <paramref name="sink"/> as they arrive (reasoning
    /// split from answer prose incrementally by <see cref="ThinkTagStreamParser"/> — see its doc
    /// for the deliberate live-delta divergences from the authoritative full parse), then the final
    /// round is reassembled into the buffered reply shape and finished through the exact same
    /// pipeline (dedup, think-stripping, pairing repair, cap detection).
    ///
    /// <para>SK's streaming auto-invoke yields chunks from EVERY internal round, not just the
    /// final one, and appends a finished tool round (assistant tool_calls + tool results) to
    /// history itself before the next round's chunks arrive. History growing between chunks is
    /// therefore the round boundary: the per-round accumulators reset there so the reassembled
    /// reply — like a buffered reply — carries the FINAL round only, while intermediate-round
    /// prose/reasoning still streams to the sink live (with separators, via
    /// <see cref="DeltaSink.RoundBreak"/>).</para>
    ///
    /// <para>Failure policy (the turn-level retry handler cannot retry a stream whose body already
    /// started): roll back this turn's half-applied tool edits exactly like the buffered path,
    /// but when real answer text already streamed out, RECORD that partial text as the assistant
    /// turn before rethrowing — the bubble keeps the partial (deltas already delivered), and a
    /// follow-up "continue" finds it in context instead of a hole. A partial that is empty or
    /// whitespace-only (or pure reasoning) is not worth an assistant message and rolls back
    /// exactly as before. Cancellation takes the same path: partial kept, tool edits dropped.</para>
    /// </summary>
    private static async Task<TurnResult> RunStreamedAttemptAsync(
        Kernel kernel,
        IChatCompletionService chat,
        ChatHistory history,
        int historyMark,
        AutoInvokeIterationFilter? capFilter,
        OpenAIPromptExecutionSettings settings,
        DeltaSink sink,
        CancellationToken ct)
    {
        // Same runaway backstop as the buffered attempt (see RunBufferedAttemptAsync): the scope
        // is per-attempt so a fallback retry starts with a fresh budget instead of the streamed
        // attempt's leftovers.
        using IDisposable requestBudget =
            LlmTurnBudget.Begin((capFilter?.MaxRounds ?? DefaultMaxRounds) + RequestBudgetSlack);

        var content = new StringBuilder();                  // current round's raw content
        var callBuilder = new FunctionCallContentBuilder(); // current round's tool-call updates
        var parser = new ThinkTagStreamParser();            // current round's live thought/text split
        AuthorRole? role = null;
        int lastHistoryCount = history.Count;

        try
        {
            await foreach (StreamingChatMessageContent chunk in chat
                .GetStreamingChatMessageContentsAsync(history, settings, kernel, ct)
                .ConfigureAwait(false))
            {
                // Round boundary: SK just appended the previous round (assistant tool_calls +
                // tool results) to history. Flush the old parser's held-back tail (it wasn't a
                // marker after all) and start clean accumulators for this round.
                if (history.Count != lastHistoryCount)
                {
                    lastHistoryCount = history.Count;
                    EmitParsed(sink, parser.Flush());
                    sink.RoundBreak();
                    content.Clear();
                    callBuilder = new FunctionCallContentBuilder();
                    parser = new ThinkTagStreamParser();
                }

                role ??= chunk.Role;
                callBuilder.Append(chunk);
                if (chunk.Content is { Length: > 0 } piece)
                {
                    content.Append(piece);
                    EmitParsed(sink, parser.Push(piece));
                }
            }
        }
        catch
        {
            // Same rollback as the buffered path: drop this turn's half-applied assistant/tool
            // edits (orphaned tool_call ids wedge strict backends), keep the user's message and
            // the entire prior conversation.
            TruncateHistoryTo(history, historyMark + 1);

            // Partial-text retention: the tokens already reached the user's bubble, so keep the
            // model's side of the story consistent — record the STRIPPED partial answer as the
            // assistant turn when it's substantial. (The authoritative full parse handles a
            // truncated <think> block: a partial that is all reasoning strips to empty and is
            // rolled back exactly as before.)
            string partialText = ThinkTagParser.Split(content.ToString()).Text;
            if (!string.IsNullOrWhiteSpace(partialText))
                history.AddAssistantMessage(partialText);
            throw;
        }

        EmitParsed(sink, parser.Flush());

        // Reassemble the final round into the buffered reply shape: raw content (think markers
        // included — FinishTurn splits/strips authoritatively) plus the accumulated tool-call
        // updates, so uninvoked-call detection and pairing repair see exactly what a buffered
        // reply would carry.
        var reply = new ChatMessageContent(role ?? AuthorRole.Assistant, content.ToString());
        foreach (FunctionCallContent call in callBuilder.Build())
            reply.Items.Add(call);

        TurnResult result = FinishTurn(history, historyMark, capFilter, reply);

        // The cap notice is surfaced-text only (FinishTurn appended it to result.Text); a
        // streamed turn must ALSO push it through the sink or the live bubble never learns why
        // the agent stopped.
        if (result.WasCapped)
            sink.Text(sink.AnyText ? "\n\n" + CapNotice : CapNotice);

        return result;
    }

    /// <summary>
    /// The shared post-reply pipeline both attempt flavors funnel through, so streamed and
    /// buffered turns leave history in exactly the same shape.
    /// </summary>
    private static TurnResult FinishTurn(
        ChatHistory history, int historyMark, AutoInvokeIterationFilter? capFilter, ChatMessageContent reply)
    {
        // The backend's reasoning rides in the content channel as <think>…</think> (folded there
        // by LlmToolCallRepairHandler on buffered bodies, or emitted inline by the backend).
        // Split it back out: a Thought for the reasoning, a clean Text for the answer. Store the
        // STRIPPED text in history so the marker never pollutes context or any later tool-call
        // parsing.
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

    /// <summary>Pushes one incremental (thought, text) parse result into the sink.</summary>
    private static void EmitParsed(DeltaSink sink, (string Thought, string Text) deltas)
    {
        sink.Thought(deltas.Thought);
        sink.Text(deltas.Text);
    }

    /// <summary>
    /// Heuristic for "the streamed body itself was unparseable" — the failure shape the buffered
    /// fallback can actually fix (the wire repair only works on buffered JSON bodies). Matches a
    /// JSON exception anywhere in the chain, or messages blaming invalid JSON / unparseable tool
    /// or function calls (SK wraps these several ways). Transport drops (IOException etc.) do NOT
    /// match: buffering wouldn't fix a dead connection, and hammering retries on top of the
    /// turn-level retry handler helps nobody.
    /// </summary>
    private static bool LooksLikeStreamedToolCallCorruption(Exception ex)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            if (e is System.Text.Json.JsonException) return true;

            string message = e.Message;
            if (message.Contains("invalid JSON", StringComparison.OrdinalIgnoreCase)) return true;
            if (message.Contains("JSON", StringComparison.OrdinalIgnoreCase)
                && (message.Contains("tool", StringComparison.OrdinalIgnoreCase)
                    || message.Contains("function", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            // Backend-worded variant (no "JSON" in the text): an upstream that rejects the
            // request over corrupted tool-call arguments — e.g. Ollama cloud's
            // 400 "invalid tool call arguments" after streamed args got concatenated.
            // The buffered retry starts the turn fresh (history was rolled back), where the
            // wire-layer repair applies.
            if (message.Contains("tool call", StringComparison.OrdinalIgnoreCase)
                && (message.Contains("invalid", StringComparison.OrdinalIgnoreCase)
                    || message.Contains("argument", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Delta sink for one turn: forwards thought/text deltas to the caller's callback, remembers
    /// whether any VISIBLE answer text went out (partial-retention + fallback eligibility), and
    /// inserts separators at tool-round boundaries so one round's prose never glues onto the
    /// next round's in the live bubble.
    /// </summary>
    private sealed class DeltaSink(Action<AgentDelta> onDelta)
    {
        private bool _anyThought;
        private bool _textBreakPending;
        private bool _thoughtBreakPending;

        /// <summary>True once any visible answer text was emitted. Gates the buffered fallback
        /// (retrying after visible text would duplicate it in the bubble) and documents that a
        /// failed stream left a partial the UI must keep.</summary>
        public bool AnyText { get; private set; }

        public void Text(string delta)
        {
            if (delta.Length == 0) return;
            if (_textBreakPending) { delta = "\n\n" + delta; _textBreakPending = false; }
            AnyText = true;
            onDelta(new AgentDelta(AgentDeltaKind.Text, delta));
        }

        public void Thought(string delta)
        {
            if (delta.Length == 0) return;
            if (_thoughtBreakPending) { delta = "\n" + delta; _thoughtBreakPending = false; }
            _anyThought = true;
            onDelta(new AgentDelta(AgentDeltaKind.Thought, delta));
        }

        /// <summary>Marks a tool-round boundary; the separator is only prepended if the NEXT round
        /// actually emits on that side, so a text-less tool round never costs blank lines.</summary>
        public void RoundBreak()
        {
            if (AnyText) _textBreakPending = true;
            if (_anyThought) _thoughtBreakPending = true;
        }
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
            // off mid tool-call JSON (truncated arguments are unrepairable). Sized for long-thinking
            // models (deepseek-class reasoning chains run past 8k); the iteration filter — not
            // MaxTokens — is what bounds a runaway loop.
            MaxTokens = 16384,

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
