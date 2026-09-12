using System.Net.Http;
using System.Text;
using System.Text.Json;
using FruityLink.Core.Abstractions;
using FruityLink.Core.Configuration;

namespace FruityLink.Llm.Auth;

/// <summary>One transcript turn included in a bug report (role = user | assistant | system).</summary>
public readonly record struct BugReportTurn(string Role, string Content);

/// <summary>
/// Files a user-initiated bug report with the AI gateway (<c>POST {gateway}/v1/bug-reports</c>,
/// bearer-authenticated with the same FL Automate account session the chat runs on; the gateway
/// stores it in its database for triage). Fire-and-forget friendly: <see cref="SendAsync"/> returns
/// false instead of throwing for every expected failure (signed out, offline, non-2xx).
/// <para>JSON is serialized by hand with System.Text.Json — NOT System.Net.Http.Json, which breaks
/// under the plugin's AssemblyLoadContext (STJ type-identity split; see the same note in
/// <see cref="AccountAuthService"/> / the embedding client).</para>
/// </summary>
public sealed class BugReportClient
{
    /// <summary>Server-side cap is ~2000 chars; trim client-side so reports never bounce on size.</summary>
    private const int MaxErrorChars = 2000;

    private readonly HttpClient _http;
    private readonly IAccountAuth _auth;
    private readonly ISettingsStore _settings;

    public BugReportClient(HttpClient http, IAccountAuth auth, ISettingsStore settings)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(auth);
        ArgumentNullException.ThrowIfNull(settings);
        _http = http;
        _auth = auth;
        _settings = settings;
    }

    /// <summary>
    /// Sends the report. Returns true when the gateway accepted it (201); false when the user is
    /// signed out, the gateway is unreachable, or it rejected the report — callers surface a quiet
    /// "couldn't send" note rather than an error bubble (a bug report must never cause a new bug).
    /// </summary>
    public async Task<bool> SendAsync(
        string errorMessage,
        IReadOnlyList<BugReportTurn> transcript,
        string? clientVersion,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(errorMessage);

        try
        {
            string? token = await _auth.GetAccessTokenAsync(ct).ConfigureAwait(false);
            if (token is null) return false;   // signed out — the gateway would 401 anyway

            AppSettings app = await _settings.LoadAsync(ct).ConfigureAwait(false);
            string url = app.AccountOrDefault.GatewayBaseUrl.TrimEnd('/') + "/v1/bug-reports";

            string body = JsonSerializer.Serialize(new BugReportRequest(
                errorMessage.Length <= MaxErrorChars ? errorMessage : errorMessage[..MaxErrorChars],
                transcript.Count == 0 ? null : transcript.Select(t => new TranscriptTurn(t.Role, t.Content)).ToArray(),
                clientVersion), LlmJson.Web);

            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

            using HttpResponseMessage response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;   // an explicit caller cancel is not a delivery failure
        }
        catch (Exception)
        {
            return false;   // offline / DNS / timeout — quiet failure by contract
        }
    }

    private sealed record BugReportRequest(string ErrorMessage, TranscriptTurn[]? Transcript, string? ClientVersion);

    private sealed record TranscriptTurn(string Role, string Content);
}
