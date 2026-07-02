using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using FruityLink.Llm.Diagnostics;
using Shouldly;
using Xunit;

namespace FruityLink.Llm.Tests;

/// <summary>
/// Behavior contract for <see cref="LlmRetryHandler"/>: transient failures (408/429/5xx, transport
/// exceptions, mid-body IO drops) are retried with byte-identical request clones; non-transient
/// responses return immediately; a server Retry-After is honored; user cancellation interrupts a
/// backoff wait instead of stalling the turn.
/// </summary>
public sealed class LlmRetryHandlerTests
{
    private static readonly Uri ChatUri = new("http://localhost:11434/v1/chat/completions");

    [Fact]
    public async Task Send_503Then200_SucceedsOnSecondAttempt()
    {
        var inner = new SequenceHandler(
            Status(HttpStatusCode.ServiceUnavailable),
            Status(HttpStatusCode.OK, """{"ok":true}"""));
        using var invoker = new HttpMessageInvoker(new LlmRetryHandler(inner, NoWait));

        HttpResponseMessage response = await invoker.SendAsync(NewRequest(), CancellationToken.None);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        inner.Calls.ShouldBe(2);
        inner.AttemptsSeen.ShouldBe(new[] { 1, 2 }); // attempt numbers stamped for the logger
    }

    [Fact]
    public async Task Send_400_IsNotRetried()
    {
        // The single-item script makes an unexpected retry fail loudly (queue exhausted).
        var inner = new SequenceHandler(Status(HttpStatusCode.BadRequest, """{"error":"bad request"}"""));
        using var invoker = new HttpMessageInvoker(new LlmRetryHandler(inner, NoWait));

        HttpResponseMessage response = await invoker.SendAsync(NewRequest(), CancellationToken.None);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        inner.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task Send_HttpRequestException_IsRetried()
    {
        var inner = new SequenceHandler(
            Throw(new HttpRequestException("connection refused")),
            Status(HttpStatusCode.OK));
        using var invoker = new HttpMessageInvoker(new LlmRetryHandler(inner, NoWait));

        HttpResponseMessage response = await invoker.SendAsync(NewRequest(), CancellationToken.None);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        inner.Calls.ShouldBe(2);
    }

    [Fact]
    public async Task Send_IOExceptionMidBody_IsRetried()
    {
        // The repair handler downstream reads the whole body, so a connection dropped mid-body
        // surfaces as an IOException (HttpIOException), NOT an HttpRequestException — it must be
        // treated as equally transient.
        var inner = new SequenceHandler(
            Throw(new IOException("connection reset mid-body")),
            Status(HttpStatusCode.OK));
        using var invoker = new HttpMessageInvoker(new LlmRetryHandler(inner, NoWait));

        HttpResponseMessage response = await invoker.SendAsync(NewRequest(), CancellationToken.None);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        inner.Calls.ShouldBe(2);
    }

    [Fact]
    public async Task Send_TransientFailuresExhausted_ReturnsLastResponse()
    {
        // Five 503s exhaust the (now five-) attempt budget; the last failure is RETURNED, not thrown.
        var inner = new SequenceHandler(
            Status(HttpStatusCode.ServiceUnavailable),
            Status(HttpStatusCode.ServiceUnavailable),
            Status(HttpStatusCode.ServiceUnavailable),
            Status(HttpStatusCode.ServiceUnavailable),
            Status(HttpStatusCode.ServiceUnavailable, """{"error":"still down"}"""));
        using var invoker = new HttpMessageInvoker(new LlmRetryHandler(inner, NoWait));

        HttpResponseMessage response = await invoker.SendAsync(NewRequest(), CancellationToken.None);

        // After MaxAttempts the last failure is RETURNED (not thrown) so callers see the real status.
        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        inner.Calls.ShouldBe(5);
    }

    [Fact]
    public async Task Send_TransientExceptionExhausted_Propagates()
    {
        var inner = new SequenceHandler(
            Throw(new HttpRequestException("down")),
            Throw(new HttpRequestException("down")),
            Throw(new HttpRequestException("down")),
            Throw(new HttpRequestException("down")),
            Throw(new HttpRequestException("still down")));
        using var invoker = new HttpMessageInvoker(new LlmRetryHandler(inner, NoWait));

        HttpRequestException ex = await Should.ThrowAsync<HttpRequestException>(
            () => invoker.SendAsync(NewRequest(), CancellationToken.None));

        ex.Message.ShouldBe("still down");
        inner.Calls.ShouldBe(5);
    }

    [Fact]
    public async Task Send_RetryAfterLongerThanComputedBackoff_IsHonored()
    {
        var inner = new SequenceHandler(
            TooManyRequests(TimeSpan.FromSeconds(1)),
            Status(HttpStatusCode.OK));
        using var invoker = new HttpMessageInvoker(new LlmRetryHandler(inner, NoWait));

        var stopwatch = Stopwatch.StartNew();
        HttpResponseMessage response = await invoker.SendAsync(NewRequest(), CancellationToken.None);
        stopwatch.Stop();

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        inner.Calls.ShouldBe(2);
        // The injected backoff is zero, so a wait of ~1s proves the server's Retry-After won
        // (900ms leaves slack for timer early-fire).
        stopwatch.ElapsedMilliseconds.ShouldBeGreaterThanOrEqualTo(900);
    }

    [Fact]
    public async Task Send_RetryAfterAbsurdValue_WaitIsCancellable()
    {
        // The 30s cap itself is too long to observe in a unit test; pin the two bounding behaviors
        // instead: the wait is honored past the computed backoff (no second attempt after 1s, where
        // the injected zero backoff would have finished immediately), and a user cancel interrupts the
        // wait immediately instead of stalling the turn for the capped duration.
        var inner = new SequenceHandler(
            TooManyRequests(TimeSpan.FromHours(1)),
            Status(HttpStatusCode.OK));
        using var invoker = new HttpMessageInvoker(new LlmRetryHandler(inner, NoWait));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));

