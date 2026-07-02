using System.ComponentModel;
using System.Text;
using FruityLink.Core.Abstractions;
using FruityLink.Core.Configuration;
using Microsoft.SemanticKernel;

namespace FruityLink.Agent.Plugins;

/// <summary>
/// Orchestration tool: lets the main agent fan independent work out to parallel sub-agents (e.g. one
/// per pattern/instrument), each with full FL Studio control, to finish multi-part jobs faster.
/// Concurrency is capped by <c>AppSettings.MaxSubAgents</c>.
/// </summary>
public sealed class OrchestrationPlugin(SubAgentService subAgents, ISettingsStore settingsStore)
{
    [KernelFunction("run_parallel_tasks")]
    [Description(
        "Run several INDEPENDENT music-production tasks in PARALLEL, each on its own sub-agent with full " +
        "FL Studio control (notes, patterns, channels, mixer, transport). Speeds up multi-part work: one " +
        "self-contained instruction per task — e.g. 'Program kick/snare/hi-hat drums in pattern 1', " +
        "'Write the C-major chord progression on the Piano channel in pattern 3', 'Add a bassline on the Color " +
        "Bass channel in pattern 3'. Each task must name its own pattern + channel(s) so tasks don't conflict. " +
        "Returns each sub-agent's result. Prefer over doing many independent edits yourself. Use ONLY for 2+ " +
        "genuinely independent, multi-step parts (e.g. distinct patterns/instruments/sections); for a single " +
        "action or a couple of quick edits, just call the native_* tools directly instead.")]
    public async Task<string> RunParallelTasksAsync(
        [Description("Independent tasks; each a clear, self-contained instruction")] string[] tasks,
        CancellationToken ct = default)
    {
        if (tasks is null || tasks.Length == 0) return "No tasks provided.";

        AppSettings app = await settingsStore.LoadAsync(ct).ConfigureAwait(false);
        int max = Math.Clamp(app.MaxSubAgents, 1, 64);
        using var gate = new SemaphoreSlim(max);

        var results = new string[tasks.Length];
        var runs = tasks.Select(async (task, i) =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try { results[i] = await subAgents.RunTaskAsync(task, ct).ConfigureAwait(false); }
            catch (Exception ex) { results[i] = $"FAILED: {ex.Message}"; }
            finally { gate.Release(); }
        }).ToList();
        await Task.WhenAll(runs).ConfigureAwait(false);

        var sb = new StringBuilder();
        sb.AppendLine($"Ran {tasks.Length} task(s) across up to {max} parallel sub-agents:");
        for (int i = 0; i < tasks.Length; i++)
            sb.AppendLine($"[{i + 1}] {results[i]}");
        return sb.ToString();
    }
}
