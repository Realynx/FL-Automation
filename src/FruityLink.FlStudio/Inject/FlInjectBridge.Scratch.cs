namespace FruityLink.FlStudio.Inject;

public sealed partial class FlInjectBridge
{
    // Native g_scratch is 1024 bytes. Reserve its last pointer-sized slot for save out-params so
    // a path and its result can coexist. The old 0x400 offset wrote beyond the native allocation.
    private const int ScratchBufferSize = 1024;
    private const ulong ScratchOutputSlotOffset = ScratchBufferSize - sizeof(ulong);
    private ScratchLease? _activeScratchLease;

    /// <summary>Owns scratch from initialization through native consumption and result reads.
    /// Request the address once: the native scratch command clears the entire buffer on every call.
    /// Lease only leaf operations; callers composing those operations must not acquire another lease.</summary>
    private async Task<ScratchLease> LeaseScratchAsync(CancellationToken ct)
    {
        ScratchLease lease = await AcquireScratchLeaseAsync(ct).ConfigureAwait(false);
        try
        {
            lease.Address = await ScratchAsync(ct).ConfigureAwait(false);
            return lease;
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    // Also used by clip collection mutations that share the gate but do not initialize scratch.
    private async Task<ScratchLease> AcquireScratchLeaseAsync(CancellationToken ct)
    {
        if (!await _scratchGate.WaitAsync(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false))
            throw new TimeoutException("FL Studio's scratch buffer is still in use by another native operation.");
        var lease = new ScratchLease(this);
        Volatile.Write(ref _activeScratchLease, lease);
        return lease;
    }

    private sealed class ScratchLease(FlInjectBridge owner) : IDisposable
    {
        private readonly object _sync = new();
        private readonly List<Task> _nativeWork = new();
        private bool _disposed;
        public ulong Address { get; set; }

        public void TrackNativeWork(Task work)
        {
            lock (_sync)
                if (!_disposed) _nativeWork.Add(work);
        }

        public void Dispose()
        {
            Task[] pending;
            lock (_sync)
            {
                if (_disposed) return;
                _disposed = true;
                pending = _nativeWork.Where(work => !work.IsCompleted).ToArray();
            }
            Interlocked.CompareExchange(ref owner._activeScratchLease, null, this);
            if (pending.Length == 0) _scratchGate.Release();
            else
            {
                // Cancellation/timeout bounds the caller's wait, not the native pointer lifetime.
                // Release only after native work actually finishes; never block FL's UI thread.
                _ = Task.WhenAll(pending).ContinueWith(static completed =>
                {
                    _ = completed.Exception;
                    _scratchGate.Release();
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
    }
}
