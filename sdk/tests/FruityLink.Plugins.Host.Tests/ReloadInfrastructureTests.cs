using Xunit;

namespace FruityLink.Plugins.Host.Tests;

public sealed class ReloadInfrastructureTests
{
    [Fact]
    public async Task SecondHostDoesNotDeleteFirstHostsShadowDependencies()
    {
        await using var scope = new PluginTestScope();
        string shadowDir = Assert.Single(Directory.GetDirectories(scope.ShadowRoot));
        string privateDll = Path.Combine(shadowDir, "PluginPrivateDependency.dll");
        Assert.True(File.Exists(privateDll));
        _ = new ShadowCopyStore(scope.ShadowRoot, scope.PluginsDir, _ => { });
        Assert.True(File.Exists(privateDll));
    }

    [Fact]
    public async Task DependencyAndSidecarChangesReloadOwningPackageOnly()
    {
        await using var scope = new PluginTestScope();
        string sibling = Path.Combine(scope.PluginsDir, "fixture-extra", "Other.dll");
        string package = Path.GetDirectoryName(scope.PluginDll)!;
        string[] changed = [Path.Combine(package, "PluginPrivateDependency.dll"), Path.Combine(package, "LifecycleFixture.deps.json")];
        var targets = PluginReloadTargets.Resolve(scope.PluginsDir, [scope.PluginDll, sibling], changed);
        Assert.Contains(scope.PluginDll, targets);
        Assert.DoesNotContain(sibling, targets);
        Assert.Equal(1, targets.Count(p => p == scope.PluginDll));
    }

    [Fact]
    public async Task BusyFileIsRetriedWithoutDispatchingIncompleteBatch()
    {
        await using var scope = new PluginTestScope();
        var timedOut = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var delivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var watcher = new PluginHotReloader(scope.PluginsDir,
            (_, _) => { delivered.TrySetResult(); return Task.CompletedTask; },
            message => { if (message.Contains("retrying later", StringComparison.Ordinal)) timedOut.TrySetResult(); },
            debounceMs: 25, stableWaitMaxMs: 100, pollMs: 20);
        string changed = Path.Combine(scope.PluginsDir, "busy.dll");
        using (var writer = File.Open(changed, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            writer.WriteByte(1);
            writer.Flush();
            await timedOut.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(delivered.Task.IsCompleted);
        }
        await delivered.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task DisposingWatcherCancelsBusyBatch()
    {
        await using var scope = new PluginTestScope();
        var timedOut = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int delivered = 0;
        using var watcher = new PluginHotReloader(scope.PluginsDir,
            (_, _) => { Interlocked.Increment(ref delivered); return Task.CompletedTask; },
            message => { if (message.Contains("retrying later", StringComparison.Ordinal)) timedOut.TrySetResult(); },
            debounceMs: 25, stableWaitMaxMs: 100, pollMs: 20);
        string changed = Path.Combine(scope.PluginsDir, "busy.dll");
        using (var writer = File.Open(changed, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            writer.WriteByte(1);
            writer.Flush();
            await timedOut.Task.WaitAsync(TimeSpan.FromSeconds(5));
            watcher.Dispose();
        }
        await Task.Delay(200);
        Assert.Equal(0, Volatile.Read(ref delivered));
    }
}
