using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FruityLink.Core.Abstractions;
using FruityLink.Core.Configuration;

namespace FruityLink.Llm.Auth;

/// <summary>
/// Default <see cref="IAccountAuth"/> over the FL Automate marketing API:
/// <list type="bullet">
///   <item><c>POST {api}/auth/login</c> — email/password → access + refresh tokens.</item>
///   <item><c>POST {api}/auth/refresh</c> — ROTATING: every response carries a NEW refresh token
///     that replaces the stored one (the old one is revoked server-side).</item>
///   <item><c>POST {api}/auth/logout</c> — best-effort server-side revoke.</item>
/// </list>
/// The refresh token is persisted encrypted via <see cref="ISecretStore"/>
/// (<c>auth:refreshToken</c>); the signed-in email/plan land in settings.json for display only.
/// Access tokens (~15 min TTL) are cached in memory and proactively refreshed when under two
/// minutes to expiry; refreshes are single-flighted so parallel turns / sub-agents never race a
/// rotation (racing would revoke the token a concurrent request just stored).
/// </summary>
public sealed class AccountAuthService : IAccountAuth
{
    /// <summary>Logical secret-store key for the (rotating) refresh token.</summary>
    internal const string RefreshTokenSecretName = "auth:refreshToken";

    /// <summary>Refresh proactively when the access token has less than this left.</summary>
    private static readonly TimeSpan RefreshSkew = TimeSpan.FromMinutes(2);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly ISettingsStore _settings;
    private readonly ISecretStore _secrets;

    /// <summary>Single-flights every token mutation (login/refresh/logout/restore).</summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    // Volatile-ish snapshot: written only inside _gate, read lock-free (reference assignments are
    // atomic; a marginally stale read only costs an extra pass through the gate).
    private string? _accessToken;
    private AccountSession? _session;

