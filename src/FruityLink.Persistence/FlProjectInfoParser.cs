namespace FruityLink.Persistence;

/// <summary>
/// Parses the multi-line string from <see cref="FruityLink.Core.Abstractions.INativeFlControl.GetProjectInfoAsync"/>
/// ("Title: …\nPath: …\nSaved: …") into the on-disk path (null when untitled) and an untitled flag.
/// </summary>
internal static class FlProjectInfoParser
{
    /// <summary>Extracts the project path (null when untitled) and untitled flag from <paramref name="info"/>.</summary>
    public static (string? Path, bool Untitled) Parse(string info)
    {
        string? path = null;
        bool untitled = false;
        foreach (string raw in info.Split('\n'))
        {
            string line = raw.Trim();
            if (line.StartsWith("Path:", StringComparison.OrdinalIgnoreCase))
            {
                string value = line[5..].Trim();
                path = (value.Length == 0 || value == "(none)") ? null : value;
            }
            else if (line.StartsWith("Saved:", StringComparison.OrdinalIgnoreCase))
            {
                untitled = line.Contains("no", StringComparison.OrdinalIgnoreCase);
            }
        }
        if (path is null) untitled = true;
        return (path, untitled);
    }
}
