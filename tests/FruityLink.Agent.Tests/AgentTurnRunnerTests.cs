using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Shouldly;
using Xunit;

namespace FruityLink.Agent.Tests;

/// <summary>
/// <see cref="AgentTurnRunner"/> owns the history-consistency invariants every agent depends on:
/// wholesale rollback of a failed turn (a half-applied turn wedges strict backends forever),
/// exactly-once reply storage across SK's may-or-may-not-append behavior, think-marker stripping
/// for EVERY round of a multi-round turn, and tool-pairing repair after a capped turn. The scripted
/// <see cref="FakeChatCompletionService"/> mutates the history mid-send exactly like SK auto-invoke.
/// </summary>
public sealed class AgentTurnRunnerTests
{
    private readonly AgentTurnRunner _runner = new();

    /// <summary>An assistant message carrying tool calls for the given ids (SK's tool_calls shape).</summary>
    private static ChatMessageContent AssistantWithToolCalls(params string[] callIds)
    {
        var message = new ChatMessageContent(AuthorRole.Assistant, string.Empty);
        foreach (string id in callIds)
            message.Items.Add(new FunctionCallContent("native_get_tempo", "NativeControl", id));
        return message;
    }

    /// <summary>A tool message answering the given call id (what SK appends after invoking a tool).</summary>
    private static ChatMessageContent ToolResult(string callId, string result) =>
        new FunctionResultContent(new FunctionCallContent("native_get_tempo", "NativeControl", callId), result)
            .ToChatMessage();

    /// <summary>Flips the kernel's cap filter via a synthetic capped round, exactly like SK would
    /// on the round that hits the configured limit.</summary>
    private static Task DriveToCapAsync(Kernel kernel)
    {
        AutoInvokeIterationFilter filter =
            kernel.AutoFunctionInvocationFilters.OfType<AutoInvokeIterationFilter>().Single();
        return SyntheticAutoInvoke.RunAsync(
            filter, SyntheticAutoInvoke.Context(kernel, requestSequenceIndex: AgentTurnRunner.DefaultMaxRounds - 1));
    }

    private static Kernel KernelWithCapFilter()
    {
        var kernel = new Kernel();
        kernel.AutoFunctionInvocationFilters.Add(new AutoInvokeIterationFilter());
        return kernel;
    }

    // ---------------- rollback ----------------

    [Fact]
    public async Task FailedSend_KeepsUserMessageAndDropsPartialToolEdits()
    {
        var kernel = new Kernel();
        var chat = new FakeChatCompletionService();
        var history = new ChatHistory();
        history.AddSystemMessage("seed prompt");

        // The send appends an assistant tool_calls message + a tool result (SK's in-place edits),
        // THEN dies — the classic mid-loop failure that leaves orphaned tool_call ids behind.
        chat.EnqueueFailure(new InvalidOperationException("backend died mid-turn"), onRequest: (h, _) =>
        {
            h.Add(AssistantWithToolCalls("call_1"));
            h.Add(ToolResult("call_1", "OK: 128 BPM"));
            return Task.CompletedTask;
        });

        await Should.ThrowAsync<InvalidOperationException>(
            () => _runner.RunTurnAsync(kernel, chat, history, "what is the tempo?"));

        // The user's message MUST survive a failed turn so a retry resumes with full context; only
        // the turn's half-applied assistant/tool edits (the orphaned tool_call the send left behind)
        // are rolled back, or a strict backend would 400 every later request.
        history.Count.ShouldBe(2);
        history[0].Role.ShouldBe(AuthorRole.System);
        history[0].Content.ShouldBe("seed prompt");
        history[1].Role.ShouldBe(AuthorRole.User);
        history[1].Content.ShouldBe("what is the tempo?");
        history.ShouldNotContain(m => m.Role == AuthorRole.Assistant);
        history.ShouldNotContain(m => m.Role == AuthorRole.Tool);
    }

