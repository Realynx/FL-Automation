using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace FruityLink.Ui.Avalonia.Services;

/// <summary>
/// A backend-agnostic snapshot of the FL Automate ACCOUNT state. Deliberately a plain POCO of
/// primitives so the Avalonia UI lib stays free of any reference to <c>FruityLink.Core</c> /
/// <c>FruityLink.Llm</c> — the FL Agent plugin maps this to/from the real
/// <c>AppSettings.Account</c> + auth service when it implements <see cref="IAccountGateway"/>.
/// The API/gateway base URLs deliberately do NOT cross this seam: they live in settings.json as
/// dev-only hand-editable overrides and the UI neither shows nor changes them.
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
}

/// <summary>
/// One model the signed-in account may pick: the pseudonymous <see cref="Id"/> chat requests send
/// as <c>model</c>, plus the friendly <see cref="DisplayName"/> the picker shows. Kept as a UI-lib
/// primitive so the card never references the Llm layer's own model type.
/// </summary>
public sealed class AccountModel
{
    public AccountModel(string id, string displayName)
    {
        Id = id ?? throw new ArgumentNullException(nameof(id));
        DisplayName = string.IsNullOrWhiteSpace(displayName) ? id : displayName;
    }

    /// <summary>The pseudonymous model id sent to the gateway.</summary>
    public string Id { get; }

    /// <summary>The friendly label shown in the picker (falls back to <see cref="Id"/>).</summary>
    public string DisplayName { get; }
}

/// <summary>
/// The seam the Avalonia Settings ACCOUNT card depends on to sign in/out of FL Automate and pick
/// the model WITHOUT the UI lib referencing the auth/persistence layers. The FL Agent plugin
/// implements this against the real auth service + settings store and re-configures the live agent
/// after each change; the standalone dev head uses <see cref="InMemoryAccountGateway"/> so
/// <c>dotnet run</c> still works with no backend.
/// All failures throw with user-readable messages the card surfaces verbatim.
/// </summary>
public interface IAccountGateway
{
    /// <summary>Loads the current account state (never null).</summary>
    AccountSnapshot Load();

    /// <summary>Absolute URL of the marketing site's account-management page (subscription
    /// upgrade/downgrade, billing). The card opens it in the system browser; signed-out visitors
    /// land on the site's login/register flow first.</summary>
    string AccountPageUrl { get; }

    /// <summary>Signs in with email/password; persists the session and live-applies it to the
    /// agent. Throws with a friendly message on bad credentials / unverified email / network.</summary>
    Task<AccountSnapshot> LoginAsync(string email, string password, CancellationToken ct = default);

    /// <summary>Signs out (revokes + clears the stored session) and live-applies. Never throws.</summary>
    Task<AccountSnapshot> LogoutAsync(CancellationToken ct = default);

    /// <summary>Persists the chosen model id and re-configures the live agent so the change takes
    /// effect without an FL restart. May throw if the re-configure fails (the on-disk save has
    /// already happened by then).</summary>
    Task SaveModelAsync(string model, CancellationToken ct = default);

    /// <summary>Fetches the models available to the account's plan (<c>GET /v1/models</c> with the
    /// session token; default model first, as served by the gateway). An empty list is valid (plan
    /// not configured yet). Throws with a friendly message on failure.</summary>
    Task<IReadOnlyList<AccountModel>> ListModelsAsync(CancellationToken ct = default);

    /// <summary>Probes the gateway (<c>GET /v1/models</c> with the session token) and returns the
    /// display names of the models available to the account's plan. Throws with a friendly message
    /// on failure.</summary>
    Task<IReadOnlyList<string>> TestConnectionAsync(CancellationToken ct = default);
}

/// <summary>
/// No-backend gateway for the standalone Avalonia dev head (<c>dotnet run</c>): keeps a pretend
/// session in memory for the process lifetime, never touches disk or the network, and serves a
/// small fake model list so the picker stays fully interactive with no agent wired.
/// </summary>
public sealed class InMemoryAccountGateway : IAccountGateway
{
    private static readonly AccountModel[] FakeModels =
    {
        new("fl-swift", "FL Swift (fast)"),
        new("fl-deep", "FL Deep (thorough)"),
    };

    private readonly AccountSnapshot _current = new();

    public string AccountPageUrl => "https://fl-automate.com/account";

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

    public Task SaveModelAsync(string model, CancellationToken ct = default)
    {
        _current.Model = model;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<AccountModel>> ListModelsAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<AccountModel>>(FakeModels);

    public Task<IReadOnlyList<string>> TestConnectionAsync(CancellationToken ct = default)
        => throw new InvalidOperationException("No gateway in standalone mode — run inside FL Studio.");

    private static AccountSnapshot Copy(AccountSnapshot s) => new()
    {
        IsLoggedIn = s.IsLoggedIn,
        Email = s.Email,
        Plan = s.Plan,
        Model = s.Model,
    };
}
