using System.Reflection;
using FruityLink.Agent.Plugins;
using FruityLink.Core.Abstractions;
using FruityLink.Core.Configuration;
using FruityLink.Core.Domain;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Shouldly;
using Xunit;

namespace FruityLink.Agent.Tests;

/// <summary>
/// Ratchet over tool-name EDIT DISTANCE vs. per-turn subsetting. The wire-layer tool-name resolver
/// (LlmToolCallRepairHandler) rewrites a response function name that is edit-distance-1 from exactly
/// one ADVERTISED name — and "advertised" is <see cref="ToolSubsetSelector"/>'s per-turn SUBSET, not
/// the full surface. If a subset ever advertises one member of a distance-1 pair without the other,
/// a real, correctly-spelled call to the unadvertised member gets silently REWRITTEN into its
/// neighbor (native_set_tempo → native_get_tempo), which then "succeeds" — executing the wrong tool
/// and bypassing SK's "wasn't defined" feedback that the full-surface fallback keys on. These tests
/// fail the moment a rename/addition creates a splittable distance-1 pair, forcing the author to
/// co-advertise the pair (see native_set_tempo in ToolSubsetSelector.CoreToolNames) or rename.
/// </summary>
public sealed class ToolSurfaceEditDistanceTests
{
    /// <summary>The registered plugin names + types of the FULL main-agent surface (mirrors
    /// <see cref="FlPluginSet.All"/>), so pair enumeration covers every advertised name even where
    /// the probe kernel below can't cheaply register the plugin (Orchestration).</summary>
    private static readonly (string PluginName, Type PluginType)[] FullSurface =
    {
        ("MusicTheory", typeof(MusicTheoryPlugin)),
        ("NativeControl", typeof(NativeControlPlugin)),
        ("Knowledge", typeof(KnowledgePlugin)),
        ("Orchestration", typeof(OrchestrationPlugin)),
        ("Versioning", typeof(VersioningPlugin)),
    };

    /// <summary>Same fake-backed kernel shape as <see cref="ToolSubsetSelectorTests"/>: the three
    /// sub-agent plugins (where every known distance-1 pair lives) plus Versioning, which is cheap
    /// to construct (its store is late-bound) so it doesn't need Orchestration's exclusion carve-out.</summary>
    private static Kernel BuildProbeKernel()
    {
        var kernel = new Kernel();
        kernel.Plugins.AddFromObject(new NativeControlPlugin(new FakeNativeFlControl()), "NativeControl");
        kernel.Plugins.AddFromObject(new MusicTheoryPlugin(new NullAuditSink(), new DefaultSettingsStore()), "MusicTheory");
        kernel.Plugins.AddFromObject(new KnowledgePlugin(new EmptyRetriever()), "Knowledge");
        kernel.Plugins.AddFromObject(new VersioningPlugin(), "Versioning");
        return kernel;
    }

    /// <summary>Every model-visible qualified tool name ("Plugin-tool", SK's OpenAI spelling).</summary>
    private static IReadOnlyList<string> QualifiedSurfaceNames() =>
        FullSurface
            .SelectMany(p => p.PluginType
                .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Select(m => (Method: m, Attr: m.GetCustomAttribute<KernelFunctionAttribute>()))
                .Where(x => x.Attr is not null)
                .Select(x => $"{p.PluginName}-{(string.IsNullOrEmpty(x.Attr!.Name) ? x.Method.Name : x.Attr.Name)}"))
            .ToArray();

    private static IReadOnlyList<(string A, string B)> DistanceOnePairs(IReadOnlyList<string> names)
    {
        var pairs = new List<(string, string)>();
        for (int i = 0; i < names.Count; i++)
            for (int j = i + 1; j < names.Count; j++)
                if (IsEditDistanceOne(names[i], names[j]))
                    pairs.Add((names[i], names[j]));
        return pairs;
    }

