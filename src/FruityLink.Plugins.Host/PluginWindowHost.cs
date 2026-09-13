using System.Runtime.CompilerServices;
using FruityLink.Plugins.Abstractions;

namespace FruityLink.Plugins.Host;

/// <summary>One plugin's access to the adapter's single native embedding slot.</summary>
internal sealed class PluginWindowHost : IAsyncFlWindowHost, IFlWindowHostFactory, IAsyncDisposable
{
    private static readonly ConditionalWeakTable<IFlWindowHost, Slot> Slots = new();
    private readonly IFlWindowHost _host;
    private readonly Slot _slot;
    private SynchronizationContext? _uiContext;
    private int _uiThread;
    private int _disposed;
    private readonly Dictionary<string, PluginWindowHost> _children = new(StringComparer.Ordinal);

    internal PluginWindowHost(IFlWindowHost host)
    {
        _host = host;
        _slot = Slots.GetValue(host, _ => new Slot());
    }

    public string LastEmbedReply { get; private set; } = "";
    public int LastInsetX { get; private set; }
    public int LastInsetY { get; private set; }
    public bool IsBridgeAvailable() => _disposed == 0 && _host.IsBridgeAvailable();

    public IFlWindowHost CreateWindowHost(string windowId, string caption)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(windowId);
        lock (_children)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            if (_children.TryGetValue(windowId, out PluginWindowHost? existing)) return existing;
            IFlWindowHost child = _host is IFlWindowHostFactory factory ? factory.CreateWindowHost(windowId, caption) : _host;
            return _children[windowId] = new PluginWindowHost(child);
        }
    }

    public Task<bool> IsBridgeAvailableAsync(CancellationToken cancellationToken = default)
        => _disposed != 0 ? Task.FromResult(false) : _host is IAsyncFlWindowHost host
            ? host.IsBridgeAvailableAsync(cancellationToken) : Task.Run(_host.IsBridgeAvailable, cancellationToken);

    public async Task<bool> TryEmbedAsync(IntPtr childHwnd, FlWindowOptions options, bool show = true,
        CancellationToken cancellationToken = default)
    {
        if (_disposed != 0) return false;
        await _slot.Gate.WaitAsync(cancellationToken);
        try
        {
            if (_disposed != 0) return false;
            if (_slot.Owner is not null && _slot.Owner != this)
            {
                LastEmbedReply = "err:window-host-in-use-by-another-plugin";
                return false;
            }
            bool alreadyOwned = _slot.Owner == this;
            _slot.Owner = this;
            if (!alreadyOwned) { _uiContext = SynchronizationContext.Current; _uiThread = Environment.CurrentManagedThreadId; }
            return await EmbedOwnedAsync(childHwnd, options, show, alreadyOwned, cancellationToken);
        }
        finally { _slot.Gate.Release(); }
    }

    private async Task<bool> EmbedOwnedAsync(IntPtr child, FlWindowOptions options, bool show, bool alreadyOwned, CancellationToken ct)
    {
        try
        {
            bool embedded = _host is IAsyncFlWindowHost host
                ? await host.TryEmbedAsync(child, options, show, ct) : _host.TryEmbed(child, show);
            LastEmbedReply = _host.LastEmbedReply;
            if (!embedded) { if (!alreadyOwned) _slot.Owner = null; return false; }
            LastInsetX = _host.LastInsetX;
            LastInsetY = _host.LastInsetY;
            return true;
        }
        catch
        {
            if (!alreadyOwned) await ReleaseOwnedAsync();
            throw;
        }
    }

    public async Task<bool> SetVisibleAsync(bool visible, bool activate = false, CancellationToken cancellationToken = default)
    {
        if (_disposed != 0) return false;
        await _slot.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_disposed != 0 || _slot.Owner != this) return false;
            if (_host is IAsyncFlWindowHost host) return await host.SetVisibleAsync(visible, activate, cancellationToken).ConfigureAwait(false);
            _host.SetVisible(visible);
            return true;
        }
        finally { _slot.Gate.Release(); }
    }

    public async Task<bool> CloseAsync(CancellationToken cancellationToken = default)
    {
        await _slot.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        // Cleanup remains retryable/idempotent after revocation; only new embed/show operations stop.
        try { return await ReleaseOwnedAsync().ConfigureAwait(false); }
        finally { _slot.Gate.Release(); }
    }

    public bool TryEmbed(IntPtr childHwnd, bool show)
    {
        if (_disposed != 0 || !_slot.Gate.Wait(0)) return false;
        try
        {
            if (_disposed != 0) return false;
            if (_slot.Owner is not null && _slot.Owner != this)
            {
                LastEmbedReply = "err:window-host-in-use-by-another-plugin";
                return false;
            }
            bool alreadyOwned = _slot.Owner == this;
            _slot.Owner = this;
            if (!alreadyOwned)
            {
                _uiContext = SynchronizationContext.Current;
                _uiThread = Environment.CurrentManagedThreadId;
            }
            return EmbedOwned(childHwnd, show, alreadyOwned);
        }
        finally { _slot.Gate.Release(); }
    }

    private bool EmbedOwned(IntPtr childHwnd, bool show, bool alreadyOwned)
    {
        try
        {
            bool embedded = _host.TryEmbed(childHwnd, show);
            LastEmbedReply = _host.LastEmbedReply;
            if (!embedded)
            {
                if (!alreadyOwned) _slot.Owner = null;
                return false;
            }
            LastInsetX = _host.LastInsetX;
            LastInsetY = _host.LastInsetY;
            return true;
        }
        catch (Exception ex)
        {
            LastEmbedReply = "err:window-host-embed-failed: " + ex.Message;
            // The adapter may throw after creating the native form. Close that partial embed
            // before making its slot available; if cleanup fails, retain ownership for retry.
            if (!alreadyOwned) CloseOwned();
            throw;
        }
    }

    public bool IsHostVisible()
    {
        if (_disposed != 0 || !_slot.Gate.Wait(0)) return false;
        try { return _slot.Owner == this && _host.IsHostVisible(); }
        finally { _slot.Gate.Release(); }
    }

    public void SetVisible(bool visible)
    {
        if (_disposed != 0 || !_slot.Gate.Wait(0)) return;
        try { if (_slot.Owner == this) _host.SetVisible(visible); }
        finally { _slot.Gate.Release(); }
    }

    public void Close()
    {
        if (_host is IAsyncFlWindowHost) { ObserveCloseAsync(); return; }
        if (_disposed != 0 || !_slot.Gate.Wait(0)) return;
        try
        {
            if (_slot.Owner != this) return;
            if (_uiThread != Environment.CurrentManagedThreadId)
            {
                LastEmbedReply = "err:close-on-child-thread-required";
                return;
            }
            CloseOwned();
        }
        finally { _slot.Gate.Release(); }
    }

    private async void ObserveCloseAsync()
    {
        try { await CloseAsync().ConfigureAwait(false); }
        catch { /* Ownership is retained; context disposal retries and reports the failure. */ }
    }

    public void SetStatusHint(string text)
    {
        if (_disposed == 0) _host.SetStatusHint(text);
    }

    public async ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref _disposed, 1);
        List<Exception> errors = [];
        PluginWindowHost[] children;
        lock (_children) children = _children.Values.ToArray();
        foreach (PluginWindowHost child in children)
        {
            try { await child.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) { errors.Add(ex); }
        }
        // Wait asynchronously for an in-flight embed before dropping this scope. Public operations
        // never block on the slot, so native FL callbacks cannot deadlock against its UI owner.
        await _slot.Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!await ReleaseOwnedAsync().ConfigureAwait(false))
                errors.Add(new InvalidOperationException("Native window cleanup could not be confirmed: " + _host.LastEmbedReply));
        }
        catch (Exception ex) { errors.Add(ex); }
        finally { _slot.Gate.Release(); }
        if (errors.Count != 0) throw new AggregateException("Plugin windows could not all close.", errors);
    }

    private async Task<bool> ReleaseOwnedAsync()
    {
        if (_slot.Owner != this) return true;
        if (_host is IAsyncFlWindowHost host)
        {
            if (!await host.CloseAsync().ConfigureAwait(false)) return false;
            ClearOwnership();
        }
        else if (_uiThread == Environment.CurrentManagedThreadId) CloseOwned();
        else if (_uiContext is not null) await CloseOnUiThreadAsync().ConfigureAwait(false);
        else throw new InvalidOperationException("Embedded window cleanup requires its original UI thread or synchronization context.");
        return true;
    }

    private Task CloseOnUiThreadAsync()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _uiContext!.Post(_ =>
        {
            try { CloseOwned(); completion.SetResult(); }
            catch (Exception ex) { completion.SetException(ex); }
        }, null);
        return completion.Task;
    }

    private void CloseOwned()
    {
        if (_slot.Owner != this) return;
        _host.Close();
        ClearOwnership();
    }

    private void ClearOwnership()
    {
        _slot.Owner = null;
        _uiContext = null;
        LastInsetX = LastInsetY = 0;
    }

    private sealed class Slot
    {
        internal SemaphoreSlim Gate { get; } = new(1, 1);
        internal PluginWindowHost? Owner { get; set; }
    }
}
