using FruityLink.Installer.Core;
using Xunit;

namespace FruityLink.Installer.Tests;

public sealed class McpCompanionProcessesTests
{
    private const string FlRoot = @"C:\FL Studio 2026";
    private static string Server(string root) => Path.Combine(root,
        "FruityLink", "tools", "fl-mcp", "server", "FlMcp.Server.exe");

    [Fact]
    public void StopsOnlyExactSelectedExecutableAndWaitsBeforeReturning()
    {
        var target = new FakeProcess(Server(FlRoot).ToUpperInvariant());
        var otherInstall = new FakeProcess(Server(@"C:\FL Studio 2025"));
        var similarDirectory = new FakeProcess(Server(FlRoot + " backup"));
        var child = new FakeProcess(Path.Combine(FlRoot, "FL64.exe"));
        var processes = new[] { target, otherInstall, similarDirectory, child };
        var manager = Manager(() => processes);

        Assert.True(manager.Close(FlRoot + @"\.\", new TestLog()));

        Assert.Equal(1, target.Terminations);
        Assert.Equal(1, target.Waits);
        Assert.True(target.HasExited);
        Assert.All(new[] { otherInstall, similarDirectory, child }, process =>
        {
            Assert.Equal(0, process.Terminations);
            Assert.False(process.HasExited);
        });
        Assert.All(processes, process => Assert.True(process.Disposed));
    }

    [Fact]
    public void UnconfirmedExitFailsAndPreservesOtherInstall()
    {
        var target = new FakeProcess(Server(FlRoot)) { ExitOnWait = false };
        var other = new FakeProcess(Server(@"C:\Other FL"));
        var log = new TestLog();

        Assert.False(Manager(() => new[] { target, other }).Close(FlRoot, log));

        Assert.Equal(1, target.Terminations);
        Assert.Equal(0, other.Terminations);
        Assert.Contains(log.Messages, message => message.Contains("Close its AI clients"));
        Assert.True(other.Disposed);
    }

    [Fact]
    public void UnknownExecutableIdentityFailsWithoutTerminatingIt()
    {
        var unknown = new FakeProcess(Server(FlRoot)) { IdentityError = new UnauthorizedAccessException() };
        var other = new FakeProcess(Server(@"C:\Other FL"));

        Assert.False(Manager(() => new[] { unknown, other }).Close(FlRoot, new TestLog()));

        Assert.Equal(0, unknown.Terminations);
        Assert.Equal(0, other.Terminations);
        Assert.True(unknown.Disposed);
        Assert.True(other.Disposed);
    }

    [Fact]
    public void RepeatedClientRestartsAreBoundedAndBlockFileChanges()
    {
        var restarted = new List<FakeProcess>();
        var manager = Manager(() =>
        {
            var next = new FakeProcess(Server(FlRoot));
            restarted.Add(next);
            return new[] { next };
        });

        Assert.False(manager.Close(FlRoot, new TestLog()));

        Assert.Equal(4, restarted.Count);
        Assert.Equal(3, restarted.Sum(process => process.Terminations));
        Assert.False(restarted[^1].HasExited);
        Assert.All(restarted, process => Assert.True(process.Disposed));
    }

    [Fact]
    public void ExitBetweenInspectionAndTerminationIsAccepted()
    {
        var target = new FakeProcess(Server(FlRoot)) { ExitDuringTerminate = true };

        Assert.True(Manager(() => new[] { target }).Close(FlRoot, new TestLog()));

        Assert.True(target.HasExited);
        Assert.Equal(0, target.Waits);
    }

    [Fact]
    public void EnumerationFailureIsReportedAsFailure()
    {
        var log = new TestLog();

        Assert.False(Manager(() => throw new InvalidOperationException("enumeration failed"))
            .Close(FlRoot, log));

        Assert.Contains(log.Messages, message => message.Contains("enumeration failed"));
    }

    private static McpCompanionProcesses Manager(Func<IReadOnlyList<IMcpCompanionProcess>> enumerate) =>
        new(enumerate, exitTimeout: TimeSpan.Zero, settleMilliseconds: 0);

    private sealed class FakeProcess(string path) : IMcpCompanionProcess
    {
        public int Id => 123;
        public string ExecutablePath => IdentityError is null ? path : throw IdentityError;
        public Exception? IdentityError { get; init; }
        public bool HasExited { get; private set; }
        public bool ExitOnWait { get; init; } = true;
        public bool ExitDuringTerminate { get; init; }
        public int Terminations { get; private set; }
        public int Waits { get; private set; }
        public bool Disposed { get; private set; }

        public void Terminate()
        {
            Terminations++;
            if (!ExitDuringTerminate) return;
            HasExited = true;
            throw new InvalidOperationException("process exited");
        }

        public bool WaitForExit(int milliseconds)
        {
            Waits++;
            HasExited = ExitOnWait;
            return HasExited;
        }

        public void Dispose() => Disposed = true;
    }

    private sealed class TestLog : IProgressLog
    {
        public List<string> Messages { get; } = new();
        public void Log(LogLevel level, string message) => Messages.Add(message);
    }
}
