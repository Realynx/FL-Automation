using System;
using System.Diagnostics;
using System.Threading;

namespace FruityLink.Installer.Core;

/// <summary>Outcome of asking FL Studio to close before we touch its files.</summary>
public sealed class FlCloseResult
{
    /// <summary>How many FL Studio processes were found running (and asked to close).</summary>
    public int ClosedCount { get; init; }

    /// <summary>How many of those are confirmed fully exited.</summary>
    public int ExitedCount { get; init; }

    /// <summary>True when every running instance actually exited (so its files are unlocked).</summary>
    public bool AllExited => ExitedCount >= ClosedCount;
}

/// <summary>
/// Seam for closing FL Studio before mutating its directory. <c>version.dll</c> and the in-process
/// FruityLink DLLs are LOADED for the whole FL session, so they stay file-locked until every FL64
/// process exits. Both install and uninstall must close FL first; the seam keeps that out of the
/// unit tests / <c>--self-test</c> (which use <see cref="NoOpProcessManager"/> and never kill a real
/// FL instance on the developer's machine).
/// </summary>
public interface IProcessManager
{
    /// <summary>
    /// Closes ALL running FL Studio instances and waits until they fully exit, then settles briefly so
    /// the OS releases the file locks. Returns how many were running and how many confirmed-exited.
    /// </summary>
    FlCloseResult CloseFlStudio(IProgressLog log);

    /// <summary>Stops MCP companion servers belonging to the selected FL directory only.</summary>
    /// <returns>False when shutdown could not be confirmed; file changes must not begin.</returns>
    bool CloseMcpCompanions(string flPath, IProgressLog log) => true;
}

/// <summary>Does nothing (tests, self-test) — never touches real processes.</summary>
public sealed class NoOpProcessManager : IProcessManager
{
    public FlCloseResult CloseFlStudio(IProgressLog log) => new();
}

/// <summary>
/// Production implementation: enumerates every <c>FL64</c> process, asks it to close, force-kills any
/// survivor, polls until none remain, then waits a short settle delay so loaded DLLs are unlocked.
/// </summary>
public sealed class RealProcessManager : IProcessManager
{
    /// <summary>FL64.exe is the shell that loads our proxy version.dll. Process name has no extension.</summary>
    public const string ProcessName = "FL64";

    private readonly TimeSpan _graceful;
    private readonly TimeSpan _exitTimeout;
    private readonly TimeSpan _settle;

    public RealProcessManager(
        TimeSpan? graceful = null, TimeSpan? exitTimeout = null, TimeSpan? settle = null)
    {
        _graceful = graceful ?? TimeSpan.FromSeconds(5);
        _exitTimeout = exitTimeout ?? TimeSpan.FromSeconds(15);
        _settle = settle ?? TimeSpan.FromMilliseconds(750);
    }

    public bool CloseMcpCompanions(string flPath, IProgressLog log) =>
        new McpCompanionProcesses(exitTimeout: _exitTimeout).Close(flPath, log);

    public FlCloseResult CloseFlStudio(IProgressLog log)
    {
        var procs = Process.GetProcessesByName(ProcessName);
        if (procs.Length == 0)
            return new FlCloseResult();

        try
        {
            log.Info($"Closing {procs.Length} running FL Studio instance(s) before modifying files...");

            // 1. Ask nicely first (lets FL flush state) ...
            foreach (var p in procs)
            {
                try { if (!p.HasExited) p.CloseMainWindow(); }
                catch { /* no window / access denied — fall through to Kill */ }
            }

            // ... and give the graceful close a moment.
            var graceDeadline = DateTime.UtcNow + _graceful;
            foreach (var p in procs)
            {
                try
                {
                    var ms = (int)Math.Max(0, (graceDeadline - DateTime.UtcNow).TotalMilliseconds);
                    if (ms > 0) p.WaitForExit(ms);
                }
                catch { /* ignore */ }
            }

            // 2. Force-kill any survivor (whole tree).
            foreach (var p in procs)
            {
                try
                {
                    if (!p.HasExited)
                    {
                        log.Warn($"  force-closing FL Studio (pid {p.Id})...");
                        p.Kill(entireProcessTree: true);
                    }
                }
                catch (Exception ex) { log.Warn($"  could not terminate pid {p.Id}: {ex.Message}"); }
            }

            // 3. Wait for the killed processes to actually exit.
            var exitDeadline = DateTime.UtcNow + _exitTimeout;
            foreach (var p in procs)
            {
                try
                {
                    var ms = (int)Math.Max(0, (exitDeadline - DateTime.UtcNow).TotalMilliseconds);
                    p.WaitForExit(ms);
                }
                catch { /* ignore */ }
            }
        }
        finally
        {
            foreach (var p in procs)
            {
                try { p.Dispose(); } catch { /* ignore */ }
            }
        }

        // 4. Poll the name list until none remain (covers handles we didn't own) or we time out.
        var pollDeadline = DateTime.UtcNow + _exitTimeout;
        Process[] remaining;
        while ((remaining = Process.GetProcessesByName(ProcessName)).Length > 0
               && DateTime.UtcNow < pollDeadline)
        {
            foreach (var p in remaining) { try { p.Dispose(); } catch { } }
            Thread.Sleep(200);
        }
        foreach (var p in remaining) { try { p.Dispose(); } catch { } }

        if (remaining.Length == 0)
        {
            // 5. Settle: the OS unmaps loaded modules a beat after the process dies.
            Thread.Sleep((int)_settle.TotalMilliseconds);
            log.Success("  all FL Studio instances closed.");
            return new FlCloseResult { ClosedCount = procs.Length, ExitedCount = procs.Length };
        }

        log.Warn($"  {remaining.Length} FL Studio instance(s) still running; some files may stay locked.");
        return new FlCloseResult { ClosedCount = procs.Length, ExitedCount = procs.Length - remaining.Length };
    }
}