    [Fact]
    public void EveryDistanceOnePair_IsCoAdvertisedInEverySubsetShape()
    {
        IReadOnlyList<(string A, string B)> pairs = DistanceOnePairs(QualifiedSurfaceNames());
        // Sanity: the known pairs must be seen (either order), or the enumeration itself is broken.
        pairs.ShouldContain(p =>
            (p.A == "NativeControl-native_get_tempo" && p.B == "NativeControl-native_set_tempo") ||
            (p.A == "NativeControl-native_set_tempo" && p.B == "NativeControl-native_get_tempo"));
        pairs.ShouldContain(p =>
            (p.A == "NativeControl-native_add_note" && p.B == "NativeControl-native_add_notes") ||
            (p.A == "NativeControl-native_add_notes" && p.B == "NativeControl-native_add_note"));

        // The probe kernel omits Orchestration; a distance-1 pair touching it would be invisible
        // to the probes below, so refuse it outright (extend the harness in the same change).
        foreach ((string a, string b) in pairs)
        {
            a.ShouldNotStartWith("Orchestration-");
            b.ShouldNotStartWith("Orchestration-");
        }

        Kernel kernel = BuildProbeKernel();
        var violations = new List<string>();

        // One probe per domain keyword = every subset SHAPE the selector can produce (unions of
        // groups can only merge subsets, so per-keyword probes are the minimal splits).
        foreach (string[] keywords in ToolSubsetSelector.GroupKeywordSets)
        {
            foreach (string keyword in keywords)
            {
                IReadOnlyList<KernelFunction>? subset =
                    ToolSubsetSelector.SelectForTurn(kernel, keyword, new ChatHistory());
                if (subset is null) continue;   // full surface — trivially co-advertised

                var advertised = subset
                    .Select(f => $"{f.Metadata.PluginName}-{f.Name}")
                    .ToHashSet(StringComparer.Ordinal);

                foreach ((string a, string b) in pairs)
                    if (advertised.Contains(a) != advertised.Contains(b))
                        violations.Add($"probe '{keyword}': advertises {(advertised.Contains(a) ? a : b)} without its distance-1 pair {(advertised.Contains(a) ? b : a)}");
            }
        }

        violations.Distinct().ShouldBeEmpty(string.Join("; ", violations.Distinct()));
    }

    /// <summary>Case-insensitive Levenshtein distance == 1 — the same predicate the wire-layer
    /// resolver's last tier uses (LlmToolCallRepairHandler.IsEditDistanceOne).</summary>
    private static bool IsEditDistanceOne(string a, string b)
    {
        if (a.Length > b.Length) (a, b) = (b, a);
        if (b.Length - a.Length > 1) return false;

        int i = 0, j = 0, edits = 0;
        while (i < a.Length && j < b.Length)
        {
            if (char.ToLowerInvariant(a[i]) == char.ToLowerInvariant(b[j])) { i++; j++; continue; }
            if (++edits > 1) return false;
            if (a.Length == b.Length) { i++; j++; }
            else { j++; }
        }
        edits += (a.Length - i) + (b.Length - j);
        return edits == 1;
    }

    // ---------------- minimal plugin-dependency fakes (never invoked by the selector) ----------------

    private sealed class NullAuditSink : IOperationAuditSink
    {
        public void Record(FlOperation operation) { }
        public IReadOnlyList<FlOperation> Drain() => Array.Empty<FlOperation>();
    }

    private sealed class DefaultSettingsStore : ISettingsStore
    {
        public Task<AppSettings> LoadAsync(CancellationToken ct = default) => Task.FromResult(new AppSettings());
        public Task SaveAsync(AppSettings settings, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class EmptyRetriever : IKnowledgeRetriever
    {
        public Task<IReadOnlyList<KnowledgeHit>> SearchAsync(string query, int topK = 5, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<KnowledgeHit>>(Array.Empty<KnowledgeHit>());
    }
}
