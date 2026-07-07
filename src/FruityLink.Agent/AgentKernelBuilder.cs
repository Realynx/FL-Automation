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
        // Bound a RUNAWAY auto-invoke loop (flaky backends can re-call tools forever) without
        // cutting off honest multi-step jobs: the cap comes from settings.json
        // (MaxToolRoundsPerTurn, default 40), clamped to a sane range. One filter per kernel —
        // the turn runner resets/reads its per-turn cap signal.
        int rounds = Math.Clamp(maxRounds ?? account.MaxToolRoundsPerTurn, 1, 200);
        kernel.AutoFunctionInvocationFilters.Add(new AutoInvokeIterationFilter(rounds));
        foreach ((string name, object instance) in plugins)
            kernel.Plugins.AddFromObject(instance, name);

        return new AgentKernel(kernel, kernel.GetRequiredService<IChatCompletionService>(), account);
    }
}
