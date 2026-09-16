namespace FruityLink.Plugins.Host;

/// <summary>
/// The plugin host's diagnostic sink: timestamps each line, mirrors it to the debugger, and appends
/// it to the host log file. Logging must never throw — every failure is swallowed. Handed around as
/// an <see cref="Action{T}"/> (<see cref="Write"/>) to every collaborator and plugin context.
/// </summary>
internal sealed class PluginHostLogger
{
    private readonly string _logFile;

    /// <param name="logFile">File the plugin-host (and plugins) append diagnostic lines to.</param>
    public PluginHostLogger(string logFile) => _logFile = logFile;

    public void Write(string message)
    {
        string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [plugin-host] {message}";
        try { System.Diagnostics.Debug.WriteLine(line); } catch { /* ignore */ }
        try
        {
            string? dir = Path.GetDirectoryName(_logFile);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.AppendAllText(_logFile, line + Environment.NewLine);
        }
        catch { /* logging must never throw */ }
    }
}
