using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;

namespace FruityLink.Installer.Core;

/// <summary>A process handle kept open from identity verification until confirmed exit.</summary>
internal interface IMcpCompanionProcess : IDisposable
{
    int Id { get; }
    string ExecutablePath { get; }
    bool HasExited { get; }
    void Terminate();
    bool WaitForExit(int milliseconds);
}

/// <summary>Stops only the selected install's server; never terminates any child FL process.</summary>
internal sealed class McpCompanionProcesses
{
    private const int ShutdownPasses = 3;
    private readonly Func<IReadOnlyList<IMcpCompanionProcess>> _enumerate;
    private readonly TimeSpan _exitTimeout;
    private readonly int _settleMilliseconds;

    internal McpCompanionProcesses(
        Func<IReadOnlyList<IMcpCompanionProcess>>? enumerate = null,
        TimeSpan? exitTimeout = null,
        int settleMilliseconds = 200)
    {
        _enumerate = enumerate ?? Enumerate;
        _exitTimeout = exitTimeout ?? TimeSpan.FromSeconds(15);
        _settleMilliseconds = Math.Max(0, settleMilliseconds);
    }

    internal bool Close(string flPath, IProgressLog log)
    {
        try
        {
            var executable = Path.GetFullPath(Path.Combine(flPath,
                "FruityLink", "tools", "fl-mcp", "server", "FlMcp.Server.exe"));
            var stopwatch = Stopwatch.StartNew();
            for (var pass = 0; pass <= ShutdownPasses; pass++)
            {
                var result = ClosePass(executable, log, stopwatch, pass < ShutdownPasses);
                if (result == PassResult.Clear) return true;
                if (result == PassResult.Failed) return false;
                Thread.Sleep(_settleMilliseconds);
            }
            return false;
        }
        catch (Exception ex)
        {
            log.Error($"Could not verify MCP companion shutdown: {ex.Message}");
            return false;
        }
    }

    private PassResult ClosePass(string executable, IProgressLog log, Stopwatch stopwatch, bool mayTerminate)
    {
        var processes = _enumerate();
        var result = PassResult.Clear;
        try
        {
            foreach (var process in processes)
            {
                if (process.HasExited) continue;
                if (!string.Equals(Path.GetFullPath(process.ExecutablePath), executable,
                    StringComparison.OrdinalIgnoreCase)) continue;
                if (!mayTerminate || !CloseOne(process, log, stopwatch))
                {
                    log.Error("The selected installation's MCP server is still running or keeps restarting. "
                        + "Close its AI clients and retry the update.");
                    return PassResult.Failed;
                }
                result = PassResult.Stopped;
            }
            return result;
        }
        finally
        {
            foreach (var process in processes) process.Dispose();
        }
    }

    private bool CloseOne(IMcpCompanionProcess process, IProgressLog log, Stopwatch stopwatch)
    {
        try
        {
            if (process.HasExited) return true;
            log.Info($"Stopping the selected installation's MCP companion (pid {process.Id}) before modifying files...");
            process.Terminate();
            var remaining = Math.Max(0, (_exitTimeout - stopwatch.Elapsed).TotalMilliseconds);
            return process.WaitForExit((int)Math.Min(int.MaxValue, remaining)) && process.HasExited;
        }
        catch (InvalidOperationException) when (process.HasExited)
        {
            return true; // The process exited between inspection and termination.
        }
    }

    private static IReadOnlyList<IMcpCompanionProcess> Enumerate() =>
        Process.GetProcessesByName("FlMcp.Server").Select(process =>
            (IMcpCompanionProcess)new CompanionProcess(process)).ToArray();

    private enum PassResult { Clear, Stopped, Failed }

    private sealed class CompanionProcess(Process process) : IMcpCompanionProcess
    {
        public int Id => process.Id;
        public string ExecutablePath
        {
            get
            {
                // Keep this process object alive by handle so a reused PID cannot redirect Kill.
                _ = process.SafeHandle;
                return process.MainModule?.FileName
                    ?? throw new InvalidOperationException($"Cannot identify MCP process {process.Id}.");
            }
        }
        public bool HasExited => process.HasExited;
        public void Terminate() => process.Kill(entireProcessTree: false);
        public bool WaitForExit(int milliseconds) => process.WaitForExit(milliseconds);
        public void Dispose() => process.Dispose();
    }
}
