using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace FruityLink.Scripting;

internal static partial class OperationDocumentation
{
    private static readonly IReadOnlyDictionary<string, string> Descriptions = Load();
    internal static string Describe(string method) => Descriptions.TryGetValue(method, out var value)
        ? value : $"Invoke the typed SDK operation {method}.";

    private static IReadOnlyDictionary<string, string> Load()
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var assembly = typeof(OperationDocumentation).Assembly;
        foreach (string name in assembly.GetManifestResourceNames())
        {
            using var stream = assembly.GetManifestResourceStream(name);
            if (stream is null) continue;
            using var reader = new StreamReader(stream);
            ReadInterface(reader, result);
        }
        return result;
    }

    private static void ReadInterface(TextReader reader, Dictionary<string, string> result)
    {
        var comment = new List<string>();
        while (reader.ReadLine() is { } line)
        {
            string trimmed = line.Trim();
            if (trimmed.StartsWith("///", StringComparison.Ordinal)) { comment.Add(trimmed[3..]); continue; }
            var match = MethodName().Match(trimmed);
            if (match.Success && comment.Count > 0)
            {
                var xml = XElement.Parse("<root>" + string.Join(" ", comment) + "</root>");
                result[match.Groups[1].Value] = Whitespace().Replace(xml.Element("summary")?.Value.Trim() ?? "", " ");
            }
            comment.Clear();
        }
    }

    [GeneratedRegex(@"\b(\w+Async)\(")]
    private static partial Regex MethodName();
    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
