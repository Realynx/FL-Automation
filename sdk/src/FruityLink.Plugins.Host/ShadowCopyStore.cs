using System.Diagnostics;
using System.Globalization;

namespace FruityLink.Plugins.Host;

/// <summary>
/// Owns the shadow-copy root for <see cref="PluginManager"/>: every plugin assembly is loaded from a
/// private per-load copy under this root, never from the original file, so the original stays
/// writable — a <c>dotnet build</c> over it succeeds and TRIGGERS the hot-reload watcher.
/// <para>
/// Each copy carries an owner marker (<c>.owner</c>: process id and start time). Copies are deleted on
/// unload, but a host that dies or is killed (FL closed by the MCP after a render, a crash) never gets
/// there, so construction also sweeps the root: a copy whose owner process is gone is stale and is
/// removed; a copy owned by a live process is never touched (even its not-yet-loaded dependencies);
/// a legacy copy without a marker is removed only when none of its files is locked.
/// </para>
/// </summary>
internal sealed class ShadowCopyStore
{
    /// <summary>Marker file written into every shadow directory before the copy starts.</summary>
    internal const string OwnerMarkerFileName = ".owner";

    private readonly string _shadowRoot;
    private readonly string _pluginsDir;
    private readonly Action<string> _log;

    /// <param name="shadowRoot">Root directory for per-load shadow copies of plugin assemblies.</param>
    /// <param name="pluginsDir">The plugins directory (distinguishes flat vs per-plugin-folder layout).</param>
    /// <param name="log">Diagnostic sink (delete failures are logged, never thrown).</param>
    /// <param name="pruneStale">Sweep stale copies left by dead hosts during construction (default on).</param>
    public ShadowCopyStore(string shadowRoot, string pluginsDir, Action<string> log, bool pruneStale = true)
    {
        _shadowRoot = Path.GetFullPath(shadowRoot);
        _pluginsDir = Path.GetFullPath(pluginsDir);
        _log = log;

        if (PathEquals(_shadowRoot, _pluginsDir) || _shadowRoot.StartsWith(
            Path.TrimEndingDirectorySeparator(_pluginsDir) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The shadow root must be outside the plugins directory.", nameof(shadowRoot));

        // Multiple FL versions/processes can share this root. Deleting it wholesale here would remove
        // dependencies that another live plugin has not loaded yet, even if its main assembly is
        // already locked; the sweep below only removes copies whose owner is provably gone.
        try { Directory.CreateDirectory(_shadowRoot); } catch { /* created lazily on first load */ }
        if (pruneStale) PruneStale();
    }

    /// <summary>The shadow root this store manages.</summary>
    public string ShadowRoot => _shadowRoot;

    public (string dir, string dll) ShadowCopy(string originalDll)
    {
        string loadDir = Path.Combine(_shadowRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(loadDir);

        try
        {
            // The marker goes in first so a concurrently starting host sees a live owner before any
            // assembly lands in the directory.
            WriteOwnerMarker(loadDir);
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

    /// <summary>
    /// Remove every shadow directory whose owner process is gone. Returns the number of directories
    /// removed. Safe to call while other hosts are live: their copies are recognised by the marker and
    /// skipped; marker-less legacy copies are probed for file locks and kept when any file is busy.
    /// </summary>
    public int PruneStale()
    {
        string[] dirs;
        try { dirs = Directory.GetDirectories(_shadowRoot); }
        catch (Exception ex)
        {
            _log($"shadow: could not enumerate '{_shadowRoot}': {ex.Message}");
            return 0;
        }

        int removed = 0;
        long bytes = 0;
        foreach (string dir in dirs)
        {
            if (!IsStale(dir)) continue;
            long size = TryDirectorySize(dir);
            if (TryDeleteDir(dir))
            {
                removed++;
                bytes += size;
            }
        }

        if (removed > 0)
            _log($"shadow: pruned {removed} stale plugin copies ({bytes / (1024 * 1024)} MB) from '{_shadowRoot}'");
        return removed;
    }

    /// <summary>Whether a shadow directory belongs to no live process. Marker-less directories count
    /// as stale only when every file in them can be opened exclusively.</summary>
    internal static bool IsStale(string dir)
    {
        string marker = Path.Combine(dir, OwnerMarkerFileName);
        if (File.Exists(marker))
        {
            if (!TryReadOwner(marker, out int pid, out long startTicks)) return false; // unreadable: be conservative
            return !IsProcessAlive(pid, startTicks);
        }
        return !AnyFileLocked(dir);
    }

    private static void WriteOwnerMarker(string dir)
    {
        using Process me = Process.GetCurrentProcess();
        long startTicks;
        try { startTicks = me.StartTime.ToUniversalTime().Ticks; }
        catch { startTicks = 0; }
        File.WriteAllText(Path.Combine(dir, OwnerMarkerFileName),
            string.Create(CultureInfo.InvariantCulture, $"{me.Id}\n{startTicks}\n"));
    }

    /// <summary>Write a marker naming an arbitrary owner (tests simulate dead or foreign hosts).</summary>
    internal static void WriteOwnerMarker(string dir, int pid, long startTicks)
        => File.WriteAllText(Path.Combine(dir, OwnerMarkerFileName),
            string.Create(CultureInfo.InvariantCulture, $"{pid}\n{startTicks}\n"));

    private static bool TryReadOwner(string marker, out int pid, out long startTicks)
    {
        pid = 0;
        startTicks = 0;
        try
        {
            string[] lines = File.ReadAllLines(marker);
            if (lines.Length == 0 || !int.TryParse(lines[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out pid)) return false;
            if (lines.Length > 1) _ = long.TryParse(lines[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out startTicks);
            return true;
        }
        catch { return false; }
    }

    private static bool IsProcessAlive(int pid, long startTicks)
    {
        Process? p = null;
        try
        {
            p = Process.GetProcessById(pid);
            if (p.HasExited) return false;
            if (startTicks == 0) return true; // owner could not record its start time
            long actual;
            try { actual = p.StartTime.ToUniversalTime().Ticks; }
            catch { return true; } // access denied (another account's host): assume live
            // A reused pid belongs to a different process; allow a second of clock jitter.
            return Math.Abs(actual - startTicks) < TimeSpan.TicksPerSecond;
        }
        catch (ArgumentException) { return false; } // no such process
        catch (InvalidOperationException) { return false; }
        catch { return true; }
        finally { p?.Dispose(); }
    }

    private static bool AnyFileLocked(string dir)
    {
        try
        {
            foreach (string file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                try
                {
                    using var _ = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None);
                }
                catch (IOException) { return true; }
                catch (UnauthorizedAccessException) { return true; }
            }
            return false;
        }
        catch { return true; }
    }

    private static long TryDirectorySize(string dir)
    {
        try
        {
            long total = 0;
            foreach (string file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                total += new FileInfo(file).Length;
            return total;
        }
        catch { return 0; }
    }

    private static void CopyDirRecursive(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (string f in Directory.EnumerateFiles(src))
            File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), overwrite: true);
        foreach (string d in Directory.EnumerateDirectories(src))
            CopyDirRecursive(d, Path.Combine(dst, Path.GetFileName(d)));
    }

    public bool TryDeleteDir(string dir)
    {
        try
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            return true;
        }
        catch (Exception ex)
        {
            _log($"shadow: could not delete '{dir}': {ex.Message} (may be cleaned after all hosts exit)");
            return false;
        }
    }

    /// <summary>Full-path, case-insensitive path equality (falls back to ordinal-ignore-case on bad paths).</summary>
    public static bool PathEquals(string a, string b)
    {
        try { return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
        catch { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }
    }
}
