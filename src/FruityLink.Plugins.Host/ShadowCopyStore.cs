namespace FruityLink.Plugins.Host;

/// <summary>
/// Owns the shadow-copy root for <see cref="PluginManager"/>: every plugin assembly is loaded from a
/// private per-load copy under this root, never from the original file, so the original stays
/// writable — a <c>dotnet build</c> over it succeeds and TRIGGERS the hot-reload watcher.
/// Construction reclaims any copies left behind by a previous (crashed) session.
/// </summary>
internal sealed class ShadowCopyStore
{
    private readonly string _shadowRoot;
    private readonly string _pluginsDir;
    private readonly Action<string> _log;

    /// <param name="shadowRoot">Root directory for per-load shadow copies of plugin assemblies.</param>
    /// <param name="pluginsDir">The plugins directory (distinguishes flat vs per-plugin-folder layout).</param>
    /// <param name="log">Diagnostic sink (delete failures are logged, never thrown).</param>
    public ShadowCopyStore(string shadowRoot, string pluginsDir, Action<string> log)
    {
        _shadowRoot = shadowRoot;
        _pluginsDir = pluginsDir;
        _log = log;

        // Reclaim any shadow copies left behind by a previous (crashed) session.
        TryDeleteDir(_shadowRoot);
        try { Directory.CreateDirectory(_shadowRoot); } catch { /* created lazily on first load */ }
    }

    public (string dir, string dll) ShadowCopy(string originalDll)
    {
        string loadDir = Path.Combine(_shadowRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(loadDir);

        string srcDir = Path.GetDirectoryName(originalDll) ?? _pluginsDir;
        if (PathEquals(srcDir, _pluginsDir))
        {
            // Flat layout: the dll + its sidecars only (siblings belong to other plugins).
            CopyFileIfExists(originalDll, loadDir);
            CopyFileIfExists(Path.ChangeExtension(originalDll, ".deps.json"), loadDir);
            CopyFileIfExists(Path.ChangeExtension(originalDll, ".runtimeconfig.json"), loadDir);
            CopyFileIfExists(Path.ChangeExtension(originalDll, ".pdb"), loadDir);
        }
        else
        {
            // Per-plugin folder: copy the whole package so private deps resolve from the shadow.
            CopyDirRecursive(srcDir, loadDir);
        }

        return (loadDir, Path.Combine(loadDir, Path.GetFileName(originalDll)));
    }

    private static void CopyFileIfExists(string src, string destDir)
    {
        if (File.Exists(src))
            File.Copy(src, Path.Combine(destDir, Path.GetFileName(src)), overwrite: true);
    }

    private static void CopyDirRecursive(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (string f in Directory.EnumerateFiles(src))
            File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), overwrite: true);
        foreach (string d in Directory.EnumerateDirectories(src))
            CopyDirRecursive(d, Path.Combine(dst, Path.GetFileName(d)));
    }

    public void TryDeleteDir(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch (Exception ex) { _log($"shadow: could not delete '{dir}': {ex.Message} (will retry next startup)"); }
    }

    /// <summary>Full-path, case-insensitive path equality (falls back to ordinal-ignore-case on bad paths).</summary>
    public static bool PathEquals(string a, string b)
    {
        try { return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
        catch { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }
    }
}
