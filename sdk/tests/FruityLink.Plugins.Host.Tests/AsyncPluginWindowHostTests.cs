using FruityLink.Plugins.Abstractions;
using Xunit;

namespace FruityLink.Plugins.Host.Tests;

public sealed class AsyncPluginWindowHostTests
{
    private static readonly FlWindowOptions Options = new("Editor", 800, 600);

    [Fact]
    public async Task LegacyCloseKeepsTheLeaseUntilAsyncCleanupHasCompleted()
    {
        var native = new AsyncHost { HoldClose = true };
        var scope = new PluginWindowHost(native);
        Assert.True(await scope.TryEmbedAsync((IntPtr)1, Options));
        scope.Close();
        await native.CloseStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task disposal = scope.DisposeAsync().AsTask();
        Assert.False(disposal.IsCompleted);
        Assert.True(native.Visible);
        native.ReleaseClose.SetResult();
        await disposal.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(native.Visible);
        Assert.Equal(1, native.CloseCalls);
        Assert.False(await scope.SetVisibleAsync(true));
        Assert.True(await scope.CloseAsync());
    }

    [Fact]
    public async Task FailedAsyncCloseRetainsTheScopeForRetry()
    {
        var native = new AsyncHost { RefuseClose = true };
        var scope = new PluginWindowHost(native);
        Assert.True(await scope.TryEmbedAsync((IntPtr)1, Options));
        Assert.False(await scope.CloseAsync());
        await Assert.ThrowsAsync<AggregateException>(() => scope.DisposeAsync().AsTask());
        Assert.True(native.Visible);
        native.RefuseClose = false;
        await scope.DisposeAsync();
        Assert.False(native.Visible);
    }

    [Fact]
    public async Task ScopedFactoryCreatesIndependentWindowsAndDisposalClosesAllChildren()
    {
        var factory = new AsyncHost();
        var plugin = new PluginWindowHost(factory);
        var first = Assert.IsAssignableFrom<IAsyncFlWindowHost>(plugin.CreateWindowHost("first", "First"));
        var second = Assert.IsAssignableFrom<IAsyncFlWindowHost>(plugin.CreateWindowHost("second", "Second"));
        Assert.Same(first, plugin.CreateWindowHost("first", "First"));
        Assert.True(await first.TryEmbedAsync((IntPtr)1, Options));
        Assert.True(await second.TryEmbedAsync((IntPtr)2, Options));
        Assert.True(first.IsHostVisible());
        Assert.True(second.IsHostVisible());
        await first.SetVisibleAsync(false);
        Assert.False(first.IsHostVisible());
        Assert.True(second.IsHostVisible());
        await plugin.DisposeAsync();
        Assert.All(factory.Children, child => Assert.Equal(1, child.CloseCalls));
        Assert.Throws<ObjectDisposedException>(() => plugin.CreateWindowHost("late", "Late"));
    }

    private sealed class AsyncHost : IAsyncFlWindowHost, IFlWindowHostFactory
    {
        internal bool HoldClose { get; init; }
        internal bool RefuseClose { get; set; }
        internal bool Visible { get; private set; }
        internal int CloseCalls { get; private set; }
        internal TaskCompletionSource CloseStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleaseClose { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal List<AsyncHost> Children { get; } = [];
        public string LastEmbedReply => RefuseClose ? "err:still-parented" : "ok";
        public int LastInsetX => 0;
        public int LastInsetY => 0;
        public bool IsBridgeAvailable() => throw new InvalidOperationException("No synchronous probe");
        public bool TryEmbed(IntPtr childHwnd, bool show) => throw new InvalidOperationException("No synchronous embed");
        public bool IsHostVisible() => Visible;
        public void SetVisible(bool visible) => throw new InvalidOperationException("No synchronous visibility");
        public void Close() => throw new InvalidOperationException("No synchronous close");
        public void SetStatusHint(string text) { }
        public Task<bool> IsBridgeAvailableAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> TryEmbedAsync(IntPtr childHwnd, FlWindowOptions options, bool show = true, CancellationToken cancellationToken = default)
        {
            Visible = show;
            return Task.FromResult(true);
        }
        public Task<bool> SetVisibleAsync(bool visible, bool activate = false, CancellationToken cancellationToken = default)
        {
            Visible = visible;
            return Task.FromResult(true);
        }
        public async Task<bool> CloseAsync(CancellationToken cancellationToken = default)
        {
            CloseCalls++;
            CloseStarted.TrySetResult();
            if (HoldClose) await ReleaseClose.Task.ConfigureAwait(false);
            if (RefuseClose) return false;
            Visible = false;
            return true;
        }
        public IFlWindowHost CreateWindowHost(string windowId, string caption)
        {
            var child = new AsyncHost();
            Children.Add(child);
            return child;
        }
    }
}
