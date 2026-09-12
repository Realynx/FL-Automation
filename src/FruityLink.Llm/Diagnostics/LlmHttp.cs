using System.Net.Http;

namespace FruityLink.Llm.Diagnostics;

/// <summary>
/// The two request/response gates shared by <see cref="LlmToolCallRepairHandler"/> and
/// <see cref="LlmLoggingHandler"/>, kept in one place so the handlers can never drift on WHAT
/// counts as a chat completion or a live SSE stream — a handler that lost the SSE guard would
/// buffer (and corrupt) the stream.
/// </summary>
internal static class LlmHttp
{
    /// <summary>True when the request targets the OpenAI-compatible chat-completions endpoint.</summary>
    internal static bool IsChatCompletions(HttpRequestMessage request) =>
        request.RequestUri?.AbsolutePath.EndsWith("chat/completions", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>True when the response body is a live server-sent-events stream, which must never
    /// be buffered/drained by a handler.</summary>
    internal static bool IsEventStream(HttpResponseMessage response) =>
        string.Equals(response.Content.Headers.ContentType?.MediaType, "text/event-stream",
            StringComparison.OrdinalIgnoreCase);
}
