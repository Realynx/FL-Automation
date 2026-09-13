using System.Reflection;
using System.Text.Json;
using FruityLink.Core.Abstractions;
using FruityLink.Plugins.Abstractions;
using FruityLink.Plugins.PythonIde.Execution;
using FruityLink.Plugins.PythonIde.Ui;
using Xunit;

namespace FruityLink.Plugins.PythonIde.Execution.Tests;

public sealed class PluginLifecycleTests
{
    [Fact]
    public async Task EnableIsIdempotentAndDisableDisposesRegistrationsExecutionThenWindow()
    {
        var fixture = new Fixture();
        Assert.Equal("fl-python-ide", fixture.Plugin.Id);
        Assert.Empty(fixture.Events);
        await fixture.Plugin.EnableAsync(fixture.Context);
        await fixture.Plugin.EnableAsync(fixture.Context);
        Assert.Equal(2, fixture.Context.Registrations.Count);
        Assert.Equal(1, fixture.Windows[0].Shows);
        await fixture.Plugin.DisableAsync();
        await fixture.Plugin.DisableAsync();
        Assert.All(fixture.Context.Registrations, registration => Assert.True(registration.Disposed));
        Assert.Equal(new[] { "execution-dispose", "window-dispose" }, fixture.Events);
    }

    [Fact]
    public async Task DisableWithExpiredTokenWaitsForExecutionBeforeClosingWindow()
    {
        var fixture = new Fixture();
        await fixture.Plugin.EnableAsync(fixture.Context);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var drain = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Executions[0].OnDispose = () => { entered.TrySetResult(); return drain.Task; };
        var disable = fixture.Plugin.DisableAsync(new CancellationToken(true));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(disable.IsCompleted);
        Assert.False(fixture.Windows[0].Disposed);
        drain.SetResult();
        await disable.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(fixture.Windows[0].Disposed);
    }

    [Fact]
    public async Task FailedWindowStartupCleansEverythingAndCanBeEnabledAgain()
    {
        var fixture = new Fixture { FailShow = true };
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Plugin.EnableAsync(fixture.Context));
        Assert.True(fixture.Executions[0].Disposed);
        Assert.True(fixture.Windows[0].Disposed);
        Assert.All(fixture.Context.Registrations, registration => Assert.True(registration.Disposed));
        fixture.FailShow = false;
        await fixture.Plugin.EnableAsync(fixture.Context);
        Assert.True(fixture.Windows[1].IsVisible);
        await fixture.Plugin.DisableAsync();
    }

    [Fact]
    public async Task FailedExecutionDisposalStillClosesWindowAndResetsLifecycle()
    {
        var fixture = new Fixture();
        await fixture.Plugin.EnableAsync(fixture.Context);
        fixture.Executions[0].OnDispose = () => throw new IOException("fake drained disposal error");
        await Assert.ThrowsAsync<AggregateException>(() => fixture.Plugin.DisableAsync());
        Assert.True(fixture.Windows[0].Disposed);
        await fixture.Plugin.EnableAsync(fixture.Context);
        Assert.Equal(2, fixture.Windows.Count);
        await fixture.Plugin.DisableAsync();
    }

    [Fact]
    public async Task CancelledEnableCreatesNoResourcesAndDisableBeforeEnableIsSafe()
    {
        var fixture = new Fixture();
        await fixture.Plugin.DisableAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Plugin.EnableAsync(fixture.Context, new CancellationToken(true)));
        Assert.Empty(fixture.Executions);
        Assert.Empty(fixture.Windows);
    }

    [Fact]
    public async Task FailedNativeDetachRetainsTheWindowForAnotherDisableAttempt()
    {
        var fixture = new Fixture();
        await fixture.Plugin.EnableAsync(fixture.Context);
        fixture.Windows[0].FailDispose = true;
        await Assert.ThrowsAsync<AggregateException>(() => fixture.Plugin.DisableAsync());
        Assert.False(fixture.Windows[0].Disposed);
        await fixture.Plugin.EnableAsync(fixture.Context);
        Assert.Single(fixture.Windows);
        fixture.Windows[0].FailDispose = false;
        await fixture.Plugin.DisableAsync();
        Assert.True(fixture.Windows[0].Disposed);
        await fixture.Plugin.EnableAsync(fixture.Context);
        Assert.Equal(2, fixture.Windows.Count);
        await fixture.Plugin.DisableAsync();
    }

    private sealed class Fixture
    {
        public Context Context { get; } = new();
        public List<string> Events { get; } = [];
        public List<FakeExecution> Executions { get; } = [];
        public List<FakeWindow> Windows { get; } = [];
        public PythonIdePlugin Plugin { get; }
        public bool FailShow { get; set; }
        public Fixture()
        {
            Plugin = new(_ => { var execution = new FakeExecution(Events); Executions.Add(execution); return execution; },
                (_, _) => { var window = new FakeWindow(Events) { FailShow = FailShow }; Windows.Add(window); return window; });
        }
    }

    private sealed class FakeExecution(List<string> events) : IPythonIdeExecution
    {
        public Func<Task>? OnDispose { get; set; }
        public bool Disposed { get; private set; }
        public Task<JsonElement> ExecuteAsync(string code, int timeoutSeconds = 60, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<JsonElement> GetCatalogAsync(string? filter = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task CancelAsync() => Task.CompletedTask;
        public async ValueTask DisposeAsync()
        {
            if (OnDispose is not null) await OnDispose();
            Disposed = true;
            events.Add("execution-dispose");
        }
    }

    private sealed class FakeWindow(List<string> events) : IPythonIdeWindowHost
    {
        public bool IsVisible { get; private set; }
        public bool Disposed { get; private set; }
        public bool FailShow { get; init; }
        public bool FailDispose { get; set; }
        public int Shows { get; private set; }
        public Task ShowAsync(CancellationToken ct = default)
        {
            if (FailShow) throw new InvalidOperationException("fake UI startup failure");
            Shows++;
            IsVisible = true;
            return Task.CompletedTask;
        }
        public Task ToggleAsync(CancellationToken ct = default) { IsVisible = !IsVisible; return Task.CompletedTask; }
        public ValueTask DisposeAsync()
        {
            if (FailDispose) throw new IOException("fake native detach failure");
            Disposed = true; IsVisible = false; events.Add("window-dispose"); return ValueTask.CompletedTask;
        }
    }

    private sealed class Context : IPluginContext, IFlMenuRegistrar, IFlToolbarRegistrar, IServiceProvider
    {
        public List<Registration> Registrations { get; } = [];
        public INativeFlControl Fl { get; } = DispatchProxy.Create<INativeFlControl, ExecutionTests.ControlProxy>();
        public IServiceProvider Services => this;
        public IFlMenuRegistrar Menu => this;
        public IFlToolbarRegistrar Toolbar => this;
        public IFlWindowHost Windows => throw new NotSupportedException();
        public void Log(string message) { }
        public object? GetService(Type serviceType) => null;
        public IDisposable AddToggle(FlNativeMenu menu, string caption, Func<bool> isChecked, Action onToggled) => Add();
        public IDisposable AddToggle(string caption, string tooltip, Func<bool> isActive, Action onToggled) => Add();
        public IDisposable AddCommand(FlNativeMenu menu, string caption, Action onInvoke) => Add();
        public IDisposable AddButton(string caption, string tooltip, Action onClick) => Add();
        public void Refresh() { }
        private Registration Add() { var registration = new Registration(); Registrations.Add(registration); return registration; }
    }

    private sealed class Registration : IDisposable
    {
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }
}
