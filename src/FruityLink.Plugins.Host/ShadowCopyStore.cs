namespace FruityLink.Plugins.Host;

/// <summary>
/// Owns the shadow-copy root for <see cref="PluginManager"/>: every plugin assembly is loaded from a
/// private per-load copy under this root, never from the original file, so the original stays
/// writable — a <c>dotnet build</c> over it succeeds and TRIGGERS the hot-reload watcher.
/// Copies from another live host are never removed during construction.
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
        _shadowRoot = Path.GetFullPath(shadowRoot);
        _pluginsDir = Path.GetFullPath(pluginsDir);
        _log = log;

        if (PathEquals(_shadowRoot, _pluginsDir) || _shadowRoot.StartsWith(
            Path.TrimEndingDirectorySeparator(_pluginsDir) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The shadow root must be outside the plugins directory.", nameof(shadowRoot));

        // Multiple FL versions/processes can share this root. Deleting it here removes dependencies
        // that another live plugin has not loaded yet, even if its main assembly is already locked.
        try { Directory.CreateDirectory(_shadowRoot); } catch { /* created lazily on first load */ }
    }

    public (string dir, string dll) ShadowCopy(string originalDll)
    {
        string loadDir = Path.Combine(_shadowRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(loadDir);

        try
        {
            string srcDir = Path.GetDirectoryName(originalDll) ?? _pluginsDir;
            // Flat plugins can reference sibling libraries and runtime assets too. Copying only the
            // entry dll breaks dependencies and can cause fallback into the host's assembly context.
            CopyDirRecursive(srcDir, loadDir);
            return (loadDir, Path.Combine(loadDir, Path.GetFileName(originalDll)));
        }
        catch
        {
            TryDeleteDir(loadDir);
            throw;
        }
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
        catch (Exception ex) { _log($"shadow: could not delete '{dir}': {ex.Message} (may be cleaned after all hosts exit)"); }
    }

    /// <summary>Full-path, case-insensitive path equality (falls back to ordinal-ignore-case on bad paths).</summary>
    public static bool PathEquals(string a, string b)
    {
        try { return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
        catch { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }
    }
}
