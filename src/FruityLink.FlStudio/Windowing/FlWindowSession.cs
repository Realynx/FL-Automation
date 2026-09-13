using System.Text;
using System.Text.Json;
using FruityLink.Plugins.Abstractions;

namespace FruityLink.FlStudio.Windowing;

/// <summary>One independent keyed FL form. Native calls run on workers while the child's dispatcher pumps.</summary>
internal sealed class FlWindowSession(Func<string, string> send, string caption) : IAsyncFlWindowHost, IFlWindowHostFactory, IFlWindowVisibilityNotifications
{
    private static long _nextId;
    private readonly string _id = Interlocked.Increment(ref _nextId).ToString("x", System.Globalization.CultureInfo.InvariantCulture);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private SynchronizationContext? _ui;
    private int _uiThread;
    private NativeChildWindow? _child;
    private NativeWindowVisibilityListener? _visibilityListener;
    private IntPtr _host, _content;
    private bool _nativePending;

    public string LastEmbedReply { get; private set; } = "";
    public event Action<bool>? UserVisibilityChanged;
    public int LastInsetX { get; private set; }
    public int LastInsetY { get; private set; }
    public IFlWindowHost CreateWindowHost(string windowId, string windowCaption) => new FlWindowSession(send, windowCaption);
    public bool IsBridgeAvailable() => false; // A synchronous probe can block another child on this UI thread.
    public bool TryEmbed(IntPtr childHwnd, bool show) { LastEmbedReply = "err:async-window-host-required"; return false; }
    public bool IsHostVisible() => NativeChildWindow.IsVisible(_host);
    public void SetVisible(bool visible) => Observe(SetVisibleAsync(visible));
    public void Close() => Observe(CloseAsync());
    public void SetStatusHint(string text) => Observe(SendAsync("hint " + text));

