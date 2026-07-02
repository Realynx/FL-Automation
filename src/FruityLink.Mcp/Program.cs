using FruityLink.Core.Abstractions;
using FruityLink.FlStudio.Inject;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;

namespace FruityLink.Mcp;

/// <summary>
/// Entry point for the FruityLink MCP server. Exposes the entire FL Studio control SDK
/// (<see cref="INativeFlControl"/>, implemented by <see cref="FlInjectBridge"/>) as MCP tools so any
/// MCP-capable host (Claude Desktop, etc.) can drive FL Studio over the standard protocol.
///
/// Transports:
///   • stdio  (default)         — the standard transport for local MCP hosts.
///   • HTTP/SSE (--http [url])   — streamable HTTP for remote/networked hosts.
///
/// The server talks to FL Studio through the injected bridge over the named pipe
/// <c>\\.\pipe\FruityLinkBridge</c>. Listing tools needs nothing; INVOKING a tool needs FL Studio
/// running with the bridge injected (otherwise tools return a clear "bridge not reachable" message).
/// </summary>
internal static class Program
{
    private const string ServerName = "FruityLink";
    private const string ServerVersion = "1.0.0";

    private const string Instructions =
        "FruityLink exposes native FL Studio control as 'native_*' tools (transport, tempo/master, " +
        "mixer, channel rack, patterns, piano-roll notes, plugins/inserts, plugin params, samples, " +
        "playlist tracks/clips, arrangements, automation clips, project lifecycle, render, in-FL chat). " +
        "Values use FL's native scales (documented per tool): volumes 0-12800, pan 0-12800 (6400=center), " +
        "MIDI keys 0-131 (60=middle C), note positions/lengths in PPQ ticks (call native_get_ppq), " +
        "normalized plugin params 0.0-1.0. Tools require FL Studio running with the FruityLink bridge " +
        "injected; if a tool reports the bridge is unreachable, ask the user to start FL and inject the bridge.";

    private static async Task Main(string[] args)
    {
        string? httpUrl = ParseHttpUrl(args);

        if (httpUrl is null)
            await RunStdioAsync(args);
        else
            await RunHttpAsync(args, httpUrl);
    }

    /// <summary>stdio transport (default). stdout is the JSON-RPC channel, so ALL logging goes to stderr.</summary>
    private static async Task RunStdioAsync(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);

        // CRITICAL for stdio: console logs MUST go to stderr or they corrupt the JSON-RPC stream on stdout.
        builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
        builder.Logging.SetMinimumLevel(LogLevel.Warning);

        builder.Services.AddSingleton<INativeFlControl, FlInjectBridge>();
        builder.Services
            .AddMcpServer(ConfigureOptions)
            .WithStdioServerTransport()
            .WithToolsFromAssembly();

        await builder.Build().RunAsync();
    }

    /// <summary>Optional streamable HTTP/SSE transport for remote/networked MCP hosts.</summary>
    private static async Task RunHttpAsync(string[] args, string url)
    {
        var builder = WebApplication.CreateBuilder(args);

        builder.Services.AddSingleton<INativeFlControl, FlInjectBridge>();
        builder.Services
            .AddMcpServer(ConfigureOptions)
            .WithHttpTransport()
            .WithToolsFromAssembly();

        var app = builder.Build();
        app.Urls.Add(url);
        app.MapMcp();
        await app.RunAsync();
    }

    private static void ConfigureOptions(ModelContextProtocol.Server.McpServerOptions options)
    {
        options.ServerInfo = new Implementation
        {
            Name = ServerName,
            Version = ServerVersion,
            Title = "FruityLink — FL Studio control",
        };
        options.ServerInstructions = Instructions;
    }

    /// <summary>Returns the bind URL if <c>--http</c>/<c>--sse</c> was passed (with optional URL), else null (stdio).</summary>
    private static string? ParseHttpUrl(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] is "--http" or "--sse")
                return (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                    ? args[i + 1]
                    : "http://127.0.0.1:3001";
        }
        return null;
    }
}
