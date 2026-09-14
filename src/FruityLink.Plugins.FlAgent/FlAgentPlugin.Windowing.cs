using FruityLink.Plugins.Abstractions;
using FruityLink.Plugins.FlAgent.Ui;
using FruityLink.Ui.Avalonia.Hosting;

namespace FruityLink.Plugins.FlAgent;

public sealed partial class FlAgentPlugin
{
    private readonly SemaphoreSlim _windowLifecycle = new(1, 1);

    private void ToggleChatVisible() => _ = Task.Run(async () =>
    {
        await _windowLifecycle.WaitAsync().ConfigureAwait(false);
        try { await ToggleChatVisibleCoreAsync().ConfigureAwait(false); }
        catch (Exception error) { _context?.Log("[fl-agent] Window toggle failed: " + error.Message); }
        finally { _windowLifecycle.Release(); }
    });

    /// <inheritdoc />
    public async Task DisableAsync(CancellationToken ct = default)
    {
        // Detachment must finish before toolkit windows or their plugin load context can disappear.
        await _windowLifecycle.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try { await DisableCoreAsync().ConfigureAwait(false); }
        catch
        {
            lock (_gate) _disposing = false;
            throw;
        }
        finally { _windowLifecycle.Release(); }
    }

    private static async Task<bool> EmbedWindowAsync(IFlWindowHost windows, IntPtr child, FlWindowOptions options, CancellationToken ct)
    {
        if (windows is not IAsyncFlWindowHost asynchronous) return windows.TryEmbed(child, show: true);
        try { return await asynchronous.TryEmbedAsync(child, options, cancellationToken: ct); }
        catch (Exception error)
        {
            // An exception can mean rollback could not confirm detachment. Do not show a fallback
            // or initialize another toolkit over that still-owned native child.
            throw new NativeWindowHostingException("FL could not safely finish creating the plugin window.", error);
        }
    }

    private static async Task DetachWindowAsync(IFlWindowHost? windows, EmbeddedAvaloniaHost? avalonia, UiHost? wpf)
    {
        if (windows is null) return;
        Task<bool> detached = avalonia is not null ? avalonia.Invoke(() => CloseNativeAsync(windows))
            : wpf is not null ? wpf.Dispatcher.InvokeAsync(() => CloseNativeAsync(windows)).Task.Unwrap()
            : CloseNativeAsync(windows);
        if (!await detached.ConfigureAwait(false))
            throw new InvalidOperationException("FL Automate could not detach its native window; the existing UI is retained for retry.");
    }

    private static async Task<bool> CloseNativeAsync(IFlWindowHost windows)
    {
        if (windows is IAsyncFlWindowHost asynchronous) return await asynchronous.CloseAsync();
        windows.Close();
        return true;
    }

    private static int Pixels(double logical, double scale)
        => double.IsFinite(logical) && logical > 0 ? (int)Math.Clamp(Math.Ceiling(logical * scale), 1, 16384) : 1;

    private sealed class NativeWindowHostingException(string message, Exception inner) : Exception(message, inner);
}