    public async Task<bool> IsBridgeAvailableAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try { return await SendAsync("ping").ConfigureAwait(false) == "pong"; }
        catch { return false; }
    }

    public async Task<bool> TryEmbedAsync(IntPtr childHwnd, FlWindowOptions options, bool show = true,
        CancellationToken cancellationToken = default)
    {
        if (!NativeChildWindow.IsOwned(childHwnd) || SynchronizationContext.Current is null)
            return Fail("err:child-ui-context-required");
        if (!ValidOptions(options)) return Fail("err:invalid-window-options");
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_child is not null)
                return _child.Handle == childHwnd && _child.IsAttachedTo(_content) && await ChangeVisibilityAsync(show, false);
            if (_nativePending && !await CloseCoreAsync())
                throw new InvalidOperationException("The previous native window has not finished closing.");
            _ui = SynchronizationContext.Current;
            _uiThread = Environment.CurrentManagedThreadId;
            return await CreateAndAttachAsync(childHwnd, options, show, cancellationToken);
        }
        finally { _gate.Release(); }
    }

    private async Task<bool> CreateAndAttachAsync(IntPtr child, FlWindowOptions options, bool show, CancellationToken ct)
    {
        if (!await IsBridgeAvailableAsync(ct)) return Fail("err:bridge-unavailable");
        ct.ThrowIfCancellationRequested();
        try
        {
            _nativePending = true;
            string title = Convert.ToHexString(Encoding.UTF8.GetBytes(string.IsNullOrWhiteSpace(options.Caption) ? caption : options.Caption));
            LastEmbedReply = await SendAsync($"winhost_create {_id} {child.ToInt64():x} 0 {options.Width} {options.Height} {options.MinimumWidth} {options.MinimumHeight} {title}");
            if (LastEmbedReply.StartsWith("err:unknown", StringComparison.Ordinal)) { _nativePending = false; return false; }
            if (!WindowEmbedReply.TryParse(LastEmbedReply, out WindowEmbedReply reply)) return await RollBackFailureAsync();
            _host = reply.Host;
            _content = reply.Content;
            if (!NativeChildWindow.IsLocal(_host) || !NativeChildWindow.IsLocal(_content)) return await RollBackFailureAsync();
            ct.ThrowIfCancellationRequested();
            _child = new NativeChildWindow(child);
            if (!_child.Attach(reply, out string diagnostic)) { LastEmbedReply = diagnostic; return await RollBackFailureAsync(); }
            _visibilityListener = new NativeWindowVisibilityListener(child, _host, _content, () => UserVisibilityChanged?.Invoke(false));
            LastInsetX = reply.X;
            LastInsetY = reply.Y;
            LastEmbedReply = await SendAsync($"winhost_bind {_id} {child.ToInt64():x}");
            if (!Succeeded(LastEmbedReply)) return await RollBackFailureAsync();
            ct.ThrowIfCancellationRequested();
            if (show && !await ChangeVisibilityAsync(true, false)) return await RollBackFailureAsync();
            ct.ThrowIfCancellationRequested();
            return true;
        }
        catch
        {
            if (!await CloseCoreAsync()) throw new InvalidOperationException("Native window rollback could not detach the child. Keep the plugin alive and retry cleanup.");
            throw;
        }
    }

    private async Task<bool> RollBackFailureAsync()
    {
        string failure = LastEmbedReply;
        if (!await CloseCoreAsync()) throw new InvalidOperationException("Native window rollback is incomplete: " + LastEmbedReply);
        LastEmbedReply = failure;
        return false;
    }

    public async Task<bool> SetVisibleAsync(bool visible, bool activate = false, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return _child is not null && await ChangeVisibilityAsync(visible, activate).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    private async Task<bool> ChangeVisibilityAsync(bool visible, bool activate)
    {
        LastEmbedReply = await SendAsync($"winhost_show {_id} {(visible ? 1 : 0)} {(activate ? 1 : 0)}").ConfigureAwait(false);
        return Succeeded(LastEmbedReply);
    }

    public async Task<bool> CloseAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await CloseCoreAsync().ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    private async Task<bool> CloseCoreAsync()
    {
        if (_child is not null && !await OnUiAsync(DetachChild).ConfigureAwait(false)) return false;
        if (_nativePending)
        {
            LastEmbedReply = await SendAsync("winhost_close " + _id).ConfigureAwait(false);
            if (!Succeeded(LastEmbedReply)) return false;
        }
        _child = null;
        _nativePending = false;
        _host = _content = IntPtr.Zero;
        LastInsetX = LastInsetY = 0;
        return true;
    }

    private bool DetachChild()
    {
        _visibilityListener?.Dispose();
        _visibilityListener = null;
        bool detached = _child!.Detach(out string diagnostic);
        if (!detached) LastEmbedReply = diagnostic;
        return detached;
    }

    private Task<bool> OnUiAsync(Func<bool> action)
    {
        if (Environment.CurrentManagedThreadId == _uiThread) return Task.FromResult(action());
        var result = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ui!.Post(_ => { try { result.SetResult(action()); } catch (Exception ex) { result.SetException(ex); } }, null);
        return result.Task;
    }

    private Task<string> SendAsync(string command) => Task.Run(() => send(command));
    private bool Fail(string diagnostic) { LastEmbedReply = diagnostic; return false; }
    private static bool ValidOptions(FlWindowOptions options) => options is not null && options.Caption is not null
        && options.Caption.Length <= 256 && options.MinimumWidth > 0 && options.MinimumHeight > 0
        && options.Width >= options.MinimumWidth && options.Height >= options.MinimumHeight
        && options.Width <= 20000 && options.Height <= 20000;
    private static bool Succeeded(string reply)
    {
        try
        {
            using var json = JsonDocument.Parse(reply);
            return json.RootElement.ValueKind == JsonValueKind.Object && json.RootElement.TryGetProperty("ok", out JsonElement ok)
                && ok.ValueKind == JsonValueKind.Number && ok.TryGetInt32(out int value) && value == 1;
        }
        catch (JsonException) { return false; }
    }
    private static async void Observe(Task task) { try { await task.ConfigureAwait(false); } catch { /* Legacy void methods cannot return errors. */ } }
}
