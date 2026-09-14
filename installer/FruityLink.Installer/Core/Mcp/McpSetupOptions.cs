namespace FruityLink.Installer.Core.Mcp;

/// <summary>Capture before elevation and pass through explicitly; never infer the elevated account.</summary>
public sealed record McpUserPaths(string Profile, string RoamingAppData, string LocalAppData, string CodexHome)
{
    public static McpUserPaths Capture()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return new(profile, Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetEnvironmentVariable("CODEX_HOME") is { Length: > 0 } home ? Path.GetFullPath(home) : Path.Combine(profile, ".codex"));
    }
}

public sealed record McpClientTarget(string Id, string Name, string ConfigPath, bool Detected);
public sealed record McpSetupOptions(IReadOnlyList<string> ClientIds, McpUserPaths UserPaths,
    string? PythonRuntimeDirectory = null, string? TemplatePath = null, string? WorkspacePath = null);

public sealed class McpSetupResult
{
    public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Details { get; init; } = Array.Empty<string>();
    public bool Success => Errors.Count == 0;
}
