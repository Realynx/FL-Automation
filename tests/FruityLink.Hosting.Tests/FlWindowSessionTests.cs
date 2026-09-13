using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia;
using Avalonia.Threading;
using FruityLink.FlStudio.Windowing;
using FruityLink.Plugins.Abstractions;
using FruityLink.Ui.Avalonia.Hosting;
using Xunit;

namespace FruityLink.Hosting.Tests;

[Collection("Windows hosting")]
public sealed class FlWindowSessionTests
{
    private static readonly FlWindowOptions Options = new("Python · editor", 1120, 780, 820, 560);

    [Fact]
    public Task NativeCloseNotificationReachesSessionButTeardownDoesNot() => OnUi(async () =>
    {
        using var child = new NativeWindow();
        using var content = new NativeWindow();
        var bridge = new Bridge(content);
        var session = new FlWindowSession(bridge.Send, "visibility");
        var changes = new List<bool>();
        session.UserVisibilityChanged += changes.Add;
        Assert.True(await session.TryEmbedAsync(child.Handle, Options, false));
        SendMessage(child.Handle, NativeWindowVisibilityListener.Message, content.Handle, content.Handle);
        Assert.Equal(new[] { false }, changes);
        Assert.True(await session.CloseAsync());
        SendMessage(child.Handle, NativeWindowVisibilityListener.Message, content.Handle, content.Handle);
        Assert.Single(changes);
    });

    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr w, IntPtr l);

    [Fact]
    public Task CancelledFinalShowDrainsThenDetachesBeforeCompleting() => OnUi(async () =>
    {
        using var child = new NativeWindow();
        using var content = new NativeWindow();
        using var release = new ManualResetEventSlim();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bridge = new Bridge(content) { BeforeShow = () => { started.SetResult(); Assert.True(release.Wait(TimeSpan.FromSeconds(5))); } };
        var session = new FlWindowSession(bridge.Send, "cancel show");
        using var cancellation = new CancellationTokenSource();
        Task<bool> pending = session.TryEmbedAsync(child.Handle, Options, true, cancellation.Token);
        await started.Task;
        Assert.Equal(content.Handle, child.Parent);
        cancellation.Cancel();
        Assert.False(pending.IsCompleted);
        release.Set();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(IntPtr.Zero, child.Parent);
        Assert.False(NativeChildWindow.CurrentThreadHasEmbeddedWindow);
        Assert.Contains(bridge.Commands, command => command.StartsWith("winhost_close ", StringComparison.Ordinal));
    });

    [Fact]
    public Task TwoSessionsOnOneUiThreadKeepIndependentParentsAndVisibilityCommands() => OnUi(async () =>
    {
        using var firstChild = new NativeWindow();
        using var secondChild = new NativeWindow();
        using var firstContent = new NativeWindow();
        using var secondContent = new NativeWindow();
        var bridge = new Bridge(firstContent, secondContent);
        var factory = new FlWindowHost(bridge.Send);
        var first = Assert.IsAssignableFrom<IAsyncFlWindowHost>(factory.CreateWindowHost("one", "one"));
        var second = Assert.IsAssignableFrom<IAsyncFlWindowHost>(factory.CreateWindowHost("two", "two"));
        try
        {
            Assert.True(await first.TryEmbedAsync(firstChild.Handle, Options, false), first.LastEmbedReply);
            Assert.True(await second.TryEmbedAsync(secondChild.Handle, Options, false), second.LastEmbedReply);
            Assert.Equal(firstContent.Handle, firstChild.Parent);
            Assert.Equal(secondContent.Handle, secondChild.Parent);
            Assert.True(await first.SetVisibleAsync(true, true));
            Assert.True(await first.TryEmbedAsync(firstChild.Handle, Options, false));
            Assert.Equal(2, bridge.Commands.Count(command => command.StartsWith("winhost_create ", StringComparison.Ordinal)));
            Assert.Contains(bridge.Commands, command => command.StartsWith("winhost_show ", StringComparison.Ordinal) && command.EndsWith(" 1 1", StringComparison.Ordinal));
            Assert.True(await first.CloseAsync());
            Assert.Equal(IntPtr.Zero, firstChild.Parent);
            Assert.Equal(secondContent.Handle, secondChild.Parent);
        }
        finally { await first.CloseAsync(); await second.CloseAsync(); }
        Assert.False(NativeChildWindow.CurrentThreadHasEmbeddedWindow);
    });

    [Fact]
    public Task CancelledCreationDrainsTheWorkerAndLeavesTheUiPumping() => OnUi(async () =>
    {
        using var child = new NativeWindow();
        using var content = new NativeWindow();
        using var release = new ManualResetEventSlim();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bridge = new Bridge(content) { BeforeCreate = () => { started.SetResult(); Assert.True(release.Wait(TimeSpan.FromSeconds(5))); } };
        var session = new FlWindowSession(bridge.Send, "cancel");
        using var cancellation = new CancellationTokenSource();
        Task<bool> pending = session.TryEmbedAsync(child.Handle, Options, false, cancellation.Token);
        await started.Task;
        var pumped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.UIThread.Post(pumped.SetResult);
        await pumped.Task;
        cancellation.Cancel();
        Assert.False(pending.IsCompleted);
        release.Set();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(IntPtr.Zero, child.Parent);
        Assert.Contains(bridge.Commands, command => command.StartsWith("winhost_close ", StringComparison.Ordinal));
    });

    [Fact]
    public Task UnavailableBridgeFallsBackBeforeAllocatingOrChangingTheChild() => OnUi(async () =>
    {
        using var child = new NativeWindow();
        IntPtr style = child.Style;
        var commands = new ConcurrentQueue<string>();
        var session = new FlWindowSession(command => { commands.Enqueue(command); throw new DllNotFoundException(); }, "missing");
        Assert.False(await session.TryEmbedAsync(child.Handle, Options));
        Assert.Equal(new[] { "ping" }, commands);
        Assert.Equal(style, child.Style);
        Assert.Equal(IntPtr.Zero, child.Parent);
        Assert.True(await session.CloseAsync());
    });

    [Fact]
    public Task FailedBindRestoresTheExternalWindowBeforeNativeClose() => OnUi(async () =>
    {
        using var owner = new NativeWindow();
        using var child = new NativeWindow(owner.Handle, 0x80000000);
        using var content = new NativeWindow();
        IntPtr style = child.Style;
        var bridge = new Bridge(content) { FailBind = true };
        var session = new FlWindowSession(bridge.Send, "bind failure");
        Assert.False(await session.TryEmbedAsync(child.Handle, Options));
        Assert.Equal(owner.Handle, child.Parent);
        Assert.Equal(style, child.Style);
        Assert.False(NativeChildWindow.CurrentThreadHasEmbeddedWindow);
    });

    [Theory]
    [InlineData("[]")]
    [InlineData("{\"ok\":true}")]
    public Task MalformedCloseKeepsTheSessionAvailableForCleanupRetry(string reply) => OnUi(async () =>
    {
        using var child = new NativeWindow();
        using var content = new NativeWindow();
        var bridge = new Bridge(content) { CloseReply = reply };
        var session = new FlWindowSession(bridge.Send, "close retry");
        Assert.True(await session.TryEmbedAsync(child.Handle, Options, false));
        Assert.False(await session.CloseAsync());
        Assert.Equal(IntPtr.Zero, child.Parent);
        bridge.CloseReply = "{\"ok\":1}";
        Assert.True(await session.CloseAsync());
    });

    private static async Task OnUi(Func<Task> action)
    {
        await Task.Run(() => EmbeddedAvaloniaHost.Instance.EnsureStarted(() => AppBuilder.Configure<Application>().UsePlatformDetect().WithInterFont()));
        await Dispatcher.UIThread.InvokeAsync(action).WaitAsync(TimeSpan.FromSeconds(15));
    }

    private sealed class Bridge(params NativeWindow[] contents)
    {
        private readonly int _uiThread = Environment.CurrentManagedThreadId;
        private readonly Dictionary<string, IntPtr> _parents = new();
        private int _next;
        internal ConcurrentQueue<string> Commands { get; } = new();
        internal Action? BeforeCreate { get; init; }
        internal Action? BeforeShow { get; init; }
        internal bool FailBind { get; init; }
        internal string CloseReply { get; set; } = "{\"ok\":1}";

        internal string Send(string command)
        {
            Assert.NotEqual(_uiThread, Environment.CurrentManagedThreadId);
            Commands.Enqueue(command);
            if (command == "ping") return "pong";
            string[] parts = command.Split(' ');
            if (parts[0] == "winhost_create") return Create(parts);
            if (parts[0] == "winhost_bind") return FailBind ? "{\"ok\":0}" : "{\"ok\":1}";
            if (parts[0] == "winhost_close") return CloseReply;
            if (parts[0] == "winhost_show") BeforeShow?.Invoke();
            return "{\"ok\":1}";
        }

        private string Create(string[] parts)
        {
            BeforeCreate?.Invoke();
            IntPtr content = contents[_next++].Handle;
            Assert.True(_parents.TryAdd(parts[1], content));
            Assert.Equal("0", parts[3]);
            Assert.Equal("1120", parts[4]);
            Assert.Equal("820", parts[6]);
            Assert.Equal("Python · editor", System.Text.Encoding.UTF8.GetString(Convert.FromHexString(parts[8])));
            return JsonSerializer.Serialize(new { ok = 1, host = $"0x{content.ToInt64():x}", content = $"0x{content.ToInt64():x}", cx = 0, cy = 0, cw = 1120, ch = 780 });
        }
    }
}
