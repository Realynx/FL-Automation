using Microsoft.Win32;

namespace FruityLink.Core.Hosting;

/// <summary>
/// The folders a sample listing searches, each with the short tag its entries carry. ONE definition,
/// shared by the SDK's <c>list_samples</c> / <c>query_samples</c> lister and resolver and by the MCP
/// server's managed-session staging policy, so a tag can never mean two different folders.
/// </summary>
/// <remarks>
/// <para>Tags: <c>[P]</c> = FL's factory packs (<c>&lt;install&gt;\Data\Patches\Packs</c>), <c>[U]</c> = the
/// user's Image-Line documents content (<c>Documents\Image-Line\FL Studio</c>), and <c>[B1]</c>,
/// <c>[B2]</c>, ... = every EXTRA folder the FL browser searches.</para>
/// <para>Live finding 2026-09-18: an agent told to "use my sampled drums" found <c>[U]</c> empty, because a
/// user's own library normally lives outside both FL roots and reaches FL's browser through
/// <b>Browser extra search folders</b>. FL 2026 stores those in the REGISTRY, not in a settings file:
/// <c>HKCU\Software\Image-Line\FL Studio &lt;major&gt;\Search paths</c>, one <c>REG_SZ</c> per folder named
/// "0", "1", ..., whose value is <c>&lt;absolute folder&gt;,&lt;display name&gt;</c> (measured on this machine:
/// <c>C:\Users\poofi\Documents\Splice\Samples\packs,splice</c>). Nothing under
/// <c>Documents\Image-Line\FL Studio\Settings\</c> holds them. Every <c>FL Studio &lt;major&gt;</c> key is read,
/// newest major first, so a machine with several FL versions still sees its folders.</para>
/// <para><see cref="EnvironmentVariable"/> adds folders that FL does not know about (a library the user
/// never added to the browser), semicolon-separated absolute paths. Those come first, then the
/// registry's, then <c>[P]</c> and <c>[U]</c>: FL's factory packs are enormous and a listing is capped, so
/// scanning the user's own folders FIRST is what makes "my drums" findable at all.</para>
/// <para>A folder that does not exist, that repeats, or that lies inside a root already in the list is
/// dropped, so one file is never listed twice under two tags and every entry round-trips through the
/// resolver unchanged.</para>
/// </remarks>
public static class FlSampleRoots
{
    /// <summary>Semicolon-separated absolute folders added as sample roots ahead of everything else.
    /// The documented escape hatch when a library is not in FL's browser folders.</summary>
    public const string EnvironmentVariable = "FRUITYLINK_SAMPLE_ROOTS";

    /// <summary>The registry key, under HKEY_CURRENT_USER, that holds FL's per-version settings.</summary>
    public const string ImageLineKey = @"Software\Image-Line";

    /// <summary>The subkey of one FL version key holding the browser's extra search folders.</summary>
    public const string SearchPathsSubkey = "Search paths";

    /// <summary>Tag of FL's factory packs.</summary>
    public const string PacksTag = "[P]";

    /// <summary>Tag of the user's Image-Line documents content.</summary>
    public const string UserTag = "[U]";

    /// <summary>Tag prefix of an FL browser extra search folder ("[B1]", "[B2]", ...).</summary>
    public const string BrowserTagPrefix = "[B";

    /// <summary>The roots for a given FL install: the environment folders, FL's browser extra search
    /// folders, then the factory packs and the user's Image-Line content.</summary>
    /// <param name="installDirectory">Folder holding FL64.exe, or null when FL is not running.</param>
    public static IReadOnlyList<(string Root, string Tag)> Discover(string? installDirectory) =>
        Compose(installDirectory, [.. EnvironmentFolders(), .. BrowserSearchFolders()]);

