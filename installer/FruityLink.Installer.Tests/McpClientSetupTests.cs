using System.Text.Json.Nodes;
using FruityLink.Installer.Core.Mcp;
using Xunit;

namespace FruityLink.Installer.Tests;

public sealed class McpClientSetupTests
{
    [Theory]
    [InlineData("claude-desktop", "mcpServers", null)]
    [InlineData("claude-code", "mcpServers", "stdio")]
    [InlineData("cursor", "mcpServers", null)]
    [InlineData("vscode", "servers", "stdio")]
    [InlineData("gemini-cli", "mcpServers", null)]
    [InlineData("windsurf", "mcpServers", null)]
    [InlineData("generic-json", "mcpServers", null)]
    public void JsonClientsReceiveExpectedSchemaAndBundledRuntime(string id, string map, string? type)
    {
        using var fixture = new McpSetupFixture();
        var result = fixture.Configure(id);
        Assert.True(result.Success, string.Join("\n", result.Errors));
        var root = McpSetupFixture.ReadJson(fixture.Config(id));
        var entry = root[map]!["flmcp"]!;
        var companion = Path.Combine(fixture.FlPath, "FruityLink", "tools", "fl-mcp");
        Assert.Equal(Path.Combine(companion, "server", "FlMcp.Server.exe"), entry["command"]!.GetValue<string>());
        Assert.Empty(entry["args"]!.AsArray());
        Assert.Equal(type, entry["type"]?.GetValue<string>());
        Assert.Equal(Path.Combine(companion, "python", "runtime"), entry["env"]!["FL_MCP_PYTHON_RUNTIME"]!.GetValue<string>());
        Assert.Null(entry["env"]!["FL_MCP_PYTHON"]);
        Assert.Equal(Path.Combine(companion, "python", "fruitylink_python-0.2.0-py3-none-any.whl"), entry["env"]!["FL_MCP_PYTHON_PATH"]!.GetValue<string>());
        Assert.Equal(Path.Combine(fixture.Paths.LocalAppData, "FlMcp", "Projects"), entry["env"]!["FL_MCP_WORKSPACE"]!.GetValue<string>());
        Assert.Null(entry["trust"]);
        Assert.Null(entry["autoApprove"]);
        if (id == "gemini-cli") Assert.Equal(3900000, entry["timeout"]!.GetValue<int>());
        Assert.Equal("fl-mcp", Assert.Single(McpSetupFixture.ReadJson(fixture.PluginState)["Enabled"]!.AsArray())!.GetValue<string>());
        Assert.Equal(1, fixture.PreflightCalls);
    }

    [Fact]
    public void ExistingSettingsArePreservedBackedUpAndSecondSetupIsIdempotent()
    {
        using var fixture = new McpSetupFixture();
        var path = fixture.Config("cursor");
        const string original = """{"theme":"dark","mcpServers":{"other":{"command":"other.exe","env":{"SECRET":"retained"}}}}""";
        McpSetupFixture.Write(path, original);
        Assert.True(fixture.Configure("cursor").Success);
        var root = McpSetupFixture.ReadJson(path);
        Assert.Equal("dark", root["theme"]!.GetValue<string>());
        Assert.Equal("retained", root["mcpServers"]!["other"]!["env"]!["SECRET"]!.GetValue<string>());
        Assert.Equal(original, File.ReadAllText(Assert.Single(fixture.Backups(path))));
        var configured = File.ReadAllBytes(path);
        var state = File.ReadAllBytes(fixture.PluginState);
        Assert.True(fixture.Configure("cursor").Success);
        Assert.Equal(configured, File.ReadAllBytes(path));
        Assert.Equal(state, File.ReadAllBytes(fixture.PluginState));
        Assert.Single(fixture.Backups(path));
        Assert.Empty(fixture.Backups(fixture.PluginState));
    }

