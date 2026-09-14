namespace FruityLink.Installer.Core.Mcp;

internal static class McpClientDiscovery
{
    public static IReadOnlyList<McpClientTarget> Discover(McpUserPaths paths)
    {
        Validate(paths);
        var targets = new List<McpClientTarget>
        {
            Target("codex", "Codex", Path.Combine(paths.CodexHome, "config.toml")),
            Target("claude-desktop", "Claude Desktop", Path.Combine(paths.RoamingAppData, "Claude", "claude_desktop_config.json")),
            Target("claude-code", "Claude Code", Path.Combine(paths.Profile, ".claude.json"), Path.Combine(paths.Profile, ".claude")),
            Target("cursor", "Cursor", Path.Combine(paths.Profile, ".cursor", "mcp.json")),
            Target("vscode", "VS Code (default profile)", Path.Combine(paths.RoamingAppData, "Code", "User", "mcp.json")),
            Target("gemini-cli", "Gemini CLI", Path.Combine(paths.Profile, ".gemini", "settings.json")),
            Target("windsurf", "Windsurf", Path.Combine(paths.Profile, ".codeium", "windsurf", "mcp_config.json")),
            Target("opencode", "OpenCode", OpenCodePath(paths)),
            new("generic-json", "Other client (export stdio JSON)", Path.Combine(paths.LocalAppData, "FlMcp", "mcp-settings.json"), false)
        };
        AddPackagedClaude(paths, targets);
        return targets;
    }

    internal static void Validate(McpUserPaths paths)
    {
        foreach (var path in new[] { paths.Profile, paths.RoamingAppData, paths.LocalAppData, paths.CodexHome })
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
                throw new ArgumentException("Original user profile, AppData, and Codex paths must be absolute.");
    }

    private static McpClientTarget Target(string id, string name, string path, string? detectionDirectory = null) =>
        new(id, name, path, File.Exists(path) || Directory.Exists(detectionDirectory ?? Path.GetDirectoryName(path)!));

    private static string OpenCodePath(McpUserPaths paths)
    {
        var root = Path.Combine(paths.Profile, ".config", "opencode");
        var jsonc = Path.Combine(root, "opencode.jsonc");
        return File.Exists(jsonc) || !File.Exists(Path.Combine(root, "opencode.json")) ? jsonc : Path.Combine(root, "opencode.json");
    }

    private static void AddPackagedClaude(McpUserPaths paths, List<McpClientTarget> targets)
    {
        var packages = Path.Combine(paths.LocalAppData, "Packages");
        if (!Directory.Exists(packages)) return;
        try
        {
            foreach (var directory in Directory.EnumerateDirectories(packages, "Claude_*"))
            {
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) continue;
                var name = Path.GetFileName(directory);
                targets.Add(new("claude-desktop-msix-" + name.ToLowerInvariant(), "Claude Desktop (Microsoft Store)",
                    Path.Combine(directory, "LocalCache", "Roaming", "Claude", "claude_desktop_config.json"), true));
            }
        }
        catch (UnauthorizedAccessException) { }
        catch (IOException) { }
    }
}
