using System.IO;
using System.Net.Http;
using FruityLink.Agent;
using FruityLink.Agent.Plugins;
using FruityLink.Core.Abstractions;
using FruityLink.Core.Domain;
using FruityLink.Llm;
using FruityLink.Llm.Auth;
using FruityLink.Llm.Diagnostics;
using FruityLink.Persistence;
using FruityLink.Plugins.Abstractions;
using FruityLink.Speech;
using FruityLink.Ui.Avalonia.Services;

namespace FruityLink.Plugins.FlAgent.Composition;

/// <summary>
/// Brings the existing FruityLink LLM agent to life for the plugin. Prefers the fully-wired
/// <see cref="FruityLink.Agent.FlAgent"/> the host already built (resolved from
/// <see cref="IPluginContext.Services"/>); if the host doesn't expose one, it builds a SMALL
/// self-contained graph out of the SAME reused types — mirroring the headless agent
/// wiring — but pointed at the host's single FL bridge
/// (<see cref="IPluginContext.Fl"/>) so the plugin never opens a second bridge.
/// No LLM logic is duplicated here; this only wires existing components together.
/// </summary>
internal static class AgentComposition
{
    // ── Shared persistence (single source of truth) ──────────────────────────────────────────
    // One storage layout + one settings store + one secret store, created once and reused by every
    // call site below instead of being re-newed in each. StoragePaths defaults to the SAME on-disk
    // location as before (%APPDATA%\FLAutomate → settings.json / secrets.json), so these instances
    // address the exact same files the agent loads from — self-composed or host-provided. Because
    // StoragePaths is deterministic this is behaviourally identical to the previous per-call
    // construction; sharing single instances additionally lets the secret store's write gate cover
    // the self-composed agent and the settings gateway together. Field order = construction order.
    private static readonly StoragePaths SharedPaths = new();
    private static readonly JsonSettingsStore SharedSettings = new(SharedPaths);
    private static readonly DpapiSecretStore SharedSecrets = new(SharedPaths);

    // ── Shared FL Automate account auth (single session for everything) ──────────────────────
    // ONE auth service over the shared stores: the kernel factory's gateway handler, the settings
    // ACCOUNT card, and the connectivity test all read the same cached access token, so a refresh
    // rotation is single-flighted process-wide (two services racing a rotation would revoke the
    // token the other just stored). Plain HttpClient — auth/model-list calls are tiny and get no
    // LLM diagnostics chain.
    private static readonly HttpClient SharedAuthHttp = new() { Timeout = TimeSpan.FromSeconds(30) };
    private static readonly AccountAuthService SharedAuth = new(SharedAuthHttp, SharedSettings, SharedSecrets);

    /// <summary>Kicks off the one-time background session restore (refresh-token → live session) so
    /// a previously signed-in user is live without touching Settings. Idempotent + non-blocking.</summary>
    private static readonly Lazy<Task<bool>> SessionRestore =
        new(() => Task.Run(() => SharedAuth.TryRestoreAsync()));

    /// <summary>
    /// Returns a ready <see cref="FruityLink.Agent.FlAgent"/>: the host's shared one when available,
    /// otherwise a freshly composed minimal one bound to <paramref name="context"/>.Fl.
    /// </summary>
    public static FruityLink.Agent.FlAgent ResolveOrBuild(IPluginContext context)
    {
        // Bring the persisted session back to life (background; the first turn awaits nothing —
        // the gateway auth handler resolves the token per-request once the restore lands).
        _ = SessionRestore.Value;

        // 1) Prefer the host's already-configured singleton (shares its FL bridge + conversation).
        if (context.Services?.GetService(typeof(FruityLink.Agent.FlAgent)) is FruityLink.Agent.FlAgent hosted)
        {
            context.Log("[fl-agent] using host-provided FlAgent.");
            return hosted;
        }

        // 2) Fallback: compose the agent from the existing types, using the host's FL control.
        context.Log("[fl-agent] composing a self-contained FlAgent (host had none).");

        var settingsStore = SharedSettings;
        var diagnostics = new LlmDiagnostics(Path.Combine(SharedPaths.BaseDirectory, "logs"));
        var kernelFactory = new ChatKernelFactory(diagnostics, SharedAuth);
        var audit = new OperationAuditSink();
        var toolFilter = new ToolCallFilter();

        var music = new MusicTheoryPlugin(audit, settingsStore);
        var nativeControl = new NativeControlPlugin(context.Fl);            // <-- shared host FL bridge
        var knowledge = new KnowledgePlugin(ResolveRetriever(context));
        var subAgents = new SubAgentService(
            kernelFactory, settingsStore, music, nativeControl, knowledge, toolFilter);
        var orchestration = new OrchestrationPlugin(subAgents, settingsStore);
        // Versioning tools ship empty here; BuildVersionControl's store is attached later via
        // FlAgent.AttachVersionControl (the store needs the live bridge and is composed after us).
        var versioning = new VersioningPlugin();
        var pluginSet = new FlPluginSet(music, nativeControl, knowledge, orchestration, versioning);

        return new FruityLink.Agent.FlAgent(kernelFactory, settingsStore, pluginSet, toolFilter);
    }

