using System.IO;
using System.Text.Json;
using FruityLink.Core.Configuration;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Shouldly;
using Xunit;

namespace FruityLink.Agent.Tests;

/// <summary>
/// The STREAMED side of <see cref="AgentTurnRunner"/> (see <see cref="AgentTurnRunnerTests"/> for
/// the history-consistency invariants both modes share): multi-chunk assembly with think markers
/// split across chunk boundaries, live delta delivery (including tool-round separators),
/// partial-text retention when a stream dies after answer text arrived, the one-shot buffered
/// fallback for JSON-shaped streamed failures, and the StreamResponses=false escape hatch.
/// </summary>
public sealed class AgentTurnRunnerStreamingTests
{
    private readonly AgentTurnRunner _runner = new();
    private readonly List<AgentDelta> _deltas = new();

    private Task<TurnResult> RunAsync(
        Kernel kernel,
        FakeChatCompletionService chat,
        ChatHistory history,
        string input = "hello",
        AccountSettings? llm = null) =>
        _runner.RunTurnAsync(kernel, chat, history, input, llm, onDelta: _deltas.Add);

    private string TextDeltas() =>
        string.Concat(_deltas.Where(d => d.Kind == AgentDeltaKind.Text).Select(d => d.Text));

    private string ThoughtDeltas() =>
        string.Concat(_deltas.Where(d => d.Kind == AgentDeltaKind.Thought).Select(d => d.Text));

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

    // ---------------- multi-chunk assembly ----------------

    [Fact]
    public async Task StreamedTurn_AssemblesChunks_AndSplitsThinkMarkersAcrossChunkBoundaries()
    {
        var kernel = new Kernel();
        var chat = new FakeChatCompletionService();
        var history = new ChatHistory();
        // The think markers are deliberately split MID-MARKER across chunks — the hostile
        // boundary case the incremental parser's holdback exists for.
        chat.EnqueueStreamedReply("<think>pl", "an</think>Hel", "lo wor", "ld");

        TurnResult result = await RunAsync(kernel, chat, history);

        result.Thought.ShouldBe("plan");
        result.Text.ShouldBe("Hello world");
        result.WasCapped.ShouldBeFalse();
        history[^1].Role.ShouldBe(AuthorRole.Assistant);
        history[^1].Content.ShouldBe("Hello world");   // marker-free, exactly like a buffered turn

        // The same content reached the live sink, INCREMENTALLY (several text deltas, no marker).
        ThoughtDeltas().ShouldBe("plan");
        TextDeltas().ShouldBe("Hello world");
        _deltas.Count(d => d.Kind == AgentDeltaKind.Text).ShouldBeGreaterThan(1);

        chat.StreamingCallCount.ShouldBe(1);
        chat.BufferedCallCount.ShouldBe(0);
    }

    // ---------------- partial-text retention ----------------

    [Fact]
    public async Task MidStreamFailure_AfterTextArrived_KeepsPartialAsAssistantTurn()
    {
        var kernel = new Kernel();
        var chat = new FakeChatCompletionService();
        var history = new ChatHistory();
        history.AddSystemMessage("seed prompt");
        chat.EnqueueStreamedFailure(new IOException("connection reset"), "Here is the fir", "st part");

        await Should.ThrowAsync<IOException>(() => RunAsync(kernel, chat, history, "long question"));

        // The tokens already reached the user's bubble (deltas below), so the model's side of the
        // story is kept consistent: the partial text IS the assistant turn — a follow-up
        // "continue" resumes from it instead of a hole.
        history.Count.ShouldBe(3);
        history[1].Role.ShouldBe(AuthorRole.User);
        history[2].Role.ShouldBe(AuthorRole.Assistant);
        history[2].Content.ShouldBe("Here is the first part");
        TextDeltas().ShouldBe("Here is the first part");

        // A transport drop is NOT retried in buffered mode — buffering wouldn't fix a dead
        // connection, and the turn-level retry handler already had its chance.
        chat.CallCount.ShouldBe(1);
    }

    [Fact]
    public async Task MidStreamFailure_BeforeAnyText_RollsBackExactlyLikeABufferedFailure()
    {
        var kernel = new Kernel();
        var chat = new FakeChatCompletionService();
        var history = new ChatHistory();
        chat.EnqueueStreamedFailure(new IOException("connection reset before first token"));

        await Should.ThrowAsync<IOException>(() => RunAsync(kernel, chat, history));

        // Nothing substantial streamed → no partial worth recording: the lone user message
        // survives, same as the buffered FailedSend_* invariants.
        history.Count.ShouldBe(1);
        history[0].Role.ShouldBe(AuthorRole.User);
        history.ShouldNotContain(m => m.Role == AuthorRole.Assistant);
        _deltas.ShouldBeEmpty();
    }

