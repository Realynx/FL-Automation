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
public sealed class OrchestrationPlugin(SubAgentService subAgents, ISettingsStore settingsStore, NativeControlPlugin nativeControl)
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

        // Gather the shared project state ONCE and hand it to every sub-agent, so N parallel sub-agents
        // don't each re-survey PPQ/channels/patterns — the biggest redundant-read source in the usage
        // logs (a fan-out fired 4× get_ppq + 3× list_channels in one second). Best-effort.
        string shared = await BuildSharedContextAsync(ct).ConfigureAwait(false);

        var results = new string[tasks.Length];
        var runs = tasks.Select(async (task, i) =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try { results[i] = await subAgents.RunTaskAsync(task, shared, ct).ConfigureAwait(false); }
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

    /// <summary>Snapshots the shared project state (PPQ + channel + pattern listings) ONCE so it can be
    /// injected into every sub-agent's prompt, sparing each one its own warm-up survey. Best-effort: any
    /// read that fails is simply omitted, and a total failure returns empty (sub-agents fall back to reading).</summary>
    private async Task<string> BuildSharedContextAsync(CancellationToken ct)
    {
        var sb = new StringBuilder();
        try { sb.AppendLine(await nativeControl.GetPpqAsync(ct).ConfigureAwait(false)); } catch { }
        try { sb.AppendLine(await nativeControl.ListChannelsAsync(ct).ConfigureAwait(false)); } catch { }
        try { sb.AppendLine(await nativeControl.ListPatternsAsync(ct).ConfigureAwait(false)); } catch { }
        return sb.ToString().TrimEnd();
    }
}