    [Fact]
    public void ExistingMatchingEntryKeepsItsNameExtraEnvironmentAndApprovalPreferences()
    {
        using var fixture = new McpSetupFixture();
        Assert.True(fixture.Configure("cursor").Success);
        var path = fixture.Config("cursor");
        var root = McpSetupFixture.ReadJson(path);
        var servers = root["mcpServers"]!.AsObject();
        var entry = servers["flmcp"]!.DeepClone().AsObject();
        servers.Remove("flmcp");
        servers["my-studio"] = entry;
        entry["env"]!["CUSTOM"] = "preserved";
        entry["env"]!["FL_MCP_PYTHON"] = @"C:\Old Python\python.exe";
        entry["disabled"] = true;
        entry["alwaysAllow"] = new JsonArray("a_user_choice");
        entry["env"]!["FL_MCP_TEMPLATE"] = "outdated";
        McpSetupFixture.Write(path, root.ToJsonString());
        Assert.True(fixture.Configure("cursor").Success);
        var after = McpSetupFixture.ReadJson(path)["mcpServers"]!.AsObject();
        Assert.Single(after);
        Assert.True(after["my-studio"]!["disabled"]!.GetValue<bool>());
        Assert.Equal("preserved", after["my-studio"]!["env"]!["CUSTOM"]!.GetValue<string>());
        Assert.Null(after["my-studio"]!["env"]!["FL_MCP_PYTHON"]);
        Assert.NotNull(after["my-studio"]!["env"]!["FL_MCP_PYTHON_RUNTIME"]);
        Assert.Equal("a_user_choice", Assert.Single(after["my-studio"]!["alwaysAllow"]!.AsArray())!.GetValue<string>());
    }

    [Fact]
    public void SideBySideVersionsGetSeparateEntriesAndRemovalKeepsOtherInstallationAndPluginFlag()
    {
        using var fixture = new McpSetupFixture();
        var options = fixture.Options("cursor");
        var otherFl = Path.Combine(fixture.Root, "FL Studio 2025");
        Assert.True(fixture.Configure("cursor").Success);
        Assert.True(fixture.Execute(options, false, false, otherFl).Success);
        var servers = McpSetupFixture.ReadJson(fixture.Config("cursor"))["mcpServers"]!.AsObject();
        Assert.Equal(2, servers.Count);
        var otherEntry = servers.Single(pair => pair.Key != "flmcp").Value!.DeepClone();
        Assert.True(fixture.Execute(options, false, true).Success);
        var remaining = Assert.Single(McpSetupFixture.ReadJson(fixture.Config("cursor"))["mcpServers"]!.AsObject());
        Assert.True(JsonNode.DeepEquals(otherEntry, remaining.Value));
        Assert.Equal("fl-mcp", Assert.Single(McpSetupFixture.ReadJson(fixture.PluginState)["Enabled"]!.AsArray())!.GetValue<string>());
        Assert.True(fixture.Execute(options, false, true, otherFl).Success);
        Assert.Empty(McpSetupFixture.ReadJson(fixture.Config("cursor"))["mcpServers"]!.AsObject());
    }

    [Fact]
    public void SameServerCommandWithDifferentFlRootIsNeverRemoved()
    {
        using var fixture = new McpSetupFixture();
        Assert.True(fixture.Configure("cursor").Success);
        var path = fixture.Config("cursor");
        var root = McpSetupFixture.ReadJson(path);
        root["mcpServers"]!["flmcp"]!["env"]!["FL_MCP_FL_EXE"] = Path.Combine(fixture.Root, "different", "FL64.exe");
        McpSetupFixture.Write(path, root.ToJsonString());
        var original = File.ReadAllBytes(path);
        Assert.True(fixture.Execute(fixture.Options("cursor"), false, true).Success);
        Assert.Equal(original, File.ReadAllBytes(path));
    }

    [Fact]
    public void DryRunBeforeInstallationDoesNotCreateDirectoriesBackupsOrLaunchPython()
    {
        using var fixture = new McpSetupFixture();
        var result = McpClientSetup.Configure(fixture.FlPath, fixture.Options("codex", "cursor"), true, new McpSetupFixture.SilentLog());
        Assert.True(result.Success, string.Join("\n", result.Errors));
        Assert.Contains(result.Details, text => text.Contains("[dry-run]", StringComparison.Ordinal));
        Assert.False(Directory.Exists(fixture.Root));
        Assert.Equal(0, fixture.PreflightCalls);
    }

