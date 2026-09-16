using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace FruityLink.Core.Hosting;

/// <summary>An ownership lease for one process tree and its optional private desktop.</summary>
public sealed class FlStudioProcessLease : IDisposable, IAsyncDisposable
{
    private readonly SafeKernelHandle process;
    private readonly SafeJobHandle job;
    private readonly SafeDesktopHandle? desktop;
    private readonly TimeSpan shutdownTimeout;
    private readonly StartupGate? startup;
    private readonly object lifetime = new();
    private int disposed;

    internal FlStudioProcessLease(SafeKernelHandle process, SafeJobHandle job, SafeDesktopHandle? desktop,
        int processId, string? desktopName, FlStudioLaunchMode mode, TimeSpan shutdownTimeout, StartupGate? startup)
    {
        this.process = process; this.job = job; this.desktop = desktop; this.shutdownTimeout = shutdownTimeout;
        this.startup = startup;
        ProcessId = processId; DesktopName = desktopName; Mode = mode;
    }

    /// <summary>Operating-system process identifier of the launched root process.</summary>
    public int ProcessId { get; }
    /// <summary>Desktop isolation mode used for the launch.</summary>
    public FlStudioLaunchMode Mode { get; }
    /// <summary>Private desktop name, or null for an interactive launch.</summary>
    public string? DesktopName { get; }
    /// <summary>Whether this launch still owns the cross-process cold-start gate.</summary>
    public bool StartupGateHeld => startup?.Held == true;
    /// <summary>Reports native readiness and releases the cold-start gate. Repeated calls are harmless.</summary>
    public void CompleteStartup() => startup?.SignalReady();
    internal bool RootHasExited => Native.WaitForSingleObject(process, 0) == Native.WaitObject0;
    /// <summary>Whether the launched root process has exited.</summary>
    public bool HasExited
    {
        get
        {
            lock (lifetime)
            {
                ThrowIfDisposed();
                var result = Native.WaitForSingleObject(process, 0);
                if (result == Native.WaitFailed) throw new Win32Exception(Marshal.GetLastWin32Error(), "WaitForSingleObject failed.");
                return result == Native.WaitObject0;
            }
        }
    }
    /// <summary>Exit code when the root process has exited; otherwise null.</summary>
    public int? ExitCode
    {
        get
        {
            lock (lifetime)
            {
                ThrowIfDisposed();
                if (!HasExited) return null;
                if (!Native.GetExitCodeProcess(process, out var code))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "GetExitCodeProcess failed.");
                return unchecked((int)code);
            }
        }
    }

    /// <summary>Waits for the launched root process to exit.</summary>
    public async Task WaitForExitAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        while (!HasExited)
            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads top-level windows belonging to the launched root process from its assigned desktop.</summary>
    public IReadOnlyList<FlStudioWindowSnapshot> ReadWindows(CancellationToken cancellationToken = default)
    {
        lock (lifetime)
        {
            ThrowIfDisposed();
            var result = new List<FlStudioWindowSnapshot>();
            var cancelled = false;
            Native.EnumWindowsProc callback = (window, _) =>
            {
                if (cancellationToken.IsCancellationRequested) { cancelled = true; return false; }
                Native.GetWindowThreadProcessId(window, out var pid);
                if (pid != ProcessId || result.Count >= 128) return result.Count < 128;
                var owner = Native.GetWindow(window, 4);
                result.Add(new(window, ProcessId, ReadClass(window), ReadTitle(window),
                    Native.IsWindowVisible(window), Native.IsWindowEnabled(window), owner,
                    owner == 0 || Native.IsWindowEnabled(owner)));
                return true;
            };
            var enumerated = desktop is null ? Native.EnumWindows(callback, 0) : Native.EnumDesktopWindows(desktop, callback, 0);
            if (cancelled) throw new OperationCanceledException(cancellationToken);
            var enumerationError = Marshal.GetLastWin32Error();
            if (!enumerated && result.Count < 128 && enumerationError != 0)
                throw new Win32Exception(enumerationError, "Window enumeration failed.");
            return result;
        }
    }

    /// <summary>Terminates only the process tree owned by this lease and waits for the root process to exit.</summary>
    public async Task TerminateAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (HasExited) return;
        if (!Native.TerminateJobObject(job, 1)) throw new Win32Exception(Marshal.GetLastWin32Error(), "TerminateJobObject failed.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(shutdownTimeout);
        try { await WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new TimeoutException($"The owned process did not stop within {shutdownTimeout}."); }
    }

    /// <summary>Ends the lease, terminating its owned job if it is still running.</summary>
    public void Dispose()
    {
        lock (lifetime)
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            startup?.SignalReady();
            job.Dispose(); // KILL_ON_JOB_CLOSE affects only this lease's assigned tree.
            Native.WaitForSingleObject(process, (uint)Math.Min(shutdownTimeout.TotalMilliseconds, uint.MaxValue));
            startup?.WaitReleased(); // Its broker may still be reading the root process handle.
            process.Dispose();
            desktop?.Dispose();
            startup?.Dispose();
            GC.SuppressFinalize(this);
        }
    }

    /// <summary>Ends the lease, terminating its owned job if it is still running.</summary>
    public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(disposed != 0, this);

    private static string ReadClass(nint window)
    { var buffer = new StringBuilder(256); Native.GetClassNameW(window, buffer, buffer.Capacity); return buffer.ToString(); }
    private static string ReadTitle(nint window)
    { var buffer = new StringBuilder(2048); return Native.SendMessageTimeoutW(window, 0x000D, (nuint)buffer.Capacity, buffer, 0x0002, 50, out _) == 0 ? "" : buffer.ToString(); }

    private static class Native
    {
        internal const uint WaitObject0 = 0, WaitFailed = uint.MaxValue;
        internal delegate bool EnumWindowsProc(nint window, nint parameter);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern uint WaitForSingleObject(SafeKernelHandle handle, uint milliseconds);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetExitCodeProcess(SafeKernelHandle process, out uint exitCode);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool TerminateJobObject(SafeJobHandle job, uint exitCode);
        [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool EnumWindows(EnumWindowsProc callback, nint parameter);
        [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool EnumDesktopWindows(SafeDesktopHandle desktop, EnumWindowsProc callback, nint parameter);
        [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(nint window, out int processId);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsWindowVisible(nint window);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsWindowEnabled(nint window);
        [DllImport("user32.dll")] internal static extern nint GetWindow(nint window, uint command);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetClassNameW(nint window, StringBuilder value, int maximum);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern nint SendMessageTimeoutW(nint window, uint message, nuint wParam, StringBuilder value, uint flags, uint timeout, out nuint result);
    }
}
