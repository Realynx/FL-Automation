using FruityLink.Installer.Core;
using Xunit;

namespace FruityLink.Installer.Tests;

public sealed class CompanionInstallTests
{
    private const string FlRoot = @"C:\FL Studio 2026";
    private const string Source = @"C:\payload\FlMcp.Server.exe";
    private static string Target => Path.Combine(FlRoot,
        "FruityLink", "tools", "fl-mcp", "server", "FlMcp.Server.exe");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CompanionShutdownCompletesBeforeAnyInstallOrUninstallMutation(bool uninstall)
    {
        var fs = Files();
        var processes = new TestProcesses(() => Assert.Equal("old", fs.ReadAllText(Target)));
        var engine = new InstallEngine(fs, "test", processes);

        var result = Execute(engine, uninstall, dryRun: false);

        Assert.True(result.Success);
        Assert.Equal(new[] { "fl", FlRoot }, processes.Calls);
        Assert.Equal(1, result.FilesAffected);
        if (uninstall) Assert.False(fs.FileExists(Target));
        else Assert.Equal("new", fs.ReadAllText(Target));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedCompanionShutdownAbortsWithoutChangingOrDeferringAnyFile(bool uninstall)
    {
        var fs = Files();
        var processes = new TestProcesses(() => { }, success: false);
        var engine = new InstallEngine(fs, "test", processes);

        var result = Execute(engine, uninstall, dryRun: false);

        Assert.Equal(OperationOutcome.Failed, result.Outcome);
        Assert.Equal(0, result.ActionsExecuted);
        Assert.Equal(0, result.FilesAffected);
        Assert.Single(result.Errors);
        Assert.Empty(result.RebootPending);
        Assert.Empty(fs.RebootScheduledDeletes);
        Assert.Equal("old", fs.ReadAllText(Target));
        Assert.Equal("new", fs.ReadAllText(Source));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DryRunDoesNotInspectOrStopCompanions(bool uninstall)
    {
        var fs = Files();
        var processes = new TestProcesses(() => throw new InvalidOperationException("must not run"));
        var engine = new InstallEngine(fs, "test", processes);

        var result = Execute(engine, uninstall, dryRun: true);

        Assert.True(result.Success);
        Assert.Empty(processes.Calls);
        Assert.Equal("old", fs.ReadAllText(Target));
    }

    private static InMemoryFileSystem Files()
    {
        var fs = new InMemoryFileSystem();
        fs.WriteAllText(Source, "new");
        fs.WriteAllText(Target, "old");
        return fs;
    }

    private static OperationResult Execute(InstallEngine engine, bool uninstall, bool dryRun)
    {
        var action = new InstallAction
        {
            Kind = uninstall ? ActionKind.DeleteFile : ActionKind.CopyFile,
            Source = Source,
            Target = Target,
        };
        return uninstall
            ? engine.ExecuteUninstall(new[] { action }, dryRun, new TestLog(), FlRoot)
            : engine.ExecuteInstall(new[] { action }, FlRoot, dryRun, new TestLog());
    }

    private sealed class TestProcesses(Action beforeShutdown, bool success = true) : IProcessManager
    {
        public List<string> Calls { get; } = new();
        public FlCloseResult CloseFlStudio(IProgressLog log)
        {
            Calls.Add("fl");
            return new FlCloseResult();
        }

        public bool CloseMcpCompanions(string flPath, IProgressLog log)
        {
            beforeShutdown();
            Calls.Add(flPath);
            return success;
        }
    }

    private sealed class TestLog : IProgressLog
    {
        public void Log(LogLevel level, string message) { }
    }
}
