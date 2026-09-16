using System.Collections.Concurrent;
using System.Text.Json;
using FruityLink.FlStudio.Inject;
using FruityLink.Scripting;
using Xunit;

namespace FruityLink.FlStudio.Tests;

public sealed class NativeCompletionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ScopedTimeoutRetainsNativeLifetimeWithoutHoldingGlobalTransportGate(bool lateFault)
    {
        using var native = new BlockedNative { FailOnRelease = lateFault };
        Task call = RunScopedAsync(new FlInjectBridge());
        await native.AdapterReturned.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            Assert.False(call.IsCompleted);
            Assert.Equal("pong", await new FlInjectBridge().RawAsync("ping").WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally { native.Release.TrySetResult(); }
        await Assert.ThrowsAsync<TimeoutException>(() => call.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task UnscopedTimeoutRemainsBoundedAndNestedScopeRestoresPolicy()
    {
        using var native = new BlockedNative();
        var bridge = new FlInjectBridge();
        Assert.False(InProcBridge.RequiresNativeCompletion);
        using (bridge.RequireNativeCompletion())
        {
            Assert.True(InProcBridge.RequiresNativeCompletion);
            using (bridge.RequireNativeCompletion()) Assert.True(InProcBridge.RequiresNativeCompletion);
            Assert.True(InProcBridge.RequiresNativeCompletion);
        }
        Assert.False(InProcBridge.RequiresNativeCompletion);
        try { await Assert.ThrowsAsync<TimeoutException>(() => bridge.SetTempoAsync(124).WaitAsync(TimeSpan.FromSeconds(5))); }
        finally { native.Release.TrySetResult(); }
    }

    [Fact]
    public async Task BatchContinuesAfterTimeoutOnlyWhenFirstNativeCallActuallyFinishes()
    {
        using var native = new BlockedNative();
        await using var dispatcher = new FlScriptingDispatcher(new FlInjectBridge());
        var batch = dispatcher.BatchAsync(new[]
        {
            new ScriptingCall("set_tempo", JsonSerializer.SerializeToElement(new { bpm = 124 })),
            new ScriptingCall("get_tempo", ScriptingJson.EmptyObject)
        }, stopOnError: false);
        await native.AdapterReturned.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            Assert.False(batch.IsCompleted);
            Assert.DoesNotContain(native.Commands, IsTempoRead);
            Assert.False(dispatcher.DrainAsync().IsCompleted);
        }
        finally { native.Release.TrySetResult(); }
        var result = await batch.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(result.StoppedOnError);
        Assert.NotNull(result.Results[0].Error);
        Assert.Equal(120.0, result.Results[1].Result);
        Assert.Single(native.Commands, IsTempoRead);
    }

    [Fact]
    public async Task CancelledCallerCannotAcknowledgeDrainOrDisposeBeforeActualNativeCompletion()
    {
        using var native = new BlockedNative();
        await using var dispatcher = new FlScriptingDispatcher(new FlInjectBridge());
        using var caller = new CancellationTokenSource();
        var call = dispatcher.InvokeAsync("set_tempo", JsonSerializer.SerializeToElement(new { bpm = 124 }), caller.Token);
        await native.AdapterReturned.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            caller.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
            var queued = dispatcher.InvokeAsync("get_tempo", ScriptingJson.EmptyObject);
            var acknowledgement = dispatcher.DrainAsync();
            var shutdown = dispatcher.DisposeAsync().AsTask();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
            Assert.False(acknowledgement.IsCompleted);
            Assert.False(shutdown.IsCompleted);
            Assert.DoesNotContain(native.Commands, IsTempoRead);
            native.Release.TrySetResult();
            await Task.WhenAll(acknowledgement, shutdown).WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { native.Release.TrySetResult(); }
    }

    [Fact]
    public async Task SoftFailingSymbolProbeStillDrainsBeforeStartingMutation()
    {
        using var native = new BlockedNative { Pause = command => command == "syms" };
        await using var dispatcher = new FlScriptingDispatcher(new FlInjectBridge());
        var call = dispatcher.InvokeAsync("get_tempo", ScriptingJson.EmptyObject);
        await native.AdapterReturned.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            Assert.False(call.IsCompleted);
            Assert.DoesNotContain(native.Commands, IsTempoRead);
        }
        finally { native.Release.TrySetResult(); }
        Assert.Equal(120.0, await call.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task ExplicitScopedCancellationWaitsForActualNativeCall()
    {
        using var native = new BlockedNative { TimeoutMilliseconds = 5000 };
        using var caller = new CancellationTokenSource();
        var call = RunScopedAsync(new FlInjectBridge(), caller.Token);
        await native.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        caller.Cancel();
        await native.AdapterReturned.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try { Assert.False(call.IsCompleted); }
        finally { native.Release.TrySetResult(); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    private static async Task RunScopedAsync(FlInjectBridge bridge, CancellationToken ct = default)
    {
        using var scope = bridge.RequireNativeCompletion();
        await bridge.SetTempoAsync(124, ct);
    }

    private static bool IsTempoRead(string command) => command.StartsWith("call f53fe0 ", StringComparison.Ordinal)
        && command.EndsWith(" 0 2", StringComparison.Ordinal);

    private sealed class BlockedNative : IDisposable
    {
        private readonly Func<string, int, CancellationToken, Task<string>>? _previous = FlInjectBridge.Transport;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AdapterReturned { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ConcurrentQueue<string> Commands { get; } = new();
        public Func<string, bool> Pause { get; init; } = command => command.EndsWith(" 11", StringComparison.Ordinal);
        public bool FailOnRelease { get; init; }
        public int TimeoutMilliseconds { get; init; } = 50;

        public BlockedNative() => FlInjectBridge.Transport = SendAsync;

        private async Task<string> SendAsync(string command, int timeout, CancellationToken ct)
        {
            try { return await InProcBridge.RawAsync(command, Pause(command) ? TimeoutMilliseconds : timeout, ct, RunNative); }
            finally { if (Pause(command)) AdapterReturned.TrySetResult(); }
        }

        private string RunNative(string command)
        {
            Commands.Enqueue(command);
            if (Pause(command))
            {
                Entered.TrySetResult();
                Release.Task.GetAwaiter().GetResult();
                if (FailOnRelease) throw new InvalidOperationException("late native failure");
            }
            return command switch
            {
                "syms" => "{\"ver\":0,\"ok\":102,\"fail\":5,\"complete\":true,\"supported\":true,\"unresolved\":[]}",
                "ping" => "pong",
                _ => "{\"ok\":1,\"ret\":\"0x1d4c0\"}"
            };
        }

        public void Dispose()
        {
            Release.TrySetResult();
            FlInjectBridge.Transport = _previous;
        }
    }
}
