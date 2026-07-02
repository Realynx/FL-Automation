using System.Diagnostics;

namespace FruityLink.Plugins.Host;

/// <summary>
/// Watches the plugins directory and feeds debounced, file-stable change batches to the
/// <see cref="PluginManager"/> for live reload. Editors and build tools emit a storm of events and
/// briefly lock the output dll mid-write, so events are coalesced over a short quiet window and each
/// changed dll is confirmed unlocked + size-stable before the reload fires (never mid-write).
/// </summary>
internal sealed class PluginHotReloader : IDisposable
{
    private const int DebounceMs = 600;       // quiet window after the last event before processing
    private const int StableWaitMaxMs = 8000; // give up waiting for an unlocked/stable file after this
    private const int PollMs = 150;

    private readonly FileSystemWatcher _watcher;
    private readonly Func<IReadOnlyCollection<string>, bool, Task> _onBatch;
    private readonly Action<string> _log;
    private readonly Timer _debounce;

    private readonly object _lock = new();
    private readonly HashSet<string> _pendingDlls = new(StringComparer.OrdinalIgnoreCase);
    private bool _structureChanged;
    private volatile bool _disposed;

    /// <param name="dir">Plugins directory to watch (created if missing).</param>
    /// <param name="onBatch">Invoked with (changed dll paths, structureChanged) after debounce.</param>
    /// <param name="log">Diagnostic sink.</param>
    public PluginHotReloader(string dir, Func<IReadOnlyCollection<string>, bool, Task> onBatch, Action<string> log)
    {
        _onBatch = onBatch;
        _log = log;
        Directory.CreateDirectory(dir);

        _watcher = new FileSystemWatcher(dir)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName
                         | NotifyFilters.LastWrite | NotifyFilters.Size,
        };
        _watcher.Changed += OnChanged;
        _watcher.Created += OnChanged;
        _watcher.Deleted += OnChanged;
        _watcher.Renamed += OnRenamed;
        _watcher.Error += (_, e) => _log("hot-reload: watcher error: " + e.GetException().Message);

        _debounce = new Timer(Fire, null, Timeout.Infinite, Timeout.Infinite);
        _watcher.EnableRaisingEvents = true;
        _log($"hot-reload: watching '{dir}'");
    }

    private void OnChanged(object sender, FileSystemEventArgs e) => Enqueue(e.FullPath);

    private void OnRenamed(object sender, RenamedEventArgs e)
    {
        Enqueue(e.OldFullPath);
        Enqueue(e.FullPath);
    }

    private void Enqueue(string path)
    {
        if (_disposed) return;
        lock (_lock)
        {
            if (path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                _pendingDlls.Add(path);
            else
                _structureChanged = true; // folder add/remove/rename, deps.json, etc.
        }
        try { _debounce.Change(DebounceMs, Timeout.Infinite); }
        catch (ObjectDisposedException) { /* disposed mid-event */ }
    }

    private void Fire(object? _)
    {
        if (_disposed) return;
        string[] dlls;
        bool structure;
        lock (_lock)
        {
            dlls = _pendingDlls.ToArray();
            _pendingDlls.Clear();
            structure = _structureChanged;
            _structureChanged = false;
        }
        if (dlls.Length == 0 && !structure) return;
        _ = ProcessAsync(dlls, structure);
    }

    private async Task ProcessAsync(string[] dlls, bool structure)
    {
        try
        {
            foreach (string d in dlls)
                if (File.Exists(d)) WaitUntilStable(d);
            await _onBatch(dlls, structure).ConfigureAwait(false);
        }
        catch (Exception ex) { _log("hot-reload: batch failed: " + ex.Message); }
    }

    // Block until the file opens (no writer lock) and its size is stable across two polls, or timeout.
    private void WaitUntilStable(string path)
    {
        var sw = Stopwatch.StartNew();
        long lastLen = -1;
        int stableHits = 0;
        while (sw.ElapsedMilliseconds < StableWaitMaxMs)
        {
            try
            {
                long len = new FileInfo(path).Length;
                using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read)) { /* opened => no exclusive writer */ }
                if (len == lastLen) { if (++stableHits >= 2) return; }
                else { stableHits = 0; lastLen = len; }
            }
            catch { stableHits = 0; /* still being written/locked */ }
            Thread.Sleep(PollMs);
        }
        _log($"hot-reload: '{Path.GetFileName(path)}' still busy after {StableWaitMaxMs}ms; proceeding anyway");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _watcher.EnableRaisingEvents = false; } catch { /* ignore */ }
        try { _watcher.Dispose(); } catch { /* ignore */ }
        try { _debounce.Dispose(); } catch { /* ignore */ }
    }
}
