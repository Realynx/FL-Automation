using System.Diagnostics;

namespace FruityLink.Installer.Core.Mcp;

internal sealed record McpLaunchSettings(string FlExecutable, string ServerExecutable, IReadOnlyDictionary<string, string> Environment)
{
    public static McpLaunchSettings Create(string flPath, McpSetupOptions options)
    {
        if (!Path.IsPathFullyQualified(flPath)) throw new ArgumentException("FL installation path must be absolute.");
        var fl = Path.GetFullPath(flPath);
        var companion = Path.Combine(fl, "FruityLink", "tools", "fl-mcp");
        var runtime = options.PythonRuntimeDirectory ?? Path.Combine(companion, "python", "runtime");
        var template = options.TemplatePath ?? Path.Combine(fl, "Data", "Templates", "Empty", "Empty.flp");
        var workspace = options.WorkspacePath ?? Path.Combine(options.UserPaths.LocalAppData, "FlMcp", "Projects");
        foreach (var path in new[] { runtime, template, workspace })
            if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("Python runtime, template, and workspace paths must be absolute.");
        return new(Path.Combine(fl, "FL64.exe"), Path.Combine(companion, "server", "FlMcp.Server.exe"), new Dictionary<string, string>
        {
            ["FL_MCP_FL_EXE"] = Path.Combine(fl, "FL64.exe"),
            ["FL_MCP_TEMPLATE"] = Path.GetFullPath(template),
            ["FL_MCP_WORKSPACE"] = Path.GetFullPath(workspace),
            ["FL_MCP_PYTHON_RUNTIME"] = Path.GetFullPath(runtime),
            ["FL_MCP_PYTHON_PATH"] = Path.Combine(companion, "python", "fruitylink_python-0.2.0-py3-none-any.whl")
        });
    }

    public void Preflight()
    {
        var runtime = Environment["FL_MCP_PYTHON_RUNTIME"];
        var python = Path.Combine(runtime, "python.exe");
        foreach (var path in new[] { FlExecutable, ServerExecutable, Environment["FL_MCP_TEMPLATE"], python,
            Path.Combine(runtime, "python314.dll"), Path.Combine(runtime, "python314.zip"), Environment["FL_MCP_PYTHON_PATH"] })
            if (!File.Exists(path)) throw new FileNotFoundException("Required installed MCP file is missing. Install FL MCP before configuring clients.", path);
        // Installation import check only. User scripts later run in the embedded FL interpreter.
        var info = new ProcessStartInfo(python)
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in new[] { "-B", "-c", "import ctypes, sys; sys.path.insert(0, sys.argv[1]); import fruitylink.embedding; sys.exit(0 if sys.version_info[:3] == (3, 14, 6) and ctypes.sizeof(ctypes.c_void_p) == 8 else 1)", Environment["FL_MCP_PYTHON_PATH"] })
            info.ArgumentList.Add(argument);
        info.Environment["PYTHONDONTWRITEBYTECODE"] = "1";
        using var process = Process.Start(info) ?? throw new IOException("Cannot start the installed Python interpreter.");
        _ = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null);
        _ = process.StandardError.BaseStream.CopyToAsync(Stream.Null);
        if (!process.WaitForExit(10000))
        {
            process.Kill(entireProcessTree: true);
            throw new IOException("Installed Python preflight timed out.");
        }
        if (process.ExitCode != 0) throw new IOException("Installed Python could not import the embedded FruityLink SDK or does not match CPython 3.14.6 x64.");
    }

    internal bool Matches(string? command, string? flExecutable) =>
        SamePath(command, ServerExecutable) && SamePath(flExecutable, FlExecutable);

    private static bool SamePath(string? first, string second)
    {
        try { return first is not null && Path.IsPathFullyQualified(first) && string.Equals(Path.GetFullPath(first), second, StringComparison.OrdinalIgnoreCase); }
        catch (ArgumentException) { return false; }
    }
}
