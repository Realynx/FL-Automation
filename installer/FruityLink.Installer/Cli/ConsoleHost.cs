using System;
using System.IO;
using System.Runtime.InteropServices;
using FruityLink.Installer.Core;

namespace FruityLink.Installer.Cli;

/// <summary>
/// Because this is a WinExe (no console, so the GUI never flashes one), headless mode must attach to
/// the parent terminal's console at runtime so its output is visible when launched from PowerShell /
/// cmd. If there is no parent console (e.g. double-clicked with args) and we're not silent, allocate
/// one so the user still sees output.
/// </summary>
public static class ConsoleHost
{
    private const int ATTACH_PARENT_PROCESS = -1;
    private const int STD_OUTPUT_HANDLE = -11;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AllocConsole();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    public static void EnsureConsole(bool silent)
    {
        // If stdout is already a valid handle the caller redirected it (a file or pipe) — leave it
        // alone so the output goes there. AttachConsole would otherwise clobber the redirection.
        var stdout = GetStdHandle(STD_OUTPUT_HANDLE);
        if (stdout != IntPtr.Zero && stdout != new IntPtr(-1))
            return;

        if (AttachConsole(ATTACH_PARENT_PROCESS))
            return;
        if (!silent)
            AllocConsole();
    }
}

/// <summary>Writes progress to the console with light coloring.</summary>
public sealed class ConsoleLog : IProgressLog
{
    public void Log(LogLevel level, string message)
    {
        var prev = Console.ForegroundColor;
        try
        {
            Console.ForegroundColor = level switch
            {
                LogLevel.Error => ConsoleColor.Red,
                LogLevel.Warn => ConsoleColor.Yellow,
                LogLevel.Success => ConsoleColor.Green,
                LogLevel.Action => ConsoleColor.Gray,
                _ => prev,
            };
            Console.WriteLine(message);
        }
        catch
        {
            // No console handle (silent, no parent) — swallow; the file log still captures it.
        }
        finally
        {
            try { Console.ForegroundColor = prev; } catch { /* ignore */ }
        }
    }
}

/// <summary>Appends progress to a rolling log file under %LocalAppData%\FruityLink.</summary>
public sealed class FileLog : IProgressLog, IDisposable
{
    private readonly StreamWriter? _writer;

    public FileLog()
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FruityLink");
            Directory.CreateDirectory(dir);
            FilePath = Path.Combine(dir, "installer-log.txt");
            _writer = new StreamWriter(FilePath, append: true) { AutoFlush = true };
            _writer.WriteLine($"---- {DateTimeOffset.Now:u} ----");
        }
        catch
        {
            _writer = null;
        }
    }

    public string? FilePath { get; }

    public void Log(LogLevel level, string message)
    {
        try { _writer?.WriteLine($"{level,-7} {message}"); }
        catch { /* ignore */ }
    }

    public void Dispose() => _writer?.Dispose();
}
