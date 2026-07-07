using System.Text;

namespace FruityLink.Agent.Manuals;

/// <summary>The plugin family a manual documents. Mirrors the two param-tool families the AI drives:
/// generators via native_*_channel_plugin_params, effects via native_*_mixer_plugin_params.</summary>
public enum ManualType
{
    Generator,
    Effect,
}

/// <summary>
/// One parsed plugin manual: the "man page" for an FL Studio plugin. Metadata comes from a small
/// YAML-ish frontmatter block at the top of the markdown file; <see cref="Body"/> is the human
/// (and model) readable manual that follows. The store hands whole manuals to the model — this
/// record is the parsed, queryable view (name/aliases/type) used to enumerate and resolve them.
/// </summary>
public sealed record PluginManual(
    string Name,
    IReadOnlyList<string> Aliases,
    ManualType Type,
    string Category,
    string Summary,
    string Body)
{
    /// <summary>Lowercased alphanumeric form used for case/space/punctuation-insensitive matching:
    /// "3x OSC", "3xOSC", "3x-osc" all collapse to "3xosc".</summary>
    internal static string Normalize(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char c in s)
            if (char.IsLetterOrDigit(c))
                sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }

    /// <summary>Every normalized token this manual answers to (its name + declared aliases).</summary>
    internal IEnumerable<string> MatchKeys() =>
        Aliases.Prepend(Name).Select(Normalize).Where(k => k.Length > 0).Distinct();

    /// <summary>Parses a manual markdown file. The frontmatter block is delimited by a leading line
    /// of exactly "---" and a following line of exactly "---"; keys are simple "key: value" pairs.
    /// A file without frontmatter still parses (name falls back to <paramref name="fallbackName"/>,
    /// type to Generator) so a half-written manual never crashes the store.</summary>
    public static PluginManual Parse(string markdown, string fallbackName)
    {
        markdown = markdown.Replace("\r\n", "\n").Replace('\r', '\n');
        var meta = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string body = markdown;

        string[] lines = markdown.Split('\n');
        if (lines.Length > 0 && lines[0].Trim() == "---")
        {
            int end = -1;
            for (int i = 1; i < lines.Length; i++)
            {
                if (lines[i].Trim() == "---") { end = i; break; }
                int colon = lines[i].IndexOf(':');
                if (colon > 0)
                {
                    string key = lines[i][..colon].Trim();
                    string val = lines[i][(colon + 1)..].Trim();
                    if (key.Length > 0) meta[key] = val;
                }
            }
            if (end > 0)
                body = string.Join('\n', lines.Skip(end + 1)).TrimStart('\n');
        }

        string name = meta.GetValueOrDefault("name", fallbackName).Trim();
        if (name.Length == 0) name = fallbackName;

        ManualType type = meta.GetValueOrDefault("type", "generator").Trim().ToLowerInvariant() switch
        {
            "effect" or "fx" => ManualType.Effect,
            _ => ManualType.Generator,
        };

        return new PluginManual(
            Name: name,
            Aliases: ParseList(meta.GetValueOrDefault("aliases", "")),
            Type: type,
            Category: meta.GetValueOrDefault("category", "").Trim(),
            Summary: meta.GetValueOrDefault("summary", "").Trim(),
            Body: body.Trim().Length == 0 ? markdown.Trim() : body.Trim());
    }

    /// <summary>Splits an "aliases" value: comma/semicolon separated, tolerant of an optional
    /// surrounding "[ ]" (so both "a, b" and "[a, b]" work).</summary>
    private static IReadOnlyList<string> ParseList(string raw)
    {
        raw = raw.Trim();
        if (raw.StartsWith('[') && raw.EndsWith(']')) raw = raw[1..^1];
        return raw
            .Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(a => a.Trim('"', '\'', ' '))
            .Where(a => a.Length > 0)
            .ToArray();
    }
}
