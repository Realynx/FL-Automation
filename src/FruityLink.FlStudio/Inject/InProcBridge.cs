using System.Runtime.InteropServices;
using System.Text;

namespace FruityLink.FlStudio.Inject;

/// <summary>
/// In-process transport for the native bridge. Used when <c>FlBridge.dll</c> is loaded INSIDE
/// FL Studio's process by the version.dll proxy → CLR-host chain (instead of being injected and
/// driven over a named pipe). Calls the bridge's <c>FlBridge_Command</c> export directly via
/// P/Invoke — identical string-in / string-out protocol as the pipe, so every typed control op in
/// <see cref="FlInjectBridge"/> works unchanged with the pipe removed.
///
/// The bridge still marshals native FL calls onto FL's main thread (SendMessage) internally, so
/// correctness is preserved; these P/Invokes are issued from our own (non-main) UI/worker threads.
/// </summary>
public static class InProcBridge
{
    // Returns the full response length (may exceed outLen → we resize and retry); writes up to
    // outLen bytes into outBuf (not null-terminated). Returns -1 on a null request.
    [DllImport("FlBridge.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int FlBridge_Command(byte[] req, byte[] outBuf, int outLen);

    /// <summary>Send a raw command to the in-process bridge and return its UTF-8 response.</summary>
    public static string Raw(string message)
    {
        byte[] req = Encoding.UTF8.GetBytes(message + "\0"); // null-terminated for the C side
        byte[] buf = new byte[65536];
        int n = FlBridge_Command(req, buf, buf.Length);
        if (n < 0) return "err:inproc";
        if (n > buf.Length)
        {
            buf = new byte[n];
            n = FlBridge_Command(req, buf, buf.Length);
            if (n < 0 || n > buf.Length) return "err:inproc";
        }
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
    /// thread-pool thread unblocks by itself if/when FL recovers.</para></summary>
    public static async Task<string> RawAsync(string message, int timeoutMs, CancellationToken ct)
    {
        // Not tied to ct: the native call can't observe cancellation, and we must still observe the
        // task's result/exception even after a timeout to avoid an unobserved-exception finalizer.
        var work = Task.Run(() => Raw(message));

        if (timeoutMs <= 0)
            return await work.ConfigureAwait(false);

        var completed = await Task.WhenAny(work, Task.Delay(timeoutMs, ct)).ConfigureAwait(false);
        if (completed == work)
            return await work.ConfigureAwait(false);

        // Timed out (or the caller cancelled). Observe the abandoned task's eventual exception so it
        // never surfaces as an unobserved-task-exception, then fail fast.
        _ = work.ContinueWith(static t => { _ = t.Exception; },
                              TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
        ct.ThrowIfCancellationRequested();
        throw new TimeoutException(
            $"FL Studio did not respond within {timeoutMs} ms (it may be busy or showing a dialog): {message}");
    }
}
