using System.Text.Json.Nodes;
using FruityLink.Installer.Core.Mcp;
using Tomlyn;
using Tomlyn.Model;
using Xunit;

namespace FruityLink.Installer.Tests;

public sealed class McpConfigurationFormatTests
{
    [Fact]
    public void CodexUsesRealTomlParserPreservesTypedSettingsAndCustomHome()
    {
        using var fixture = new McpSetupFixture();
        var path = fixture.Config("codex");
        const string original = """"
            model = "gpt-test"
            instruction = """multiple
            lines"""
            [projects.'C:\Music Projects']
            trust_level = "untrusted"
            [mcp_servers."unrelated.server"]
            command = 'C:\Other Server\server.exe'
            args = ["--flag", "value with space"]
            env = { FOO = "keep" }
            """";
        McpSetupFixture.Write(path, original);
        Assert.True(fixture.Configure("codex").Success);
        var root = ReadToml(path);
        Assert.Equal("gpt-test", root["model"]);
        Assert.Equal("multiple\nlines", root["instruction"]);
        Assert.Equal("untrusted", ((TomlTable)((TomlTable)root["projects"])["C:\\Music Projects"])["trust_level"]);
        var servers = (TomlTable)root["mcp_servers"];
        var unrelated = (TomlTable)servers["unrelated.server"];
        Assert.Equal(@"C:\Other Server\server.exe", unrelated["command"]);
        Assert.Equal(2, ((TomlArray)unrelated["args"]).Count);
        var entry = (TomlTable)servers["flmcp"];
        Assert.Equal(3900L, entry["tool_timeout_sec"]);
        Assert.Equal(30L, entry["startup_timeout_sec"]);
        Assert.False(entry.ContainsKey("auto_approve"));
        Assert.Equal(original, File.ReadAllText(Assert.Single(fixture.Backups(path))));
        var configured = File.ReadAllBytes(path);
        Assert.True(fixture.Configure("codex").Success);
        Assert.Equal(configured, File.ReadAllBytes(path));
        Assert.Single(fixture.Backups(path));
    }

    [Fact]
    public void CodexPreservesMatchingEntryFlagsAndSeparatesFlVersionsOnRemoval()
    {
        using var fixture = new McpSetupFixture();
        Assert.True(fixture.Configure("codex").Success);
        var path = fixture.Config("codex");
        var root = ReadToml(path);
        var entry = (TomlTable)((TomlTable)root["mcp_servers"])["flmcp"];
        entry["enabled"] = false;
        entry["tool_timeout_sec"] = 1500L;
        ((TomlTable)entry["env"])["USER_FIELD"] = "preserved";
        ((TomlTable)entry["env"])["FL_MCP_PYTHON"] = @"C:\Old Python\python.exe";
        McpSetupFixture.Write(path, TomlSerializer.Serialize(root));
        Assert.True(fixture.Configure("codex").Success);
        var updated = (TomlTable)((TomlTable)ReadToml(path)["mcp_servers"])["flmcp"];
        Assert.Equal(false, updated["enabled"]);
        Assert.Equal(1500L, updated["tool_timeout_sec"]);
        Assert.Equal("preserved", ((TomlTable)updated["env"])["USER_FIELD"]);
        Assert.False(((TomlTable)updated["env"]).ContainsKey("FL_MCP_PYTHON"));
        Assert.Equal(Path.Combine(fixture.FlPath, "FruityLink", "tools", "fl-mcp", "python", "runtime"),
            ((TomlTable)updated["env"])["FL_MCP_PYTHON_RUNTIME"]);
        var otherFl = Path.Combine(fixture.Root, "FL 2025");
        Assert.True(fixture.Execute(fixture.Options("codex"), false, false, otherFl).Success);
        Assert.True(fixture.Execute(fixture.Options("codex"), false, true).Success);
        var remaining = Assert.Single((TomlTable)ReadToml(path)["mcp_servers"]);
        var remainingEnvironment = (TomlTable)((TomlTable)remaining.Value)["env"];
        Assert.Equal(Path.Combine(otherFl, "FL64.exe"), remainingEnvironment["FL_MCP_FL_EXE"]);
    }

