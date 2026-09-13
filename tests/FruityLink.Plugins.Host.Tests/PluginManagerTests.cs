using Xunit;

namespace FruityLink.Plugins.Host.Tests;

public sealed class PluginManagerTests
{
    [Fact]
    public async Task ScriptingRuntimeOwnerIsSharedAcrossPluginReloads()
    {
        await using var scope = new PluginTestScope();
        Assert.True(await scope.Manager.EnableAsync(PluginTestScope.PluginId));
        Assert.True(await scope.Manager.ReloadAsync(PluginTestScope.PluginId));
        Assert.Equal(2, scope.ScriptingTypes.Count);
        Assert.All(scope.ScriptingTypes, type => Assert.Same(typeof(FruityLink.Scripting.FlScriptingDispatcher), type));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    public async Task DiscoversPrivateDependencyFromShadowCopy(bool flat, bool includeDeps)
    {
        await using var scope = new PluginTestScope(flat, includeDeps: includeDeps);
        Assert.Equal("dependency-ok", Assert.Single(scope.Manager.List()).Version);
        Assert.True(await scope.Manager.EnableAsync(PluginTestScope.PluginId));
        using var writableOriginal = File.Open(scope.PluginDll, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Fact]
    public async Task FailedEnableCleansResourcesAndRetryUsesFreshInstance()
    {
        await using var scope = new PluginTestScope();
        scope.OnEvent = message => { if (message.StartsWith("enable:", StringComparison.Ordinal)) throw new InvalidOperationException("activation failed"); };
        Assert.False(await scope.Manager.EnableAsync(PluginTestScope.PluginId));
        Assert.Contains("disable:False", scope.Events);
        Assert.Equal("[]", scope.Manager.MenuRegistry.ListJson());
        Assert.Equal("[]", scope.Manager.ToolbarRegistry.ListJson());
        Assert.False(Assert.Single(scope.Manager.List()).Loaded);
        scope.OnEvent = null;
        Assert.True(await scope.Manager.EnableAsync(PluginTestScope.PluginId));
        Assert.Equal(2, scope.Events.Count(e => e == "enable:1"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisableCleansPrewarmEvenWhenPrepareFailed(bool failPrepare)
    {
        await using var scope = new PluginTestScope(enabled: true);
        scope.OnEvent = message => { if (failPrepare && message == "prepare") throw new InvalidOperationException("prepare failed"); };
        await scope.Manager.PreWarmEnabledAsync();
        Assert.True(await scope.Manager.DisableAsync(PluginTestScope.PluginId));
        Assert.Contains("disable:False", scope.Events);
        Assert.Equal("[]", scope.Manager.MenuRegistry.ListJson());
    }

    [Fact]
    public async Task PrewarmAndEnableShareContextAndRemainIdempotent()
    {
        await using var scope = new PluginTestScope(enabled: true);
        await scope.Manager.PreWarmEnabledAsync();
        await scope.Manager.PreWarmEnabledAsync();
        Assert.True(await scope.Manager.EnableAsync(PluginTestScope.PluginId));
        Assert.True(await scope.Manager.EnableAsync(PluginTestScope.PluginId));
        Assert.Equal(1, scope.Events.Count(e => e == "prepare"));
        Assert.Equal(1, scope.Events.Count(e => e == "enable:1"));
    }

    [Fact]
    public async Task FailedDisableStillRemovesContributions()
    {
        await using var scope = new PluginTestScope();
        Assert.True(await scope.Manager.EnableAsync(PluginTestScope.PluginId));
        scope.OnEvent = message => { if (message.StartsWith("disable:", StringComparison.Ordinal)) throw new InvalidOperationException("disable failed"); };
        Assert.False(await scope.Manager.DisableAsync(PluginTestScope.PluginId));
        Assert.Equal("[]", scope.Manager.MenuRegistry.ListJson());
        Assert.Equal("[]", scope.Manager.ToolbarRegistry.ListJson());
        Assert.False(scope.Manager.IsEnabled(PluginTestScope.PluginId));
        Assert.True(Assert.Single(scope.Manager.List()).Loaded);
        Assert.False(await scope.Manager.EnableAsync(PluginTestScope.PluginId));
        scope.OnEvent = null;
        Assert.True(await scope.Manager.DisableAsync(PluginTestScope.PluginId));
        Assert.False(Assert.Single(scope.Manager.List()).Loaded);
    }

    [Fact]
    public async Task InvalidReplacementKeepsRunningPluginAndReturnsFailure()
    {
        await using var scope = new PluginTestScope();
        Assert.True(await scope.Manager.EnableAsync(PluginTestScope.PluginId));
        File.WriteAllText(scope.PluginDll, "an interrupted build");
        Assert.False(await scope.Manager.ReloadAsync(PluginTestScope.PluginId));
        Assert.DoesNotContain("disable:False", scope.Events);
        Assert.True(Assert.Single(scope.Manager.List()).Loaded);
        Assert.True(scope.Manager.IsEnabled(PluginTestScope.PluginId));
        Assert.Single(Directory.GetDirectories(scope.ShadowRoot));
    }

    [Fact]
    public async Task RediscoveryDoesNotCreateExtraCopiesOrInstances()
    {
        await using var scope = new PluginTestScope();
        string originalShadow = Assert.Single(Directory.GetDirectories(scope.ShadowRoot));
        scope.Manager.Discover();
        Assert.Equal(originalShadow, Assert.Single(Directory.GetDirectories(scope.ShadowRoot)));
        Assert.Single(scope.Manager.List());
    }

    [Fact]
    public async Task ReloadKeepsDesiredStateAndUsesFreshInstance()
    {
        await using var scope = new PluginTestScope();
        Assert.True(await scope.Manager.EnableAsync(PluginTestScope.PluginId));
        Assert.True(await scope.Manager.ReloadAsync(PluginTestScope.PluginId));
        Assert.True(scope.Manager.IsEnabled(PluginTestScope.PluginId));
        Assert.Equal(2, scope.Events.Count(e => e == "enable:1"));
        Assert.Equal(1, scope.Events.Count(e => e == "disable:False"));
    }

    [Fact]
    public async Task DisposeDuringActivationDoesNotInvalidateGateRelease()
    {
        await using var scope = new PluginTestScope();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        scope.WaitForEnable = () => { entered.SetResult(); return release.Task; };
        Task<bool> activation = scope.Manager.EnableAsync(PluginTestScope.PluginId);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        scope.Manager.Dispose();
        release.SetResult();
        Assert.True(await activation.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(await scope.Manager.EnableAsync(PluginTestScope.PluginId));
        Assert.False(await scope.Manager.ReloadAsync(PluginTestScope.PluginId));
    }
}
