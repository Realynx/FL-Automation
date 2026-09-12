using System.Net.Http;

namespace FruityLink.Llm.Diagnostics;

/// <summary>
/// Clones an <see cref="HttpRequestMessage"/> so it can be resent (a request message can only be
/// sent once): method + URI + <c>Version</c>, a fresh <see cref="ByteArrayContent"/> over the
/// caller's pre-buffered body (with the original content headers), the request headers, and the
/// request options. Shared by <c>GatewayAuthHandler</c> (401 refresh-retry) and
/// <see cref="LlmRetryHandler"/> (transient-failure retries); each caller stamps its own marker
/// (the fresh <c>Authorization</c> header / the attempt-number option) on the clone afterwards.
/// </summary>
internal static class HttpRequestCloner
{
    internal static HttpRequestMessage Clone(HttpRequestMessage request, byte[]? body)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri) { Version = request.Version };

        if (body is not null)
        {
            var content = new ByteArrayContent(body);
            if (request.Content is not null)
            {
                foreach (KeyValuePair<string, IEnumerable<string>> header in request.Content.Headers)
                    content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
            clone.Content = content;
        }

        foreach (KeyValuePair<string, IEnumerable<string>> header in request.Headers)
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        foreach (KeyValuePair<string, object?> option in (IEnumerable<KeyValuePair<string, object?>>)request.Options)
            ((IDictionary<string, object?>)clone.Options)[option.Key] = option.Value;

        return clone;
    }
}