    [Fact]
    public async Task FailedSend_PreservesFullPriorConversation()
    {
        var kernel = new Kernel();
        var chat = new FakeChatCompletionService();
        var history = new ChatHistory();
        history.AddSystemMessage("seed prompt");
        history.AddUserMessage("earlier question");
        history.AddAssistantMessage("earlier answer");

        // A backend 500 that survives all retries surfaces here as a thrown send after SK has already
        // half-applied a tool round — reproducing the transient-5xx failure from the field.
        chat.EnqueueFailure(new InvalidOperationException("500 after retries"), onRequest: (h, _) =>
        {
            h.Add(AssistantWithToolCalls("call_1"));
            return Task.CompletedTask;
        });

        await Should.ThrowAsync<InvalidOperationException>(
            () => _runner.RunTurnAsync(kernel, chat, history, "new question"));

        // The ENTIRE prior conversation plus the new user message survive; only the partial tool
        // edit is gone. This is what lets "try again" continue with full context.
        history.Count.ShouldBe(4);
        history[0].Content.ShouldBe("seed prompt");
        history[1].Content.ShouldBe("earlier question");
        history[2].Content.ShouldBe("earlier answer");
        history[3].Role.ShouldBe(AuthorRole.User);
        history[3].Content.ShouldBe("new question");
        history.Count(m => m.Role == AuthorRole.Tool).ShouldBe(0);
    }

    // ---------------- EnsureInHistory ----------------

    [Fact]
    public async Task Reply_NotAppendedBySk_IsAppendedWithStrippedText()
    {
        var kernel = new Kernel();
        var chat = new FakeChatCompletionService();
        var history = new ChatHistory();
        chat.EnqueueReply("<think>plan</think>done");

        TurnResult result = await _runner.RunTurnAsync(kernel, chat, history, "hello");

        result.Thought.ShouldBe("plan");
        result.Text.ShouldBe("done");
        result.WasCapped.ShouldBeFalse();
        history.Count.ShouldBe(2);
        history[^1].Role.ShouldBe(AuthorRole.Assistant);
        history[^1].Content.ShouldBe("done");
    }

    [Fact]
    public async Task Reply_AlreadyAppendedBySkAsSameInstance_IsNotDuplicated()
    {
        var kernel = new Kernel();
        var chat = new FakeChatCompletionService();
        var history = new ChatHistory();
        var reply = new ChatMessageContent(AuthorRole.Assistant, "<think>plan</think>done");
        chat.EnqueueReply(reply, onRequest: (h, _) => { h.Add(reply); return Task.CompletedTask; });

        TurnResult result = await _runner.RunTurnAsync(kernel, chat, history, "hello");

        result.Text.ShouldBe("done");
        history.Count.ShouldBe(2);
        history[^1].ShouldBeSameAs(reply);
        history[^1].Content.ShouldBe("done");   // the marker was stripped in place
    }

    [Fact]
    public async Task Reply_AlreadyAppendedBySkAsIdenticalCopy_IsStrippedInPlaceNotDuplicated()
    {
        var kernel = new Kernel();
        var chat = new FakeChatCompletionService();
        var history = new ChatHistory();
        // SK appended its OWN instance with identical (non-empty) content, marker included.
        chat.EnqueueReply("<think>plan</think>done", onRequest: (h, _) =>
        {
            h.Add(new ChatMessageContent(AuthorRole.Assistant, "<think>plan</think>done"));
            return Task.CompletedTask;
        });

        await _runner.RunTurnAsync(kernel, chat, history, "hello");

        history.Count.ShouldBe(2);
        history[^1].Content.ShouldBe("done");
    }

