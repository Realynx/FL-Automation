using System.IO;
using FruityLink.FlStudio.Inject;
using FruityLink.Scripting;

namespace FruityLink.Host;

/// <summary>Hosts the authenticated scripting surface for an explicitly owned standalone FL process.</summary>
internal static class AutomationHost
{
    internal const string EnabledVariable = "FRUITYLINK_AUTOMATION";
    internal const string DiscoveryVariable = "FRUITYLINK_AUTOMATION_DISCOVERY";

    internal sealed record Configuration(string DiscoveryDirectory);

    /// <summary>Reads the opt-in configuration without changing normal interactive startup.</summary>
    internal static bool TryReadConfiguration(Func<string, string?> environment,
        out Configuration? configuration, out string? error)
    {
        ArgumentNullException.ThrowIfNull(environment);
        configuration = null;
        error = null;
        string? enabled = environment(EnabledVariable);
        if (!string.Equals(enabled, "1", StringComparison.Ordinal)) return false;

        string? directory = environment(DiscoveryVariable);
        if (string.IsNullOrWhiteSpace(directory))
        {
            error = $"{DiscoveryVariable} must name an absolute per-job directory when {EnabledVariable}=1.";
            return false;
        }
        if (!Path.IsPathFullyQualified(directory))
        {
            error = $"{DiscoveryVariable} must be an absolute path when {EnabledVariable}=1.";
            return false;
        }

        configuration = new(Path.GetFullPath(directory));
        return true;
    }

    /// <summary>
    /// Waits for FL's native objects and main message pump, then publishes one process-scoped endpoint.
    /// This path deliberately does not initialize the plugin host, WPF, toolbars, or persisted plugins.
    /// </summary>
    internal static void Run(Configuration configuration, Action waitForFlReady, Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(waitForFlReady);
        ArgumentNullException.ThrowIfNull(log);

        waitForFlReady();
        var server = new ScriptingPipeServer(new FlScriptingDispatcher(new FlInjectBridge()), new()
        {
            DiscoveryDirectory = configuration.DiscoveryDirectory,
            Log = message => log("standalone automation: " + message)
        });

        try
        {
            server.StartAsync().GetAwaiter().GetResult();
        }
        catch
        {
            server.DisposeAsync().AsTask().GetAwaiter().GetResult();
            throw;
        }

        log($"standalone automation endpoint ready: {server.Endpoint.PipeName}");
        AppDomain.CurrentDomain.ProcessExit += (_, _) => DisposeBestEffort(server, log);
    }

    private static void DisposeBestEffort(ScriptingPipeServer server, Action<string> log)
    {
        try
        {
            if (!server.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2)))
                log("standalone automation shutdown did not drain before process exit");
        }
        catch (Exception ex) { log("standalone automation shutdown failed: " + ex.Message); }
    }
}
