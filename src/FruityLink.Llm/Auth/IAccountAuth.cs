namespace FruityLink.Llm.Auth;

/// <summary>Signed-in account state for display + proactive-refresh decisions. Decoded client-side
/// from the access token's JWT payload (base64url JSON — no signature verification needed or done).</summary>
/// <param name="Email">Account email.</param>
/// <param name="Tier">Subscription tier ('free' | 'pro' | 'studio').</param>
/// <param name="Plan">Effective plan ('free' | 'beta' | 'pro' | 'studio') — what gates gateway models.</param>
/// <param name="SubActive">Whether the subscription is active.</param>
/// <param name="ExpiresAt">Access-token expiry (~15 min TTL).</param>
public sealed record AccountSession(
    string Email,
    string Tier,
    string Plan,
    bool SubActive,
    DateTimeOffset ExpiresAt);

/// <summary>
/// FL Automate account authentication: email/password login against the marketing API, rotating
/// refresh-token persistence, and cached access tokens for the AI gateway. The single source of
/// truth for "who is signed in" — the settings UI, the kernel factory's auth handler, and the
/// connectivity test all go through this.
/// </summary>
public interface IAccountAuth
{
    /// <summary>True when a session is live (a restore or login succeeded and wasn't logged out).</summary>
    bool IsLoggedIn { get; }

    /// <summary>The current session (from the latest access token), or null when logged out.</summary>
    AccountSession? Session { get; }

    /// <summary>Raised after any login / logout / restore state change (any thread).</summary>
    event Action? StateChanged;

    /// <summary>
    /// Signs in with email/password. On success the rotating refresh token is persisted (encrypted)
    /// and the account identity lands in settings.json for display. Throws
    /// <see cref="AccountAuthException"/> with a user-friendly message on failure (invalid
    /// credentials, unverified email, network).
    /// </summary>
    Task<AccountSession> LoginAsync(string email, string password, CancellationToken ct = default);

    /// <summary>Signs out: best-effort revokes the refresh token server-side, then always clears the
    /// stored tokens + display identity locally. Never throws.</summary>
    Task LogoutAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns a valid access token for the gateway, or null when logged out. Proactively refreshes
    /// (single-flight) when the cached token has under two minutes to live, persisting the ROTATED
    /// refresh token each time.
    /// </summary>
    Task<string?> GetAccessTokenAsync(CancellationToken ct = default);

    /// <summary>
    /// Forces one refresh now (used after a gateway 401 on a token we thought was fresh) and returns
    /// the new access token, or null if the refresh failed / logged out. Single-flighted with
    /// <see cref="GetAccessTokenAsync"/>; a token already renewed by a concurrent caller is returned
    /// as-is instead of burning another refresh.
    /// </summary>
    Task<string?> RefreshAccessTokenAsync(CancellationToken ct = default);

    /// <summary>
    /// Restores a previous session at startup from the persisted refresh token (one refresh
    /// round-trip). Returns true when signed in afterwards. Never throws — a revoked/expired token
    /// just leaves the user logged out.
    /// </summary>
    Task<bool> TryRestoreAsync(CancellationToken ct = default);
}

/// <summary>Auth failure with a message safe to show the user verbatim.</summary>
public sealed class AccountAuthException : Exception
{
    public AccountAuthException(string message) : base(message) { }
    public AccountAuthException(string message, Exception inner) : base(message, inner) { }
}
