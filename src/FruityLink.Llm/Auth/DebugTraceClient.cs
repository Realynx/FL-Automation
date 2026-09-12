using System.Net.Http;
using System.Text;
using System.Text.Json;
using FruityLink.Core.Abstractions;
using FruityLink.Core.Configuration;
using FruityLink.Llm.Diagnostics;

namespace FruityLink.Llm.Auth;

/// <summary>
/// Opt-in remote debugging uploader: ships one agent turn's debug transcript (the same JSON record
/// <c>turns-*.jsonl</c> keeps locally — every tool call with args/result/timing, every LLM request's
/// usage, reasoning, final text) to the AI gateway (<c>POST {gateway}/v1/debug-traces</c>, bearer-
/// authenticated with the account session) for remote review of beta sessions.
///
/// <para><b>Privacy gate lives HERE, re-checked per turn:</b> unless the persisted
/// <see cref="AccountSettings.ShareDebugData"/> opt-in is true, <see cref="TrySendTurnAsync"/> drops
/// the record without any network I/O — so flipping the Settings toggle applies to the very next
/// turn, and the default (false) means nothing ever leaves the machine.</para>
///
/// <para>Fire-and-forget friendly like <see cref="BugReportClient"/>: returns false instead of
/// throwing for every expected failure (opted out, signed out, offline, non-2xx) — telemetry must
/// never fault or slow a turn. JSON is serialized by hand with System.Text.Json — NOT
/// System.Net.Http.Json, which breaks under the plugin's AssemblyLoadContext (STJ type-identity
/// split; see the same note in <see cref="AccountAuthService"/>).</para>
/// </summary>
public sealed class DebugTraceClient
{
    private static readonly string ClientVersion =
        typeof(DebugTraceClient).Assembly.GetName().Version?.ToString() ?? "unknown";

    private readonly HttpClient _http;
    private readonly IAccountAuth _auth;
    private readonly ISettingsStore _settings;

    public DebugTraceClient(HttpClient http, IAccountAuth auth, ISettingsStore settings)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(auth);
        ArgumentNullException.ThrowIfNull(settings);
        _http = http;
        _auth = auth;
        _settings = settings;
    }

    /// <summary>
    /// Sends one finished turn's serialized transcript record (as produced by the session
    /// transcript writer) if — and only if — the user opted in. Returns true when the gateway
    /// accepted it (2xx); false for every quiet-drop path.
    /// </summary>
    public async Task<bool> TrySendTurnAsync(string turnJson, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(turnJson)) return false;

        try
        {
            AppSettings app = await _settings.LoadAsync(ct).ConfigureAwait(false);
            AccountSettings account = app.AccountOrDefault;
            if (!account.ShareDebugData) return false;   // privacy default: OFF — no network I/O at all

            string? token = await _auth.GetAccessTokenAsync(ct).ConfigureAwait(false);
            if (token is null) return false;   // signed out — the gateway would 401 anyway

            string url = account.GatewayBaseUrl.TrimEnd('/') + "/v1/debug-traces";

            // Re-embed the already-serialized turn record verbatim (parse → JsonElement) so the
            // envelope stays a single well-formed JSON document without string splicing.
            using JsonDocument turn = JsonDocument.Parse(turnJson);
            string body = JsonSerializer.Serialize(
                new DebugTraceRequest(turn.RootElement, ClientVersion), LlmJson.Web);

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
            return false;   // malformed record / offline / DNS / timeout — quiet failure by contract
        }
    }

    private sealed record DebugTraceRequest(JsonElement Turn, string? ClientVersion);
}
