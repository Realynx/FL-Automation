using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using FruityLink.Llm.Diagnostics;

namespace FruityLink.Llm.Auth;

/// <summary>
/// Front of the gateway HTTP chain: stamps <c>Authorization: Bearer &lt;access token&gt;</c> on
/// every request (overwriting the connector's placeholder key), retries ONCE after a forced token
/// refresh on 401, and maps the gateway's billing statuses onto user-readable errors:
/// <list type="bullet">
///   <item><b>401</b> — expired/invalid token → one refresh + resend; still 401 → "sign in again".</item>
///   <item><b>402</b> — monthly token quota exhausted for the plan.</item>
///   <item><b>403</b> — subscription inactive.</item>
///   <item><b>503</b> with <c>"no_backend_configured"</c> — the plan has no AI backend wired up
///   yet (the gateway no longer silently serves mock replies). Any other 503 passes through to the
///   retry layer like an ordinary server error.</item>
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
                using HttpRequestMessage retry = HttpRequestCloner.Clone(request, body);
                retry.Headers.Authorization = new AuthenticationHeaderValue("Bearer", fresh);
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
            HttpStatusCode.ServiceUnavailable =>
                await MapServiceUnavailableAsync(response, cancellationToken).ConfigureAwait(false),
            _ => response,
        };
    }

    /// <summary>
    /// A 503 whose body is the gateway's <c>no_backend_configured</c> error (the account's plan has
    /// no AI backend wired up yet) becomes a clear user-facing message — the server's own
    /// <c>"message"</c> field when it sends one. Every other 503 (proxy hiccup, upstream restart)
    /// passes through untouched for the retry layer. Reading the body here is safe: it buffers, and
    /// a 503 is never a live SSE stream.
    /// </summary>
    private static async Task<HttpResponseMessage> MapServiceUnavailableAsync(
        HttpResponseMessage response, CancellationToken ct)
    {
        string body;
        try { body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false); }
        catch { return response; }   // unreadable body → treat as an ordinary 503

        if (!body.Contains("no_backend_configured", StringComparison.Ordinal))
            return response;

        // Prefer the server-provided human message. Manual JsonDocument on purpose — the
        // System.Net.Http.Json helpers break inside FL's plugin ALC (see AccountAuthService.cs).
        string message = "The AI service isn't configured for your plan yet — please try again later or contact support.";
        try
        {
            using JsonDocument doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("message", out JsonElement serverMessage)
                && serverMessage.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(serverMessage.GetString()))
            {
                message = serverMessage.GetString()!;
            }
        }
        catch (JsonException) { /* marker matched but body isn't clean JSON — keep the default text */ }

        throw Fail(response, message);
    }

    private static HttpRequestException Fail(HttpResponseMessage response, string message)
    {
        response.Dispose();
        return new HttpRequestException(message);
    }
}
