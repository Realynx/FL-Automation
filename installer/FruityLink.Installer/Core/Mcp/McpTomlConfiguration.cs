using Tomlyn;
using Tomlyn.Model;
using Tomlyn.Parsing;

namespace FruityLink.Installer.Core.Mcp;

internal static class McpTomlConfiguration
{
    public static McpConfigChange? Prepare(McpClientTarget target, McpLaunchSettings launch, bool remove)
    {
        var original = McpConfigStore.Read(target.ConfigPath);
        if (original is null && remove) return null;
        var text = McpConfigStore.Decode(original);
        // TomlTable deserialization alone accepts some duplicate keys; validate the full syntax first.
        _ = SyntaxParser.ParseStrict(text);
        var root = string.IsNullOrWhiteSpace(text) ? new TomlTable() : TomlSerializer.Deserialize<TomlTable>(text)
            ?? throw new InvalidDataException("Codex configuration must be a TOML table.");
        var before = TomlSerializer.Serialize(root);
        var servers = Child(root, "mcp_servers", !remove);
        if (servers is null) return null;
        if (remove)
        {
            foreach (var entry in servers.ToArray())
                if (entry.Value is TomlTable value && Matches(value, launch)) servers.Remove(entry.Key);
        }
        else Update(servers, launch);
        var content = TomlSerializer.Serialize(root);
        return content == before ? null : new(target.ConfigPath, original, content, (remove ? "Remove FL MCP from " : "Connect FL MCP to ") + target.Name);
    }

    private static TomlTable? Child(TomlTable parent, string key, bool create)
    {
        if (parent.TryGetValue(key, out var existing))
            return existing as TomlTable ?? throw new InvalidDataException("Expected a TOML table at " + key + ".");
        if (!create) return null;
        var table = new TomlTable();
        parent.Add(key, table);
        return table;
    }

    private static bool Matches(TomlTable server, McpLaunchSettings launch)
    {
        server.TryGetValue("command", out var command);
        server.TryGetValue("env", out var environment);
        object? executable = null;
        if (environment is TomlTable env) env.TryGetValue("FL_MCP_FL_EXE", out executable);
        return launch.Matches(command as string, executable as string);
    }

    private static void Update(TomlTable servers, McpLaunchSettings launch)
    {
        var name = servers.FirstOrDefault(pair => pair.Value is TomlTable value && Matches(value, launch)).Key;
        if (name is null)
        {
            name = servers.ContainsKey("flmcp") ? McpJsonConfiguration.StableName(launch.FlExecutable) : "flmcp";
            if (servers.ContainsKey(name)) throw new InvalidDataException("FL MCP configuration name is occupied by another entry; no entry was overwritten.");
        }
        var entry = Child(servers, name, true)!;
        entry["command"] = launch.ServerExecutable;
        entry["args"] = new TomlArray();
        entry.Remove("url");
        var env = Child(entry, "env", true)!;
        env.Remove("FL_MCP_PYTHON");
        foreach (var pair in launch.Environment) env[pair.Key] = pair.Value;
        if (!entry.ContainsKey("startup_timeout_sec")) entry["startup_timeout_sec"] = 30L;
        if (!entry.ContainsKey("tool_timeout_sec")) entry["tool_timeout_sec"] = 3900L;
    }
}