        await Should.ThrowAsync<TaskCanceledException>(() => invoker.SendAsync(NewRequest(), cts.Token));

        inner.Calls.ShouldBe(1); // still inside the honored backoff — the retry never fired
    }

    [Fact]
    public async Task Send_RequestBody_IsByteIdenticalAcrossAttempts()
    {
        const string body = """{"model":"m","messages":[{"role":"user","content":"hållo 🎹"}]}""";
        byte[] expected = Encoding.UTF8.GetBytes(body);
        var inner = new SequenceHandler(
            Status(HttpStatusCode.ServiceUnavailable),
            Status(HttpStatusCode.ServiceUnavailable),
            Status(HttpStatusCode.OK));
        using var invoker = new HttpMessageInvoker(new LlmRetryHandler(inner, NoWait));

        HttpResponseMessage response = await invoker.SendAsync(NewRequest(body), CancellationToken.None);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        inner.RequestBodies.Count.ShouldBe(3);
        foreach (byte[]? sent in inner.RequestBodies)
            sent.ShouldBe(expected); // every clone carries the exact original bytes
        inner.ContentTypes.ShouldAllBe(t => t != null && t.Contains("application/json")); // content headers copied
        inner.AttemptsSeen.ShouldBe(new[] { 1, 2, 3 });
    }

    [Fact]
    public void DefaultBackoff_GrowsExponentially_SpansAboutFortySeconds_AndIsCapped()
    {
        // The production schedule must ride out a backend that needs ~30s to recover: the four waits
        // between five attempts grow exponentially, are each capped at 20s (+ up to 500ms jitter),
        // and together span ~40s. (Jitter of 0-500ms is why the bounds are ranges.)
        TimeSpan w1 = LlmRetryHandler.DefaultBackoff(1);
        TimeSpan w2 = LlmRetryHandler.DefaultBackoff(2);
        TimeSpan w3 = LlmRetryHandler.DefaultBackoff(3);
        TimeSpan w4 = LlmRetryHandler.DefaultBackoff(4);
        TimeSpan w5 = LlmRetryHandler.DefaultBackoff(5);

        w1.TotalMilliseconds.ShouldBeInRange(2000, 2500);
        w2.TotalMilliseconds.ShouldBeInRange(4800, 5300);
        w3.TotalMilliseconds.ShouldBeInRange(11520, 12020);
        w4.TotalMilliseconds.ShouldBeInRange(20000, 20500);   // 27.6s base, capped to 20s
        w5.TotalMilliseconds.ShouldBeInRange(20000, 20500);   // 66s base, capped to 20s

        double totalSeconds = (w1 + w2 + w3 + w4).TotalSeconds;   // waits after attempts 1-4
        totalSeconds.ShouldBeGreaterThanOrEqualTo(38);
    }

    // ---- plumbing ----------------------------------------------------------------------------

    /// <summary>Zero-delay backoff so retry tests exercise the loop without real multi-second waits;
    /// the real schedule is asserted separately in <see cref="DefaultBackoff_GrowsExponentially_SpansAboutFortySeconds_AndIsCapped"/>.</summary>
    private static TimeSpan NoWait(int attempt) => TimeSpan.Zero;

    private static HttpRequestMessage NewRequest(string body = """{"model":"m","messages":[]}""") =>
        new(HttpMethod.Post, ChatUri)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

    private static Func<HttpRequestMessage, HttpResponseMessage> Status(HttpStatusCode status, string body = "{}") =>
        _ => new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

    private static Func<HttpRequestMessage, HttpResponseMessage> Throw(Exception ex) => _ => throw ex;

    private static Func<HttpRequestMessage, HttpResponseMessage> TooManyRequests(TimeSpan retryAfter) =>
        _ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            };
            response.Headers.RetryAfter = new RetryConditionHeaderValue(retryAfter);
            return response;
        };

    /// <summary>
    /// Inner-handler fake for retry tests: plays a scripted queue of responders (one per attempt,
    /// each may throw) and records what every attempt actually sent, so tests can assert attempt
    /// counts, stamped attempt numbers, copied content headers, and byte-identical bodies. An
    /// attempt beyond the script fails loudly (queue exhausted).
    /// </summary>
    private sealed class SequenceHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] responders) : HttpMessageHandler
    {
        private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _responders = new(responders);

        public int Calls { get; private set; }
        public List<int> AttemptsSeen { get; } = [];
        public List<byte[]?> RequestBodies { get; } = [];
        public List<string?> ContentTypes { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            AttemptsSeen.Add(request.Options.TryGetValue(LlmLoggingHandler.AttemptKey, out int attempt) ? attempt : 0);
            RequestBodies.Add(request.Content is null
                ? null
                : await request.Content.ReadAsByteArrayAsync(cancellationToken));
            ContentTypes.Add(request.Content?.Headers.ContentType?.ToString());
            return _responders.Dequeue()(request);
        }
    }
}
