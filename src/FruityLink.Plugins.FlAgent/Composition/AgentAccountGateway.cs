using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FruityLink.Core.Abstractions;
using FruityLink.Core.Configuration;
using FruityLink.Llm.Auth;
using FruityLink.Ui.Avalonia.Services;
using CoreAppSettings = FruityLink.Core.Configuration.AppSettings;

namespace FruityLink.Plugins.FlAgent.Composition;

/// <summary>
/// Bridges the backend-agnostic Avalonia Settings ACCOUNT card (<see cref="IAccountGateway"/>) to
/// the AGENT's real world: sign in/out through <see cref="IAccountAuth"/> (marketing API + rotating
/// refresh token in the encrypted secret store), the model choice through the SAME
/// <see cref="ISettingsStore"/> (→ <c>settings.json</c>) the agent loads from, and the model list /
/// connection test through the gateway's <c>/v1/models</c>. The API/gateway base URLs never cross
/// this seam — they stay in settings.json as dev-only hand-editable overrides the UI can't touch.
/// After every state change it live-applies: the
/// in-FL chat tab's kernel is invalidated and <see cref="FruityLink.Agent.FlAgent.ConfigureAsync"/>
/// rebuilds the main agent — no FL restart needed.
///
/// <para>The UI never sees a token, the password past the login call, or any FruityLink.Core type —
/// only the flat <see cref="AccountSnapshot"/> DTO crosses the seam.</para>
/// </summary>
internal sealed class AgentAccountGateway : IAccountGateway
{
    private readonly ISettingsStore _settings;
    private readonly IAccountAuth _auth;
    private readonly GatewayConnectivity _connectivity;
    private readonly FruityLink.Agent.FlAgent _agent;
    private readonly FruityLink.Agent.ChatBridgeService? _chatBridge;

    public AgentAccountGateway(
        ISettingsStore settings,
        IAccountAuth auth,
        GatewayConnectivity connectivity,
        FruityLink.Agent.FlAgent agent,
        FruityLink.Agent.ChatBridgeService? chatBridge = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _auth = auth ?? throw new ArgumentNullException(nameof(auth));
        _connectivity = connectivity ?? throw new ArgumentNullException(nameof(connectivity));
        _agent = agent ?? throw new ArgumentNullException(nameof(agent));
        // Optional: hosts that run the in-FL chat tab pass its service so account changes reach
        // that agent too; hosts without the tab (or older call sites) omit it.
        _chatBridge = chatBridge;
    }

    /// <summary>Derived from the settings' API base so a dev/staging override in settings.json
    /// points the link at the matching site.</summary>
    public string AccountPageUrl
        => _settings.LoadAsync().GetAwaiter().GetResult().AccountOrDefault.AccountPageUrl;

    public AccountSnapshot Load()
    {
        // Small local JSON read; the store uses ConfigureAwait(false) throughout so this can't
        // deadlock the UI thread. Kept synchronous to prefill the fields the instant the panel opens.
        CoreAppSettings app = _settings.LoadAsync().GetAwaiter().GetResult();
        AccountSettings account = app.AccountOrDefault;

        // Live session wins for identity (fresher plan); the persisted display identity covers the
        // window between startup and the background session restore completing.
        AccountSession? session = _auth.Session;
        return new AccountSnapshot
        {
            IsLoggedIn = _auth.IsLoggedIn || account.Email is not null,
            Email = session?.Email ?? account.Email ?? string.Empty,
            Plan = session?.Plan ?? account.Plan ?? string.Empty,
            Model = account.Model,
            ShareDebugData = account.ShareDebugData,
        };
    }

    public async Task<AccountSnapshot> LoginAsync(string email, string password, CancellationToken ct = default)
    {
        // Throws AccountAuthException with a user-friendly message on failure; the card shows it verbatim.
        await _auth.LoginAsync(email, password, ct).ConfigureAwait(false);
        await ReconfigureAgentsAsync(ct).ConfigureAwait(false);
        return Load();
    }

    public async Task<AccountSnapshot> LogoutAsync(CancellationToken ct = default)
    {
        await _auth.LogoutAsync(ct).ConfigureAwait(false);
        try { await ReconfigureAgentsAsync(ct).ConfigureAwait(false); }
        catch { /* logged out regardless; the next turn reports the signed-out state */ }
        return Load();
    }

    public async Task SaveModelAsync(string model, CancellationToken ct = default)
    {
        CoreAppSettings app = await _settings.LoadAsync(ct).ConfigureAwait(false);

        // Build from the LOADED record via `with`, not the positional constructor: AccountSettings
        // carries non-UI-surfaced init properties (the base URLs — dev-only settings.json
        // overrides — plus AllowParallelToolCalls and the display identity) that a positional
        // rebuild would silently reset on every save from the settings card.
        AccountSettings account = app.AccountOrDefault with
        {
            Model = string.IsNullOrWhiteSpace(model) ? AccountSettings.DefaultModel : model.Trim(),
        };

        // Persist FIRST (this is the durable part); only the model changes — theme/embeddings/etc.
        // on the existing AppSettings are preserved via the record `with`.
        await _settings.SaveAsync(app with { Account = account }, ct).ConfigureAwait(false);

        // Then live-apply. If this throws, the save has already landed on disk — the caller
        // surfaces it as "saved, but couldn't reconfigure".
        await ReconfigureAgentsAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Persists the debug-data opt-in. No agent re-configure: the uploader re-reads the
    /// flag from settings on every turn, so the change is live the moment the save lands.</summary>
    public async Task SaveShareDebugDataAsync(bool enabled, CancellationToken ct = default)
    {
        CoreAppSettings app = await _settings.LoadAsync(ct).ConfigureAwait(false);
        AccountSettings account = app.AccountOrDefault with { ShareDebugData = enabled };
        await _settings.SaveAsync(app with { Account = account }, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<AccountModel>> ListModelsAsync(CancellationToken ct = default)
    {
        CoreAppSettings app = await _settings.LoadAsync(ct).ConfigureAwait(false);
        IReadOnlyList<GatewayModel> models = await _connectivity
            .ListModelsAsync(app.AccountOrDefault.GatewayBaseUrl, ct).ConfigureAwait(false);
        return models.Select(m => new AccountModel(m.Id, m.DisplayName)).ToList();
    }

    public async Task<IReadOnlyList<string>> TestConnectionAsync(CancellationToken ct = default)
    {
        CoreAppSettings app = await _settings.LoadAsync(ct).ConfigureAwait(false);
        IReadOnlyList<GatewayModel> models = await _connectivity
            .ListModelsAsync(app.AccountOrDefault.GatewayBaseUrl, ct).ConfigureAwait(false);
        return models.Select(m => m.DisplayName).ToList();
    }

    /// <summary>
    /// Rebuild the agents' kernels from the just-changed state. The in-FL chat tab's agent is
    /// invalidated FIRST (it lazily rebuilds on its next turn), so even if the main agent's
    /// re-configure throws, the tab still picks up the change.
    /// </summary>
    private async Task ReconfigureAgentsAsync(CancellationToken ct)
    {
        _chatBridge?.InvalidateKernel();
        await _agent.ConfigureAsync(ct).ConfigureAwait(false);
    }
}
