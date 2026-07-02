using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;

namespace FruityLink.Llm.Auth;

/// <summary>
/// Front of the gateway HTTP chain: stamps <c>Authorization: Bearer &lt;access token&gt;</c> on
/// every request (overwriting the connector's placeholder key), retries ONCE after a forced token
/// refresh on 401, and maps the gateway's billing statuses onto user-readable errors:
/// <list type="bullet">
///   <item><b>401</b> — expired/invalid token → one refresh + resend; still 401 → "sign in again".</item>
///   <item><b>402</b> — monthly token quota exhausted for the plan.</item>
///   <item><b>403</b> — subscription inactive.</item>
/// </list>
/// Sits OUTERMOST (in front of the turn budget / retry / logging handlers) so a 401-refresh-retry
/// re-enters the whole pipeline as an ordinary request.
/// </summary>
public sealed class GatewayAuthHandler : DelegatingHandler
{
    private readonly IAccountAuth _auth;

    public GatewayAuthHandler(IAccountAuth auth, HttpMessageHandler innerHandler)
        : base(innerHandler)
    {
        ArgumentNullException.ThrowIfNull(auth);
        _auth = auth;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string? token = await _auth.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        if (token is null)
        {
            throw new HttpRequestException(
                "Not signed in — open Settings and sign in to your FL Automate account.");
        }

        // Buffer the body up front so a 401 retry can resend an identical clone
        // (an HttpRequestMessage can only be sent once).
        byte[]? body = request.Content is null
            ? null
            : await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        HttpResponseMessage response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            // The token the gateway saw is bad/expired (clock skew, revocation, a stale cache).
            // Force ONE refresh and resend once; a second 401 means the session is truly dead.
            string? fresh = await _auth.RefreshAccessTokenAsync(cancellationToken).ConfigureAwait(false);
            if (fresh is not null && fresh != token)
            {
                response.Dispose();
                using HttpRequestMessage retry = Clone(request, body, fresh);
                response = await base.SendAsync(retry, cancellationToken).ConfigureAwait(false);
            }
        }

        return response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => throw Fail(response,
                "Your FL Automate session has expired — open Settings and sign in again."),
            HttpStatusCode.PaymentRequired => throw Fail(response,
                "Monthly AI token quota reached for your plan — it resets next cycle, or upgrade at fl-automate.com."),
            HttpStatusCode.Forbidden => throw Fail(response,
                "Subscription inactive — check your FL Automate account at fl-automate.com."),
            _ => response,
        };
    }

    private static HttpRequestException Fail(HttpResponseMessage response, string message)
    {
        response.Dispose();
        return new HttpRequestException(message);
    }

    private static HttpRequestMessage Clone(HttpRequestMessage request, byte[]? body, string token)
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

        clone.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return clone;
    }
}
