using System.Net;
using System.Net.Http.Headers;

namespace FruityLink.Llm.Diagnostics;

/// <summary>
/// Retries transient LLM failures (connection errors, timeouts, 408/429/5xx) with exponential
/// backoff + jitter, so an intermittent backend blip no longer aborts a whole tool-calling turn.
/// Each attempt is sent as a fresh clone (an <see cref="HttpRequestMessage"/> can only be sent once).
/// Non-transient failures (e.g. 400/401/404) are returned immediately — retrying won't help.
/// </summary>
public sealed class LlmRetryHandler : DelegatingHandler
{
    private const int MaxAttempts = 3;

    public LlmRetryHandler(HttpMessageHandler innerHandler) : base(innerHandler) { }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // Buffer the body once up front so every attempt can send an identical clone.
        byte[]? body = request.Content is null
            ? null
            : await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);

        for (int attempt = 1; ; attempt++)
        {
            using HttpRequestMessage clone = Clone(request, body, attempt);

            try
            {
                HttpResponseMessage response = await base.SendAsync(clone, cancellationToken).ConfigureAwait(false);

                if (attempt < MaxAttempts && IsTransientStatus(response.StatusCode))
                {
                    // Honor a server-supplied Retry-After (common on 429) when it's longer than our
                    // computed backoff — otherwise we just burn attempts hammering a rate-limited backend.
                    TimeSpan? retryAfter = response.Headers.RetryAfter?.Delta
                        ?? (response.Headers.RetryAfter?.Date is { } date
                            ? date - DateTimeOffset.UtcNow
                            : (TimeSpan?)null);
                    response.Dispose();
                    await BackoffAsync(attempt, cancellationToken, retryAfter).ConfigureAwait(false);
                    continue;
                }

                return response;
            }
            catch (HttpRequestException) when (attempt < MaxAttempts)
            {
                await BackoffAsync(attempt, cancellationToken).ConfigureAwait(false);
            }
            catch (TaskCanceledException) when (attempt < MaxAttempts && !cancellationToken.IsCancellationRequested)
            {
                // A per-request timeout (not a user cancel) — treat as transient.
                await BackoffAsync(attempt, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static bool IsTransientStatus(HttpStatusCode status) => (int)status switch
    {
        408 => true, // Request Timeout
        429 => true, // Too Many Requests
        500 or 502 or 503 or 504 => true,
        _ => false,
    };

    private static async Task BackoffAsync(int attempt, CancellationToken ct, TimeSpan? retryAfter = null)
    {
        int baseMs = 250 * (int)Math.Pow(2, attempt - 1); // 250, 500, 1000…
        int jitter = Random.Shared.Next(0, 200);
        int computedMs = baseMs + jitter;

        // Respect Retry-After when the server asks for a longer wait, but cap it so a hostile/absurd
        // value can't stall the turn for minutes.
        if (retryAfter is { } ra && ra > TimeSpan.Zero)
        {
            int raMs = (int)Math.Min(ra.TotalMilliseconds, 30_000);
            if (raMs > computedMs) computedMs = raMs;
        }

        await Task.Delay(computedMs, ct).ConfigureAwait(false);
    }

    private static HttpRequestMessage Clone(HttpRequestMessage request, byte[]? body, int attempt)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri) { Version = request.Version };

        if (body is not null)
        {
            var content = new ByteArrayContent(body);
            if (request.Content is not null)
                foreach (KeyValuePair<string, IEnumerable<string>> header in request.Content.Headers)
                    content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            clone.Content = content;
        }

        foreach (KeyValuePair<string, IEnumerable<string>> header in request.Headers)
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);

        // Preserve any options the OpenAI SDK set, then stamp the attempt number for the logger.
        foreach (KeyValuePair<string, object?> option in (IEnumerable<KeyValuePair<string, object?>>)request.Options)
            ((IDictionary<string, object?>)clone.Options)[option.Key] = option.Value;
        clone.Options.Set(LlmLoggingHandler.AttemptKey, attempt);

        return clone;
    }
}
