using FruityLink.Installer.Core;
using FruityLink.Installer.Core.Mcp;
using FruityLink.Installer.Gui;
using Xunit;

namespace FruityLink.Installer.Tests;

public sealed class McpGuiWorkflowTests
{
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public void ClientUpdatesAreOrderedAroundFileChanges(bool install, bool dryRun)
    {
        var calls = new List<string>();
        var outcome = McpOperationCoordinator.Run(install, dryRun,
            () => { calls.Add("files"); return new OperationResult { DryRun = dryRun }; },
            () => { calls.Add("clients"); return new McpSetupResult(); });

        Assert.Equal(install ? ["files", "clients"] : ["clients", "files"], calls);
        Assert.True(outcome.Files.Success);
        Assert.True(outcome.Clients!.Success);
        Assert.False(outcome.FilesSkipped);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedOrRebootPendingInstallationDoesNotConfigureClients(bool rebootPending)
    {
        var files = new OperationResult();
        if (rebootPending) files.RebootPending.Add("locked.dll");
        else files.Errors.Add("Required payload missing");
        var configured = false;

        var outcome = McpOperationCoordinator.Run(install: true, dryRun: false, () => files,
            () => { configured = true; return new McpSetupResult(); });

        Assert.False(configured);
        Assert.Null(outcome.Clients);
        Assert.Same(files, outcome.Files);
    }

    [Fact]
    public void ClientSetupFailurePreservesSuccessfulFileInstallOutcome()
    {
        var files = new OperationResult { FilesAffected = 12 };
        var setup = new McpSetupResult { Errors = ["Client configuration is locked"] };

        var outcome = McpOperationCoordinator.Run(install: true, dryRun: false, () => files, () => setup);

        Assert.True(outcome.Files.Success);
        Assert.Equal(12, outcome.Files.FilesAffected);
        Assert.False(outcome.Clients!.Success);
        Assert.False(outcome.FilesSkipped);
    }

    [Fact]
    public void UninstallKeepsFilesWhenClientCleanupFails()
    {
        var filesCalled = false;
        var outcome = McpOperationCoordinator.Run(install: false, dryRun: false,
            () => { filesCalled = true; return new OperationResult(); },
            () => new McpSetupResult { Errors = ["Cannot update selected client"] });

        Assert.False(filesCalled);
        Assert.True(outcome.FilesSkipped);
        Assert.False(outcome.Clients!.Success);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnexpectedClientExceptionBecomesExplicitSetupFailure(bool install)
    {
        var outcome = McpOperationCoordinator.Run(install, dryRun: false,
            () => new OperationResult(), () => throw new IOException("Client file is unavailable"));

        Assert.Equal(!install, outcome.FilesSkipped);
        Assert.Contains("Client file is unavailable", outcome.Clients!.Errors);
    }

    [Fact]
    public void NoSelectedClientsMeansOnlyFilesAreProcessed()
    {
        var calls = 0;
        var outcome = McpOperationCoordinator.Run(install: true, dryRun: false,
            () => { calls++; return new OperationResult(); }, updateClients: null);

        Assert.Equal(1, calls);
        Assert.Null(outcome.Clients);
    }

    [Fact]
    public void ElevationPreservesOriginatingPathsSelectionsAndOverrides()
    {
        var paths = new McpUserPaths(@"C:\Users\Original User", @"C:\Original\Roaming",
            @"C:\Original\Local", @"D:\Original Codex Home");
        var options = new McpSetupOptions(["codex", "claude-desktop"], paths,
            @"D:\Private Python", @"D:\Templates\Empty.flp", @"D:\Music Workspace");

        Assert.Equal(new[]
        {
            "--mcp-profile", paths.Profile,
            "--mcp-app-data", paths.RoamingAppData,
            "--mcp-local-app-data", paths.LocalAppData,
            "--mcp-codex-home", paths.CodexHome,
            "--mcp-clients", "codex,claude-desktop",
            "--mcp-python-runtime", options.PythonRuntimeDirectory!,
            "--mcp-template", options.TemplatePath!,
            "--mcp-workspace", options.WorkspacePath!,
        }, McpElevationArguments.Create(options));
    }

    [Fact]
    public void ElevationPreservesUserPathsWithoutOptingIntoAnyClient()
    {
        var paths = new McpUserPaths(@"C:\User", @"C:\User\Roaming", @"C:\User\Local", @"C:\User\.codex");

        var args = McpElevationArguments.Create(new McpSetupOptions([], paths));

        Assert.Equal(8, args.Count);
        Assert.DoesNotContain("--mcp-clients", args);
        Assert.DoesNotContain("--mcp-python-runtime", args);
        Assert.Contains(paths.Profile, args);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PreviewNeverOffersToLaunchFlOrClaimsARealInstall(bool clientSetupSelected)
    {
        var outcome = new McpOperationOutcome(new OperationResult { DryRun = true },
            clientSetupSelected ? new McpSetupResult() : null);

        var finish = InstallerFinishState.Create(outcome, install: true, dryRun: true,
            "FruityLink", flExecutableExists: true);

        Assert.Contains("Dry run", finish.Headline);
        Assert.False(finish.OfferLaunch);
        Assert.False(finish.LaunchInitiallyChecked);
    }

    [Fact]
    public void SuccessfulClientSetupDoesNotDefaultToLaunchingFl()
    {
        var outcome = new McpOperationOutcome(new OperationResult(), new McpSetupResult());

        var finish = InstallerFinishState.Create(outcome, install: true, dryRun: false,
            "FruityLink", flExecutableExists: true);

        Assert.True(finish.OfferLaunch);
        Assert.False(finish.LaunchInitiallyChecked);
    }

    [Fact]
    public void ClientFailureIsClearlyDifferentFromFailedFileInstallation()
    {
        var outcome = new McpOperationOutcome(new OperationResult(),
            new McpSetupResult { Errors = ["Config is locked"] });

        var finish = InstallerFinishState.Create(outcome, install: true, dryRun: false,
            "FruityLink", flExecutableExists: true);

        Assert.Contains("Files installed", finish.Headline);
        Assert.Contains("needs attention", finish.Headline);
        Assert.False(finish.OfferLaunch);
    }
}