    [Fact]
    public async Task TwoDistinctEmptyContentReplies_DoNotCollide()
    {
        var kernel = new Kernel();
        var chat = new FakeChatCompletionService();
        var history = new ChatHistory();
        // A weak model ending its turn on a bare tool call yields TWO distinct empty-content
        // assistant messages (SK's appended round + the final reply). Content-equality dedup on
        // empty strings would silently drop one of them.
        chat.EnqueueReply(new ChatMessageContent(AuthorRole.Assistant, string.Empty), onRequest: (h, _) =>
        {
            h.Add(new ChatMessageContent(AuthorRole.Assistant, string.Empty));
            return Task.CompletedTask;
        });

        await _runner.RunTurnAsync(kernel, chat, history, "hello");

        history.Count(m => m.Role == AuthorRole.Assistant).ShouldBe(2);
    }

    // ---------------- think-stripping across rounds ----------------

    [Fact]
    public async Task MultiRoundTurn_StripsThinkFromEveryAssistantRound()
    {
        var kernel = new Kernel();
        var chat = new FakeChatCompletionService();
        var history = new ChatHistory();
        history.AddSystemMessage("seed prompt");

        // Simulate a 3-round tool turn: SK appends one reasoning-bearing assistant message (plus
        // its tool result) per round, and the final reply carries reasoning of its own.
        chat.EnqueueReply("<think>round 3 reasoning</think>final answer", onRequest: (h, _) =>
        {
            var round1 = AssistantWithToolCalls("call_1");
            round1.Content = "<think>round 1 reasoning</think>calling tool";
            h.Add(round1);
            h.Add(ToolResult("call_1", "OK: 128 BPM"));

            var round2 = AssistantWithToolCalls("call_2");
            round2.Content = "<think>round 2 reasoning</think>";
            h.Add(round2);
            h.Add(ToolResult("call_2", "OK: 96 ticks per quarter note"));
            return Task.CompletedTask;
        });

        TurnResult result = await _runner.RunTurnAsync(kernel, chat, history, "hello");

        result.Thought.ShouldBe("round 3 reasoning");
        result.Text.ShouldBe("final answer");
        foreach (ChatMessageContent message in history.Where(m => m.Role == AuthorRole.Assistant))
            ThinkTagParser.ContainsMarker(message.Content ?? string.Empty).ShouldBeFalse(
                $"assistant message '{message.Content}' still carries a think marker");
        history.First(m => m.Role == AuthorRole.Assistant).Content.ShouldBe("calling tool");
        history[^1].Content.ShouldBe("final answer");
    }

    // ---------------- capped turns ----------------

    [Fact]
    public async Task CappedTurn_AppendsCapNoticeToSurfacedTextButNotHistory()
    {
        Kernel kernel = KernelWithCapFilter();
        var chat = new FakeChatCompletionService();
        var history = new ChatHistory();
        chat.EnqueueReply("done", onRequest: (_, k) => DriveToCapAsync(k!));

        TurnResult result = await _runner.RunTurnAsync(kernel, chat, history, "hello");

        result.WasCapped.ShouldBeTrue();
        result.Text.ShouldBe("done\n\n" + AgentTurnRunner.CapNotice);
        // The notice is surfaced-text only: history keeps the clean reply (the synthetic tool
        // results carry the in-context signal instead).
        history[^1].Content.ShouldBe("done");
    }

    [Fact]
    public async Task CappedTurn_WithEmptyReply_SurfacesJustTheNotice()
    {
        Kernel kernel = KernelWithCapFilter();
        var chat = new FakeChatCompletionService();
        var history = new ChatHistory();
        chat.EnqueueReply(new ChatMessageContent(AuthorRole.Assistant, string.Empty), onRequest: (_, k) => DriveToCapAsync(k!));

        TurnResult result = await _runner.RunTurnAsync(kernel, chat, history, "hello");

        result.Text.ShouldBe(AgentTurnRunner.CapNotice);
    }

