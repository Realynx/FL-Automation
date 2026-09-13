using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Text.Json;
using FruityLink.Core.Abstractions;

namespace FruityLink.FlStudio.Inject;

// Low-level transport: the named pipe / in-process transport, the pipe gate, op logging.
// Partial of the FlInjectBridge god-class split; see FlInjectBridge.cs for the class doc.
public sealed partial class FlInjectBridge
{
    // ---- low-level transport -------------------------------------------------

    // Serializes all bridge I/O. The bridge runs each command on FL's main (UI) thread and serves
    // one caller at a time, so concurrent callers — e.g. parallel sub-agents — must not collide on it.
    private static readonly SemaphoreSlim _pipeGate = new(1, 1);

    // Serializes multi-step ops that BUILD a struct in the shared scratch buffer before a native call
    // (e.g. clip insert). _pipeGate only serializes a SINGLE message; without this a second scratch-
    // building op (likely under parallel sub-agents) can clobber the first's half-built struct between
    // its pokes and the insert — a corruption class that contributed to the clip-insert crash. Distinct
    // from _pipeGate (each RawAsync still takes that) so leasing a whole sequence never self-deadlocks.
    private static readonly SemaphoreSlim _scratchGate = new(1, 1);

    // ---- op logging (append-only; best-effort — never throws, never blocks the op) -----------------
    // Every MUTATING clip/arrangement/pattern/transport op appends ONE high-level line here so a
    // "playhead stuck / song won't play after the AI arranged" report (or any arrange-broke-playback) is
    // ALWAYS diagnosable after the fact — you can read back the exact sequence of ops the AI ran. Lives
    // under the SAME base dir StoragePaths uses (%APPDATA%\FLAutomate) → \logs\fl-ops-<yyyyMMdd>.log. Raw
    // peek/poke/call are deliberately NOT logged (too noisy + not the intent); only these typed ops are.
    private static readonly object _opLogGate = new();

    private static void LogOp(string method, string args = "")
    {
        try
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FLAutomate", "logs");
            string line = $"{DateTime.Now:HH:mm:ss.fff} {method}({args})" + Environment.NewLine;
            lock (_opLogGate)
            {
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, $"fl-ops-{DateTime.Now:yyyyMMdd}.log"), line);
            }
        }
        catch { /* logging must never fail or slow an op */ }
    }

    /// <summary>
    /// Pluggable command transport. When null (default), commands go over the named pipe to the
    /// injected bridge. When the bridge is hosted IN-PROCESS (the version.dll proxy → CLR-host
    /// install), <see cref="UseInProcessTransport"/> routes commands through a direct P/Invoke of
    /// the bridge's <c>FlBridge_Command</c> export — same protocol, same process, no named pipe.
    /// Static so it applies to every <see cref="FlInjectBridge"/> instance the DI container makes.
    /// </summary>
    public static Func<string, int, CancellationToken, Task<string>>? Transport { get; set; }

    /// <summary>Route all bridge commands through the in-process P/Invoke transport (no named pipe).
    /// Called by FruityLink.Host once it is running inside FL Studio's process.</summary>
    public static void UseInProcessTransport() => Transport = InProcBridge.RawAsync;

    /// <summary>Send a raw bridge command and return its UTF-8 response. INTERNAL (capability lockdown,
    /// re/17 #50): raw command/memory access is not part of the plugin-facing surface — third-party
    /// plugins receive only the typed <see cref="INativeFlControl"/>, so they cannot reach poke/call by
    /// casting to this concrete type. Exposed to the in-proc host via InternalsVisibleTo.</summary>
    internal async Task<string> RawAsync(string message, int timeoutMs = 4000, CancellationToken ct = default)
    {
        await _pipeGate.WaitAsync(ct).ConfigureAwait(false);
        var completion = InProcBridge.RequiresNativeCompletion ? new NativeCallCompletion() : null;
        try
        {
            ScratchLease? scratch = Volatile.Read(ref _activeScratchLease);
            using var observation = InProcBridge.ObserveNativeWork(work =>
            {
                scratch?.TrackNativeWork(work);
                completion?.Track(work);
            });
            // In-process transport (proxy/CLR-host install): bypass the named pipe entirely.
            var transport = Transport;
            if (transport is not null)
                return await transport(message, timeoutMs, ct).ConfigureAwait(false);

            // Bound the ENTIRE pipe exchange (connect + write + read) by timeoutMs, not just the
            // connect. The bridge runs each command on FL's main thread; if that thread is wedged
            // (a modal dialog, or an FL-internal repaint/message storm) the response never comes and
            // the read would otherwise block until ct — hanging the agent turn. A linked CTS turns
            // "no response in timeoutMs" into a bounded TimeoutException so the tool fails fast.
            using var timeoutCts = timeoutMs > 0 ? CancellationTokenSource.CreateLinkedTokenSource(ct) : null;
            timeoutCts?.CancelAfter(timeoutMs);
            var io = timeoutCts?.Token ?? ct;
            try
            {
                await using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                await pipe.ConnectAsync(timeoutMs > 0 ? timeoutMs : Timeout.Infinite, io);
                pipe.ReadMode = PipeTransmissionMode.Message;
                byte[] outb = Encoding.UTF8.GetBytes(message);
                await pipe.WriteAsync(outb, io);
                await pipe.FlushAsync(io);

                using var response = new MemoryStream();
                var buf = new byte[16384];
                do
                {
                    int n = await pipe.ReadAsync(buf, io);
                    if (n == 0) throw new EndOfStreamException("The native bridge closed before completing its response.");
                    response.Write(buf, 0, n);
                } while (!pipe.IsMessageComplete);
                return Encoding.UTF8.GetString(response.GetBuffer(), 0, checked((int)response.Length));
            }
            catch (OperationCanceledException) when (timeoutCts is { IsCancellationRequested: true } && !ct.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"FL Studio's native bridge did not respond within {timeoutMs} ms (it may be busy or showing a dialog): {message}");
            }
        }
        finally
        {
            _pipeGate.Release();
            // Keep the caller's operation active until native code returns, without retaining the
            // global I/O gate across SendMessage, which can reenter ordinary host/UI operations.
            if (completion is not null) await completion.DrainAsync().ConfigureAwait(false);
        }
    }

    /// <summary>True if the bridge is injected and its worker is responding.</summary>
    public async Task<bool> IsLoadedAsync(CancellationToken ct = default)
    {
        try { return (await RawAsync("ping", 1200, ct)).Trim() == "pong"; }
        catch { return false; }
    }

    /// <summary>True only when FL has its main window and initialized project objects, not merely a responding bridge.</summary>
    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        try { return (await RawAsync("fl_ready", 1200, ct)).Trim() == "1"; }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return false; }
    }
}
