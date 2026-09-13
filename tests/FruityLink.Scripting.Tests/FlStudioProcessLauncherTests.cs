using FruityLink.Core.Hosting;
using Xunit;

namespace FruityLink.Scripting.Tests;

public sealed class FlStudioProcessLauncherTests
{
    [Fact]
    public void CommandLineQuotesWindowsArgumentsWithoutShellParsing()
    {
        var command = FlStudioProcessLauncher.BuildCommandLine(
            @"C:\Program Files\Image-Line\FL64.exe",
            ["plain", "two words", "", @"ends\", "say\"hello"]);

        Assert.Equal("\"C:\\Program Files\\Image-Line\\FL64.exe\" plain \"two words\" \"\" ends\\ \"say\\\"hello\"", command);
    }

    [Fact]
    public void EnvironmentBlockAppliesOverridesAndEndsWithDoubleNull()
    {
        var name = "FRUITYLINK_LAUNCH_TEST_" + Guid.NewGuid().ToString("N");
        var block = FlStudioProcessLauncher.BuildEnvironmentBlock(new Dictionary<string, string?> { [name] = "héllo" });

        Assert.Contains($"{name}=héllo\0", block, StringComparison.Ordinal);
        Assert.EndsWith("\0\0", block, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PrivateDesktopLeaseOwnsAndTerminatesProcess()
    {
        var executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
        await using var lease = FlStudioProcessLauncher.Start(new(executable)
        {
            Arguments = ["/d", "/c", "ping -n 30 127.0.0.1 > nul"],
            Mode = FlStudioLaunchMode.PrivateDesktop
        });

        Assert.True(lease.ProcessId > 0);
        Assert.StartsWith("FruityLink-", lease.DesktopName, StringComparison.Ordinal);
        Assert.True(lease.StartupGateHeld);
        Assert.Empty(lease.ReadWindows());
        lease.CompleteStartup();
        lease.CompleteStartup();
        await lease.TerminateAsync();
        Assert.True(lease.HasExited);
        Assert.NotNull(lease.ExitCode);
    }

    [Fact]
    public async Task ColdStartsSerializeUntilFirstLeaseReportsReady()
    {
        var executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
        var options = new FlStudioLaunchOptions(executable)
        {
            Arguments = ["/d", "/c", "ping -n 30 127.0.0.1 > nul"],
            StartupTimeout = TimeSpan.FromSeconds(5)
        };
        await using var first = FlStudioProcessLauncher.Start(options);
        var pending = Task.Run(() => FlStudioProcessLauncher.Start(options));

        await Task.Delay(150);
        Assert.False(pending.IsCompleted);
        first.CompleteStartup();
        await using var second = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        second.CompleteStartup();
        await second.TerminateAsync();
        await first.TerminateAsync();
    }

    [Fact]
    public async Task ColdStartWaitHonorsTimeoutAndCancellation()
    {
        var executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
        var arguments = new[] { "/d", "/c", "ping -n 30 127.0.0.1 > nul" };
        await using var first = FlStudioProcessLauncher.Start(new(executable) { Arguments = arguments });

        Assert.Throws<TimeoutException>(() => FlStudioProcessLauncher.Start(new(executable)
        {
            Arguments = arguments,
            StartupTimeout = TimeSpan.FromMilliseconds(100)
        }));
        using var cancellation = new CancellationTokenSource(100);
        Assert.ThrowsAny<OperationCanceledException>(() => FlStudioProcessLauncher.Start(
            new(executable) { Arguments = arguments }, cancellation.Token));
        first.CompleteStartup();
        await first.TerminateAsync();
    }
}