    [Fact]
    public async Task CappedTurn_AppendsSyntheticResultsForOrphanedToolCallIds()
    {
        Kernel kernel = KernelWithCapFilter();
        var chat = new FakeChatCompletionService();
        var history = new ChatHistory();

        // The terminated loop left "call_orphan" without a tool result while "call_answered" got
        // one — the shape that 400s every later request on strict OpenAI-compatible backends.
        chat.EnqueueReply("stopped early", onRequest: async (h, k) =>
        {
            h.Add(AssistantWithToolCalls("call_orphan", "call_answered"));
            h.Add(ToolResult("call_answered", "OK: 128 BPM"));
            await DriveToCapAsync(k!);
        });

        await _runner.RunTurnAsync(kernel, chat, history, "hello");

        // history: [user, assistant(tool_calls), tool(call_answered), tool(SYNTHETIC), reply] —
        // the synthetic result sits directly after the existing result block, preserving the
        // strict adjacent-pairing shape.
        history.Count.ShouldBe(5);
        history[3].Role.ShouldBe(AuthorRole.Tool);
        FunctionResultContent synthetic = history[3].Items.OfType<FunctionResultContent>().Single();
        synthetic.CallId.ShouldBe("call_orphan");
        synthetic.Result.ShouldBe(AgentTurnRunner.SkippedToolResult);
    }

    // ---------------- pairing repair WITHOUT the cap firing ----------------
    // SK's function-calls processor skips the auto-invocation filter for calls that fail validation
    // ("wasn't defined"), so a turn can leave orphaned tool_call ids behind with the cap filter
    // never fired — and SK's own 128-attempt exit returns the reply WITH its uninvoked calls, no
    // exception. Pairing repair must therefore run after EVERY turn, not only capped ones.

    [Fact]
    public async Task UncappedTurn_WithOrphanedToolCalls_IsStillRepaired()
    {
        Kernel kernel = KernelWithCapFilter();
        var chat = new FakeChatCompletionService();
        var history = new ChatHistory();

        // SK appended an assistant tool_calls round whose call never got a result (the filter-blind
        // error path), then returned a normal final reply — nothing flips the cap filter.
        chat.EnqueueReply("partial answer", onRequest: (h, _) =>
        {
            h.Add(AssistantWithToolCalls("call_orphan"));
            return Task.CompletedTask;
        });

        TurnResult result = await _runner.RunTurnAsync(kernel, chat, history, "hello");

        result.WasCapped.ShouldBeFalse();
        result.Text.ShouldBe("partial answer");   // no cap notice — the turn wasn't capped
        // But the orphan is still paired, or every later request 400s on strict backends.
        history.Count.ShouldBe(4);   // user, assistant(tool_calls), tool(SYNTHETIC), reply
        history[2].Role.ShouldBe(AuthorRole.Tool);
        FunctionResultContent synthetic = history[2].Items.OfType<FunctionResultContent>().Single();
        synthetic.CallId.ShouldBe("call_orphan");
        synthetic.Result.ShouldBe(AgentTurnRunner.SkippedToolResult);
    }

    [Fact]
    public async Task ReplyCarryingUninvokedToolCalls_IsTreatedAsCapped_AndPaired()
    {
        // SK's internal 128-attempt limit exits by returning the assistant message WITH its
        // uninvoked FunctionCallContent items — no exception, possibly no cap-filter fire.
        Kernel kernel = KernelWithCapFilter();
        var chat = new FakeChatCompletionService();
        var history = new ChatHistory();
        ChatMessageContent reply = AssistantWithToolCalls("call_128");
        reply.Content = "ran out";
        chat.EnqueueReply(reply);

        TurnResult result = await _runner.RunTurnAsync(kernel, chat, history, "hello");

        result.WasCapped.ShouldBeTrue();
        result.Text.ShouldBe("ran out\n\n" + AgentTurnRunner.CapNotice);
        // The reply's own tool_call id must be answered before the next request goes out.
        history[^1].Role.ShouldBe(AuthorRole.Tool);
        FunctionResultContent synthetic = history[^1].Items.OfType<FunctionResultContent>().Single();
        synthetic.CallId.ShouldBe("call_128");
        synthetic.Result.ShouldBe(AgentTurnRunner.SkippedToolResult);
    }
}
