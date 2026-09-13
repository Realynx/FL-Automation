using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("FruityLink.Scripting.Tests")]

namespace FruityLink.Scripting;

/// <summary>Finds the framework's private runtime without initializing Python or consulting a system interpreter.</summary>
public static class EmbeddedPythonRuntimeLocator
{
    private const string WheelName = "fruitylink_python-0.2.0-py3-none-any.whl";

    /// <summary>Returns the process-pinned configuration, including initialization in progress, or null before first use.</summary>
    public static EmbeddedPythonOptions? GetActiveOptions() => EmbeddedPythonEngine.GetActiveOptions();

    /// <summary>Resolves explicit method/environment overrides, an active interpreter, or the installed private runtime.
    /// Host directory defaults to the shared Scripting assembly directory, never a plugin shadow-copy directory.
    /// A complete legacy MCP bundle is preferred before first use so both plugins select the same interpreter.</summary>
    public static EmbeddedPythonOptions Resolve(string? hostDirectory = null, string? runtimeDirectory = null, string? pythonPackagePath = null)
    {
        var host = hostDirectory ?? Path.GetDirectoryName(typeof(EmbeddedPythonRuntimeLocator).Assembly.Location)
            ?? throw new InvalidOperationException("Cannot locate the FruityLink host directory.");
        return ResolveCore(host, GetActiveOptions(), Environment.GetEnvironmentVariable, File.Exists,
            runtimeDirectory, pythonPackagePath);
    }

    internal static EmbeddedPythonOptions ResolveCore(string hostDirectory, EmbeddedPythonOptions? active,
        Func<string, string?> environment, Func<string, bool> fileExists,
        string? runtimeDirectory = null, string? pythonPackagePath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostDirectory);
        if (!Path.IsPathFullyQualified(hostDirectory)) throw new ArgumentException("FruityLink host directory must be absolute.");
        var runtime = runtimeDirectory ?? EnvironmentOverride(environment, "FRUITYLINK_PYTHON_RUNTIME", "FL_MCP_PYTHON_RUNTIME");
        var package = pythonPackagePath ?? EnvironmentOverride(environment, "FRUITYLINK_PYTHON_PATH", "FL_MCP_PYTHON_PATH");
        if (active is not null && runtime is null && package is null) return active;
        var defaults = active ?? InstalledOptions(hostDirectory, fileExists);
        var resolved = new EmbeddedPythonOptions(Absolute(runtime ?? defaults.RuntimeDirectory), Absolute(package ?? defaults.PythonPackagePath));
        if (active is not null && (!SamePath(active.RuntimeDirectory, resolved.RuntimeDirectory) || !SamePath(active.PythonPackagePath, resolved.PythonPackagePath)))
            throw new InvalidOperationException("FL already owns another embedded Python configuration. Remove the conflicting override or restart FL to change it.");
        return active ?? resolved;
    }

    private static EmbeddedPythonOptions InstalledOptions(string hostDirectory, Func<string, bool> fileExists)
    {
        var legacy = OptionsUnder(Path.Combine(hostDirectory, "tools", "fl-mcp", "python"));
        if (fileExists(Path.Combine(legacy.RuntimeDirectory, "python314.dll")) && fileExists(legacy.PythonPackagePath)) return legacy;
        return OptionsUnder(Path.Combine(hostDirectory, "python"));
    }

    private static EmbeddedPythonOptions OptionsUnder(string directory) => new(Path.Combine(directory, "runtime"), Path.Combine(directory, WheelName));
    private static string? EnvironmentOverride(Func<string, string?> environment, string current, string legacy)
    {
        var value = environment(current);
        if (string.IsNullOrWhiteSpace(value)) value = environment(legacy);
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
    private static string Absolute(string path) => Path.IsPathFullyQualified(path)
        ? Path.GetFullPath(path) : throw new ArgumentException("Embedded Python runtime and package locations must be absolute.");
    private static bool SamePath(string first, string second) => string.Equals(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(first)), Path.TrimEndingDirectorySeparator(second), StringComparison.OrdinalIgnoreCase);
}
