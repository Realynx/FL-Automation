namespace FruityLink.Plugins.Host;

/// <summary>Maps package dependency and directory changes back to their plugin entry assemblies.</summary>
internal static class PluginReloadTargets
{
    public static IReadOnlyCollection<string> Resolve(string pluginsDir, IEnumerable<string> knownDlls, IEnumerable<string> changedPaths)
    {
        string root = Path.GetFullPath(pluginsDir);
        string[] changed = changedPaths.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string dll in knownDlls)
        {
            string package = Path.GetDirectoryName(Path.GetFullPath(dll))!;
            if (changed.Any(path => AffectsPackage(path, package, root))) targets.Add(dll);
        }
        foreach (string path in changed)
            if (path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) targets.Add(path);
        return targets;
    }

    private static bool AffectsPackage(string changed, string package, string root)
    {
        if (ShadowCopyStore.PathEquals(changed, root)) return true; // watcher overflow/reconcile
        return ShadowCopyStore.PathEquals(changed, package)
            || changed.StartsWith(Path.TrimEndingDirectorySeparator(package) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);
    }
}
