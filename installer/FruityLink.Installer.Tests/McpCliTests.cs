using FruityLink.Installer.Cli;
using FruityLink.Installer.Core;
using Xunit;

namespace FruityLink.Installer.Tests;

public sealed class McpCliTests
{
    [Fact]
    public void ClientConfigurationIsExplicitlySelected()
    {
        var options = CliOptions.Parse(["--install", "--silent"]);
        Assert.Empty(options.McpClientIds);
        Assert.False(options.ConfigureMcp);
        Assert.False(options.ListMcpClients);
        Assert.True(McpCli.Apply(@"C:\not-installed", options, remove: false, new TestLog()));
    }

    [Fact]
    public void ParsesClientListAndCustomPathsWithSpaces()
    {
        var options = CliOptions.Parse(["--configure-mcp", "--mcp-clients=codex, claude-code",
            "--mcp-template", @"D:\Music Projects\Empty.flp", "--mcp-workspace", @"D:\MCP Projects"]);
        Assert.True(options.IsHeadlessVerb);
        Assert.False(options.RunGui);
        Assert.True(options.ConfigureMcp);
        Assert.Equal(["codex", "claude-code"], options.McpClientIds);
        Assert.Equal(@"D:\Music Projects\Empty.flp", options.McpTemplate);
        Assert.Equal(@"D:\MCP Projects", options.McpWorkspace);
        Assert.Empty(options.Unknown);
    }

    [Fact]
    public void CustomProfileDoesNotReuseCurrentUsersConfigDirectories()
    {
        var options = CliOptions.Parse(["--mcp-profile", @"C:\Target User"]);
        var settings = McpCli.ResolveOptions(options);
        Assert.Equal(@"C:\Target User", settings.UserPaths.Profile);
        Assert.Equal(@"C:\Target User\AppData\Roaming", settings.UserPaths.RoamingAppData);
        Assert.Equal(@"C:\Target User\AppData\Local", settings.UserPaths.LocalAppData);
        Assert.Equal(@"C:\Target User\.codex", settings.UserPaths.CodexHome);
    }

    [Fact]
    public void ElevationRoundTripPreservesOriginatingUserAndEveryOverride()
    {
        var options = CliOptions.Parse(["--install", "--mcp-clients", "codex,claude-code",
            "--mcp-profile", @"C:\Original User", "--mcp-app-data", @"D:\Roaming Data",
            "--mcp-local-app-data", @"D:\Local Data", "--mcp-codex-home", @"D:\Codex Data",
            "--mcp-python-runtime", @"D:\Python Runtime", "--mcp-template", @"D:\Templates\blank.flp",
            "--mcp-workspace", @"D:\Music Workspace"]);
        var arguments = new List<string> { "--install", "--silent" };
        McpCli.AppendElevationArguments(arguments, options);
        var elevated = CliOptions.Parse(arguments.ToArray());
        var before = McpCli.ResolveOptions(options);
        var after = McpCli.ResolveOptions(elevated);
        Assert.Equal(before.UserPaths, after.UserPaths);
        Assert.Equal(before.ClientIds, after.ClientIds);
        Assert.Equal(before.PythonRuntimeDirectory, after.PythonRuntimeDirectory);
        Assert.Equal(before.TemplatePath, after.TemplatePath);
        Assert.Equal(before.WorkspacePath, after.WorkspacePath);
        Assert.Empty(elevated.Unknown);
    }

    [Theory]
    [InlineData("--mcp-clients")]
    [InlineData("--mcp-profile")]
    [InlineData("--mcp-template")]
    [InlineData("--mcp-python-runtime")]
    public void MissingValueIsRejected(string option)
    {
        var options = CliOptions.Parse(["--configure-mcp", option]);
        Assert.NotEmpty(options.Unknown);
        Assert.Equal(ExitCodes.BadArgs, CliRunner.Run(options, new TestLog()));
    }

    [Theory]
    [InlineData("--install")]
    [InlineData("--uninstall")]
    public void ConfigureCannotRunAlongsideAnotherMutationVerb(string other)
    {
        var options = CliOptions.Parse(["--configure-mcp", other, "--mcp-clients", "codex"]);
        Assert.Equal(ExitCodes.BadArgs, CliRunner.Run(options, new TestLog()));
    }

    [Fact]
    public void ConfigureWithoutClientsAndInstallWithoutMcpAreRejected()
    {
        Assert.False(McpCli.Validate(CliOptions.Parse(["--configure-mcp"]), new TestLog()));
        Assert.False(McpCli.Validate(CliOptions.Parse(["--install", "--without-mcp", "--mcp-clients", "codex"]), new TestLog()));
    }

    [Fact]
    public void OldWorkerExecutableOptionIsRejected()
    {
        var options = CliOptions.Parse(["--configure-mcp", "--mcp-clients", "codex", "--mcp-python", @"C:\Python\python.exe"]);
        Assert.Contains("--mcp-python", options.Unknown);
        Assert.Equal(ExitCodes.BadArgs, CliRunner.Run(options, new TestLog()));
    }

    private sealed class TestLog : IProgressLog
    {
        public void Log(LogLevel level, string message) { }
    }
}
