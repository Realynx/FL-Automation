using System.IO;
using FruityLink.Agent.Plugins;
using FruityLink.Core.Abstractions;
using FruityLink.Core.Configuration;
using FruityLink.Llm;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace FruityLink.Agent;

/// <summary>A kernel prepared for agent turns, plus the pieces a turn needs alongside it.</summary>
/// <param name="Kernel">The kernel with tool plugins + filters registered.</param>
/// <param name="Chat">The kernel's chat completion service.</param>
/// <param name="Settings">The account settings the kernel was built from — carried so the turn
/// runner can read per-connection knobs (e.g. <see cref="AccountSettings.AllowParallelToolCalls"/>)
/// without re-loading the settings store.</param>
internal sealed record AgentKernel(Kernel Kernel, IChatCompletionService Chat, AccountSettings Settings);

/// <summary>
/// Builds an agent-ready Semantic Kernel from the CURRENT persisted settings: load
/// <see cref="AccountSettings"/> (gateway base + model), create the kernel via the factory, attach
/// the tool-call UI filter and the auto-invoke iteration cap, and register the given tool plugins.
/// Authentication is handled per-request by the factory's gateway auth handler, so no key/secret
/// is resolved here.
///
/// <para>This is the single implementation of the configure block previously copied across
/// <c>FlAgent</c>, <c>ChatBridgeService</c>, and <c>SubAgentService</c>. Which plugins go in is
/// data (<see cref="FlPluginSet.All"/> vs the sub-agent view without orchestration), not code.</para>
/// </summary>
internal sealed class AgentKernelBuilder(
    IChatKernelFactory kernelFactory,
    ISettingsStore settingsStore)
{
    /// <summary>The finite round value used for "unlimited" (MaxToolRoundsPerTurn &lt;= 0). Large enough
    /// that the iteration filter never trips on real work, small enough that <c>rounds + RequestBudgetSlack</c>
    /// stays well inside int range (no overflow in the turn runner's budget).</summary>
    private const int UnlimitedRounds = 1_000_000;

    /// <summary>
    /// Builds a fresh kernel from the settings on disk. Construction is local (no network call), so
    /// this succeeds even while offline or logged out — failures surface on the first turn.
    /// </summary>
    /// <param name="plugins">Tool plugins to register, with their model-visible names.</param>
    /// <param name="toolFilter">Shared invocation filter that surfaces tool calls in the UI.</param>
    /// <param name="maxRounds">Explicit auto-invoke round cap per turn, or null to use the
    /// persisted <see cref="AccountSettings.MaxToolRoundsPerTurn"/> (see
    /// <see cref="AutoInvokeIterationFilter"/>).</param>
    /// <param name="ct">Cancels the settings load.</param>
    public async Task<AgentKernel> BuildAsync(
        IReadOnlyList<(string Name, object Instance)> plugins,
        ToolCallFilter toolFilter,
        int? maxRounds = null,
        CancellationToken ct = default)
    {
        AppSettings app = await settingsStore.LoadAsync(ct).ConfigureAwait(false);
        AccountSettings account = app.AccountOrDefault;

        Kernel kernel = kernelFactory.CreateKernel(account);
        kernel.FunctionInvocationFilters.Add(toolFilter);
        // Stop the auto-invoke loop the moment the user hits Stop: this cancellation filter ends the
        // loop (via Terminate) BEFORE the next tool call is dispatched, so no further FL mutation runs
        // after a cancel. Registered ahead of the round-cap filter so the cancel check wins outermost;
        // kept in its OWN filter so cancellation stays independent of the (now unlimited) round cap.
        kernel.AutoFunctionInvocationFilters.Add(new TurnCancellationAutoInvokeFilter());
        // Tool-call rounds are UNLIMITED by default (MaxToolRoundsPerTurn <= 0): a real multi-step job
        // (chop + arrange + route a whole session) chains many rounds, and an artificial cap cut honest
        // work short. The user's STOP button is the control for a genuinely-wrong turn; SK's own internal
        // auto-invoke limit + the "say continue" resume are the soft backstops. "Unlimited" resolves to a
        // large FINITE value so the filter never trips and the runner's request-budget (rounds + slack)
        // can't overflow. An explicit positive setting still clamps to a sane range for anyone who wants a cap.
        int configured = maxRounds ?? account.MaxToolRoundsPerTurn;
        int rounds = configured <= 0 ? UnlimitedRounds : Math.Clamp(configured, 1, 200);
        kernel.AutoFunctionInvocationFilters.Add(new AutoInvokeIterationFilter(rounds));
        foreach ((string name, object instance) in plugins)
            kernel.Plugins.AddFromObject(instance, name);

        // Multi-version tool gating: hide native tools whose required FL symbol didn't resolve on the
        // running FL version (a signature that didn't match this build), so a version gap makes a tool
        // quietly UNAVAILABLE instead of firing a wrong address inside FL. Additive + fail-open: when
        // the bridge can't report status (mock/tests, or FL not ready), NOTHING is gated and the full
        // surface is advertised exactly as before — the common case where every symbol resolves.
        foreach ((string name, object instance) in plugins)
            if (instance is NativeControlPlugin native)
                await ApplyVersionGateAsync(kernel, name, native, ct).ConfigureAwait(false);

        return new AgentKernel(kernel, kernel.GetRequiredService<IChatCompletionService>(), account);
    }

    /// <summary>
    /// Prunes the native tools whose required FL symbol is UNRESOLVED on this FL build from the
    /// kernel, so they are never advertised (in either the subset or full-surface path) nor invocable.
    /// Logs the detected version + unresolved symbols prominently regardless, so a version gap is
    /// visible even when nothing maps to a gated tool. Best-effort: any failure here leaves the full
    /// surface intact (a diagnostic must never break kernel construction).
    /// </summary>
    private static async Task ApplyVersionGateAsync(
        Kernel kernel, string pluginName, NativeControlPlugin native, CancellationToken ct)
    {
        FlSymbolStatus? status;
        try { status = await native.GetSymbolStatusAsync(ct).ConfigureAwait(false); }
        catch { return; }                       // unknown → advertise everything (fail open)
        if (status is null) return;             // bridge not ready / can't report → fail open

        IReadOnlyCollection<string> gated = NativeSymbolGate.UnavailableTools(status.Unresolved);
        LogVersionGate(status, gated);

        if (gated.Count == 0) return;           // common case: nothing to hide
        if (!kernel.Plugins.TryGetPlugin(pluginName, out KernelPlugin? plugin) || plugin is null) return;

        var gatedSet = new HashSet<string>(gated, StringComparer.Ordinal);
        var kept = plugin.Where(fn => !gatedSet.Contains(fn.Name)).ToList();
        if (kept.Count == plugin.FunctionCount) return;   // none of the gated names are actually registered

        // KernelPlugins are immutable, so swap the whole plugin for a filtered copy. The kept
        // KernelFunctions are re-hosted under the same plugin name; the NativeControlPlugin instance is
        // untouched, so direct (non-LLM) callers — undo/redo replay, version control — keep working.
        kernel.Plugins.Remove(plugin);
        kernel.Plugins.Add(KernelPluginFactory.CreateFromFunctions(pluginName, kept));
    }

    /// <summary>
    /// Append the version-gate outcome to <c>%APPDATA%\FLAutomate\logs\fl-symbols-&lt;date&gt;.log</c>
    /// so a version gap is diagnosable after the fact. Best-effort — never throws, never blocks build.
    /// </summary>
    private static void LogVersionGate(FlSymbolStatus status, IReadOnlyCollection<string> gatedTools)
    {
        try
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FLAutomate", "logs");
            Directory.CreateDirectory(dir);

            string versionLabel = NativeSymbolGate.VersionName(status.Version);
            string line =
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} FL={versionLabel} (ver={status.Version}) " +
                $"symbols ok={status.Resolved} fail={status.Failed}";
            if (status.Failed > 0)
                line += "; unresolved=[" + string.Join(", ", status.Unresolved.OrderBy(s => s)) + "]";
            line += gatedTools.Count > 0
                ? "; TOOLS HIDDEN=[" + string.Join(", ", gatedTools.OrderBy(s => s)) + "]"
                : "; tools hidden=none";

            File.AppendAllText(Path.Combine(dir, $"fl-symbols-{DateTime.Now:yyyyMMdd}.log"), line + Environment.NewLine);
        }
        catch { /* logging must never fail or slow kernel construction */ }
    }
}
