using FruityLink.Installer.Core;
using FruityLink.Installer.Core.Mcp;

namespace FruityLink.Installer.Cli;

/// <summary>Client setup options and reporting shared by command-line operations and GUI startup.</summary>
public static class McpCli
{
    public static McpSetupOptions ResolveOptions(CliOptions options)
    {
        var current = McpUserPaths.Capture();
        var profile = options.McpProfile ?? current.Profile;
        var customProfile = options.McpProfile is not null;
        var paths = new McpUserPaths(
            profile,
            options.McpAppData ?? (customProfile ? Path.Combine(profile, "AppData", "Roaming") : current.RoamingAppData),
            options.McpLocalAppData ?? (customProfile ? Path.Combine(profile, "AppData", "Local") : current.LocalAppData),
            options.McpCodexHome ?? (customProfile ? Path.Combine(profile, ".codex") : current.CodexHome));
        return new McpSetupOptions(options.McpClientIds, paths, options.McpPythonRuntime, options.McpTemplate, options.McpWorkspace);
    }

    public static bool Validate(CliOptions options, IProgressLog log)
    {
        if (options.ConfigureMcp && options.McpClientIds.Count == 0)
        {
            log.Error("--configure-mcp requires --mcp-clients. Use --list-mcp-clients to see available IDs.");
            return false;
        }
        if (options.Install && options.WithoutMcp && options.McpClientIds.Count > 0)
        {
            log.Error("Client setup requires the FLMCP component. Remove --without-mcp or --mcp-clients.");
            return false;
        }
        if (options.McpClientIds.Count == 0) return true;
        var known = McpClientSetup.Discover(ResolveOptions(options).UserPaths)
            .Select(target => target.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unknown = options.McpClientIds.Where(id => !known.Contains(id)).ToArray();
        if (unknown.Length == 0) return true;
        log.Error("Unknown MCP client ID(s): " + string.Join(", ", unknown) + ". Use --list-mcp-clients.");
        return false;
    }

    public static int List(CliOptions options, IProgressLog log)
    {
        foreach (var client in McpClientSetup.Discover(ResolveOptions(options).UserPaths))
        {
            log.Info($"{client.Id}: {client.Name} ({(client.Detected ? "detected" : "not detected")})");
            log.Info("  " + client.ConfigPath);
        }
        return ExitCodes.Success;
    }

    public static bool Apply(string flPath, CliOptions options, bool remove, IProgressLog log)
    {
        if (options.McpClientIds.Count == 0) return true;
        var settings = ResolveOptions(options);
        var result = remove
            ? McpClientSetup.Remove(flPath, settings, options.DryRun, log)
            : McpClientSetup.Configure(flPath, settings, options.DryRun, log);
        foreach (var error in result.Errors) log.Error("MCP client setup: " + error);
        if (result.Success && !options.DryRun && !remove)
            log.Info("Client configuration saved. " + McpSetupGuidance.NextSteps);
        return result.Success;
    }

    /// <summary>Capture the originating user's destinations before UAC can change the process identity.</summary>
    public static void AppendElevationArguments(List<string> arguments, CliOptions options)
    {
        if (options.McpClientIds.Count == 0) return;
        arguments.AddRange(McpElevationArguments.Create(ResolveOptions(options)));
    }
}
