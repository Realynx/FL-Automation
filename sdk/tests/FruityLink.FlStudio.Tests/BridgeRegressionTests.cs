using FruityLink.FlStudio.Inject;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace FruityLink.FlStudio.Tests;

public sealed class BridgeRegressionTests : IDisposable
{
    private readonly Func<string, int, CancellationToken, Task<string>>? _original = FlInjectBridge.Transport;

    public void Dispose() => FlInjectBridge.Transport = _original;

    [Fact]
    public async Task LoadedBridgeIsUnavailableUntilFlReadinessSucceeds()
    {
        bool ready = false;
        FlInjectBridge.Transport = (message, _, _) => Task.FromResult(message == "ping" ? "pong" : ready ? "1" : "0");
        var bridge = new FlInjectBridge();
        Assert.True(await bridge.IsLoadedAsync());
        Assert.False(await bridge.IsAvailableAsync());
        ready = true;
        Assert.True(await bridge.IsAvailableAsync());
    }

    [Fact]
    public async Task StartupCallReportsNativeErrorInsteadOfJsonParsingNoise()
    {
        FlInjectBridge.Transport = (_, _, _) => Task.FromResult("err:no-mainwindow");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new FlInjectBridge().GetTempoAsync());
        Assert.Contains("err:no-mainwindow", error.Message);
    }

    [Theory]
    [InlineData(192, "4068000000000000")]
    [InlineData(-192, "0")]
    public async Task SeekUsesVersionResolvedSymbol(int tick, string bits)
    {
        string? command = null;
        FlInjectBridge.Transport = (message, _, _) =>
        {
            command = message;
            return Task.FromResult("{\"ok\":1,\"ret\":\"0x0\",\"xmm0\":\"0x0\"}");
        };

        await new FlInjectBridge().SeekAsync(tick);

        Assert.Equal($"callf sym:FLtr_SeekToSongTick {bits} 0", command);
    }

    [Fact]
    public async Task SymbolCacheIsNotReusedAfterTransportChanges()
    {
        FlInjectBridge.Transport = (_, _, _) => Task.FromResult("{\"ver\":1,\"ok\":1,\"fail\":0}");
        var bridge = new FlInjectBridge();
        Assert.Equal(1, (await bridge.GetSymbolStatusAsync())!.Version);

        FlInjectBridge.Transport = (_, _, _) => Task.FromResult(
            "{\"ver\":2,\"ok\":1,\"fail\":1,\"unresolved\":[{\"name\":\"HostClassRef\"}]}");

        var status = await bridge.GetSymbolStatusAsync();
        Assert.Equal(2, status!.Version);
        Assert.Contains("HostClassRef", status.Unresolved);
    }

    [Fact]
    public async Task ReadinessResponseDoesNotPoisonSymbolCache()
    {
        int calls = 0;
        FlInjectBridge.Transport = (_, _, _) => Task.FromResult(++calls == 1
            ? "{\"ver\":0,\"ok\":0,\"fail\":0}"
            : "{\"ver\":2,\"ok\":1,\"fail\":0}");

        var bridge = new FlInjectBridge();
        Assert.Null(await bridge.GetSymbolStatusAsync());
        Assert.Equal(2, (await bridge.GetSymbolStatusAsync())!.Version);
        Assert.Equal(2, (await new FlInjectBridge().GetSymbolStatusAsync())!.Version);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task SymbolQueryPropagatesCallerCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        FlInjectBridge.Transport = (_, _, token) =>
        {
            cancellation.Cancel();
            return Task.FromCanceled<string>(token);
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new FlInjectBridge().GetSymbolStatusAsync(cancellation.Token));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1000)]
    public async Task CancelledInProcessCommandNeverLoadsOrCallsNativeBridge(int timeout)
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            InProcBridge.RawAsync("mutate", timeout, cancellation.Token));
    }

    [Fact]
    public async Task BridgeInstancesSerializeTransportCalls()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        FlInjectBridge.Transport = async (_, _, token) =>
        {
            Interlocked.Increment(ref calls);
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
            return "pong";
        };

        Task<string> first = new FlInjectBridge().RawAsync("ping");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var cancellation = new CancellationTokenSource();
        Task<string> second = new FlInjectBridge().RawAsync("ping", ct: cancellation.Token);
        try
        {
            Assert.Equal(1, Volatile.Read(ref calls));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
        }
        finally { release.TrySetResult(); }
        Assert.Equal("pong", await first);
        Assert.Equal(1, calls);
    }
}
