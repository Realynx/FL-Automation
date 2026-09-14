using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FruityLink.Installer.Core.Mcp;

internal static class McpJsonConfiguration
{
    private static readonly JsonSerializerOptions Output = new() { WriteIndented = true };

    public static McpConfigChange? Prepare(McpClientTarget target, McpLaunchSettings launch, bool remove)
    {
        var original = McpConfigStore.Read(target.ConfigPath);
        if (remove && original is null) return null;
        RejectEmptyExisting(original);
        var root = Parse(McpConfigStore.Decode(original), target.Id is "vscode" or "opencode");
        var before = root.DeepClone();
        var servers = ServerMap(root, target.Id, !remove);
        if (servers is null) return null;
        if (remove) RemoveMatches(servers, launch, target.Id);
        else UpdateServer(root, servers, target.Id, launch);
        return JsonNode.DeepEquals(before, root) ? null :
            new(target.ConfigPath, original, root.ToJsonString(Output) + "\n", (remove ? "Remove FL MCP from " : "Connect FL MCP to ") + target.Name);
    }

    internal static JsonObject Parse(string text, bool comments = false)
    {
        if (string.IsNullOrWhiteSpace(text)) return new JsonObject();
        var options = new JsonDocumentOptions { CommentHandling = comments ? JsonCommentHandling.Skip : JsonCommentHandling.Disallow, AllowTrailingCommas = comments };
        using var document = JsonDocument.Parse(text, options);
        CheckDuplicates(document.RootElement);
        return JsonNode.Parse(text, documentOptions: options) as JsonObject ?? throw new InvalidDataException("Client configuration must be a JSON object.");
    }

    private static void RejectEmptyExisting(byte[]? original)
    {
        if (original is not null && string.IsNullOrWhiteSpace(McpConfigStore.Decode(original)))
            throw new JsonException("Existing JSON configuration is empty.");
    }

    private static void CheckDuplicates(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException("Client configuration contains duplicate JSON keys.");
                CheckDuplicates(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) CheckDuplicates(item);
    }

    internal static JsonObject? Child(JsonObject parent, string key, bool create)
    {
        if (parent.TryGetPropertyValue(key, out var existing))
            return existing as JsonObject ?? throw new InvalidDataException("Expected a configuration object at " + key + ".");
        if (!create) return null;
        var result = new JsonObject();
        parent.Add(key, result);
        return result;
    }

    private static JsonObject? ServerMap(JsonObject root, string id, bool create)
    {
        if (id != "opencode") return Child(root, id == "vscode" ? "servers" : "mcpServers", create);
        var mcp = Child(root, "mcp", create);
        if (mcp is null) return null;
        var legacy = mcp.Any(pair => pair.Key != "servers" && pair.Value is JsonObject item && item.ContainsKey("type"));
        if (legacy && mcp.ContainsKey("servers")) throw new InvalidDataException("OpenCode configuration mixes v1 and v2 MCP layouts; resolve it before setup.");
        return legacy ? mcp : Child(mcp, "servers", create);
    }

    private static void RemoveMatches(JsonObject servers, McpLaunchSettings launch, string id)
    {
        foreach (var item in servers.ToArray())
            if (item.Value is JsonObject server && Matches(server, launch, id)) servers.Remove(item.Key);
    }

    private static void UpdateServer(JsonObject root, JsonObject servers, string id, McpLaunchSettings launch)
    {
        var matching = servers.FirstOrDefault(pair => pair.Value is JsonObject value && Matches(value, launch, id));
        var name = matching.Key ?? ChooseName(servers, launch);
        var entry = Child(servers, name, true)!;
        var openCode = id == "opencode";
        entry["command"] = openCode ? new JsonArray(launch.ServerExecutable) : JsonValue.Create(launch.ServerExecutable);
        if (openCode) entry["type"] = "local";
        else
        {
            entry["args"] = new JsonArray();
            if (id is "vscode" or "claude-code") entry["type"] = "stdio";
        }
        foreach (var obsolete in new[] { "url", "httpUrl", "serverUrl" }) entry.Remove(obsolete);
        var env = Child(entry, openCode ? "environment" : "env", true)!;
        env.Remove("FL_MCP_PYTHON");
        foreach (var pair in launch.Environment) env[pair.Key] = pair.Value;
        SetTimeout(root, entry, id);
    }

    private static string ChooseName(JsonObject servers, McpLaunchSettings launch)
    {
        if (!servers.ContainsKey("flmcp")) return "flmcp";
        var name = StableName(launch.FlExecutable);
        if (servers.ContainsKey(name)) throw new InvalidDataException("Both FL MCP configuration names are occupied by unrelated entries; no entry was overwritten.");
        return name;
    }

    internal static string StableName(string path) => "flmcp-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path.ToUpperInvariant())))[..10].ToLowerInvariant();

    private static bool Matches(JsonObject server, McpLaunchSettings launch, string id)
    {
        var command = id == "opencode" ? (server["command"] as JsonArray)?.FirstOrDefault() : server["command"];
        var environment = server[id == "opencode" ? "environment" : "env"] as JsonObject;
        return launch.Matches(String(command), String(environment?["FL_MCP_FL_EXE"]));
    }

    private static string? String(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static void SetTimeout(JsonObject root, JsonObject entry, string id)
    {
        if (id == "gemini-cli" && !entry.ContainsKey("timeout")) entry["timeout"] = 3900000;
        if (id == "opencode" && root["mcp"] is JsonObject mcp && mcp.ContainsKey("servers"))
        {
            var timeouts = Child(entry, "timeout", true)!;
            if (!timeouts.ContainsKey("startup")) timeouts["startup"] = 30000;
            if (!timeouts.ContainsKey("execution")) timeouts["execution"] = 3900000;
        }
    }

    internal static McpConfigChange? EnablePlugin(McpUserPaths paths)
    {
        var path = Path.Combine(paths.LocalAppData, "FruityLink", "plugins.json");
        var original = McpConfigStore.Read(path);
        RejectEmptyExisting(original);
        var root = Parse(McpConfigStore.Decode(original));
        var enabled = root["Enabled"] as JsonArray;
        if (root.ContainsKey("Enabled") && enabled is null) throw new InvalidDataException("FruityLink plugin enabled-state must contain an Enabled array.");
        enabled ??= new JsonArray();
        if (enabled.Any(item => item is not JsonValue value || !value.TryGetValue<string>(out _))) throw new InvalidDataException("FruityLink enabled plugin IDs must be strings.");
        if (enabled.Any(item => String(item) == "fl-mcp")) return null;
        if (!root.ContainsKey("Enabled")) root["Enabled"] = enabled;
        enabled.Add("fl-mcp");
        return new(path, original, root.ToJsonString(Output) + "\n", "Enable FL MCP in FruityLink for the original user");
    }
}