    [Fact]
    public async Task MidStreamCancellation_AfterTextArrived_KeepsPartialAsAssistantTurn()
    {
        var kernel = new Kernel();
        var chat = new FakeChatCompletionService();
        var history = new ChatHistory();
        // The user's Cancel surfaces as an OperationCanceledException from inside the stream.
        chat.EnqueueStreamedFailure(new OperationCanceledException(), "partial answer");

        await Should.ThrowAsync<OperationCanceledException>(() => RunAsync(kernel, chat, history));

        history[^1].Role.ShouldBe(AuthorRole.Assistant);
        history[^1].Content.ShouldBe("partial answer");
        TextDeltas().ShouldBe("partial answer");
        chat.CallCount.ShouldBe(1);   // cancellation must never trigger the buffered fallback
    }

    [Fact]
    public async Task MidStreamFailure_InsideThinkBlock_RecordsNoPartial()
    {
        var kernel = new Kernel();
        var chat = new FakeChatCompletionService();
        var history = new ChatHistory();
        // The stream died before the reasoning block closed: everything received is thought, and
        // a pure-reasoning partial is NOT an answer worth an assistant message.
        chat.EnqueueStreamedFailure(new IOException("dropped"), "<think>half a plan");

        await Should.ThrowAsync<IOException>(() => RunAsync(kernel, chat, history));

        history.Count.ShouldBe(1);
        history[0].Role.ShouldBe(AuthorRole.User);
        TextDeltas().ShouldBe(string.Empty);
        ThoughtDeltas().ShouldBe("half a plan");
    }

    // ---------------- streamed tool rounds ----------------

    [Fact]
    public async Task StreamedToolRound_ResetsAccumulatorsPerRound_AndStreamsBothRoundsLive()
    {
        var kernel = new Kernel();
        var chat = new FakeChatCompletionService();
        var history = new ChatHistory();

        // Round 1 streams its prose, then SK's auto-invoke appends the finished tool round
        // (assistant tool_calls + tool result) to history BETWEEN chunks — the mutation part —
        // and round 2 streams the final answer. The runner must reassemble the FINAL round only.
        chat.EnqueueStream(
            FakeChatCompletionService.StreamPart.Text("<think>r1</think>calling tool"),
            FakeChatCompletionService.StreamPart.Mutation((h, _) =>
            {
                ChatMessageContent round1 = AssistantWithToolCalls("call_1");
                round1.Content = "<think>r1</think>calling tool";
                h.Add(round1);
                h.Add(ToolResult("call_1", "OK: 128 BPM"));
                return Task.CompletedTask;
            }),
            FakeChatCompletionService.StreamPart.Text("<think>r2</think>final answer"));

        TurnResult result = await RunAsync(kernel, chat, history);

        // TurnResult carries the final round only — parity with the buffered shape.
        result.Thought.ShouldBe("r2");
        result.Text.ShouldBe("final answer");
        result.WasCapped.ShouldBeFalse();

        // History: [user, assistant(tool_calls), tool, reply], every round think-stripped.
        history.Count.ShouldBe(4);
        history[1].Content.ShouldBe("calling tool");
        history[^1].Content.ShouldBe("final answer");
        foreach (ChatMessageContent message in history.Where(m => m.Role == AuthorRole.Assistant))
            ThinkTagParser.ContainsMarker(message.Content ?? string.Empty).ShouldBeFalse();

        // Live deltas covered BOTH rounds, with separators at the round boundary.
        TextDeltas().ShouldBe("calling tool\n\nfinal answer");
        ThoughtDeltas().ShouldBe("r1\nr2");
    }