    public AccountAuthService(HttpClient http, ISettingsStore settings, ISecretStore secrets)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(secrets);
        _http = http;
        _settings = settings;
        _secrets = secrets;
    }

    /// <inheritdoc />
    public bool IsLoggedIn => _session is not null;

    /// <inheritdoc />
    public AccountSession? Session => _session;

    /// <inheritdoc />
    public event Action? StateChanged;

    /// <inheritdoc />
    public async Task<AccountSession> LoginAsync(string email, string password, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        ArgumentException.ThrowIfNullOrEmpty(password);

        string apiBase = await GetApiBaseAsync(ct).ConfigureAwait(false);

        HttpResponseMessage response;
        try
        {
            // Exact body shape — the API validates strictly and rejects unknown fields.
            response = await _http.PostAsync(
                    $"{apiBase}/auth/login", JsonBody(new LoginRequest(email.Trim(), password)), ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new AccountAuthException(
                "Couldn't reach the FL Automate servers — check your internet connection and try again.", ex);
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.Unauthorized)
                throw new AccountAuthException("Invalid email or password.");
            if (response.StatusCode == HttpStatusCode.Forbidden)
                throw new AccountAuthException(await ReadErrorAsync(response,
                    "Your email address isn't verified yet — check your inbox for the verification link.", ct).ConfigureAwait(false));
            if (!response.IsSuccessStatusCode)
                throw new AccountAuthException(await ReadErrorAsync(response,
                    $"Sign-in failed ({(int)response.StatusCode}). Please try again.", ct).ConfigureAwait(false));

            TokenPairResponse? tokens = await ReadJsonAsync<TokenPairResponse>(response, ct).ConfigureAwait(false);
            if (tokens?.AccessToken is null || tokens.RefreshToken is null)
                throw new AccountAuthException("Sign-in failed: the server returned an unexpected response.");

            AccountSession session = AccessTokenPayload.Decode(tokens.AccessToken)
                ?? throw new AccountAuthException("Sign-in failed: the server returned an unreadable token.");

            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await _secrets.SetAsync(RefreshTokenSecretName, tokens.RefreshToken, ct).ConfigureAwait(false);
                _accessToken = tokens.AccessToken;
                _session = session;
            }
            finally { _gate.Release(); }

            await PersistIdentityAsync(session.Email, session.Plan, ct).ConfigureAwait(false);
            StateChanged?.Invoke();
            return session;
        }
    }

    /// <inheritdoc />
    public async Task LogoutAsync(CancellationToken ct = default)
    {
        string? refreshToken;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            refreshToken = await _secrets.GetAsync(RefreshTokenSecretName, ct).ConfigureAwait(false);
            try { await _secrets.DeleteAsync(RefreshTokenSecretName, ct).ConfigureAwait(false); }
            catch { /* local cleanup is best-effort */ }
            _accessToken = null;
            _session = null;
        }
        finally { _gate.Release(); }

        // Server-side revoke is best-effort: the local session is gone either way.
        if (!string.IsNullOrEmpty(refreshToken))
        {
            try
            {
                string apiBase = await GetApiBaseAsync(ct).ConfigureAwait(false);
                using HttpResponseMessage _ = await _http.PostAsync(
                        $"{apiBase}/auth/logout", JsonBody(new RefreshRequest(refreshToken)), ct)
                    .ConfigureAwait(false);
            }
            catch { /* offline logout still succeeds locally */ }
        }

        try { await PersistIdentityAsync(null, null, ct).ConfigureAwait(false); }
        catch { /* display-only */ }
        StateChanged?.Invoke();
    }

    /// <inheritdoc />
    public async Task<string?> GetAccessTokenAsync(CancellationToken ct = default)
    {
        string? token = _accessToken;
        if (token is not null && HasSlack(token)) return token;
        return await RefreshCoreAsync(callerToken: token, force: false, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<string?> RefreshAccessTokenAsync(CancellationToken ct = default)
        => RefreshCoreAsync(callerToken: _accessToken, force: true, ct);

    /// <inheritdoc />
    public async Task<bool> TryRestoreAsync(CancellationToken ct = default)
    {
        if (IsLoggedIn) return true;
        try
        {
            string? token = await RefreshCoreAsync(callerToken: null, force: true, ct).ConfigureAwait(false);
            if (token is null) return false;

            // Refresh identity display in settings (plan may have changed since last run).
            if (_session is { } s)
            {
                try { await PersistIdentityAsync(s.Email, s.Plan, ct).ConfigureAwait(false); }
                catch { /* display-only */ }
            }
            StateChanged?.Invoke();
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return false; }
    }

    // ── refresh core ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Single-flight refresh. <paramref name="callerToken"/> is the token the caller last saw:
    /// if a concurrent caller already rotated past it, the fresh token is returned without another
    /// refresh round-trip (avoids refresh storms after a burst of 401s).
    /// </summary>
    private async Task<string?> RefreshCoreAsync(string? callerToken, bool force, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Someone else refreshed while we waited (the cached token is no longer the one the
            // caller saw), or — non-forced — the cached token still has slack. Either way, reuse it.
            string? current = _accessToken;
            if (current is not null && (!force || current != callerToken) && HasSlack(current))
                return current;

            string? refreshToken = await _secrets.GetAsync(RefreshTokenSecretName, ct).ConfigureAwait(false);
            if (string.IsNullOrEmpty(refreshToken))
            {
                _accessToken = null;
                _session = null;
                return null;
            }

            string apiBase = await GetApiBaseAsync(ct).ConfigureAwait(false);
            HttpResponseMessage response;
            try
            {
                response = await _http.PostAsync(
                        $"{apiBase}/auth/refresh", JsonBody(new RefreshRequest(refreshToken)), ct)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                // Transient network failure: keep the session + refresh token so we recover once
                // the network returns. Hand back the cached token if it hasn't truly expired yet.
                return current is not null && !IsExpired(current) ? current : null;
            }

            using (response)
            {
                if (!response.IsSuccessStatusCode)
                {
                    // The refresh token is revoked/expired (or rejected) — the session is over.
                    // Drop it locally (token + the display identity) so we stop retrying a dead
                    // token and the settings card stops claiming "signed in".
                    try { await _secrets.DeleteAsync(RefreshTokenSecretName, ct).ConfigureAwait(false); }
                    catch { /* best-effort */ }
                    _accessToken = null;
                    _session = null;
                    try { await PersistIdentityAsync(null, null, ct).ConfigureAwait(false); }
                    catch { /* display-only */ }
                    StateChanged?.Invoke();
                    return null;
                }

                TokenPairResponse? tokens = await ReadJsonAsync<TokenPairResponse>(response, ct).ConfigureAwait(false);
                if (tokens?.AccessToken is null || tokens.RefreshToken is null)
                    return null;

                // ROTATION: persist the replacement refresh token FIRST — the old one is already
                // revoked server-side, so losing the new one would strand the session.
                await _secrets.SetAsync(RefreshTokenSecretName, tokens.RefreshToken, ct).ConfigureAwait(false);
                _accessToken = tokens.AccessToken;
                _session = AccessTokenPayload.Decode(tokens.AccessToken);
                return tokens.AccessToken;
            }
        }
        finally { _gate.Release(); }
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────

    // Deliberately NOT System.Net.Http.Json (PostAsJsonAsync/ReadFromJsonAsync): the plugin ALC
    // loads its own System.Text.Json (10.x) while System.Net.Http.Json resolves from the host
    // framework (9.x), and the mixed type identities blow up with MissingMethodException inside
    // FL. Serializing by hand keeps every JSON type on the plugin-local assembly.
    private static StringContent JsonBody<T>(T body)
        => new(JsonSerializer.Serialize(body, Json), Encoding.UTF8, "application/json");

    private static async Task<T?> ReadJsonAsync<T>(HttpResponseMessage response, CancellationToken ct)
        where T : class
    {
        string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        try { return JsonSerializer.Deserialize<T>(body, Json); }
        catch (JsonException) { return null; } // callers treat null as "unexpected response"
    }

    private static bool HasSlack(string token)
        => AccessTokenPayload.Decode(token) is { } p && p.ExpiresAt - DateTimeOffset.UtcNow > RefreshSkew;

    private static bool IsExpired(string token)
        => AccessTokenPayload.Decode(token) is not { } p || p.ExpiresAt <= DateTimeOffset.UtcNow;

    private async Task<string> GetApiBaseAsync(CancellationToken ct)
    {
        AppSettings app = await _settings.LoadAsync(ct).ConfigureAwait(false);
        return app.AccountOrDefault.ApiBaseUrl.TrimEnd('/');
    }

    /// <summary>Writes the display-only identity (email + plan) into settings.json.</summary>
    private async Task PersistIdentityAsync(string? email, string? plan, CancellationToken ct)
    {
        AppSettings app = await _settings.LoadAsync(ct).ConfigureAwait(false);
        AccountSettings account = app.AccountOrDefault with { Email = email, Plan = plan };
        await _settings.SaveAsync(app with { Account = account }, ct).ConfigureAwait(false);
    }

    private static async Task<string> ReadErrorAsync(HttpResponseMessage response, string fallback, CancellationToken ct)
    {
        try
        {
            string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using JsonDocument doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out JsonElement err) && err.ValueKind == JsonValueKind.String)
            {
                string? message = err.GetString();
                if (!string.IsNullOrWhiteSpace(message)) return message;
            }
        }
        catch { /* fall back below */ }
        return fallback;
    }

    // ── wire shapes (exact — the API validates strictly) ────────────────────────────────────

    private sealed record LoginRequest(
        [property: JsonPropertyName("email")] string Email,
        [property: JsonPropertyName("password")] string Password);

    private sealed record RefreshRequest(
        [property: JsonPropertyName("refreshToken")] string RefreshToken);

    private sealed record TokenPairResponse(
        [property: JsonPropertyName("accessToken")] string? AccessToken,
        [property: JsonPropertyName("refreshToken")] string? RefreshToken);
}
