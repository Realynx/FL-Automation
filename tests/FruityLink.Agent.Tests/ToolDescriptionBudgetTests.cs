using System.ComponentModel;
using System.Reflection;
using FruityLink.Agent.Plugins;
using Microsoft.SemanticKernel;
using Shouldly;
using Xunit;

namespace FruityLink.Agent.Tests;

/// <summary>
/// Token-budget ratchet over the advertised tool surface. Every tool schema is re-sent on EVERY
/// request, so description bloat and surface creep are a recurring per-turn cost — and the main
/// thing that overwhelms small models' tool choice. These tests pin the budgets the pruned surface
/// ships with: hard word caps per description, and a ceiling on the advertised-tool COUNT (the
/// post-prune baseline measured when this project landed). Adding a tool or fattening a description
/// past the cap must be a deliberate, test-visible decision.
/// </summary>
public sealed class ToolDescriptionBudgetTests
{
    /// <summary>Hard cap on a tool's [Description], in words.</summary>
    private const int MaxToolDescriptionWords = 40;

    /// <summary>Hard cap on a parameter's [Description], in words.</summary>
    private const int MaxParameterDescriptionWords = 12;

    /// <summary>The post-prune advertised-tool count measured 2026-07-02 (NativeControl 77 +
    /// MusicTheory 3 + Knowledge 1 + Orchestration 1), raised by 2 for Versioning
    /// (list_versions + get_version_changes), then by 2 more for the plugin "man db" on Knowledge
    /// (list_plugin_manuals + get_plugin_manual), then by 2 more (2026-07-07): Knowledge search_manual
    /// (FL Studio manual RAG) + NativeControl native_list_mixer_tracks (resolve a mixer bus NAME to
    /// its index instead of scanning), then by 1 (2026-07-07): native_set_mixer_track_muted (mixer
    /// mute had NO tool at all — the model muted via volume=0, which lost the prior level), then by 9
    /// more (2026-07-07 tool-surface gap-fill): surgical piano-roll editing native_edit_notes/
    /// native_delete_notes (was: clear the whole pattern + rebuild) + native_clone_pattern; the rename
    /// family native_set_pattern_name/native_set_channel_name/native_set_mixer_track_name (labels the
    /// model creates, which its own name→index lookups then rely on); native_solo_channel/
    /// native_solo_track; native_set_loop_region. The ratchet only stops growth — shrinking the surface
    /// is always welcome; if a new tool is genuinely needed, raise this literal in the same change that
    /// adds it.</summary>
    private const int MaxAdvertisedToolCount = 98;

    /// <summary>Every plugin type whose [KernelFunction] methods reach the model.</summary>
    private static readonly Type[] AdvertisedPluginTypes =
    {
        typeof(NativeControlPlugin),
        typeof(MusicTheoryPlugin),
        typeof(KnowledgePlugin),
        typeof(OrchestrationPlugin),
        typeof(VersioningPlugin),
    };

    private static IReadOnlyList<MethodInfo> AdvertisedTools() =>
        AdvertisedPluginTypes
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            .Where(m => m.GetCustomAttribute<KernelFunctionAttribute>() is not null)
            .ToArray();

    private static string ToolName(MethodInfo method) =>
        method.GetCustomAttribute<KernelFunctionAttribute>()?.Name ?? method.Name;

    /// <summary>Words = whitespace-separated tokens containing at least one letter or digit, so
    /// punctuation-only tokens ("—", "/") don't count against the budget.</summary>
    private static int CountWords(string text) =>
        text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Count(token => token.Any(char.IsLetterOrDigit));

    [Fact]
    public void AdvertisedToolCount_StaysAtOrBelowThePostPruneBaseline()
    {
        int count = AdvertisedTools().Count;

        count.ShouldBeLessThanOrEqualTo(MaxAdvertisedToolCount,
            $"tool-surface creep: {count} advertised tools exceed the post-prune baseline of {MaxAdvertisedToolCount}. " +
            "Prune a tool or consciously raise the ratchet in the same change.");
    }

    [Fact]
    public void EveryTool_HasADescriptionWithinTheWordBudget()
    {
        var violations = new List<string>();
        foreach (MethodInfo tool in AdvertisedTools())
        {
            string? description = tool.GetCustomAttribute<DescriptionAttribute>()?.Description;
            if (string.IsNullOrWhiteSpace(description))
            {
                violations.Add($"{ToolName(tool)}: missing [Description]");
                continue;
            }

            int words = CountWords(description);
            if (words > MaxToolDescriptionWords)
                violations.Add($"{ToolName(tool)}: {words} words (cap {MaxToolDescriptionWords})");
        }

        violations.ShouldBeEmpty(string.Join("; ", violations));
    }

    [Fact]
    public void EveryParameter_HasADescriptionWithinTheWordBudget()
    {
        var violations = new List<string>();
        foreach (MethodInfo tool in AdvertisedTools())
        {
            foreach (ParameterInfo parameter in tool.GetParameters())
            {
                // CancellationToken is invisible to the model — SK strips it from the schema.
                if (parameter.ParameterType == typeof(CancellationToken))
                    continue;

                string? description = parameter.GetCustomAttribute<DescriptionAttribute>()?.Description;
                if (string.IsNullOrWhiteSpace(description))
                {
                    violations.Add($"{ToolName(tool)}.{parameter.Name}: missing [Description]");
                    continue;
                }

                int words = CountWords(description);
                if (words > MaxParameterDescriptionWords)
                    violations.Add($"{ToolName(tool)}.{parameter.Name}: {words} words (cap {MaxParameterDescriptionWords})");
            }
        }

        violations.ShouldBeEmpty(string.Join("; ", violations));
    }

    [Fact]
    public void ToolNames_AreUniqueAcrossTheWholeSurface()
    {
        // Two plugins exposing the same model-visible name would silently split the model's choice
        // (the exact failure the master-volume prune removed).
        var duplicates = AdvertisedTools()
            .GroupBy(ToolName, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToArray();

        duplicates.ShouldBeEmpty(string.Join(", ", duplicates));
    }
}
