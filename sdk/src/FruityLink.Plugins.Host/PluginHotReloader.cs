using System.Diagnostics;

namespace FruityLink.Plugins.Host;

/// <summary>Coalesces package changes and reloads only after changed files stop being written.</summary>
internal sealed class PluginHotReloader : IDisposable
{
    private readonly FileSystemWatcher _watcher;
    private readonly Func<IReadOnlyCollection<string>, bool, Task> _onBatch;
    private readonly Action<string> _log;
    private readonly Timer _debounce;
    private readonly int _debounceMs;
    private readonly int _stableWaitMaxMs;
    private readonly int _pollMs;
    private readonly SemaphoreSlim _processing = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _sync = new();
    private readonly HashSet<string> _pendingPaths = new(StringComparer.OrdinalIgnoreCase);
    private bool _structureChanged;
    private volatile bool _disposed;

    public PluginHotReloader(string dir, Func<IReadOnlyCollection<string>, bool, Task> onBatch, Action<string> log,
        int debounceMs = 600, int stableWaitMaxMs = 8000, int pollMs = 150)
    {
        _onBatch = onBatch;
        _log = log;
        _debounceMs = debounceMs;
        _stableWaitMaxMs = stableWaitMaxMs;
        _pollMs = pollMs;
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
        _watcher.Error += (_, e) =>
        {
            _log("hot-reload: watcher error: " + e.GetException().Message);
            Enqueue(dir); // events may have been lost; reconcile the whole root
        };
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
        lock (_sync)
        {
            if (_disposed) return;
            _pendingPaths.Add(path);
            if (!path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) _structureChanged = true;
            try { _debounce.Change(_debounceMs, Timeout.Infinite); }
            catch (ObjectDisposedException) { /* shutdown raced a file event */ }
        }
    }

    private void Fire(object? state)
    {
        string[] paths;
        bool structure;
        lock (_sync)
        {
            if (_disposed) return;
            paths = _pendingPaths.ToArray();
            _pendingPaths.Clear();
            structure = _structureChanged;
            _structureChanged = false;
        }
        if (paths.Length > 0) _ = ProcessAsync(paths, structure);
    }

    private async Task ProcessAsync(string[] paths, bool structure)
    {
        try
        {
            await _processing.WaitAsync(_shutdown.Token).ConfigureAwait(false);
            try { await ProcessStableBatchAsync(paths, structure).ConfigureAwait(false); }
            finally { _processing.Release(); }
        }
        catch (OperationCanceledException) when (_disposed) { /* shutdown */ }
        catch (Exception ex) { _log("hot-reload: batch failed: " + ex.Message); }
    }

    private async Task ProcessStableBatchAsync(string[] paths, bool structure)
    {
        foreach (string path in paths)
        {
            if (await WaitUntilStableAsync(path, _shutdown.Token).ConfigureAwait(false)) continue;
            // Retain the complete batch until a long rebuild finishes, even if no new event arrives.
            foreach (string retry in paths) Enqueue(retry);
            return;
        }
        if (!_disposed) await _onBatch(paths, structure).ConfigureAwait(false);
    }

    private async Task<bool> WaitUntilStableAsync(string path, CancellationToken ct)
    {
        var elapsed = Stopwatch.StartNew();
        (long Length, DateTime LastWrite)? previous = null;
        int stableHits = 0;
        while (elapsed.ElapsedMilliseconds < _stableWaitMaxMs)
        {
            ct.ThrowIfCancellationRequested();
            if (!File.Exists(path)) return true; // deletion or directory event
            var current = TryReadStamp(path);
            stableHits = current is not null && current == previous ? stableHits + 1 : 0;
            if (stableHits >= 2) return true;
            previous = current;
            await Task.Delay(_pollMs, ct).ConfigureAwait(false);
        }
        _log($"hot-reload: '{Path.GetFileName(path)}' still busy after {_stableWaitMaxMs}ms; retrying later");
        return false;
    }

    private static (long Length, DateTime LastWrite)? TryReadStamp(string path)
    {
        try
        {
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return (stream.Length, File.GetLastWriteTimeUtc(path));
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _pendingPaths.Clear();
        }
        _shutdown.Cancel();
        _watcher.Dispose();
        _debounce.Dispose();
        // In-flight batches still use the cancellation token and release the semaphore.
    }
}
