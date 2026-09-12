using System.Net;

namespace FruityLink.Llm.Diagnostics;

/// <summary>
/// Retries transient LLM failures (connection errors, timeouts, 408/429/5xx) with exponential
/// backoff + jitter, so an intermittent backend blip no longer aborts a whole tool-calling turn.
/// Each attempt is sent as a fresh clone (an <see cref="HttpRequestMessage"/> can only be sent once).
/// Non-transient failures (e.g. 400/401/404) are returned immediately — retrying won't help.
/// </summary>
public sealed class LlmRetryHandler : DelegatingHandler
{
    // Span the retry budget across ~40s (four waits of ~2s, 5s, 12s, 20s between five attempts): a
    // self-hosted backend that goes cold/OOM after an idle gap can take ~30s to come back, and the
    // old 3-attempt / ~250ms-500ms-1s schedule gave up in under 2s — long before recovery, turning
    // a transient blip into a failed turn. See BackoffAsync for the schedule.
    private const int MaxAttempts = 5;

    /// <summary>Ceiling on a single backoff wait so a runaway exponent (or an absurd Retry-After) can
    /// never stall one turn for minutes.</summary>
    private const int MaxBackoffMs = 20_000;

    /// <summary>Maps a 1-based attempt number to how long to wait before the NEXT attempt. Injectable
    /// so tests exercise the retry logic without real multi-second waits; production uses
    /// <see cref="DefaultBackoff"/>.</summary>
    private readonly Func<int, TimeSpan> _backoff;

    public LlmRetryHandler(HttpMessageHandler innerHandler, Func<int, TimeSpan>? backoff = null)
        : base(innerHandler)
        => _backoff = backoff ?? DefaultBackoff;

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // Buffer the body once up front so every attempt can send an identical clone.
        byte[]? body = request.Content is null
            ? null
            : await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);

        for (int attempt = 1; ; attempt++)
        {
            // Clone preserves any options the OpenAI SDK set; then stamp the attempt number for the logger.
            using HttpRequestMessage clone = HttpRequestCloner.Clone(request, body);
            clone.Options.Set(LlmLoggingHandler.AttemptKey, attempt);

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
            catch (IOException) when (attempt < MaxAttempts)
            {
                // Mid-body reads surface I/O failures as HttpIOException (an IOException, NOT an
                // HttpRequestException): the repair handler downstream reads the entire response
                // body, so a connection dropped mid-body lands here — equally transient.
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

    private async Task BackoffAsync(int attempt, CancellationToken ct, TimeSpan? retryAfter = null)
    {
        int computedMs = Math.Max(0, (int)_backoff(attempt).TotalMilliseconds);

        // Respect Retry-After when the server asks for a longer wait, but cap it so a hostile/absurd
        // value can't stall the turn for minutes.
        if (retryAfter is { } ra && ra > TimeSpan.Zero)
        {
            int raMs = (int)Math.Min(ra.TotalMilliseconds, 30_000);
            if (raMs > computedMs) computedMs = raMs;
        }

        if (computedMs > 0)
            await Task.Delay(computedMs, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The production backoff schedule: exponential growth (~2s, 5s, 12s, then capped ~20s) plus a
    /// little jitter, so the four waits across five attempts total ~40s — enough to ride out a
    /// self-hosted backend that needs ~30s to recover after going cold/OOM — while no single wait
    /// exceeds <see cref="MaxBackoffMs"/>.
    /// </summary>
    /// <param name="attempt">1-based number of the attempt that just failed.</param>
    internal static TimeSpan DefaultBackoff(int attempt)
    {
        double baseMs = 2000 * Math.Pow(2.4, attempt - 1); // 2000, 4800, 11520, 27648(→cap)…
        int ms = (int)Math.Min(baseMs, MaxBackoffMs) + Random.Shared.Next(0, 500);
        return TimeSpan.FromMilliseconds(ms);
    }
}