    [Fact]
    public void EmptySelectionNeverEnablesPluginOrPreflights()
    {
        using var fixture = new McpSetupFixture();
        Assert.True(fixture.Configure().Success);
        Assert.False(Directory.Exists(fixture.Root));
        Assert.Equal(0, fixture.PreflightCalls);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("")]
    [InlineData("""{"mcpServers":null}""")]
    [InlineData("""{"mcpServers":{},"mcpServers":{}}""")]
    [InlineData("""{"other":[{"value":1,"value":2}]}""")]
    public void MalformedJsonAbortsAllSelectedWritesAndPluginEnable(string content)
    {
        using var fixture = new McpSetupFixture();
        var malformedPath = fixture.Config("cursor");
        McpSetupFixture.Write(malformedPath, content);
        var result = fixture.Configure("claude-code", "cursor");
        Assert.False(result.Success);
        Assert.Equal(content, File.ReadAllText(malformedPath));
        Assert.False(File.Exists(fixture.Config("claude-code")));
        Assert.False(File.Exists(fixture.PluginState));
        Assert.Empty(fixture.Backups(malformedPath));
        Assert.Equal(0, fixture.PreflightCalls);
    }

    [Fact]
    public void FailedInstalledFilePreflightDoesNotWritePreparedChanges()
    {
        using var fixture = new McpSetupFixture();
        var result = McpClientSetup.Configure(fixture.FlPath, fixture.Options("codex", "cursor"), false, new McpSetupFixture.SilentLog());
        Assert.False(result.Success);
        Assert.Contains(result.Errors, error => error.Contains("Required installed MCP file is missing", StringComparison.Ordinal));
        Assert.False(Directory.Exists(fixture.Root));
    }

    [Theory]
    [InlineData("python.exe")]
    [InlineData("python314.dll")]
    [InlineData("python314.zip")]
    [InlineData("Data/Templates/Empty/Empty.flp")]
    public void MissingRuntimeOrLaunchTemplateFailsBeforeAnyClientChanges(string missingFile)
    {
        using var fixture = new McpSetupFixture();
        var companion = Path.Combine(fixture.FlPath, "FruityLink", "tools", "fl-mcp");
        foreach (var relative in new[] { "FL64.exe", "Data/Templates/Empty/Empty.flp",
            "FruityLink/tools/fl-mcp/server/FlMcp.Server.exe",
            "FruityLink/tools/fl-mcp/python/fruitylink_python-0.2.0-py3-none-any.whl" })
            if (relative != missingFile) McpSetupFixture.Write(Path.Combine(fixture.FlPath, relative), "fixture");
        foreach (var name in new[] { "python.exe", "python314.dll", "python314.zip" })
            if (name != missingFile) McpSetupFixture.Write(Path.Combine(companion, "python", "runtime", name), "fixture");

        var result = McpClientSetup.Configure(fixture.FlPath, fixture.Options("codex"), false, new McpSetupFixture.SilentLog());

        Assert.False(result.Success);
        Assert.Contains(result.Errors, error => error.Contains("Required installed MCP file is missing", StringComparison.Ordinal));
        Assert.False(File.Exists(fixture.Config("codex")));
        Assert.False(File.Exists(fixture.PluginState));
    }

    [Fact]
    public void ExplicitRuntimeTemplateWorkspaceAreUsedWithoutRecapturingElevatedUserPaths()
    {
        using var fixture = new McpSetupFixture();
        var options = fixture.Options("cursor") with
        {
            PythonRuntimeDirectory = Path.Combine(fixture.Root, "custom-runtime"),
            TemplatePath = Path.Combine(fixture.Root, "custom-template.flp"),
            WorkspacePath = Path.Combine(fixture.Root, "custom-projects")
        };
        Assert.True(fixture.Execute(options, false, false).Success);
        var env = McpSetupFixture.ReadJson(fixture.Config("cursor"))["mcpServers"]!["flmcp"]!["env"]!;
        Assert.Equal(options.PythonRuntimeDirectory, env["FL_MCP_PYTHON_RUNTIME"]!.GetValue<string>());
        Assert.Equal(options.TemplatePath, env["FL_MCP_TEMPLATE"]!.GetValue<string>());
        Assert.Equal(options.WorkspacePath, env["FL_MCP_WORKSPACE"]!.GetValue<string>());
    }

    [Fact]
    public void RelativeOverrideAndUnknownSelectionAreRejectedWithoutWrites()
    {
        using var fixture = new McpSetupFixture();
        Assert.False(fixture.Execute(fixture.Options("cursor") with { PythonRuntimeDirectory = "relative-runtime" }, false, false).Success);
        Assert.False(fixture.Configure("missing-client").Success);
        Assert.False(Directory.Exists(fixture.Root));
    }
}
