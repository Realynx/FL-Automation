using System.ComponentModel;
using System.Text;
using FruityLink.Core.Abstractions;
using FruityLink.Core.Configuration;
using Microsoft.SemanticKernel;
using static FruityLink.Agent.Plugins.PluginSupport;

namespace FruityLink.Agent.Plugins;

/// <summary>
/// Orchestration tool: lets the main agent fan independent work out to parallel sub-agents (e.g. one
/// per pattern/instrument), each with full FL Studio control, to finish multi-part jobs faster.
/// Concurrency is capped by <c>AppSettings.MaxSubAgents</c>. The when-to-fan-out guidance lives in
/// the system prompt (FAN OUT / DON'T OVER-ORCHESTRATE), so the description stays terse.
/// </summary>
public sealed class OrchestrationPlugin(SubAgentService subAgents, ISettingsStore settingsStore)
{
    [KernelFunction("run_parallel_tasks")]
    [Description("Run 2+ INDEPENDENT multi-step music tasks in PARALLEL sub-agents, each with full FL Studio control. One self-contained instruction per task, naming its own pattern + channel(s). Returns each result. For single/quick edits call native_* tools directly.")]
    public async Task<string> RunParallelTasksAsync(
        [Description("Independent tasks; each a self-contained instruction")] string[] tasks,
        CancellationToken ct = default)
    {
        if (tasks is null || tasks.Length == 0) return Err("no tasks provided");

        AppSettings app = await settingsStore.LoadAsync(ct).ConfigureAwait(false);
        int max = Math.Clamp(app.MaxSubAgents, 1, 64);
        using var gate = new SemaphoreSlim(max);

        var results = new string[tasks.Length];
        var runs = tasks.Select(async (task, i) =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try { results[i] = await subAgents.RunTaskAsync(task, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { results[i] = Err(ex.Message); }
            finally { gate.Release(); }
        }).ToList();
        await Task.WhenAll(runs).ConfigureAwait(false);

        var sb = new StringBuilder();
        sb.AppendLine($"ran {tasks.Length} task(s) across up to {max} parallel sub-agents:");
        for (int i = 0; i < tasks.Length; i++)
            sb.AppendLine($"[{i + 1}] {results[i]}");
        return Ok(sb.ToString().TrimEnd());
    }
}
