using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using FruityLink.Agent;
using FruityLink.Agent.Plugins;
using FruityLink.Agent.Versioning;
using FruityLink.Core.Abstractions;
using FruityLink.Core.Configuration;
using FruityLink.Core.Domain;
using FruityLink.Knowledge;
using FruityLink.Llm;
using FruityLink.Llm.Auth;
using FruityLink.Llm.Diagnostics;
using FruityLink.Persistence;
using FruityLink.Plugins.Abstractions;
using FruityLink.Speech;
using FruityLink.Ui.Avalonia.Services;
using CoreAppSettings = FruityLink.Core.Configuration.AppSettings;

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

    // ── Shared inverse-operation journal + registry (granular undo/redo) ─────────────────────────
    // One journal (the per-turn recorder) + one populated registry (op-id → read/apply), shared by the
    // NativeControlPlugin capture seam, the version-control store (undo/redo replay), and the coordinator
    // (turn-boundary drain). They carry no per-project state, so a single process-wide instance is correct.
    private static readonly ChangeJournal SharedJournal = new();
    private static readonly IInverseOpRegistry SharedRegistry = InverseOps.CreateRegistry();

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

    // ── Opt-in remote debug traces ────────────────────────────────────────────────────────────
    // ONE uploader on the shared auth/settings; the privacy gate (AccountSettings.ShareDebugData,
    // default OFF) lives inside the client and is re-read per turn, so the Settings toggle applies
    // immediately without recomposition.
    private static readonly Lazy<DebugTraceClient> SharedDebugTraces =
        new(() => new DebugTraceClient(SharedAuthHttp, SharedAuth, SharedSettings));

    /// <summary>
    /// Returns a ready <see cref="FruityLink.Agent.FlAgent"/>: the host's shared one when available,
    /// otherwise a freshly composed minimal one bound to <paramref name="context"/>.Fl.
    /// </summary>
    public static FruityLink.Agent.FlAgent ResolveOrBuild(IPluginContext context)
    {
        // Bring the persisted session back to life (background; the first turn awaits nothing —
        // the gateway auth handler resolves the token per-request once the restore lands).
        _ = SessionRestore.Value;

        // Offer every finished turn's transcript record to the opt-in debug uploader — for BOTH the
        // host-provided and self-composed agent (the transcript scope is ambient either way). The
        // uploader itself drops everything unless "Share debug data" is enabled, so wiring the sink
        // unconditionally leaks nothing. Fire-and-forget: a slow/failed upload never blocks a turn.
        SessionTranscript.UploadSink ??= json => _ = SharedDebugTraces.Value.TrySendTurnAsync(json);

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
        // The fallback hook persists the healed model choice so a plan change (which strands the
        // saved model and 400s every turn) self-repairs instead of bricking the chat until the
        // user re-picks a model by hand.
        var kernelFactory = new ChatKernelFactory(diagnostics, SharedAuth, HealStaleModelChoice);
        var audit = new OperationAuditSink();
        var toolFilter = new ToolCallFilter();

        var music = new MusicTheoryPlugin(audit, settingsStore);
        // Wire the capture seam so each Phase-1 mutating tool journals its BEFORE-value for granular undo.
        var nativeControl = new NativeControlPlugin(
            context.Fl, new ChangeCapture(context.Fl, SharedJournal, SharedRegistry));   // <-- shared host FL bridge
        var knowledge = new KnowledgePlugin(ResolveRetriever(context));
        var subAgents = new SubAgentService(
            kernelFactory, settingsStore, music, nativeControl, knowledge, toolFilter);
        var orchestration = new OrchestrationPlugin(subAgents, settingsStore, nativeControl);
        // Versioning tools ship empty here; BuildVersionControl's store is attached later via
        // FlAgent.AttachVersionControl (the store needs the live bridge and is composed after us).
        var versioning = new VersioningPlugin();
        var pluginSet = new FlPluginSet(music, nativeControl, knowledge, orchestration, versioning);

        return new FruityLink.Agent.FlAgent(kernelFactory, settingsStore, pluginSet, toolFilter);
    }

    /// <summary>Client for the gateway's <c>/v1/bug-reports</c> endpoint (the chat's "Report bug"
    /// button on failed turns). Rides the same shared auth session + settings as everything else.</summary>
    public static BugReportClient BuildBugReportClient()
        => new(SharedAuthHttp, SharedAuth, SharedSettings);

    /// <summary>Client for the gateway's public <c>/v1/client-version</c> endpoint (the once-per-run
    /// update check behind the chat's "update available" notice). No auth — the endpoint is public —
    /// but it reuses the shared HttpClient + settings (gateway base URL).</summary>
    public static UpdateCheckClient BuildUpdateCheckClient()
        => new(SharedAuthHttp, SharedSettings);

    /// <summary>
    /// Self-heal for a stale saved model (see <see cref="LlmModelFallbackHandler"/>): persist
    /// <c>"default"</c> so future kernel builds stop paying the 400-then-fallback round trip and
    /// the Settings card shows the real state. Only rewrites the setting while it still holds the
    /// rejected model (never clobber a pick the user made in the meantime). Fire-and-forget — the
    /// heal must never block or fail the in-flight turn, which the wire fallback already saved.
    /// </summary>
    private static void HealStaleModelChoice(LlmModelFallback fallback) =>
        _ = Task.Run(async () =>
        {
            try
            {
                CoreAppSettings app = await SharedSettings.LoadAsync().ConfigureAwait(false);
                AccountSettings account = app.AccountOrDefault;
                if (!string.Equals(account.Model, fallback.StaleModel, StringComparison.Ordinal))
                    return;
                await SharedSettings
                    .SaveAsync(app with { Account = account with { Model = AccountSettings.DefaultModel } })
                    .ConfigureAwait(false);
            }
            catch { /* best-effort; the wire fallback keeps turns working regardless */ }
        });

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
    // FRESH per-RUNTIME session id: undo history covers ONLY the current FL run. A shared "default" session
    // let the user undo a change from a PRIOR run against a now-different project state → crash. On close
    // those changes are gone (or already baked into the saved .flp, so reverting them is meaningless).
    // OpenSessionAsync purges every other session's data so stale runs can never be undone.
    private static readonly string VersionSessionId = "run-" + System.Guid.NewGuid().ToString("N");

    /// <summary>
    /// Builds the backend project version-control store bound to the host's FL bridge, opens its session
    /// (loads history + runs crash-recovery detection off-thread), and returns it — or null if it can't be
    /// constructed (the UI then keeps its in-memory stub, staying inert but alive).
    /// </summary>
    public static FruityLink.Core.Abstractions.IProjectVersionControl? BuildVersionControl(IPluginContext context)
    {
        try
        {
            var vc = new JsonProjectVersionControl(SharedPaths, context.Fl, SharedRegistry);
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
        => new(vc, agent, audit: null, journal: SharedJournal, granularTools: InverseOps.GranularToolNames);

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

    /// <summary>
    /// Resolves the RAG retriever for <c>search_manual</c>/<c>search_knowledge</c>:
    /// the host's own if it exposes one; else — when the shipped FL Studio manual vector DB is
    /// present — a retriever backed by it that embeds queries THROUGH THE GATEWAY (metered,
    /// per-tier model), matching how the corpus was embedded; else a no-op.
    /// </summary>
    private static IKnowledgeRetriever ResolveRetriever(IPluginContext context)
    {
        if (context.Services?.GetService(typeof(IKnowledgeRetriever)) is IKnowledgeRetriever hosted)
            return hosted;

        string? dbPath = LocateManualDb();
        if (dbPath is null)
        {
            context.Log("[fl-agent] FL Studio manual DB not found — manual search returns nothing until it is installed.");
            return new NullKnowledgeRetriever();
        }

        context.Log($"[fl-agent] FL Studio manual RAG enabled ({dbPath}).");
        return new GatewayManualRetriever(dbPath, SharedSettings, SharedAuth, context);
    }

    /// <summary>Finds the shipped manual DB: %APPDATA%\FLAutomate first, then next to this plugin.</summary>
    private static string? LocateManualDb()
    {
        string appData = SharedPaths.KnowledgeDbFile;
        string asmDir = Path.GetDirectoryName(typeof(AgentComposition).Assembly.Location) ?? AppContext.BaseDirectory;
        foreach (string candidate in new[]
                 {
                     appData,
                     Path.Combine(asmDir, "knowledge", "fl-manual.db"),
                     Path.Combine(asmDir, "fl-manual.db"),
                 })
        {
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    /// <summary>Empty knowledge base: keeps the agent fully functional minus RAG when none is wired.</summary>
    private sealed class NullKnowledgeRetriever : IKnowledgeRetriever
    {
        public Task<IReadOnlyList<KnowledgeHit>> SearchAsync(string query, int topK = 5, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<KnowledgeHit>>(Array.Empty<KnowledgeHit>());
    }

    /// <summary>
    /// RAG retriever over the shipped FL Studio manual vector DB. The underlying
    /// <see cref="KnowledgeService"/> is built LAZILY on the first search (off the plugin-enable
    /// path), reading the gateway base URL from settings and embedding the query through the
    /// gateway's <c>/v1/embeddings</c> with the account JWT — so the query uses the SAME model the
    /// corpus was embedded with, and the call is metered/quota-checked like chat. If the user isn't
    /// signed in (or the gateway is unreachable) the search fails gracefully via the tool's ERR path.
    /// </summary>
    private sealed class GatewayManualRetriever : IKnowledgeRetriever
    {
        private readonly string _dbPath;
        private readonly JsonSettingsStore _settings;
        private readonly IAccountAuth _auth;
        private readonly IPluginContext _context;
        private readonly SemaphoreSlim _gate = new(1, 1);
        private volatile KnowledgeService? _service;

        public GatewayManualRetriever(
            string dbPath, JsonSettingsStore settings, IAccountAuth auth, IPluginContext context)
        {
            _dbPath = dbPath;
            _settings = settings;
            _auth = auth;
            _context = context;
        }

        public async Task<IReadOnlyList<KnowledgeHit>> SearchAsync(
            string query, int topK = 5, CancellationToken ct = default)
        {
            KnowledgeService service = await EnsureServiceAsync(ct).ConfigureAwait(false);
            return await service.SearchAsync(query, topK, ct).ConfigureAwait(false);
        }

        private async Task<KnowledgeService> EnsureServiceAsync(CancellationToken ct)
        {
            if (_service is not null) return _service;
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (_service is null)
                {
                    FruityLink.Core.Configuration.AppSettings app =
                        await _settings.LoadAsync(ct).ConfigureAwait(false);
                    // Gateway OpenAI-compatible base ("{gateway}/v1"); the embeddings client posts to
                    // "{base}/embeddings". The gateway ignores the requested model and uses the plan's
                    // fixed embedding model, so "default"/dimensions here are placeholders.
                    string endpoint = app.AccountOrDefault.GatewayOpenAiBase;

                    // JWT is stamped per-request by GatewayAuthHandler (no static api key on the client).
                    var authed = new GatewayAuthHandler(
                        _auth, new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All });
                    var http = new HttpClient(authed) { Timeout = TimeSpan.FromSeconds(60) };

                    var embeddings = new OpenAiCompatibleEmbeddingClient(
                        http, new EmbeddingSettings(Endpoint: endpoint, Model: "default", Dimensions: 3072));
                    _service = new KnowledgeService(embeddings, _dbPath, http);
                    _context.Log("[fl-agent] manual RAG retriever ready (gateway embeddings).");
                }
                return _service;
            }
            finally
            {
                _gate.Release();
            }
        }
    }
}