    [Fact]
    public async Task StreamedReplyCarryingUninvokedToolCalls_IsTreatedAsCapped_AndPaired()
    {
        // SK's internal attempt limit exits by returning the assistant message WITH its uninvoked
        // tool calls; on the wire those arrive as streamed tool-call updates. The runner must
        // reassemble them onto the reply so cap detection + pairing repair work like buffered mode.
        var kernel = new Kernel();
        var chat = new FakeChatCompletionService();
        var history = new ChatHistory();
        ChatMessageContent reply = AssistantWithToolCalls("call_128");
        reply.Content = "ran out";
        chat.EnqueueReply(reply);   // served over the streaming API as one chunk + call update

        TurnResult result = await RunAsync(kernel, chat, history);

        result.WasCapped.ShouldBeTrue();
        result.Text.ShouldBe("ran out\n\n" + AgentTurnRunner.CapNotice);
        // The live bubble learns why the agent stopped too.
        TextDeltas().ShouldBe("ran out\n\n" + AgentTurnRunner.CapNotice);
        // And the orphaned call id is answered before any later request goes out.
        history[^1].Role.ShouldBe(AuthorRole.Tool);
        FunctionResultContent synthetic = history[^1].Items.OfType<FunctionResultContent>().Single();
        synthetic.CallId.ShouldBe("call_128");
        synthetic.Result.ShouldBe(AgentTurnRunner.SkippedToolResult);
    }

    // ---------------- buffered fallback ----------------

    [Fact]
    public async Task JsonShapedStreamFailure_BeforeAnyText_RetriesOnceInBufferedMode()
    {
        var kernel = new Kernel();
        var chat = new FakeChatCompletionService();
        var history = new ChatHistory();
        // The corrupted-streamed-tool-call shape: SK chokes on unparseable JSON before any answer
        // text streams out. The wire repair only works on buffered bodies, so the runner retries
        // buffered — where the same backend succeeds.
        chat.EnqueueStreamedFailure(new JsonException("The function call arguments were invalid JSON"));
        chat.EnqueueReply("recovered");

        TurnResult result = await RunAsync(kernel, chat, history);

        result.Text.ShouldBe("recovered");
        chat.StreamingCallCount.ShouldBe(1);
        chat.BufferedCallCount.ShouldBe(1);
        // Exactly one user message and one clean assistant reply — the failed attempt left nothing.
        history.Count.ShouldBe(2);
        history[^1].Content.ShouldBe("recovered");
        // The buffered retry still delivers through the sink, so the bubble fills in.
        TextDeltas().ShouldBe("recovered");
    }

    [Fact]
    public async Task JsonShapedStreamFailure_AfterTextArrived_DoesNotRetry_AndKeepsPartial()
    {
        var kernel = new Kernel();
        var chat = new FakeChatCompletionService();
        var history = new ChatHistory();
        // Once visible text is in the user's bubble a silent retry would DUPLICATE it — surface
        // the failure with the partial kept instead.
        chat.EnqueueStreamedFailure(new JsonException("bad json"), "partial text out");

        await Should.ThrowAsync<JsonException>(() => RunAsync(kernel, chat, history));

        chat.CallCount.ShouldBe(1);
        history[^1].Role.ShouldBe(AuthorRole.Assistant);
        history[^1].Content.ShouldBe("partial text out");
    }

    // ---------------- StreamResponses escape hatch ----------------

    [Fact]
    public async Task StreamResponsesOff_UsesBufferedMode_AndStillDeliversDeltas()
    {
        var kernel = new Kernel();
        var chat = new FakeChatCompletionService();
        var history = new ChatHistory();
        chat.EnqueueReply("<think>plan</think>done");

        TurnResult result = await RunAsync(
            kernel, chat, history, llm: new AccountSettings { StreamResponses = false });

        result.Thought.ShouldBe("plan");
        result.Text.ShouldBe("done");
        chat.BufferedCallCount.ShouldBe(1);
        chat.StreamingCallCount.ShouldBe(0);
        // One delivery contract regardless of mode: the sink got the whole pieces at once.
        _deltas.ShouldBe(new[]
        {
            new AgentDelta(AgentDeltaKind.Thought, "plan"),
            new AgentDelta(AgentDeltaKind.Text, "done"),
        });
    }

    [Fact]
    public async Task NoDeltaSink_UsesBufferedMode()
    {
        // Sink-less turn callers (sub-agents, the in-FL bridge tab) must stay on the buffered
        // path, where the wire-layer tool-call repair still protects them.
        var kernel = new Kernel();
        var chat = new FakeChatCompletionService();
        var history = new ChatHistory();
        chat.EnqueueReply("done");

        TurnResult result = await _runner.RunTurnAsync(kernel, chat, history, "hello");

        result.Text.ShouldBe("done");
        chat.BufferedCallCount.ShouldBe(1);
        chat.StreamingCallCount.ShouldBe(0);
    }
}
