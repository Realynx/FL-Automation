using System.Runtime.CompilerServices;
using System.Text.Json;

[assembly: InternalsVisibleTo("FruityLink.Installer.Tests")]

namespace FruityLink.Installer.Core.Mcp;

/// <summary>Native installer setup; no shell commands, client CLI invocation, downloads or auto-approval.</summary>
public static class McpClientSetup
{
    public static IReadOnlyList<McpClientTarget> Discover(McpUserPaths paths) => McpClientDiscovery.Discover(paths);

    public static McpSetupResult Configure(string flPath, McpSetupOptions options, bool dryRun, IProgressLog log) =>
        Execute(flPath, options, dryRun, false, log, launch => launch.Preflight());

    public static McpSetupResult Remove(string flPath, McpSetupOptions options, bool dryRun, IProgressLog log) =>
        Execute(flPath, options, dryRun, true, log, _ => { });

    internal static McpSetupResult Execute(string flPath, McpSetupOptions options, bool dryRun, bool remove,
        IProgressLog log, Action<McpLaunchSettings> preflight)
    {
        var errors = new List<string>();
        var details = new List<string>();
        if (options.ClientIds.Count == 0) return new();
        try
        {
            var launch = McpLaunchSettings.Create(flPath, options);
            var changes = Plan(options, launch, remove, details);
            if (!dryRun && !remove) preflight(launch);
            foreach (var change in changes)
            {
                var message = dryRun ? "[dry-run] " + change.Description + ": " + change.Path : McpConfigStore.Write(change);
                details.Add(message);
                log.Info(message);
            }
            if (!remove) details.Add(McpSetupGuidance.NextSteps);
        }
        catch (Exception ex)
        {
            var message = Failure(ex);
            errors.Add(message);
            log.Error(message);
        }
        return new() { Errors = errors, Details = details };
    }

    private static IReadOnlyList<McpConfigChange> Plan(McpSetupOptions options, McpLaunchSettings launch, bool remove, List<string> details)
    {
        var catalog = Discover(options.UserPaths).ToDictionary(target => target.Id, StringComparer.OrdinalIgnoreCase);
        var changes = new List<McpConfigChange>();
        foreach (var id in options.ClientIds.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!catalog.TryGetValue(id, out var target)) throw new ArgumentException("Unknown MCP client selection: " + id);
            McpConfigChange? change;
            try
            {
                change = target.Id == "codex" ? McpTomlConfiguration.Prepare(target, launch, remove) :
                    McpJsonConfiguration.Prepare(target, launch, remove);
            }
            catch (Exception ex) { throw new InvalidDataException(target.Name + " configuration at " + target.ConfigPath + ": " + Failure(ex), ex); }
            if (change is not null) changes.Add(change);
            else details.Add(target.Name + (remove ? ": no matching FL installation entry to remove." : ": FL MCP configuration already matches."));
        }
        if (!remove && McpJsonConfiguration.EnablePlugin(options.UserPaths) is { } enable) changes.Insert(0, enable);
        return changes;
    }

    private static string Failure(Exception ex) => ex switch
    {
        JsonException => "Invalid JSON configuration; no malformed settings were overwritten.",
        Tomlyn.TomlException => "Invalid TOML configuration; no malformed settings were overwritten.",
        _ => ex.Message
    };
}
