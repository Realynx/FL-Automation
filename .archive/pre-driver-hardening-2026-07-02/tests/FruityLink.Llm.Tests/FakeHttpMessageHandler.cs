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

    private FakeHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        => _responder = responder;

    /// <summary>Returns a fixed status code and (optional) JSON body for every request.</summary>
    public static FakeHttpMessageHandler Json(HttpStatusCode status, string body)
        => new(_ => new HttpResponseMessage(status)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
        });

    /// <summary>Throws the given exception for every request (simulates a transport failure).</summary>
    public static FakeHttpMessageHandler Throws(Exception ex)
        => new(_ => throw ex);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastRequest = request;
        return Task.FromResult(_responder(request));
    }
}
