using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace FruityLink.Agent.Tests;

/// <summary>
/// Scripted <see cref="IChatCompletionService"/> for turn-engine tests: each enqueued step either
/// returns a canned reply or throws, and can first mutate the passed <see cref="ChatHistory"/> /
/// poke the kernel's filters — reproducing what SK's auto-invoke loop does DURING a send (it
/// appends assistant tool_calls messages and tool results to the history in place, and drives the
/// auto-function-invocation filters). That in-flight mutation is exactly the seam
/// <c>AgentTurnRunner</c>'s rollback / dedup / pairing-repair logic exists for, and it cannot be
/// exercised with a plain "return one message" stub.
/// </summary>
internal sealed class FakeChatCompletionService : IChatCompletionService
{
    /// <summary>One scripted send: optional in-flight history/kernel mutation, then reply or throw.</summary>
    private sealed record ScriptedStep(
        ChatMessageContent? Reply,
        Exception? Failure,
        Func<ChatHistory, Kernel?, Task>? OnRequest);

    private readonly Queue<ScriptedStep> _script = new();

    public IReadOnlyDictionary<string, object?> Attributes { get; } = new Dictionary<string, object?>();

    /// <summary>Number of completion requests observed.</summary>
    public int CallCount { get; private set; }

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

    public async Task<IReadOnlyList<ChatMessageContent>> GetChatMessageContentsAsync(
        ChatHistory chatHistory,
        PromptExecutionSettings? executionSettings = null,
        Kernel? kernel = null,
        CancellationToken cancellationToken = default)
    {
        CallCount++;
        LastExecutionSettings = executionSettings;

        if (_script.Count == 0)
            throw new InvalidOperationException("FakeChatCompletionService: no scripted step left for this request.");

        ScriptedStep step = _script.Dequeue();
        if (step.OnRequest is not null)
            await step.OnRequest(chatHistory, kernel).ConfigureAwait(false);
        if (step.Failure is not null)
            throw step.Failure;

        return new[] { step.Reply! };
    }

    /// <summary>The turn engine is non-streaming BY DESIGN (buffered bodies are what the wire-layer
    /// repair assumes) — any streaming call from production code is a bug this fake surfaces loudly.</summary>
    public IAsyncEnumerable<StreamingChatMessageContent> GetStreamingChatMessageContentsAsync(
        ChatHistory chatHistory,
        PromptExecutionSettings? executionSettings = null,
        Kernel? kernel = null,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("The agent turn engine must use non-streaming completions.");
}
