using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace FruityLink.Llm.Auth;

/// <summary>
/// "Test connection" for the Settings card: calls the gateway's <c>GET /v1/models</c> with the
/// signed-in account's Bearer token and returns the model ids available to the account's plan.
/// Replaces the old multi-provider reachability probe. Throws <see cref="AccountAuthException"/>
/// with a user-readable message on any failure (not signed in, auth rejected, network down).
/// </summary>
public sealed class GatewayConnectivity
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

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

    /// <summary>Lists the model ids the account may use, probing <c>{gatewayBaseUrl}/v1/models</c>.</summary>
    public async Task<IReadOnlyList<string>> ListModelsAsync(string gatewayBaseUrl, CancellationToken ct = default)
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

            // OpenAI-style model list: {"object":"list","data":[{"id":"..."}, …]}
            try
            {
                await using Stream stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                using JsonDocument doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
                var ids = new List<string>();
                if (doc.RootElement.TryGetProperty("data", out JsonElement data) && data.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement item in data.EnumerateArray())
                    {
                        if (item.TryGetProperty("id", out JsonElement id) && id.ValueKind == JsonValueKind.String)
                            ids.Add(id.GetString()!);
                    }
                }
                return ids;
            }
            catch (JsonException)
            {
                throw new AccountAuthException("The gateway answered, but not with a model list — check the Gateway URL.");
            }
        }
    }
}
