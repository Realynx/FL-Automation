using System.Runtime.CompilerServices;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace FruityLink.Agent.Tests;

/// <summary>
/// Scripted <see cref="IChatCompletionService"/> for turn-engine tests: each enqueued step either
/// returns a canned reply (buffered or streamed as chunks) or throws, and can first mutate the
/// passed <see cref="ChatHistory"/> / poke the kernel's filters — reproducing what SK's auto-invoke
/// loop does DURING a send (it appends assistant tool_calls messages and tool results to the
/// history in place, and drives the auto-function-invocation filters). That in-flight mutation is
/// exactly the seam <c>AgentTurnRunner</c>'s rollback / dedup / pairing-repair logic exists for,
/// and it cannot be exercised with a plain "return one message" stub.
///
/// <para>Streaming steps are scripted as a sequence of <see cref="StreamPart"/>s — text chunks,
/// mid-stream history mutations (SK appends a finished tool round to history BETWEEN rounds'
/// chunks), and a terminal failure — so the runner's per-round accumulator resets, partial-text
/// retention, and buffered fallback can all be driven chunk by chunk. A buffered step served to a
/// STREAMING request is converted faithfully: one chunk carrying the whole content plus a
/// function-call update per <see cref="FunctionCallContent"/> item (what a backend's single-chunk
/// tool-call response looks like on the wire).</para>
/// </summary>
internal sealed class FakeChatCompletionService : IChatCompletionService
{
    /// <summary>One piece of a scripted stream: exactly one of text chunk / history mutation /
    /// terminal failure.</summary>
    public sealed record StreamPart(
        string? Chunk = null,
        Func<ChatHistory, Kernel?, Task>? Mutate = null,
        Exception? Failure = null)
    {
        public static StreamPart Text(string chunk) => new(Chunk: chunk);
        public static StreamPart Mutation(Func<ChatHistory, Kernel?, Task> mutate) => new(Mutate: mutate);
        public static StreamPart Throw(Exception failure) => new(Failure: failure);
    }

    /// <summary>One scripted send: optional in-flight history/kernel mutation, then reply or throw
    /// (buffered), or a part-by-part stream.</summary>
    private sealed record ScriptedStep(
        ChatMessageContent? Reply,
        Exception? Failure,
        Func<ChatHistory, Kernel?, Task>? OnRequest,
        IReadOnlyList<StreamPart>? StreamParts = null);

    private readonly Queue<ScriptedStep> _script = new();

    public IReadOnlyDictionary<string, object?> Attributes { get; } = new Dictionary<string, object?>();

    /// <summary>Number of completion requests observed (buffered + streaming).</summary>
    public int CallCount { get; private set; }

    /// <summary>Number of BUFFERED (non-streaming) completion requests observed.</summary>
    public int BufferedCallCount { get; private set; }

    /// <summary>Number of STREAMING completion requests observed.</summary>
    public int StreamingCallCount { get; private set; }

    /// <summary>The execution settings of the most recent request (temperature/tool advertising checks).</summary>
    public PromptExecutionSettings? LastExecutionSettings { get; private set; }

    /// <summary>Scripts a successful send. <paramref name="onRequest"/> runs BEFORE the reply is
    /// returned, with the live history and kernel — use it to simulate SK's mid-send edits.</summary>
    public void EnqueueReply(ChatMessageContent reply, Func<ChatHistory, Kernel?, Task>? onRequest = null) =>
        _script.Enqueue(new ScriptedStep(reply, null, onRequest));

    /// <summary>Scripts a successful send returning a plain assistant text reply.</summary>
    public void EnqueueReply(string text, Func<ChatHistory, Kernel?, Task>? onRequest = null) =>
        EnqueueReply(new ChatMessageContent(AuthorRole.Assistant, text), onRequest);

    /// <summary>Scripts a failing send. <paramref name="onRequest"/> still runs first, so a test can
    /// leave half-applied history edits behind exactly like a turn that dies mid auto-invoke.</summary>
    public void EnqueueFailure(Exception failure, Func<ChatHistory, Kernel?, Task>? onRequest = null) =>
        _script.Enqueue(new ScriptedStep(null, failure, onRequest));