    /// <summary>The same composition from an explicit extra-folder list, for tests and callers that
    /// discover the folders themselves. Order is preserved; unusable and duplicate folders are dropped.</summary>
    public static IReadOnlyList<(string Root, string Tag)> Compose(string? installDirectory, IEnumerable<string> extraFolders)
    {
        ArgumentNullException.ThrowIfNull(extraFolders);
        var fixedRoots = new List<(string Root, string Tag)>();
        if (!string.IsNullOrWhiteSpace(installDirectory))
            fixedRoots.Add((Path.Combine(installDirectory, "Data", "Patches", "Packs"), PacksTag));
        fixedRoots.Add((Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Image-Line", "FL Studio"), UserTag));

        var roots = new List<(string Root, string Tag)>();
        var seen = new List<string>(fixedRoots.Select(root => Normalize(root.Root)));
        var extras = new List<string>();
        foreach (var folder in extraFolders)
        {
            var full = TryFullPath(folder);
            if (full is null || !Directory.Exists(full)) continue;
            var normalized = Normalize(full);
            if (seen.Any(known => IsSameOrUnder(normalized, known)) || extras.Any(known => IsSameOrUnder(normalized, known)))
                continue;   // one file must never be listed twice under two tags
            extras.Add(normalized);
            roots.Add((full, $"{BrowserTagPrefix}{roots.Count + 1}]"));
        }
        roots.AddRange(fixedRoots);
        return roots;
    }

    /// <summary>The folders named by <see cref="EnvironmentVariable"/>, in the order given; never throws.</summary>
    public static IReadOnlyList<string> EnvironmentFolders()
    {
        var value = Environment.GetEnvironmentVariable(EnvironmentVariable);
        return string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    /// <summary>FL's "Browser extra search folders", newest FL version first; empty when none are configured
    /// or the registry cannot be read. Never throws: a missing setting must not break a sample listing.</summary>
    public static IReadOnlyList<string> BrowserSearchFolders()
    {
        if (!OperatingSystem.IsWindows()) return [];
        var folders = new List<string>();
        try
        {
            using var imageLine = Registry.CurrentUser.OpenSubKey(ImageLineKey);
            if (imageLine is null) return [];
            foreach (var version in imageLine.GetSubKeyNames()
                         .Where(name => name.StartsWith("FL Studio ", StringComparison.OrdinalIgnoreCase))
                         .OrderByDescending(MajorVersion).ThenByDescending(name => name, StringComparer.OrdinalIgnoreCase))
            {
                using var paths = imageLine.OpenSubKey($@"{version}\{SearchPathsSubkey}");
                if (paths is null) continue;
                foreach (var name in paths.GetValueNames().OrderBy(NumericOrder).ThenBy(name => name, StringComparer.OrdinalIgnoreCase))
                {
                    var folder = SearchPathFolder(paths.GetValue(name) as string);
                    if (folder is not null) folders.Add(folder);
                }
            }
        }
        catch (Exception error) when (error is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return folders;   // whatever was read before the refusal is still better than nothing
        }
        return folders;
    }

    /// <summary>The folder out of one "Search paths" value (<c>&lt;folder&gt;,&lt;display name&gt;</c>).
    /// A folder name may itself contain a comma, so every split point is tried from the right and the
    /// first candidate that IS a directory wins; the whole value is the last resort.</summary>
    public static string? SearchPathFolder(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var text = value.Trim();
        for (int comma = text.LastIndexOf(','); comma > 0; comma = text.LastIndexOf(',', comma - 1))
        {
            var candidate = text[..comma].Trim();
            if (candidate.Length > 0 && Directory.Exists(candidate)) return candidate;
        }
        return Directory.Exists(text) ? text : null;
    }

    private static int MajorVersion(string versionKeyName) =>
        int.TryParse(versionKeyName["FL Studio ".Length..].Trim(), out var major) ? major : -1;

    private static int NumericOrder(string valueName) => int.TryParse(valueName, out var index) ? index : int.MaxValue;

    private static string? TryFullPath(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return null;
        try { return Path.GetFullPath(folder.Trim()); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }

    private static string Normalize(string path)
    {
        try { return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { return path; }
    }

    private static bool IsSameOrUnder(string candidate, string root) =>
        candidate.Equals(root, StringComparison.OrdinalIgnoreCase) ||
        candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}
