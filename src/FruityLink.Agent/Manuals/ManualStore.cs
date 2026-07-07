using System.Reflection;

namespace FruityLink.Agent.Manuals;

/// <summary>
/// The "man db" for FL Studio plugins: an extensible store of markdown manuals the agent consults
/// before designing a sound or an effect chain. Manuals ship as <c>*.md</c> EMBEDDED RESOURCES under
/// <c>src/FruityLink.Agent/Manuals/</c> (see the &lt;EmbeddedResource&gt; glob in FruityLink.Agent.csproj),
/// so they travel inside FruityLink.Agent.dll wherever it is deployed — including the FL Agent plugin
/// publish closure — with no build-and-stage change. Adding a plugin is deliberately zero-code:
/// drop a new <c>Manuals\yourplugin.md</c> file (frontmatter + body) and rebuild; it is auto-discovered.
///
/// Lookup is forgiving because the model (and the FL UI) spell plugin names many ways: resolution is
/// case/space/punctuation-insensitive, honors declared aliases, then falls back to substring and
/// edit-distance fuzzy matching ("3xosc"/"3x osc"/"3xOSC" and "reverb" all resolve).
/// </summary>
public sealed class ManualStore
{
    private readonly IReadOnlyList<PluginManual> _manuals;

    /// <summary>Process-wide default, loaded once from this assembly's embedded manuals. The store is
    /// immutable + dependency-free, so a shared instance is safe and lets KnowledgePlugin default to it
    /// without a composition-root change.</summary>
    public static ManualStore Default { get; } = new(LoadEmbedded(typeof(ManualStore).Assembly));

    public ManualStore(IReadOnlyList<PluginManual> manuals) => _manuals = manuals;

    /// <summary>All manuals, ordered by type then name for a stable catalog.</summary>
    public IReadOnlyList<PluginManual> All => _manuals
        .OrderBy(m => m.Type)
        .ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    /// <summary>A compact one-line-per-manual catalog: "Name [type] — summary".</summary>
    public string Catalog() => _manuals.Count == 0
        ? "(no plugin manuals installed)"
        : string.Join("\n", All.Select(m =>
            $"- {m.Name} [{m.Type.ToString().ToLowerInvariant()}]" +
            (string.IsNullOrEmpty(m.Summary) ? "" : $" — {m.Summary}")));

    /// <summary>Resolves a plugin name to its manual, or null. Tries, in order: exact normalized match
    /// on name/aliases, then substring either direction (so "reverb" finds "Fruity Reverb 2"), then the
    /// nearest edit-distance key within a small tolerance (typos like "3xoscc").</summary>
    public PluginManual? Resolve(string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return null;
        string q = PluginManual.Normalize(query);
        if (q.Length == 0) return null;

        // 1) exact normalized match on any key.
        foreach (var m in _manuals)
            if (m.MatchKeys().Contains(q))
                return m;

        // 2) substring either direction (query in key, or key in query) — favor the longest key so a
        //    specific alias beats an incidental short one. "reverb" ⊂ "fruityreverb2" → Fruity Reverb 2.
        PluginManual? sub = null;
        int subKeyLen = -1;
        foreach (var m in _manuals)
            foreach (string key in m.MatchKeys())
                if ((key.Contains(q) || q.Contains(key)) && key.Length > subKeyLen)
                {
                    sub = m;
                    subKeyLen = key.Length;
                }
        if (sub is not null) return sub;

        // 3) fuzzy: nearest key by edit distance, tolerant to ~1/3 of the query length (min 2).
        int tolerance = Math.Max(2, q.Length / 3);
        PluginManual? best = null;
        int bestDist = int.MaxValue;
        foreach (var m in _manuals)
            foreach (string key in m.MatchKeys())
            {
                int d = Levenshtein(q, key);
                if (d < bestDist) { bestDist = d; best = m; }
            }
        return bestDist <= tolerance ? best : null;
    }

    /// <summary>Manual names, for a "did you mean…" hint when Resolve fails.</summary>
    public IReadOnlyList<string> Names() => All.Select(m => m.Name).ToArray();

    private static IReadOnlyList<PluginManual> LoadEmbedded(Assembly assembly)
    {
        var list = new List<PluginManual>();
        foreach (string resource in assembly.GetManifestResourceNames())
        {
            // Only our manual resources: "<RootNamespace>.Manuals.<file>.md".
            if (!resource.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) continue;
            if (!resource.Contains(".Manuals.", StringComparison.OrdinalIgnoreCase)) continue;

            using Stream? s = assembly.GetManifestResourceStream(resource);
            if (s is null) continue;
            using var reader = new StreamReader(s);
            string markdown = reader.ReadToEnd();

            // Fallback name from the resource file stem (…Manuals.<stem>.md) if frontmatter omits it.
            string stem = resource[..^3];                       // drop ".md"
            int dot = stem.LastIndexOf('.');
            string fallback = dot >= 0 ? stem[(dot + 1)..] : stem;

            list.Add(PluginManual.Parse(markdown, fallback));
        }
        return list;
    }

    /// <summary>Classic iterative-DP Levenshtein distance (small strings — plugin names).</summary>
    private static int Levenshtein(string a, string b)
    {
        if (a.Length == 0) return b.Length;
        if (b.Length == 0) return a.Length;

        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) prev[j] = j;

        for (int i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }
}
