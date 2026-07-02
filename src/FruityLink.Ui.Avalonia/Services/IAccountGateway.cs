using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace FruityLink.Ui.Avalonia.Services;

/// <summary>
/// A backend-agnostic snapshot of the FL Automate ACCOUNT state + connection settings. Deliberately
/// a plain POCO of primitives so the Avalonia UI lib stays free of any reference to
/// <c>FruityLink.Core</c> / <c>FruityLink.Llm</c> — the FL Agent plugin maps this to/from the real
/// <c>AppSettings.Account</c> + auth service when it implements <see cref="IAccountGateway"/>.
///
/// <para>No secret ever crosses this seam: the password only flows INTO
/// <see cref="IAccountGateway.LoginAsync"/>, and tokens live in the encrypted secret store.</para>
/// </summary>
public sealed class AccountSnapshot
{
    /// <summary>True when a session is live (signed in on this machine).</summary>
    public bool IsLoggedIn { get; set; }

    /// <summary>Signed-in email (display only; empty when logged out).</summary>
    public string Email { get; set; } = string.Empty;

    /// <summary>Signed-in plan ('free' | 'beta' | 'pro' | 'studio'; display only).</summary>
    public string Plan { get; set; } = string.Empty;

    /// <summary>Model id sent to the gateway; "default" lets the gateway pick the plan's model.</summary>
    public string Model { get; set; } = "default";

    /// <summary>Marketing/auth API base URL (advanced; normally the default).</summary>
    public string ApiBaseUrl { get; set; } = "https://fl-automate.com/api";

    /// <summary>AI gateway base URL (advanced; normally the default).</summary>
    public string GatewayBaseUrl { get; set; } = "https://ai.fl-automate.com";
}

/// <summary>
/// The seam the Avalonia Settings ACCOUNT card depends on to sign in/out of FL Automate and edit
/// the connection (model + advanced URLs) WITHOUT the UI lib referencing the auth/persistence
/// layers. The FL Agent plugin implements this against the real auth service + settings store and
/// re-configures the live agent after each change; the standalone dev head uses
/// <see cref="InMemoryAccountGateway"/> so <c>dotnet run</c> still works with no backend.
/// All failures throw with user-readable messages the card surfaces verbatim.
/// </summary>
public interface IAccountGateway
{
    /// <summary>Loads the current account state (never null).</summary>
    AccountSnapshot Load();

    /// <summary>Signs in with email/password; persists the session and live-applies it to the
    /// agent. Throws with a friendly message on bad credentials / unverified email / network.</summary>
    Task<AccountSnapshot> LoginAsync(string email, string password, CancellationToken ct = default);

    /// <summary>Signs out (revokes + clears the stored session) and live-applies. Never throws.</summary>
    Task<AccountSnapshot> LogoutAsync(CancellationToken ct = default);

    /// <summary>Persists the model + advanced URLs and re-configures the live agent so the change
    /// takes effect without an FL restart. May throw if the re-configure fails (the on-disk save
    /// has already happened by then).</summary>
    Task SaveAsync(string model, string apiBaseUrl, string gatewayBaseUrl, CancellationToken ct = default);

    /// <summary>Probes the gateway (<c>GET /v1/models</c> with the session token) and returns the
    /// model ids available to the account's plan. Throws with a friendly message on failure.</summary>
    Task<IReadOnlyList<string>> TestConnectionAsync(CancellationToken ct = default);
}

/// <summary>
/// No-backend gateway for the standalone Avalonia dev head (<c>dotnet run</c>): keeps a pretend
/// session in memory for the process lifetime, never touches disk or the network. Lets the Settings
/// UI stay fully interactive with no agent wired.
/// </summary>
public sealed class InMemoryAccountGateway : IAccountGateway
{
    private readonly AccountSnapshot _current = new();

    public AccountSnapshot Load() => Copy(_current);

    public Task<AccountSnapshot> LoginAsync(string email, string password, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrEmpty(password))
            throw new InvalidOperationException("Enter an email and password.");
        _current.IsLoggedIn = true;
        _current.Email = email.Trim();
        _current.Plan = "standalone";
        return Task.FromResult(Copy(_current));
    }

    public Task<AccountSnapshot> LogoutAsync(CancellationToken ct = default)
    {
        _current.IsLoggedIn = false;
        _current.Email = string.Empty;
        _current.Plan = string.Empty;
        return Task.FromResult(Copy(_current));
    }

    public Task SaveAsync(string model, string apiBaseUrl, string gatewayBaseUrl, CancellationToken ct = default)
    {
        _current.Model = model;
        _current.ApiBaseUrl = apiBaseUrl;
        _current.GatewayBaseUrl = gatewayBaseUrl;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>> TestConnectionAsync(CancellationToken ct = default)
        => throw new InvalidOperationException("No gateway in standalone mode — run inside FL Studio.");

    private static AccountSnapshot Copy(AccountSnapshot s) => new()
    {
        IsLoggedIn = s.IsLoggedIn,
        Email = s.Email,
        Plan = s.Plan,
        Model = s.Model,
        ApiBaseUrl = s.ApiBaseUrl,
        GatewayBaseUrl = s.GatewayBaseUrl,
    };
}
