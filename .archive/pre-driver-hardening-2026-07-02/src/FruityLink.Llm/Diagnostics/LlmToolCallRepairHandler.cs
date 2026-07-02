using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FruityLink.Llm.Diagnostics;

/// <summary>
/// Sanitizes malformed tool-call argument JSON in (non-streaming) chat-completion responses before
/// Semantic Kernel parses it. Some OpenAI-compatible backends emit invalid arguments such as
/// <c>{}{}</c> (a concatenation of repeated JSON objects); SK then rejects the whole turn with
/// "Function call arguments were invalid JSON". This collapses such values to the first valid
/// object (or <c>{}</c>) so the tool call succeeds.
/// <para>
/// It also folds a message's reasoning channel into the content channel: GLM (and other thinking
/// models over the OpenAI-compatible API) return reasoning in a non-standard
/// <c>message.reasoning_content</c> / <c>message.reasoning</c> field that SK's typed deserialization
/// silently drops. Prepending it to <c>content</c> as <c>&lt;think&gt;…&lt;/think&gt;</c> lets it
/// survive into <see cref="Microsoft.SemanticKernel.ChatMessageContent.Content"/>, where the agent
/// splits it back out into a "thought" delta.
/// </para>
/// </summary>
public sealed class LlmToolCallRepairHandler : DelegatingHandler
{
    public LlmToolCallRepairHandler(HttpMessageHandler innerHandler) : base(innerHandler) { }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        HttpResponseMessage response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode) return response;
        if (request.RequestUri?.AbsolutePath.EndsWith("chat/completions", StringComparison.OrdinalIgnoreCase) != true)
            return response;

        // Only hard-skip a real streaming (SSE) body — buffering that would break the stream. For
        // everything else (application/json, text/json, text/plain, or a server that omits the type)
        // we attempt repair; RepairBody fails safe and returns the body unchanged if it isn't JSON.
        string? mediaType = response.Content.Headers.ContentType?.MediaType;
        if (string.Equals(mediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
            return response;

        string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        // Fire the JSON pass when there is tool-call argument JSON to repair OR a reasoning field to fold
        // into content. A body with neither is handed through verbatim (no parse, no re-serialize).
        bool hasToolCalls = body.Contains("\"tool_calls\"", StringComparison.Ordinal);
        bool hasReasoning = body.Contains("\"reasoning_content\"", StringComparison.Ordinal)
            || body.Contains("\"reasoning\"", StringComparison.Ordinal);
        string outBody = (hasToolCalls || hasReasoning) ? RepairBody(body) : body;

        // Always re-buffer: ReadAsStringAsync consumed the stream, so hand the SDK a fresh, readable
        // copy regardless of whether the underlying response was buffered.
        HttpContentHeaders oldHeaders = response.Content.Headers;
        var content = new StringContent(outBody, Encoding.UTF8, "application/json");
        foreach (var header in oldHeaders)
        {
            if (string.Equals(header.Key, "Content-Length", StringComparison.OrdinalIgnoreCase)) continue;
            if (string.Equals(header.Key, "Content-Type", StringComparison.OrdinalIgnoreCase)) continue;
            // We re-emit a plain UTF-8 body; carrying the original encoding/checksum headers would lie
            // about the new bytes (the transport already decompressed the response for us).
            if (string.Equals(header.Key, "Content-Encoding", StringComparison.OrdinalIgnoreCase)) continue;
            if (string.Equals(header.Key, "Content-MD5", StringComparison.OrdinalIgnoreCase)) continue;
            content.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
        response.Content = content;
        return response;
    }

    private static string RepairBody(string body)
    {
        try
        {
            JsonNode? root = JsonNode.Parse(body);
            JsonArray? choices = root?["choices"]?.AsArray();
            if (choices is null) return body;

            bool changed = false;
            foreach (JsonNode? choice in choices)
            {
                JsonNode? message = choice?["message"];
                if (message is null) continue;

                // 1) Repair malformed tool calls — the malformations weak OpenAI-compatible backends
                //    (Ollama, MiniMax, LM Studio) routinely emit and that SK then rejects.
                JsonArray? toolCalls = message["tool_calls"]?.AsArray();
                if (toolCalls is not null)
                {
                    foreach (JsonNode? call in toolCalls)
                    {
                        if (call is null) continue;

                        // Synthesize a missing/empty id: the follow-up tool-result message carries this
                        // as tool_call_id, and strict backends 400 the NEXT request if it's blank.
                        if (string.IsNullOrEmpty(ReadString(call["id"])))
                        {
                            call["id"] = "call_" + Guid.NewGuid().ToString("N").Substring(0, 24);
                            changed = true;
                        }
                        if (ReadString(call["type"]) is null)
                        {
                            call["type"] = "function";
                            changed = true;
                        }

                        JsonObject? fn = call["function"]?.AsObject();
                        if (fn is null) continue;

                        // Strip a namespacing prefix some models prepend (functions.foo / tools.foo)
                        // so the name matches a registered SK function.
                        string? name = ReadString(fn["name"]);
                        if (name is not null &&
                            (name.StartsWith("functions.", StringComparison.Ordinal) ||
                             name.StartsWith("tools.", StringComparison.Ordinal)))
                        {
                            fn["name"] = name.Substring(name.LastIndexOf('.') + 1);
                            changed = true;
                        }

                        // SK requires `arguments` to be a JSON *string* holding an object. Coerce every
                        // other shape: JSON null / missing -> "{}", an object/array value -> stringified,
                        // a non-string scalar -> "{}", and a string -> repaired (unwraps double-encoding,
                        // collapses {}{}, trims prose). fn["arguments"] returns null for both absent and
                        // JSON-null, which is exactly the case we want to backfill.
                        JsonNode? argsNode = fn["arguments"];
                        if (argsNode is JsonObject or JsonArray)
                        {
                            fn["arguments"] = argsNode.ToJsonString();
                            changed = true;
                        }
                        else if (argsNode is null)
                        {
                            fn["arguments"] = "{}";
                            changed = true;
                        }
                        else
                        {
                            string? args = TryGetString(argsNode);
                            if (args is null)
                            {
                                fn["arguments"] = "{}";
                                changed = true;
                            }
                            else
                            {
                                string fixedArgs = RepairArguments(args);
                                if (!string.Equals(fixedArgs, args, StringComparison.Ordinal))
                                {
                                    fn["arguments"] = fixedArgs;
                                    changed = true;
                                }
                            }
                        }
                    }
                }

                // 2) Fold reasoning into the content channel so SK's typed deserialization keeps it.
                if (FoldReasoningIntoContent(message))
                    changed = true;
            }

            return changed ? root!.ToJsonString() : body;
        }
        catch
        {
            return body; // never let repair break a response
        }
    }

    /// <summary>
    /// Folds a message's non-standard <c>reasoning_content</c> / <c>reasoning</c> field into its
    /// <c>content</c> as a leading <c>&lt;think&gt;…&lt;/think&gt;</c> block, then removes the source
    /// field. No-ops (returns false) when neither field holds a non-empty string, so a response
    /// without reasoning is left exactly as-is. Tool-call-only messages (content null/absent) become
    /// content = just the <c>&lt;think&gt;…&lt;/think&gt;</c> block; tool_calls are never touched.
    /// </summary>
    private static bool FoldReasoningIntoContent(JsonNode message)
    {
        string? reasoning = ReadString(message["reasoning_content"]) ?? ReadString(message["reasoning"]);
        if (string.IsNullOrEmpty(reasoning)) return false;

        string content = ReadString(message["content"]) ?? string.Empty;
        message["content"] = $"<think>{reasoning}</think>{content}";

        JsonObject obj = message.AsObject();
        obj.Remove("reasoning_content");
        obj.Remove("reasoning");
        return true;
    }

    private static string? ReadString(JsonNode? node) => node is null ? null : TryGetString(node);

    private static string? TryGetString(JsonNode node)
    {
        try { return node.GetValue<string>(); }
        catch { return null; }
    }

    internal static string RepairArguments(string args)
    {
        string s = args.Trim();
        if (s.Length == 0) return "{}";

        // Already a clean object — the common case.
        if (IsJsonObject(s)) return s;

        // Double-encoded: the value is a JSON string whose *content* is the real object
        // (e.g. "\"{\\\"bar\\\":4}\""). Unwrap one level and re-check.
        if (TryUnwrapJsonString(s, out string inner))
        {
            inner = inner.Trim();
            if (IsJsonObject(inner)) return inner;
            string? innerFirst = ExtractFirstJsonObject(inner);
            if (innerFirst is not null && IsJsonObject(innerFirst)) return innerFirst;
        }

        // Concatenated ({}{}), fenced (```json {...}```), or prose-prefixed — take the first object.
        string? first = ExtractFirstJsonObject(s);
        if (first is not null && IsJsonObject(first)) return first;

        return "{}";
    }

    /// <summary>True only when <paramref name="s"/> parses to a JSON *object* root (not a bare
    /// string/number/null, which SK cannot bind to a function's arguments).</summary>
    private static bool IsJsonObject(string s)
    {
        try { using JsonDocument d = JsonDocument.Parse(s); return d.RootElement.ValueKind == JsonValueKind.Object; }
        catch { return false; }
    }

    /// <summary>If <paramref name="s"/> is a JSON string literal, yields its decoded content.</summary>
    private static bool TryUnwrapJsonString(string s, out string inner)
    {
        inner = string.Empty;
        try
        {
            using JsonDocument d = JsonDocument.Parse(s);
            if (d.RootElement.ValueKind == JsonValueKind.String)
            {
                inner = d.RootElement.GetString() ?? string.Empty;
                return true;
            }
        }
        catch { /* not a JSON string */ }
        return false;
    }

    private static string? ExtractFirstJsonObject(string s)
    {
        int start = s.IndexOf('{');
        if (start < 0) return null;

        int depth = 0;
        bool inString = false;
        bool escape = false;
        for (int i = start; i < s.Length; i++)
        {
            char c = s[i];
            if (inString)
            {
                if (escape) escape = false;
                else if (c == '\\') escape = true;
                else if (c == '"') inString = false;
            }
            else if (c == '"') inString = true;
            else if (c == '{') depth++;
            else if (c == '}')
            {
                depth--;
                if (depth == 0) return s.Substring(start, i - start + 1);
            }
        }
        return null;
    }
}
