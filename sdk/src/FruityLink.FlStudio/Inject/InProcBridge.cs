using System.Runtime.InteropServices;
using System.Text;

namespace FruityLink.FlStudio.Inject;

/// <summary>
/// In-process transport for the native bridge. Used when <c>FlBridge.dll</c> is loaded INSIDE
/// FL Studio's process by the version.dll proxy → CLR-host chain (instead of being injected and
/// driven over a named pipe). Calls the bridge's owned-response export directly via
/// P/Invoke — identical string-in / string-out protocol as the pipe, so every typed control op in
/// <see cref="FlInjectBridge"/> works unchanged with the pipe removed.
///
/// The bridge still marshals native FL calls onto FL's main thread (SendMessage) internally, so
/// correctness is preserved; these P/Invokes are issued from our own (non-main) UI/worker threads.
/// </summary>
public static class InProcBridge
{
    private static readonly AsyncLocal<Action<Task>?> NativeWorkObserver = new();
    private static readonly AsyncLocal<bool> NativeCompletionRequired = new();

    internal static bool RequiresNativeCompletion => NativeCompletionRequired.Value;

    internal static IDisposable RequireNativeCompletion()
    {
        var scope = new NativeCompletionScope(NativeCompletionRequired.Value);
        NativeCompletionRequired.Value = true;
        return scope;
    }

    private sealed class NativeCompletionScope(bool previous) : IDisposable
    {
        public void Dispose() => NativeCompletionRequired.Value = previous;
    }

    // The caller may finish waiting while native code still holds scratch pointers. Observation
    // flows through a transport delegate without serializing native SendMessage/reentrant calls.
    internal static IDisposable ObserveNativeWork(Action<Task>? observer)
    {
        var scope = new NativeWorkObservation(NativeWorkObserver.Value);
        NativeWorkObserver.Value = observer;
        return scope;
    }

    private sealed class NativeWorkObservation(Action<Task>? previous) : IDisposable
    {
        public void Dispose() => NativeWorkObserver.Value = previous;
    }

    // Legacy export returns the full response length but writes at most outLen bytes. Never retry a
    // command to resize its buffer: commands can mutate state. Returns -1 on a null request.
    [DllImport("FlBridge.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int FlBridge_Command(byte[] req, byte[] outBuf, int outLen);

    [DllImport("FlBridge.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int FlBridge_CommandAlloc(byte[] req, out nint response);

    [DllImport("FlBridge.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern void FlBridge_FreeResponse(nint response);

    /// <summary>Send a raw command to the in-process bridge and return its UTF-8 response.</summary>
    public static string Raw(string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        byte[] req = Encoding.UTF8.GetBytes(message + "\0");
        int length;
        nint response;
        try
        {
            length = FlBridge_CommandAlloc(req, out response);
        }
        catch (EntryPointNotFoundException)
        {
            return RawLegacy(req);
        }
        try
        {
            if (length < 0) return "err:inproc";
            if (length == 0) return string.Empty;
            return response == 0 ? "err:inproc" : Marshal.PtrToStringUTF8(response, length);
        }
        finally { if (response != 0) FlBridge_FreeResponse(response); }
    }

    private static string RawLegacy(byte[] req)
    {
        byte[] buf = new byte[65536];
        int n = FlBridge_Command(req, buf, buf.Length);
        if (n < 0) return "err:inproc";
        // Retrying executes the command again, which can repeat a mutation or drain a queue twice.
        if (n > buf.Length) return "err:response-too-large:update-native-bridge";
        return Encoding.UTF8.GetString(buf, 0, n);
    }

    /// <summary>Transport adapter matching <see cref="FlInjectBridge.Transport"/>. Runs the (potentially
    /// blocking, main-thread-marshalled) native call off the caller's thread, bounded by
    /// <paramref name="timeoutMs"/>.
    ///
    /// <para><see cref="Raw"/> ultimately blocks in a native <c>SendMessage</c> to FL's main (UI)
    /// thread. If that thread is wedged — a modal dialog running a nested message loop, or an
    /// FL-internal repaint/message storm — the <c>SendMessage</c> never returns and the P/Invoke
    /// cannot be interrupted (cancellation tokens can't unblock native code). Previously this method
    /// ignored <paramref name="timeoutMs"/> and awaited that call forever, so a single wedged
    /// FL main thread hung the whole agent turn. Now we run the call detached and race it against the
    /// timeout: on timeout we abandon the (still-blocked) native call and raise a bounded
    /// <see cref="TimeoutException"/>, so the tool fails fast instead of hanging. The orphaned
    /// thread-pool thread unblocks by itself if/when FL recovers. The managed scratch lease observes
    /// that actual completion and keeps its buffer reserved until native code releases its pointers,
    /// even though the caller has already received cancellation or timeout. A caller using
    /// <see cref="FlInjectBridge.RequireNativeCompletion"/> instead retains its managed operation
    /// until that actual native task completes, then receives the timeout/cancellation. This lets
    /// scripting serialize mutations and acknowledge safe shutdown without changing ordinary UI
    /// callers' bounded waits.</para></summary>
    public static Task<string> RawAsync(string message, int timeoutMs, CancellationToken ct)
        => RawAsync(message, timeoutMs, ct, Raw);

    // Inject the blocking native work for tests while retaining the actual dispatch, timeout and
    // cancellation behavior. Production always uses the allocated-response Raw implementation.
    internal static Task<string> RawAsync(string message, int timeoutMs, CancellationToken ct, Func<string, string> nativeCall)
        => RawAsync(message, timeoutMs, ct, nativeCall, UiThreadProbe.Describe);

    /// <summary><paramref name="probe"/> runs only after a timeout and must not touch FL's UI thread; it names
    /// what is visibly blocking it (a plugin's modal sign-in dialog, an FL message box) so the error points at the
    /// plugin instead of the bridge. Live 2026-09-14: an unlicensed Super VHS opened its cloud sign-in dialog on
    /// FL's UI thread and the NEXT request timed out with no hint of the cause.</summary>
    internal static async Task<string> RawAsync(string message, int timeoutMs, CancellationToken ct, Func<string, string> nativeCall,
        Func<string?> probe)
    {
        ct.ThrowIfCancellationRequested();
        var work = Task.Run(() => nativeCall(message), ct);
        NativeWorkObserver.Value?.Invoke(work);
        try
        {
            var timeout = timeoutMs > 0 ? TimeSpan.FromMilliseconds(timeoutMs) : Timeout.InfiniteTimeSpan;
            return await work.WaitAsync(timeout, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException(TimeoutMessage(message, timeoutMs, SafeProbe(probe)));
        }
        finally
        {
            _ = work.ContinueWith(static t => { _ = t.Exception; }, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    /// <summary>The bounded-timeout error text. <paramref name="hint"/> (from <see cref="UiThreadProbe"/>) names the
    /// visible windows on FL's UI thread when it is hung; null means nothing beyond the generic advice is known.</summary>
    internal static string TimeoutMessage(string message, int timeoutMs, string? hint)
    {
        string cause = hint is null
            ? "it may be busy or showing a dialog"
            : $"{hint}; a plugin waiting for a sign-in/licence or message box blocks FL's UI thread until it is dismissed in the GUI, then retry the request and re-inspect the slot it targeted";
        return $"FL Studio did not respond within {timeoutMs} ms ({cause}): {message}";
    }

    private static string? SafeProbe(Func<string?> probe)
    {
        try { return probe(); }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            return null;   // the probe is best-effort diagnostics; never let it mask the timeout
        }
    }
}