    [Theory]
    [InlineData("[broken")]
    [InlineData("model = \"one\"\nmodel = \"two\"")]
    [InlineData("mcp_servers = 42")]
    public void InvalidTomlNeverOverwritesOrEnablesPlugin(string text)
    {
        using var fixture = new McpSetupFixture();
        var path = fixture.Config("codex");
        McpSetupFixture.Write(path, text);
        var result = fixture.Configure("cursor", "codex");
        Assert.False(result.Success);
        Assert.Equal(text, File.ReadAllText(path));
        Assert.False(File.Exists(fixture.Config("cursor")));
        Assert.False(File.Exists(fixture.PluginState));
    }

    [Fact]
    public void NewOpenCodeUsesCurrentNestedSchemaWithoutChangingApprovalPolicy()
    {
        using var fixture = new McpSetupFixture();
        Assert.True(fixture.Configure("opencode").Success);
        var root = McpSetupFixture.ReadJson(fixture.Config("opencode"));
        var entry = root["mcp"]!["servers"]!["flmcp"]!;
        Assert.Equal("local", entry["type"]!.GetValue<string>());
        Assert.Equal(Path.Combine(fixture.FlPath, "FruityLink", "tools", "fl-mcp", "server", "FlMcp.Server.exe"),
            Assert.Single(entry["command"]!.AsArray())!.GetValue<string>());
        Assert.NotNull(entry["environment"]);
        Assert.Null(entry["env"]);
        Assert.Null(entry["enabled"]);
        Assert.Null(entry["disabled"]);
        Assert.Equal(3900000, entry["timeout"]!["execution"]!.GetValue<int>());
        Assert.Equal(30000, entry["timeout"]!["startup"]!.GetValue<int>());
        Assert.True(fixture.Execute(fixture.Options("opencode"), false, true).Success);
        Assert.Empty(McpSetupFixture.ReadJson(fixture.Config("opencode"))["mcp"]!["servers"]!.AsObject());
    }

    [Fact]
    public void ExistingOpenCodeLegacyLayoutAndJsonCommentsRemainSemanticallyValid()
    {
        using var fixture = new McpSetupFixture();
        var path = Path.Combine(fixture.Paths.Profile, ".config", "opencode", "opencode.json");
        const string original = """
            {
              // Existing v1 settings
              "model":"retained",
              "mcp":{"other":{"type":"local","command":["other"],"enabled":false,},},
            }
            """;
        McpSetupFixture.Write(path, original);
        Assert.Equal(path, fixture.Config("opencode"));
        Assert.True(fixture.Configure("opencode").Success);
        var root = McpSetupFixture.ReadJson(path);
        Assert.Null(root["mcp"]!["servers"]);
        Assert.Equal("retained", root["model"]!.GetValue<string>());
        Assert.False(root["mcp"]!["other"]!["enabled"]!.GetValue<bool>());
        Assert.Equal("local", root["mcp"]!["flmcp"]!["type"]!.GetValue<string>());
        Assert.Null(root["mcp"]!["flmcp"]!["timeout"]);
        Assert.Equal(original, File.ReadAllText(Assert.Single(fixture.Backups(path))));
    }

    [Fact]
    public void MixedOpenCodeSchemasRefuseAmbiguousMutation()
    {
        using var fixture = new McpSetupFixture();
        var path = fixture.Config("opencode");
        const string original = """{"mcp":{"legacy":{"type":"local","command":["old"]},"servers":{}}}""";
        McpSetupFixture.Write(path, original);
        Assert.False(fixture.Configure("opencode").Success);
        Assert.Equal(original, File.ReadAllText(path));
        Assert.False(File.Exists(fixture.PluginState));
    }

    [Theory]
    [InlineData("vscode", true)]
    [InlineData("cursor", false)]
    public void CommentsAndTrailingCommasAreAcceptedOnlyForClientsSupportingJsonc(string id, bool expected)
    {
        using var fixture = new McpSetupFixture();
        McpSetupFixture.Write(fixture.Config(id), "{\n// retained setting\n\"inputs\":[],\n}");
        var result = fixture.Configure(id);
        Assert.Equal(expected, result.Success);
        if (expected) Assert.Empty(McpSetupFixture.ReadJson(fixture.Config(id))["inputs"]!.AsArray());
    }

    private static TomlTable ReadToml(string path) => TomlSerializer.Deserialize<TomlTable>(File.ReadAllText(path))!;
}
