using System.Text;
using System.Text.Json;
using FruityLink.Core.Abstractions;

namespace FruityLink.Llm.Diagnostics;

/// <summary>
/// Logs every LLM HTTP attempt to <see cref="ILlmDiagnostics"/>. On failure (non-2xx or an
/// exception) it also captures the request and response bodies — the response is re-buffered so
/// downstream code (Semantic Kernel) can still read it. On successful chat completions it parses
/// the response's <c>usage</c> block and records per-request token/byte telemetry (including the
/// byte share of the request's <c>tools</c> array) via the sink's optional
/// <see cref="ILlmUsageTelemetry"/> capability — the numbers the token-diet workstreams are
/// measured against.
/// </summary>
public sealed class LlmLoggingHandler : DelegatingHandler
{
    internal static readonly HttpRequestOptionsKey<int> AttemptKey = new("fl-llm-attempt");

    private const int MaxPreview = 6000;

    private readonly ILlmDiagnostics _diagnostics;
    private readonly ILlmUsageTelemetry? _usage;

    public LlmLoggingHandler(HttpMessageHandler innerHandler, ILlmDiagnostics diagnostics)
        : base(innerHandler)
    {
        _diagnostics = diagnostics;
        // Usage telemetry is an optional capability of the sink (see ILlmUsageTelemetry for why it
        // is not part of Core's ILlmDiagnostics); a sink without it still gets the call records.
        _usage = diagnostics as ILlmUsageTelemetry;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        long startTicks = Environment.TickCount64;
        int attempt = request.Options.TryGetValue(AttemptKey, out int a) ? a : 1;

        try
        {
            HttpResponseMessage response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            long elapsed = Environment.TickCount64 - startTicks;
            bool ok = response.IsSuccessStatusCode;

            string? requestPreview = null;
            string? responsePreview = null;
            if (!ok)
            {
                requestPreview = await ReadRequestAsync(request).ConfigureAwait(false);
                responsePreview = await ReadAndRebufferResponseAsync(response).ConfigureAwait(false);
            }

            _diagnostics.Record(new LlmCallRecord
            {
                Timestamp = DateTimeOffset.Now,
                Method = request.Method.Method,
                Uri = request.RequestUri?.ToString() ?? string.Empty,
                StatusCode = (int)response.StatusCode,
                ElapsedMs = elapsed,
                Ok = ok,
                Attempt = attempt,
                Error = ok ? null : $"{(int)response.StatusCode} {response.ReasonPhrase}".Trim(),
                RequestPreview = requestPreview,
                ResponsePreview = responsePreview,
            });

            if (ok)
                await TryRecordUsageAsync(request, response).ConfigureAwait(false);

            return response;
        }
        catch (Exception ex)
        {
            long elapsed = Environment.TickCount64 - startTicks;
            string? requestPreview = await ReadRequestAsync(request).ConfigureAwait(false);

            _diagnostics.Record(new LlmCallRecord
            {
                Timestamp = DateTimeOffset.Now,
                Method = request.Method.Method,
                Uri = request.RequestUri?.ToString() ?? string.Empty,
                StatusCode = null,
                ElapsedMs = elapsed,
                Ok = false,
                Attempt = attempt,
                Error = $"{ex.GetType().Name}: {ex.Message}",
                RequestPreview = requestPreview,
            });

            throw;
        }
    }

