using System.Net.Http.Headers;
using System.Text;
using FruityLink.Core.Abstractions;

namespace FruityLink.Llm.Diagnostics;

/// <summary>
/// Logs every LLM HTTP attempt to <see cref="ILlmDiagnostics"/>. On failure (non-2xx or an
/// exception) it also captures the request and response bodies — the response is re-buffered so
/// downstream code (Semantic Kernel) can still read it.
/// </summary>
public sealed class LlmLoggingHandler : DelegatingHandler
{
    internal static readonly HttpRequestOptionsKey<int> AttemptKey = new("fl-llm-attempt");

    private const int MaxPreview = 6000;

    private readonly ILlmDiagnostics _diagnostics;

    public LlmLoggingHandler(ILlmDiagnostics diagnostics) => _diagnostics = diagnostics;

    public LlmLoggingHandler(HttpMessageHandler innerHandler, ILlmDiagnostics diagnostics)
        : base(innerHandler) => _diagnostics = diagnostics;

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
    /// with an identical buffer so downstream readers still get it.</summary>
    private static async Task<string?> ReadAndRebufferResponseAsync(HttpResponseMessage response)
    {
        try
        {
            byte[] bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            HttpContentHeaders oldHeaders = response.Content.Headers;

            var rebuffered = new ByteArrayContent(bytes);
            foreach (var header in oldHeaders)
                rebuffered.Headers.TryAddWithoutValidation(header.Key, header.Value);

            response.Content = rebuffered;
            return Truncate(Encoding.UTF8.GetString(bytes));
        }
        catch
        {
            return null;
        }
    }

    private static string Truncate(string text) =>
        text.Length <= MaxPreview ? text : text[..MaxPreview] + $"… ({text.Length} chars total)";
}