    /// <summary>Scripts a STREAMED send as an explicit part sequence (chunks / mid-stream history
    /// mutations / a terminal throw). Only valid for a streaming request — a buffered request
    /// dequeuing this step fails loudly, because the mode mismatch is itself the bug.</summary>
    public void EnqueueStream(params StreamPart[] parts) =>
        _script.Enqueue(new ScriptedStep(null, null, null, parts));

    /// <summary>Scripts a STREAMED send that yields the given text chunks then ends normally.</summary>
    public void EnqueueStreamedReply(params string[] chunks) =>
        EnqueueStream(chunks.Select(StreamPart.Text).ToArray());

    /// <summary>Scripts a STREAMED send that yields <paramref name="chunksBeforeFailure"/> then
    /// throws <paramref name="failure"/> — the mid-stream connection drop / parse failure the
    /// turn-level retry handler can never retry.</summary>
    public void EnqueueStreamedFailure(Exception failure, params string[] chunksBeforeFailure) =>
        EnqueueStream(chunksBeforeFailure.Select(StreamPart.Text).Append(StreamPart.Throw(failure)).ToArray());

    public async Task<IReadOnlyList<ChatMessageContent>> GetChatMessageContentsAsync(
        ChatHistory chatHistory,
        PromptExecutionSettings? executionSettings = null,
        Kernel? kernel = null,
        CancellationToken cancellationToken = default)
    {
        BufferedCallCount++;
        ScriptedStep step = NextStep(executionSettings);

        if (step.StreamParts is not null)
            throw new InvalidOperationException(
                "FakeChatCompletionService: a stream-scripted step was consumed by a BUFFERED request — the turn engine picked the wrong mode.");

        if (step.OnRequest is not null)
            await step.OnRequest(chatHistory, kernel).ConfigureAwait(false);
        if (step.Failure is not null)
            throw step.Failure;

        return new[] { step.Reply! };
    }

    public async IAsyncEnumerable<StreamingChatMessageContent> GetStreamingChatMessageContentsAsync(
        ChatHistory chatHistory,
        PromptExecutionSettings? executionSettings = null,
        Kernel? kernel = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        StreamingCallCount++;
        ScriptedStep step = NextStep(executionSettings);

        if (step.OnRequest is not null)
            await step.OnRequest(chatHistory, kernel).ConfigureAwait(false);

        if (step.StreamParts is not null)
        {
            foreach (StreamPart part in step.StreamParts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (part.Mutate is not null)
                    await part.Mutate(chatHistory, kernel).ConfigureAwait(false);
                if (part.Failure is not null)
                    throw part.Failure;
                if (part.Chunk is not null)
                    yield return new StreamingChatMessageContent(AuthorRole.Assistant, part.Chunk);
            }
            yield break;
        }

        if (step.Failure is not null)
            throw step.Failure;

        // A buffered-scripted reply served over the streaming API: one chunk carrying the whole
        // content, with each FunctionCallContent item converted to the wire's streamed tool-call
        // update shape (fully-qualified name, same call id) so FunctionCallContentBuilder
        // reassembles the identical calls.
        ChatMessageContent reply = step.Reply!;
        var chunk = new StreamingChatMessageContent(reply.Role, reply.Content);
        int index = 0;
        foreach (FunctionCallContent call in reply.Items.OfType<FunctionCallContent>())
        {
            string name = string.IsNullOrEmpty(call.PluginName)
                ? call.FunctionName
                : call.PluginName + "-" + call.FunctionName;
            chunk.Items.Add(new StreamingFunctionCallUpdateContent(call.Id, name, null, index++));
        }
        yield return chunk;
    }

    private ScriptedStep NextStep(PromptExecutionSettings? executionSettings)
    {
        CallCount++;
        LastExecutionSettings = executionSettings;

        if (_script.Count == 0)
            throw new InvalidOperationException("FakeChatCompletionService: no scripted step left for this request.");

        return _script.Dequeue();
    }
}
