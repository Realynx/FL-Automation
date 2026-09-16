using FruityLink.Plugins.Abstractions;
using Xunit;

namespace FruityLink.Plugins.Host.Tests;

public sealed class WindowVisibilityTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "fruitylink-visibility-" + Guid.NewGuid().ToString("N"));
    private WindowVisibilityStore Store => new(_directory, _ => { });
    private static readonly FlWindowOptions Options = new("Test", 640, 480);

    [Fact]
    public async Task ManagerSuspendsPersistenceBeforeCallingPluginDisable()
    {
        await using var fixture = new PluginTestScope();
        IFlWindowVisibilityState? preference = null;
        fixture.OnWindows = windows => preference = Assert.IsAssignableFrom<IFlWindowVisibilityState>(windows);
        Assert.True(await fixture.Manager.EnableAsync(PluginTestScope.PluginId));
        Assert.NotNull(preference);
        preference.RememberVisibility(true);
        fixture.OnEvent = message => { if (message.StartsWith("disable:", StringComparison.Ordinal)) preference.RememberVisibility(false); };
        Assert.True(await fixture.Manager.DisableAsync(PluginTestScope.PluginId));
        Assert.True(await fixture.Manager.EnableAsync(PluginTestScope.PluginId));
        Assert.True(preference.StartupVisible);
    }

    [Fact]
    public async Task NativeCloseRestoresHiddenAndMenuReopenRestoresVisibleAcrossNewScopes()
    {
        var native = new Host();
        await using (var first = new PluginWindowHost(native, Store, "editor"))
        {
            Assert.True(await first.TryEmbedAsync((IntPtr)1, Options));
            native.UserClose();
        }
        await using (var next = new PluginWindowHost(native, Store, "EDITOR"))
        {
            Assert.False(next.StartupVisible);
            Assert.True(await next.TryEmbedAsync((IntPtr)2, Options));
            Assert.False(native.Visible);
            Assert.True(await next.SetVisibleAsync(true));
        }
        await using var reopened = new PluginWindowHost(native, Store, "editor");
        Assert.True(reopened.StartupVisible);
    }

    [Fact]
    public async Task TeardownHideAndLateNativeNotificationDoNotReplaceUserChoice()
    {
        var native = new Host();
        await using (var scope = new PluginWindowHost(native, Store, "editor"))
        {
            Assert.True(await scope.TryEmbedAsync((IntPtr)1, Options));
            scope.RememberVisibility(true);
            scope.SuspendVisibilityPersistence();
            await scope.SetVisibleAsync(false);
            native.UserClose();
            scope.RememberVisibility(false);
        }
        Assert.True(Store.Load("editor", ""));
    }

    [Fact]
    public async Task FailedShowKeepsSavedHiddenPreference()
    {
        Store.Save("editor", "", false);
        var native = new Host { FailShow = true };
        await using var scope = new PluginWindowHost(native, Store, "editor");
        Assert.True(await scope.TryEmbedAsync((IntPtr)1, Options));
        Assert.False(await scope.SetVisibleAsync(true));
        Assert.False(Store.Load("editor", ""));
    }

    [Fact]
    public async Task WindowIdsAndOtherPluginsHaveIndependentPreferences()
    {
        await using (var scope = new PluginWindowHost(new Host(), Store, "editor"))
        {
            var first = Assert.IsAssignableFrom<IFlWindowVisibilityState>(scope.CreateWindowHost("a/b", "First"));
            first.RememberVisibility(false);
            var second = Assert.IsAssignableFrom<IFlWindowVisibilityState>(scope.CreateWindowHost("a", "Second"));
            Assert.True(second.StartupVisible);
        }
        await using var next = new PluginWindowHost(new Host(), Store, "editor");
        Assert.False(Assert.IsAssignableFrom<IFlWindowVisibilityState>(next.CreateWindowHost("a/b", "First")).StartupVisible);
        Assert.True(next.StartupVisible);
        await using var other = new PluginWindowHost(new Host(), Store, "other");
        Assert.True(Assert.IsAssignableFrom<IFlWindowVisibilityState>(other.CreateWindowHost("a/b", "First")).StartupVisible);
    }

    [Fact]
    public async Task NotificationCannotHideAnotherScopesPreferenceOnLegacySharedHost()
    {
        var native = new Host();
        await using var owner = new PluginWindowHost(native, Store, "owner");
        await using var other = new PluginWindowHost(native, Store, "other");
        Assert.True(await owner.TryEmbedAsync((IntPtr)1, Options));
        native.UserClose();
        Assert.False(owner.StartupVisible);
        Assert.True(other.StartupVisible);
    }

    [Fact]
    public void MissingOrMalformedStateDoesNotPreventStartup()
    {
        Assert.True(Store.Load("editor", ""));
        Assert.False(Directory.Exists(_directory));
        Store.Save("editor", "", false);
        File.WriteAllText(Assert.Single(Directory.GetFiles(_directory)), "broken");
        Assert.True(Store.Load("editor", ""));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private sealed class Host : IAsyncFlWindowHost, IFlWindowHostFactory, IFlWindowVisibilityNotifications
    {
        public event Action<bool>? UserVisibilityChanged;
        internal bool Visible { get; private set; }
        internal bool FailShow { get; init; }
        internal void UserClose() { Visible = false; UserVisibilityChanged?.Invoke(false); }
        public bool IsBridgeAvailable() => true;
        public string LastEmbedReply => "ok";
        public int LastInsetX => 0;
        public int LastInsetY => 0;
        public bool TryEmbed(IntPtr childHwnd, bool show) { Visible = show; return true; }
        public bool IsHostVisible() => Visible;
        public void SetVisible(bool visible) => Visible = visible;
        public void Close() => Visible = false;
        public void SetStatusHint(string text) { }
        public Task<bool> IsBridgeAvailableAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> TryEmbedAsync(IntPtr childHwnd, FlWindowOptions options, bool show = true, CancellationToken cancellationToken = default)
            => Task.FromResult(TryEmbed(childHwnd, show));
        public Task<bool> SetVisibleAsync(bool visible, bool activate = false, CancellationToken cancellationToken = default)
        {
            if (FailShow) return Task.FromResult(false);
            Visible = visible;
            return Task.FromResult(true);
        }
        public Task<bool> CloseAsync(CancellationToken cancellationToken = default) { Close(); return Task.FromResult(true); }
        public IFlWindowHost CreateWindowHost(string windowId, string caption) => new Host();
    }
}
