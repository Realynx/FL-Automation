using System.Text;
using FruityLink.Installer.Core.Mcp;
using Xunit;

namespace FruityLink.Installer.Tests;

public sealed class McpProfileSafetyTests
{
    [Fact]
    public void DiscoveryUsesSuppliedOriginPathsAndDetectsPackagedClaudeVariants()
    {
        using var fixture = new McpSetupFixture();
        var package = Path.Combine(fixture.Paths.LocalAppData, "Packages", "Claude_abc123");
        Directory.CreateDirectory(package);
        Directory.CreateDirectory(Path.Combine(fixture.Paths.Profile, ".cursor"));
        var targets = McpClientSetup.Discover(fixture.Paths);
        Assert.Equal(10, targets.Count);
        Assert.Equal(Path.Combine(fixture.Paths.CodexHome, "config.toml"), targets.Single(target => target.Id == "codex").ConfigPath);
        Assert.True(targets.Single(target => target.Id == "cursor").Detected);
        Assert.False(targets.Single(target => target.Id == "codex").Detected);
        var packaged = Assert.Single(targets, target => target.Id.StartsWith("claude-desktop-msix-", StringComparison.Ordinal));
        Assert.Equal("Claude Desktop (Microsoft Store)", packaged.Name);
        Assert.Equal(Path.Combine(package, "LocalCache", "Roaming", "Claude", "claude_desktop_config.json"), packaged.ConfigPath);
        Assert.True(packaged.Detected);
        Assert.True(fixture.Configure(packaged.Id).Success);
        Assert.True(File.Exists(packaged.ConfigPath));
        Assert.False(File.Exists(fixture.Config("claude-desktop")));
    }

    [Fact]
    public void EnablingPluginPreservesOtherIdsAndSettingsAndCreatesExactBackup()
    {
        using var fixture = new McpSetupFixture();
        const string original = """{"Enabled":["custom-plugin","fl-agent"],"Other":{"keep":true}}""";
        McpSetupFixture.Write(fixture.PluginState, original);
        Assert.True(fixture.Configure("cursor").Success);
        var state = McpSetupFixture.ReadJson(fixture.PluginState);
        Assert.Equal(new[] { "custom-plugin", "fl-agent", "fl-mcp" }, state["Enabled"]!.AsArray().Select(node => node!.GetValue<string>()));
        Assert.True(state["Other"]!["keep"]!.GetValue<bool>());
        Assert.Equal(original, File.ReadAllText(Assert.Single(fixture.Backups(fixture.PluginState))));
        Assert.True(fixture.Configure("cursor").Success);
        Assert.Single(fixture.Backups(fixture.PluginState));
    }

    [Theory]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("""{"Enabled":null}""")]
    [InlineData("""{"Enabled":"fl-mcp"}""")]
    [InlineData("""{"Enabled":[42]}""")]
    [InlineData("""{"Enabled":[null]}""")]
    public void InvalidEnabledStateAbortsClientWritesAndPreservesBytes(string content)
    {
        using var fixture = new McpSetupFixture();
        McpSetupFixture.Write(fixture.PluginState, content);
        Assert.False(fixture.Configure("cursor").Success);
        Assert.Equal(content, File.ReadAllText(fixture.PluginState));
        Assert.False(File.Exists(fixture.Config("cursor")));
        Assert.Empty(fixture.Backups(fixture.PluginState));
        Assert.Equal(0, fixture.PreflightCalls);
    }

    [Fact]
    public void ExistingEnabledFlagAndNoMatchingRemovalDoNotRewriteFiles()
    {
        using var fixture = new McpSetupFixture();
        const string state = """{ "Enabled": ["fl-mcp", "other"] }""";
        McpSetupFixture.Write(fixture.PluginState, state);
        var result = fixture.Execute(fixture.Options("cursor"), false, true);
        Assert.True(result.Success);
        Assert.Equal(state, File.ReadAllText(fixture.PluginState));
        Assert.False(File.Exists(fixture.Config("cursor")));
        Assert.Empty(fixture.Backups(fixture.PluginState));
    }

    [Fact]
    public void AtomicWriterRefusesConcurrentClientChangesWithoutOverwritingOrCreatingBackup()
    {
        using var fixture = new McpSetupFixture();
        var path = fixture.Config("cursor");
        McpSetupFixture.Write(path, """{"value":1}""");
        var launch = McpLaunchSettings.Create(fixture.FlPath, fixture.Options("cursor"));
        var target = McpClientSetup.Discover(fixture.Paths).Single(item => item.Id == "cursor");
        var change = McpJsonConfiguration.Prepare(target, launch, false)!;
        const string changed = """{"value":2}""";
        McpSetupFixture.Write(path, changed);
        Assert.Throws<IOException>(() => McpConfigStore.Write(change));
        Assert.Equal(changed, File.ReadAllText(path));
        Assert.Empty(fixture.Backups(path));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp"));
    }

    [Fact]
    public void InvalidUtf8IsRejectedWithoutReplacingConfiguration()
    {
        using var fixture = new McpSetupFixture();
        var path = fixture.Config("cursor");
        McpSetupFixture.Write(path, "{}");
        byte[] invalid = [0xff, 0xfe, 0x00];
        File.WriteAllBytes(path, invalid);
        Assert.False(fixture.Configure("cursor").Success);
        Assert.Equal(invalid, File.ReadAllBytes(path));
        Assert.False(File.Exists(fixture.PluginState));
    }

    [Fact]
    public void Utf8BomIsAcceptedAndBackupPreservesOriginalEncoding()
    {
        using var fixture = new McpSetupFixture();
        var path = fixture.Config("cursor");
        McpSetupFixture.Write(path, "{}");
        byte[] original = [0xef, 0xbb, 0xbf, .. Encoding.UTF8.GetBytes("{}")];
        File.WriteAllBytes(path, original);
        Assert.True(fixture.Configure("cursor").Success);
        Assert.Equal(original, File.ReadAllBytes(Assert.Single(fixture.Backups(path))));
    }

    [Fact]
    public void RelativeUserProfileIsRejectedBeforeAnyReadOrWrite()
    {
        using var fixture = new McpSetupFixture();
        var options = fixture.Options("cursor") with { UserPaths = fixture.Paths with { Profile = "relative-profile" } };
        Assert.False(fixture.Execute(options, false, false).Success);
        Assert.False(Directory.Exists(fixture.Root));
    }
}
