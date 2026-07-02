using System.Net;
using System.Net.Http;

namespace FruityLink.Llm.Tests;

/// <summary>
/// Test double for <see cref="HttpMessageHandler"/> that returns a canned response (or throws),
/// while capturing the last request so assertions can inspect headers and the request URI.
/// </summary>
internal sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

    /// <summary>The most recent request seen by the handler, for header/URL assertions.</summary>
    public HttpRequestMessage? LastRequest { get; private set; }

    /// <summary>Total requests that REACHED this handler (a request blocked by an outer handler,
    /// e.g. the turn budget, is not counted) — for loop-bound assertions.</summary>
    public int RequestCount { get; private set; }

    private FakeHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        => _responder = responder;

    /// <summary>Every request seen by the handler, in order (each with a buffered body snapshot in
    /// <see cref="RequestBodies"/> — the message content may be disposed by the time asserts run).</summary>
    public List<HttpRequestMessage> Requests { get; } = new();

    /// <summary>The request bodies, read at send time, aligned with <see cref="Requests"/>.</summary>
    public List<string?> RequestBodies { get; } = new();

    /// <summary>Returns a fixed status code and (optional) JSON body for every request.</summary>
    public static FakeHttpMessageHandler Json(HttpStatusCode status, string body)
        => new(_ => JsonResponse(status, body));

    /// <summary>Routes every request through <paramref name="responder"/> (scripted scenarios:
    /// per-URL routing, 401-then-200 sequences, rotation chains).</summary>
    public static FakeHttpMessageHandler From(Func<HttpRequestMessage, HttpResponseMessage> responder)
        => new(responder);

    /// <summary>Builds a JSON <see cref="HttpResponseMessage"/> (for scripted responders).</summary>
    public static HttpResponseMessage JsonResponse(HttpStatusCode status, string body)
        => new(status)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
        };

    /// <summary>Throws the given exception for every request (simulates a transport failure).</summary>
    public static FakeHttpMessageHandler Throws(Exception ex)
        => new(_ => throw ex);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastRequest = request;
        RequestCount++;
        Requests.Add(request);
        RequestBodies.Add(request.Content is null
            ? null
            : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        return _responder(request);
    }
}
