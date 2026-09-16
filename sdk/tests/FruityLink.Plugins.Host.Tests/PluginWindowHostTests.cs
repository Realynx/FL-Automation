using System.Collections.Concurrent;
using FruityLink.Plugins.Abstractions;
using Xunit;

namespace FruityLink.Plugins.Host.Tests;

public sealed class PluginWindowHostTests
{
    [Fact]
    public async Task ThrowingPartialEmbedClosesBeforeReleasingTheSlot()
    {
        var native = new WindowHost { ThrowOnEmbed = true };
        await using var first = new PluginWindowHost(native);
        await using var second = new PluginWindowHost(native);
        Assert.Throws<InvalidOperationException>(() => first.TryEmbed((IntPtr)1, true));
        Assert.Equal(1, native.Closes);
        Assert.False(native.Visible);
        native.ThrowOnEmbed = false;
        Assert.True(second.TryEmbed((IntPtr)2, true));
    }

    [Fact]
    public async Task FailedNativeCleanupRetainsPluginUntilDisableRetrySucceeds()
    {
        var native = new WindowHost { ThrowOnClose = true };
        await using var scope = new PluginTestScope(windows: native);
        scope.OnWindows = windows => Assert.True(windows.TryEmbed((IntPtr)1, true));
        Assert.True(await scope.Manager.EnableAsync(PluginTestScope.PluginId));
        Assert.False(await scope.Manager.DisableAsync(PluginTestScope.PluginId));
        Assert.True(native.Visible);
        Assert.False(await scope.Manager.EnableAsync(PluginTestScope.PluginId));
        Assert.False(await scope.Manager.ReloadAsync(PluginTestScope.PluginId));
        Assert.Single(scope.UiHosts); // Neither enable nor reload replaces the unresolved UI owner.
        native.ThrowOnClose = false;
        Assert.True(await scope.Manager.DisableAsync(PluginTestScope.PluginId));
        Assert.False(native.Visible);
        Assert.True(await scope.Manager.EnableAsync(PluginTestScope.PluginId));
        Assert.Equal(2, scope.UiHosts.Count);
    }

    [Fact]
    public async Task OnlyOwnerMayControlSlotAndCloseMakesItAvailableToAnotherPlugin()
    {
        var native = new WindowHost();
        await using var first = new PluginWindowHost(native);
        await using var second = new PluginWindowHost(native);
        Assert.True(first.TryEmbed((IntPtr)1, true));
        Assert.False(second.TryEmbed((IntPtr)2, true));
        Assert.Contains("another-plugin", second.LastEmbedReply);
        Assert.False(second.IsHostVisible());
        second.SetVisible(false);
        second.Close();
        Assert.True(first.IsHostVisible());
        Assert.Equal(1, native.Embeds);
        Assert.Equal(0, native.Closes);
        first.SetVisible(false);
        Assert.False(native.Visible);
        first.Close();
        Assert.True(second.TryEmbed((IntPtr)2, true));
        Assert.Equal(1, native.Closes);
    }

    [Fact]
    public async Task FailedEmbedDoesNotKeepTheSlot()
    {
        var native = new WindowHost { CanEmbed = false };
        await using var first = new PluginWindowHost(native);
        await using var second = new PluginWindowHost(native);
        Assert.False(first.TryEmbed((IntPtr)1, true));
        native.CanEmbed = true;
        Assert.True(second.TryEmbed((IntPtr)2, true));
        first.Close();
        Assert.Equal(0, native.Closes);
    }

    [Fact]
    public async Task DisablingFailedPluginClosesItsSlotAndRevokesStaleCallbacks()
    {
        var native = new WindowHost();
        await using var scope = new PluginTestScope(windows: native);
        IFlWindowHost? stale = null;
        scope.OnWindows = windows => { stale = windows; Assert.True(windows.TryEmbed((IntPtr)1, true)); };
        Assert.True(await scope.Manager.EnableAsync(PluginTestScope.PluginId));
        scope.OnEvent = message => { if (message.StartsWith("disable:", StringComparison.Ordinal)) throw new InvalidOperationException("broken plugin cleanup"); };
        Assert.False(await scope.Manager.DisableAsync(PluginTestScope.PluginId));
        Assert.Equal(1, native.Closes);
        Assert.NotNull(stale);
        Assert.False(stale.TryEmbed((IntPtr)1, true));
        stale.SetVisible(true);
        stale.Close();
        Assert.Equal(1, native.Closes);
        Assert.False(native.Visible);
        await using var next = new PluginWindowHost(native);
        Assert.True(next.TryEmbed((IntPtr)2, true));
    }

    [Fact]
    public async Task ScopeCleanupReturnsToTheChildsOriginalUiContext()
    {
        using var ui = new UiContext();
        var native = new WindowHost();
        var scope = new PluginWindowHost(native);
        await ui.Run(() => Assert.True(scope.TryEmbed((IntPtr)1, true))).WaitAsync(TimeSpan.FromSeconds(5));
        await scope.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(ui.ThreadId, native.CloseThread);
        Assert.Equal(1, native.Closes);
    }

    private sealed class WindowHost : IFlWindowHost
    {
        public bool CanEmbed { get; set; } = true;
        public bool ThrowOnEmbed { get; set; }
        public bool ThrowOnClose { get; set; }
        public bool Visible { get; private set; }
        public int Embeds { get; private set; }
        public int Closes { get; private set; }
        public int CloseThread { get; private set; }
        public string LastEmbedReply => CanEmbed ? "ok" : "err:unsupported-build";
        public int LastInsetX => 2;
        public int LastInsetY => 24;
        public bool IsBridgeAvailable() => true;
        public bool TryEmbed(IntPtr childHwnd, bool show)
        {
            Embeds++;
            Visible = CanEmbed && show;
            if (ThrowOnEmbed) throw new InvalidOperationException("partial native embed");
            return CanEmbed;
        }
        public bool IsHostVisible() => Visible;
        public void SetVisible(bool visible) => Visible = visible;
        public void Close()
        {
            Closes++;
            CloseThread = Environment.CurrentManagedThreadId;
            if (ThrowOnClose) throw new InvalidOperationException("native close failed");
            Visible = false;
        }
        public void SetStatusHint(string text) { }
    }

    private sealed class UiContext : SynchronizationContext, IDisposable
    {
        private readonly BlockingCollection<Action> _queue = new();
        private readonly Thread _thread;
        public int ThreadId => _thread.ManagedThreadId;

        public UiContext()
        {
            _thread = new Thread(() =>
            {
                SetSynchronizationContext(this);
                foreach (Action action in _queue.GetConsumingEnumerable()) action();
            }) { IsBackground = true };
            _thread.Start();
        }

        public override void Post(SendOrPostCallback callback, object? state) => _queue.Add(() => callback(state));

        public Task Run(Action action)
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Post(_ =>
            {
                try { action(); completion.SetResult(); }
                catch (Exception ex) { completion.SetException(ex); }
            }, null);
            return completion.Task;
        }

        public void Dispose()
        {
            _queue.CompleteAdding();
            Assert.True(_thread.Join(TimeSpan.FromSeconds(5)));
            _queue.Dispose();
        }
    }
}
