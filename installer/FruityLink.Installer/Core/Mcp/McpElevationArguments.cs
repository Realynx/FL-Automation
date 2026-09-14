namespace FruityLink.Installer.Core.Mcp;

/// <summary>Preserves the originating user's client choices and configuration roots across UAC.</summary>
public static class McpElevationArguments
{
    public static IReadOnlyList<string> Create(McpSetupOptions options)
    {
        var args = new List<string>
        {
            "--mcp-profile", options.UserPaths.Profile,
            "--mcp-app-data", options.UserPaths.RoamingAppData,
            "--mcp-local-app-data", options.UserPaths.LocalAppData,
            "--mcp-codex-home", options.UserPaths.CodexHome,
        };
        if (options.ClientIds.Count > 0)
        {
            args.Add("--mcp-clients");
            args.Add(string.Join(',', options.ClientIds));
        }
        AddOverride(args, "--mcp-python-runtime", options.PythonRuntimeDirectory);
        AddOverride(args, "--mcp-template", options.TemplatePath);
        AddOverride(args, "--mcp-workspace", options.WorkspacePath);
        return args;
    }

    private static void AddOverride(List<string> args, string name, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        args.Add(name);
        args.Add(value);
    }
}
