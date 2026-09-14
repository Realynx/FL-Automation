using System.Text.Json.Nodes;
using FruityLink.Installer.Core;
using FruityLink.Installer.Core.Mcp;

namespace FruityLink.Installer.Tests;

internal sealed class McpSetupFixture : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "FruityLink-McpSetup-Tests", Guid.NewGuid().ToString("N"));
    public McpUserPaths Paths { get; }
    public string FlPath => Path.Combine(Root, "FL Studio 2026");
    public string PluginState => Path.Combine(Paths.LocalAppData, "FruityLink", "plugins.json");
    public int PreflightCalls { get; private set; }
    public McpSetupFixture()
    {
        Paths = new(Path.Combine(Root, "Profile"), Path.Combine(Root, "Roaming"), Path.Combine(Root, "Local"), Path.Combine(Root, "CustomCodex"));
    }

    public string Config(string id) => McpClientSetup.Discover(Paths).Single(target => target.Id == id).ConfigPath;
    public McpSetupOptions Options(params string[] ids) => new(ids, Paths);
    public McpSetupResult Configure(params string[] ids) => Execute(Options(ids), false, false);
    public McpSetupResult Execute(McpSetupOptions options, bool dryRun, bool remove, string? flPath = null) =>
        McpClientSetup.Execute(flPath ?? FlPath, options, dryRun, remove, new SilentLog(), _ => PreflightCalls++);
    public static void Write(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }
    public static JsonObject ReadJson(string path) => JsonNode.Parse(File.ReadAllText(path))!.AsObject();
    public string[] Backups(string path) => Directory.Exists(Path.GetDirectoryName(path)) ?
        Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + ".flmcp-*.bak") : [];

    public void Dispose()
    {
        var full = Path.GetFullPath(Root);
        var allowed = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "FruityLink-McpSetup-Tests")) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(allowed, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unsafe scratch test cleanup.");
        if (Directory.Exists(full)) Directory.Delete(full, true);
    }
    internal sealed class SilentLog : IProgressLog
    {
        public void Log(LogLevel level, string message) { }
    }
}
