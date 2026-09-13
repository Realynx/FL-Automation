using Avalonia;
using Avalonia.Threading;
using FruityLink.Plugins.Abstractions;
using FruityLink.Plugins.PythonIde.Documents;
using FruityLink.Plugins.PythonIde.Execution;
using FruityLink.Ui.Avalonia.Hosting;

namespace FruityLink.Plugins.PythonIde.Ui;

/// <summary>Hosts one editor on the framework's permanent Avalonia thread.</summary>
public sealed class PythonIdeWindowHost(IPluginContext context, IPythonIdeExecution execution) : IPythonIdeWindowHost
{
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly EmbeddedAvaloniaHost _host = EmbeddedAvaloniaHost.Instance;
    private PythonIdeWindow? _window;
    private EmbeddedAvaloniaView? _view;
    private volatile bool _embedded;
    private volatile bool _visible;
    private bool _disposed;

    /// <inheritdoc />
    public bool IsVisible => _embedded ? context.Windows.IsHostVisible() : _visible;

    /// <inheritdoc />
    public Task ShowAsync(CancellationToken ct = default) => SetPresentationAsync(toggle: false, ct);

    /// <inheritdoc />
    public Task ToggleAsync(CancellationToken ct = default) => SetPresentationAsync(toggle: true, ct);

    private async Task SetPresentationAsync(bool toggle, CancellationToken ct)
    {
        await _lifecycle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            bool visible = !toggle || !IsVisible;
            await EnsureWindowAsync(ct).ConfigureAwait(false);
            await Dispatcher.UIThread.InvokeAsync(() => ApplyVisibilityAsync(visible, activate: toggle && visible, ct));
        }
        finally { _lifecycle.Release(); }
    }

    private async Task EnsureWindowAsync(CancellationToken ct)
    {
        if (_window is not null) return;
        await Task.Run(() => _host.EnsureStarted(() => AppBuilder.Configure<Application>()
            .UsePlatformDetect().WithInterFont())).ConfigureAwait(false);
        await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            _window = new PythonIdeWindow(execution, new DraftStore(DraftStore.DefaultDirectory));
            _view = new EmbeddedAvaloniaView(_window);
            _view.HiddenByUser += OnHidden;
            try { await _window.RestoreDraftAsync(); }
            catch (Exception exception) { context.Log("[fl-python-ide] Draft recovery: " + exception.Message); }
            ct.ThrowIfCancellationRequested();
            await TryEmbedAsync(ct);
        });
    }

    private async Task TryEmbedAsync(CancellationToken ct)
    {
        try
        {
            if (context.Windows is not IAsyncFlWindowHost && !context.Windows.IsBridgeAvailable())
            {
                LogExternalFallback(context.Log, "err:bridge-unavailable");
                return;
            }
            _view!.PrepareForEmbedding();
            _embedded = await EmbedWindowAsync(context.Windows, _view.Handle, _view.GetWindowOptions("FL Python IDE"), context.Log, ct);
            if (_embedded) _view.PinToHostContent(context.Windows.LastInsetX, context.Windows.LastInsetY);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception exception) when (context.Windows is not IAsyncFlWindowHost)
        { LogExternalFallback(context.Log, exception.Message); }
    }

    internal static async Task<bool> EmbedWindowAsync(IFlWindowHost windows, IntPtr child, FlWindowOptions options,
        Action<string> log, CancellationToken ct)
    {
        bool embedded = windows is IAsyncFlWindowHost asynchronous
            ? await asynchronous.TryEmbedAsync(child, options, show: false, cancellationToken: ct)
            : windows.TryEmbed(child, show: false);
        if (!embedded) LogExternalFallback(log, windows.LastEmbedReply);
        return embedded;
    }

    private static void LogExternalFallback(Action<string> log, string? diagnostic)
    {
        const int limit = 2048;
        string value = string.IsNullOrWhiteSpace(diagnostic) ? "(no bridge diagnostic)" : diagnostic;
        string bounded = new(value.Take(limit).Select(character =>
            char.IsControl(character) || character is '\u2028' or '\u2029' ? ' ' : character).ToArray());
        string suffix = value.Length > limit ? " [truncated]" : "";
        log("[fl-python-ide] Native embedding unavailable; using external window. bridge=" + bounded + suffix);
    }

    private async Task ApplyVisibilityAsync(bool visible, bool activate, CancellationToken ct)
    {
        if (_embedded)
        {
            if (context.Windows is IAsyncFlWindowHost asynchronous)
            {
                if (!await asynchronous.SetVisibleAsync(visible, activate, ct))
                    throw new InvalidOperationException("FL Python IDE could not change its native window visibility.");
            }
            else context.Windows.SetVisible(visible);
            if (visible) _view!.ForceRender();
        }
        else if (visible) _view!.ShowExternal();
        else _view!.SetVisible(false);
        if (visible && activate) _window!.FocusEditor();
        _visible = visible;
        (context.Windows as IFlWindowVisibilityState)?.RememberVisibility(visible);
    }

    private void OnHidden()
    {
        _visible = false;
        (context.Windows as IFlWindowVisibilityState)?.RememberVisibility(false);
        _ = PersistHiddenDraftAsync();
    }

    private async Task PersistHiddenDraftAsync()
    {
        try { if (_window is not null) await _window.PersistDraftAsync(); }
        catch (Exception exception) { context.Log("[fl-python-ide] Draft persistence: " + exception.Message); }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            if (_window is null) { _disposed = true; return; }
            await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                try { await _window.PrepareCloseAsync(); }
                catch (Exception exception) { context.Log("[fl-python-ide] Draft persistence: " + exception.Message); }
                finally { await CloseViewAsync(); }
            });
            _disposed = true;
        }
        finally { _lifecycle.Release(); }
    }

    private async Task CloseViewAsync()
    {
        if (context.Windows is IAsyncFlWindowHost asynchronous)
        {
            if (!await asynchronous.CloseAsync())
                throw new InvalidOperationException("FL Python IDE could not detach its native window; it remains open for a safe retry.");
        }
        else if (_embedded) context.Windows.Close();
        _view!.HiddenByUser -= OnHidden;
        _view.Close();
        _view = null;
        _window = null;
        _embedded = _visible = false;
    }
}
