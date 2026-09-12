using FruityLink.Agent.Plugins;
using FruityLink.Core.Abstractions;
using FruityLink.Llm;
using Microsoft.SemanticKernel.ChatCompletion;

namespace FruityLink.Agent;

/// <summary>
/// Runs a single, self-contained task on a fresh sub-agent: its own kernel + conversation, the FL
/// control / music-theory / knowledge tools (but NOT the orchestration tool — sub-agents never spawn
/// further sub-agents; see <see cref="FlPluginSet.SubAgentPlugins"/>), and a task-focused prompt.
/// The orchestrator fans many of these out in parallel so multi-part jobs (e.g. one pattern per
/// agent) complete faster. A thin adapter over <see cref="AgentKernelBuilder"/> +
/// <see cref="AgentTurnRunner"/>, so sub-agents share the exact turn pipeline (settings, rollback,
/// think-stripping, cap handling) of the main agent instead of a drifted copy.
/// </summary>
public sealed class SubAgentService(
    IChatKernelFactory kernelFactory,
    ISettingsStore settingsStore,
    MusicTheoryPlugin musicTheory,
    NativeControlPlugin nativeControl,
    KnowledgePlugin knowledge,
    ToolCallFilter toolFilter,
    AgentPromptOptions? promptOptions = null)
{
    private readonly AgentKernelBuilder _builder = new(kernelFactory, settingsStore);
    private readonly AgentTurnRunner _runner = new();
    private readonly AgentPromptOptions _promptOptions = promptOptions ?? new();

    /// <summary>Runs one task to completion (auto-invoking tools) and returns the sub-agent's summary.
    /// <paramref name="sharedContext"/> (optional) is the parent's already-fetched project state, prepended
    /// so this sub-agent doesn't re-survey PPQ/channels/patterns the orchestrator already read once.</summary>
    public async Task<string> RunTaskAsync(string task, string? sharedContext = null, CancellationToken ct = default)
    {
        // Fresh kernel + history per call: parallel sub-agents must not share mutable turn state.
        // toolFilter is shared on purpose so sub-agent tool calls surface in the UI too.
        AgentKernel agentKernel = await _builder
            .BuildAsync(FlPluginSet.SubAgentPlugins(musicTheory, nativeControl, knowledge), toolFilter, ct: ct)
            .ConfigureAwait(false);

        var history = new ChatHistory();
        history.AddSystemMessage(SystemPrompts.BuildSubAgent(_promptOptions));

        string input = string.IsNullOrWhiteSpace(sharedContext)
            ? task
            : $"CURRENT PROJECT STATE (already fetched for you — use these values directly; do NOT call " +
              $"native_get_ppq / native_list_channels / native_list_patterns to re-read them):\n{sharedContext}\n\n" +
              $"YOUR TASK: {task}";

        TurnResult result = await _runner
            .RunTurnAsync(agentKernel.Kernel, agentKernel.Chat, history, input, agentKernel.Settings, ct: ct)
            .ConfigureAwait(false);

        // A sub-agent that ends on a tool call with empty content (common with weak models) would
        // otherwise return "" — making the orchestrator's report an unreadable blank. Give a clear
        // fallback; the runner already stripped any folded <think> reasoning so it never leaks
        // into the report.
        return string.IsNullOrWhiteSpace(result.Text) ? "(task completed; no summary produced)" : result.Text;
    }
}
