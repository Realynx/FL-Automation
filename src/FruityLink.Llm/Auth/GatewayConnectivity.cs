using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace FruityLink.Llm.Auth;

/// <summary>
/// One entry of the gateway's <c>GET /v1/models</c> list: the pseudonymous id chat requests send
/// as <c>model</c>, plus the friendly label the Settings picker shows (falls back to the id when
/// the gateway sends no <c>display_name</c>).
/// </summary>
public sealed record GatewayModel(string Id, string DisplayName);

/// <summary>
/// The Settings card's window onto the gateway's <c>GET /v1/models</c>: called with the signed-in
/// account's Bearer token, it returns the models available to the account's plan (both for the
/// model picker and the "Test connection" probe). Replaces the old multi-provider reachability
/// probe. Throws <see cref="AccountAuthException"/> with a user-readable message on any failure
/// (not signed in, auth rejected, network down).
/// </summary>
public sealed class GatewayConnectivity
{
    private readonly HttpClient _http;
    private readonly IAccountAuth _auth;

    /// <summary>Creates a probe over the injected client (not disposed here).</summary>
    public GatewayConnectivity(HttpClient http, IAccountAuth auth)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(auth);
        _http = http;
        _auth = auth;
    }

    /// <summary>Lists the models the account may use (id + display label, default model first, as
    /// served by the gateway), probing <c>{gatewayBaseUrl}/v1/models</c>. An empty list is a valid
    /// answer (plan not configured yet) — the caller decides how to present it.</summary>
    public async Task<IReadOnlyList<GatewayModel>> ListModelsAsync(string gatewayBaseUrl, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gatewayBaseUrl);

        string? token = await _auth.GetAccessTokenAsync(ct).ConfigureAwait(false);
        if (token is null)
            throw new AccountAuthException("Not signed in — sign in first, then test the connection.");

        string url = $"{gatewayBaseUrl.TrimEnd('/')}/v1/models";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new AccountAuthException($"Couldn't reach the AI gateway at {gatewayBaseUrl}.", ex);
        }

        using (response)
        {
            switch (response.StatusCode)
            {
                case HttpStatusCode.Unauthorized:
                    throw new AccountAuthException("The gateway rejected the session token — sign in again.");
                case HttpStatusCode.PaymentRequired:
                    throw new AccountAuthException("Connected, but the monthly AI token quota for your plan is used up.");
                case HttpStatusCode.Forbidden:
                    throw new AccountAuthException("Connected, but your subscription is inactive — check your FL Automate account.");
                default:
                    if (!response.IsSuccessStatusCode)
                        throw new AccountAuthException($"The gateway answered with status {(int)response.StatusCode}.");
                    break;
            }

            // OpenAI-style model list, extended with our friendly label:
            // {"object":"list","data":[{"id":"<pseudonym>","object":"model","display_name":"…"}, …]}
            // Parsed by hand (JsonDocument) — System.Net.Http.Json is banned in plugin code (ALC
            // type-split MissingMethodException; see AccountAuthService.cs).
            try
            {
                await using Stream stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                using JsonDocument doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
                var models = new List<GatewayModel>();
                if (doc.RootElement.TryGetProperty("data", out JsonElement data) && data.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement item in data.EnumerateArray())
                    {
                        if (item.ValueKind != JsonValueKind.Object
                            || !item.TryGetProperty("id", out JsonElement id)
                            || id.ValueKind != JsonValueKind.String
                            || string.IsNullOrWhiteSpace(id.GetString()))
                        {
                            continue;   // tolerate malformed entries rather than failing the whole list
                        }

                        string modelId = id.GetString()!;
                        string label = item.TryGetProperty("display_name", out JsonElement name)
                                       && name.ValueKind == JsonValueKind.String
                                       && !string.IsNullOrWhiteSpace(name.GetString())
                            ? name.GetString()!
                            : modelId;   // no display_name → show the id itself
                        models.Add(new GatewayModel(modelId, label));
                    }
                }
                return models;
            }
            catch (JsonException)
            {
                throw new AccountAuthException("The gateway answered, but not with a model list — check the Gateway URL.");
            }
        }
    }
}
