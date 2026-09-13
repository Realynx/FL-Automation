using System.Reflection;
using System.Text.Json;
using FruityLink.Core.Abstractions;
using FruityLink.Plugins.PythonIde.Execution;
using FruityLink.Scripting;
using Xunit;

namespace FruityLink.Plugins.PythonIde.Execution.Tests;

public sealed class ExecutionTests
{
    [Fact]
    public async Task CatalogIsGeneratedWithoutInitializingPythonOrCallingFl()
    {
        var fl = DispatchProxy.Create<INativeFlControl, ControlProxy>();
        var created = 0;
        await using var service = new PythonIdeExecutionService(fl, handler => { created++; return new CallbackRuntime(handler); });
        var catalog = await service.GetCatalogAsync("tempo");
        Assert.Equal(0, created);
        Assert.Empty(((ControlProxy)fl).Calls);
        Assert.Contains(catalog.GetProperty("operations").EnumerateArray(), item => item.GetProperty("name").GetString() == "get_tempo");
    }

    [Fact]
    public async Task ExecutionsReuseOneLeaseAndCallTheSharedTypedDispatcher()
    {
        var fl = DispatchProxy.Create<INativeFlControl, ControlProxy>();
        var created = 0;
        await using var service = new PythonIdeExecutionService(fl, handler => { created++; return new CallbackRuntime(handler); });
        var first = await service.ExecuteAsync("first", 15);
        var second = await service.ExecuteAsync("second", 20);
        Assert.Equal(1, created);
        Assert.Equal(123, first.GetProperty("result").GetDouble());
        Assert.Equal("second", second.GetProperty("stdout").GetString());
        Assert.Equal(new[] { "GetTempoAsync", "GetTempoAsync" }, ((ControlProxy)fl).Calls);
    }

    [Fact]
    public async Task StopWaitsForActualNativeCompletionAndAllowsAnotherRunAfterward()
    {
        var fl = DispatchProxy.Create<INativeFlControl, ControlProxy>();
        var control = (ControlProxy)fl;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var native = new TaskCompletionSource<double>(TaskCreationOptions.RunContinuationsAsynchronously);
        control.GetTempo = () => { entered.TrySetResult(); return native.Task; };
        await using var service = new PythonIdeExecutionService(fl, handler => new CallbackRuntime(handler));
        var execution = service.ExecuteAsync("running");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var cancel = service.CancelAsync();
        Assert.False(execution.IsCompleted);
        Assert.False(cancel.IsCompleted);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ExecuteAsync("overlap"));
        native.SetResult(125);
        await cancel.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution);
        control.GetTempo = () => Task.FromResult(130.0);
        Assert.Equal(130, (await service.ExecuteAsync("recovered")).GetProperty("result").GetDouble());
    }

    [Fact]
    public async Task DisableCancelsAndDrainsBeforeDisposingRuntimeAndRejectsNewWork()
    {
        var fl = DispatchProxy.Create<INativeFlControl, ControlProxy>();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var native = new TaskCompletionSource<double>(TaskCreationOptions.RunContinuationsAsynchronously);
        ((ControlProxy)fl).GetTempo = () => { entered.TrySetResult(); return native.Task; };
        CallbackRuntime? runtime = null;
        var service = new PythonIdeExecutionService(fl, handler => runtime = new CallbackRuntime(handler));
        var execution = service.ExecuteAsync("running");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var dispose = service.DisposeAsync().AsTask();
        Assert.False(dispose.IsCompleted);
        Assert.False(runtime!.Disposed);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => service.ExecuteAsync("too late"));
        native.SetResult(125);
        await dispose.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution);
        Assert.True(runtime.Disposed);
        await service.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => service.GetCatalogAsync());
    }

    [Theory]
    [InlineData("", 60)]
    [InlineData("result=1", 0)]
    [InlineData("result=1", 301)]
    public async Task InvalidOrCancelledRequestsDoNotInitializeInterpreter(string code, int timeout)
    {
        var fl = DispatchProxy.Create<INativeFlControl, ControlProxy>();
        var created = false;
        await using var service = new PythonIdeExecutionService(fl, handler => { created = true; return new CallbackRuntime(handler); });
        await Assert.ThrowsAnyAsync<ArgumentException>(() => service.ExecuteAsync(code, timeout));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ExecuteAsync("valid", ct: new CancellationToken(true)));
        Assert.False(created);
    }

    [Fact]
    public async Task ExecutionFailureDoesNotLeaveWindowPermanentlyBusy()
    {
        var fl = DispatchProxy.Create<INativeFlControl, ControlProxy>();
        var fail = true;
        ((ControlProxy)fl).GetTempo = () => fail ? throw new IOException("fake native failure") : Task.FromResult(140.0);
        await using var service = new PythonIdeExecutionService(fl, handler => new CallbackRuntime(handler));
        await Assert.ThrowsAsync<IOException>(() => service.ExecuteAsync("first"));
        fail = false;
        Assert.Equal(140, (await service.ExecuteAsync("second")).GetProperty("result").GetDouble());
    }

    private sealed class CallbackRuntime(Func<string, JsonElement, CancellationToken, Task<object?>> handler) : IEmbeddedPythonRuntime
    {
        public bool Disposed { get; private set; }
        public async Task<JsonElement> ExecuteAsync(string code, int timeoutSeconds, CancellationToken ct = default)
        {
            var result = await handler("invoke", JsonSerializer.SerializeToElement(new { operation = "get_tempo", arguments = new { } }), ct);
            return JsonSerializer.SerializeToElement(new { ok = true, result, stdout = code, stderr = "" });
        }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }

    public class ControlProxy : DispatchProxy
    {
        public List<string> Calls { get; } = [];
        public Func<Task<double>> GetTempo { get; set; } = () => Task.FromResult(123.0);
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            Calls.Add(method!.Name);
            return method.Name == "GetTempoAsync" ? GetTempo() : throw new InvalidOperationException("Unexpected native operation: " + method.Name);
        }
    }
}
