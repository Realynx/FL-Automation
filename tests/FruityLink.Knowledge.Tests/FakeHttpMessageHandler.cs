using System.Net;

namespace FruityLink.Knowledge.Tests;

/// <summary>Returns a single canned HTTP response for any request, for testing the web path.</summary>
internal sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly string _content;
    private readonly string _mediaType;
    private readonly HttpStatusCode _status;

    public FakeHttpMessageHandler(string content, string mediaType = "text/html", HttpStatusCode status = HttpStatusCode.OK)
    {
        _content = content;
        _mediaType = mediaType;
        _status = status;
    }

    /// <summary>The last request URI this handler received, if any.</summary>
    public Uri? LastRequestUri { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastRequestUri = request.RequestUri;
        var response = new HttpResponseMessage(_status)
        {
            Content = new StringContent(_content, System.Text.Encoding.UTF8, _mediaType),
        };
        return Task.FromResult(response);
    }
}
