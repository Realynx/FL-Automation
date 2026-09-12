namespace FruityLink.Installer.Core;

/// <summary>Severity / category of a progress line. The GUI and console color these.</summary>
public enum LogLevel
{
    Info,
    Action,
    Warn,
    Error,
    Success,
}

/// <summary>
/// Sink for human-readable progress. Implemented by the console (CLI), a file logger, and the
/// WPF window. The install engine writes exclusively through this so it has no UI dependency.
/// </summary>
public interface IProgressLog
{
    void Log(LogLevel level, string message);
}

public static class ProgressLogExtensions
{
    public static void Info(this IProgressLog log, string message) => log.Log(LogLevel.Info, message);
    public static void Action(this IProgressLog log, string message) => log.Log(LogLevel.Action, message);
    public static void Warn(this IProgressLog log, string message) => log.Log(LogLevel.Warn, message);
    public static void Error(this IProgressLog log, string message) => log.Log(LogLevel.Error, message);
    public static void Success(this IProgressLog log, string message) => log.Log(LogLevel.Success, message);
}

/// <summary>Fans one log out to several sinks (e.g. console + file, or window + file).</summary>
public sealed class CompositeLog : IProgressLog
{
    private readonly IProgressLog[] _sinks;
    public CompositeLog(params IProgressLog[] sinks) => _sinks = sinks;

    public void Log(LogLevel level, string message)
    {
        foreach (var sink in _sinks)
        {
            try { sink.Log(level, message); }
            catch { /* a failing sink (e.g. closed console) must not break the install */ }
        }
    }
}
