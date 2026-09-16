using System.Linq;

namespace FruityLink.FlStudio;

/// <summary>Tolerant resolution of a requested plugin name against the names in FL's plugin database
/// (the <c>.fst</c> file names <c>ListAvailablePluginsAsync</c> reports).</summary>
/// <remarks>
/// Rules, in order; the first rule with exactly one hit wins and a rule with several hits stops the search
/// as ambiguous (so a vague name never silently picks one of several plugins):
/// <list type="number">
/// <item>exact name, case-insensitive;</item>
/// <item>equal after normalisation (letters and digits only, lower case: "Pro R2" = "Pro-R 2");</item>
/// <item>containment either way after normalisation, which absorbs vendor prefixes and suffixes
/// ("FabFilter Pro-R 2" -> "Pro-R 2", "Pro-R" -> "Pro-R 2").</item>
/// </list>
/// A miss carries the closest installed names (by edit distance over the normalised forms) so the error
/// can list what to pass instead.
/// </remarks>
public static class PluginNameResolver
{
    /// <summary>Outcome of <see cref="Match"/>: the resolved name (null on a miss or ambiguity), the rule that
    /// decided it, and the candidates that rule considered (the closest names on a miss).</summary>
    public sealed record PluginNameMatch(string? Name, string Rule, IReadOnlyList<string> Candidates);

    private const int ClosestCount = 5;

    /// <summary>Resolve <paramref name="requested"/> against <paramref name="available"/>.</summary>
    public static PluginNameMatch Match(string requested, IEnumerable<string> available)
    {
        ArgumentNullException.ThrowIfNull(available);
        var names = available.Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        string want = (requested ?? string.Empty).Trim();
        if (want.Length == 0 || names.Count == 0) return new(null, "none", Array.Empty<string>());

        var exact = names.Where(n => string.Equals(n, want, StringComparison.OrdinalIgnoreCase)).ToList();
        if (exact.Count == 1) return new(exact[0], "exact", exact);

        string key = Normalize(want);
        if (key.Length == 0) return new(null, "none", Closest(key, names));

        var normalized = names.Where(n => Normalize(n) == key).ToList();
        if (normalized.Count == 1) return new(normalized[0], "normalized", normalized);
        if (normalized.Count > 1) return new(null, "ambiguous", normalized);

        if (key.Length >= 2)
        {
            var contained = names.Where(n =>
            {
                string k = Normalize(n);
                return k.Length > 0 && (key.Contains(k, StringComparison.Ordinal) || k.Contains(key, StringComparison.Ordinal));
            }).ToList();
            if (contained.Count == 1) return new(contained[0], "contains", contained);
            if (contained.Count > 1) return new(null, "ambiguous", contained);
        }
        return new(null, "none", Closest(key, names));
    }

    /// <summary>Error text for a failed match: the closest names on a miss, every candidate on an ambiguity.</summary>
    public static string DescribeFailure(string kind, string requested, PluginNameMatch match)
    {
        ArgumentNullException.ThrowIfNull(match);
        string list = string.Join(", ", match.Candidates.Select(n => $"'{n}'"));
        if (match.Rule == "ambiguous")
            return $"{kind} plugin '{requested}' matches several installed plugins: {list}. Pass one of those exact names.";
        return match.Candidates.Count == 0
            ? $"{kind} plugin '{requested}' not found and no plugins are installed in that database folder (list_available_plugins shows the exact names)."
            : $"{kind} plugin '{requested}' not found. Closest installed names: {list} (list_available_plugins shows the exact names).";
    }

    /// <summary>Letters and digits only, lower-cased: the comparison form that ignores vendor punctuation and spacing.</summary>
    public static string Normalize(string name)
        => new string((name ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static IReadOnlyList<string> Closest(string key, List<string> names)
        => names.Select(n => (Name: n, Distance: EditDistance(key, Normalize(n))))
            .OrderBy(x => x.Distance).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .Take(ClosestCount).Select(x => x.Name).ToList();

    /// <summary>Levenshtein distance; names are short, so the O(n*m) table is negligible.</summary>
    internal static int EditDistance(string a, string b)
    {
        if (a.Length == 0) return b.Length;
        if (b.Length == 0) return a.Length;
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) previous[j] = j;
        for (int i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int substitute = previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1);
                current[j] = Math.Min(Math.Min(previous[j] + 1, current[j - 1] + 1), substitute);
            }
            (previous, current) = (current, previous);
        }
        return previous[b.Length];
    }
}