    /// <summary>
    /// Parses the <c>usage</c> block of a 2xx chat-completion response and records one
    /// per-request telemetry sample (prompt/completion tokens plus request/tools byte sizes).
    /// Best-effort by design: any missing piece (no usage sink, streaming body, non-JSON body,
    /// absent usage block) silently skips — telemetry must never break the call path.
    /// </summary>
    private async Task TryRecordUsageAsync(HttpRequestMessage request, HttpResponseMessage response)
    {
        if (_usage is null) return;
        if (request.RequestUri?.AbsolutePath.EndsWith("chat/completions", StringComparison.OrdinalIgnoreCase) != true)
            return;

        try
        {
            // Never drain a real SSE stream. Everything else is safe to buffer: the agent runs
            // non-streaming, and the repair handler beneath us has already re-buffered the body
            // into a repeatable StringContent; LoadIntoBufferAsync covers any wiring without it.
            string? mediaType = response.Content.Headers.ContentType?.MediaType;
            if (string.Equals(mediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
                return;

            await response.Content.LoadIntoBufferAsync().ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

            using JsonDocument doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                !doc.RootElement.TryGetProperty("usage", out JsonElement usage) ||
                usage.ValueKind != JsonValueKind.Object)
                return;

            long promptTokens = ReadLong(usage, "prompt_tokens");
            long completionTokens = ReadLong(usage, "completion_tokens");

            // The request body is re-readable (the retry handler buffers every attempt into a
            // ByteArrayContent clone), so measuring it here costs no extra network work.
            string? requestBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync().ConfigureAwait(false);
            long requestBytes = requestBody is null ? 0 : Encoding.UTF8.GetByteCount(requestBody);
            long toolsBytes = requestBody is null ? 0 : MeasureToolsPropertyBytes(requestBody);

            _usage.RecordUsage(new LlmUsageSample(promptTokens, completionTokens, requestBytes, toolsBytes));
        }
        catch
        {
            // Telemetry must never break the call path.
        }
    }

    private static long ReadLong(JsonElement obj, string property) =>
        obj.TryGetProperty(property, out JsonElement element) && element.TryGetInt64(out long value) ? value : 0;

    /// <summary>
    /// Measures the UTF-8 byte length of the request's <c>"tools": […]</c> property (key through
    /// closing bracket) WITHOUT a full JSON parse: find a <c>"tools"</c> key followed by
    /// <c>:</c> and <c>[</c>, then bracket-match with string/escape awareness. A deliberate cheap
    /// scan — this runs on every successful request, and the value only feeds telemetry, so the
    /// (unlikely) false positive of a message containing the literal <c>"tools": [</c> is an
    /// accepted trade-off. Returns 0 when the request advertises no tools.
    /// </summary>
    internal static long MeasureToolsPropertyBytes(string requestJson)
    {
        const string key = "\"tools\"";
        int idx = 0;
        while ((idx = requestJson.IndexOf(key, idx, StringComparison.Ordinal)) >= 0)
        {
            int i = SkipWhitespace(requestJson, idx + key.Length);
            if (i < requestJson.Length && requestJson[i] == ':')
            {
                i = SkipWhitespace(requestJson, i + 1);
                if (i < requestJson.Length && requestJson[i] == '[')
                {
                    int end = FindBalancedArrayEnd(requestJson, i);
                    if (end > i)
                        return Encoding.UTF8.GetByteCount(requestJson.AsSpan(idx, end - idx + 1));
                }
            }
            idx += key.Length;
        }
        return 0;
    }

    private static int SkipWhitespace(string s, int i)
    {
        while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
        return i;
    }

    /// <summary>Index of the <c>]</c> closing the array opened at <paramref name="start"/>
    /// (string- and escape-aware), or -1 when it never closes.</summary>
    private static int FindBalancedArrayEnd(string s, int start)
    {
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
            else if (c == '[') depth++;
            else if (c == ']')
            {
                depth--;
                if (depth == 0) return i;
            }
        }
        return -1;
    }

    private static async Task<string?> ReadRequestAsync(HttpRequestMessage request)
    {
        if (request.Content is null) return null;
        try
        {
            string body = await request.Content.ReadAsStringAsync().ConfigureAwait(false);
            return Truncate(body);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Reads an error response body for logging, then replaces the (now-consumed) content
    /// with a fresh buffer so downstream readers still get it. The original media type is kept
    /// (error pages are often HTML/plaintext); the stale byte-describing headers are dropped by
    /// <see cref="HttpContentRebuffer"/>.</summary>
    private static async Task<string?> ReadAndRebufferResponseAsync(HttpResponseMessage response)
    {
        try
        {
            string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            HttpContentRebuffer.Replace(response, body);
            return Truncate(body);
        }
        catch
        {
            return null;
        }
    }

    private static string Truncate(string text) =>
        text.Length <= MaxPreview ? text : text[..MaxPreview] + $"… ({text.Length} chars total)";
}