    /// <summary>
    /// Builds the gateway the Avalonia Settings ACCOUNT card uses to sign in/out and edit the
    /// agent's connection. It targets the SAME auth session + on-disk stores the agent runs on
    /// (<c>%APPDATA%\FLAutomate</c> via <see cref="StoragePaths"/>): connection → <c>settings.json</c>,
    /// refresh token → encrypted <c>secrets.json</c>. On login/logout/save it re-configures
    /// <paramref name="agent"/> live. Because <see cref="StoragePaths"/> is deterministic, these
    /// store instances address the exact same files the agent uses whether it's the host's shared
    /// agent or a self-composed one.
    /// </summary>
    public static IAccountGateway BuildAccountGateway(FruityLink.Agent.FlAgent agent)
    {
        var connectivity = new GatewayConnectivity(SharedAuthHttp, SharedAuth);
        return new AgentAccountGateway(SharedSettings, SharedAuth, connectivity, agent);
    }

    // ── Project-state version control + backup ────────────────────────────────────────────────────
    // Constructed HERE (not resolved from the host) so it's live in-FL: the .flp backups + index.json
    // live under the SAME %APPDATA%\FLAutomate data dir (SharedPaths) as the rest, and it drives FL
    // through the host's single safe bridge (context.Fl). Keyed on a stable session id so history +
    // crash-recovery survive restarts (follow-up: key on the real chat/project session).
    private const string VersionSessionId = "default";

    /// <summary>
    /// Builds the backend project version-control store bound to the host's FL bridge, opens its session
    /// (loads history + runs crash-recovery detection off-thread), and returns it — or null if it can't be
    /// constructed (the UI then keeps its in-memory stub, staying inert but alive).
    /// </summary>
    public static FruityLink.Core.Abstractions.IProjectVersionControl? BuildVersionControl(IPluginContext context)
    {
        try
        {
            var vc = new JsonProjectVersionControl(SharedPaths, context.Fl);
            _ = vc.OpenSessionAsync(VersionSessionId);   // fire-and-forget: load index + detect recovery
            return vc;
        }
        catch (Exception ex)
        {
            context.Log("[fl-agent] project version control unavailable: " + ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Builds the coordinator that commits one project version per mutating AI work-unit. The presenter
    /// calls <see cref="FruityLink.Agent.ProjectVersionCoordinator.OnTurnCompletedAsync"/> at each turn
    /// boundary; the coordinator hooks <paramref name="agent"/>'s tool-invocation stream to gate commits.
    /// </summary>
    public static FruityLink.Agent.ProjectVersionCoordinator BuildVersionCoordinator(
        FruityLink.Agent.FlAgent agent, FruityLink.Core.Abstractions.IProjectVersionControl vc)
        => new(vc, agent);

    /// <summary>Adapts the backend <see cref="FruityLink.Core.Abstractions.IProjectVersionControl"/> onto the
    /// UI-local gateway the Avalonia history panel binds to (mirrors <see cref="BuildAccountGateway"/>).</summary>
    public static FruityLink.Ui.Avalonia.Services.IProjectVersionControl BuildVersionControlGateway(
        FruityLink.Core.Abstractions.IProjectVersionControl vc)
        => new AgentVersionControlGateway(vc);

    /// <summary>
    /// Returns the local Whisper speech-to-text service plus whether WE own it (and so must dispose
    /// it). Prefers the host's shared singleton (from <see cref="IPluginContext.Services"/>, owned by
    /// the host); otherwise builds one rooted at <c>%APPDATA%\FruityLink</c> — the SAME app-data dir
    /// and constructor the app uses — and reports it as owned. Construction is cheap and touches no
    /// audio device, so a missing mic/model only surfaces later when the user taps the mic.
    /// </summary>
    public static (IDictationService? Service, bool Owned) ResolveOrBuildDictation(IPluginContext context)
    {
        if (context.Services?.GetService(typeof(IDictationService)) is IDictationService hosted)
        {
            context.Log("[fl-agent] using host-provided dictation service.");
            return (hosted, false);
        }

        try
        {
            string dataDir = SharedPaths.BaseDirectory;   // %APPDATA%\FruityLink, as the app uses
            context.Log("[fl-agent] building a local Whisper dictation service.");
            return (new WhisperDictationService(dataDir), true);
        }
        catch (Exception ex)
        {
            // Speech is a nice-to-have; never block the chat window on it. The mic just won't show.
            context.Log("[fl-agent] dictation unavailable: " + ex.Message);
            return (null, false);
        }
    }

    /// <summary>Reuse the host's RAG retriever if present; otherwise a no-op (search returns nothing).</summary>
    private static IKnowledgeRetriever ResolveRetriever(IPluginContext context)
        => context.Services?.GetService(typeof(IKnowledgeRetriever)) as IKnowledgeRetriever
           ?? new NullKnowledgeRetriever();

    /// <summary>Empty knowledge base: keeps the agent fully functional minus RAG when none is wired.</summary>
    private sealed class NullKnowledgeRetriever : IKnowledgeRetriever
    {
        public Task<IReadOnlyList<KnowledgeHit>> SearchAsync(string query, int topK = 5, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<KnowledgeHit>>(Array.Empty<KnowledgeHit>());
    }
}
