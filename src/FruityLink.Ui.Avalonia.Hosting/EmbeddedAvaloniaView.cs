using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using FruityLink.Plugins.Abstractions;

namespace FruityLink.Ui.Avalonia.Hosting;

/// <summary>
/// Wraps ANY Avalonia <see cref="Window"/> living on the <see cref="EmbeddedAvaloniaHost"/> UI thread
/// with the small Win32 glue a plugin needs to reparent it into FL's native host form and keep it
/// painting. The plugin owns the reparent itself (the SDK's <c>IFlWindowHost.TryEmbed</c> takes this
/// view's <see cref="Handle"/>); this class only provides the HWND, an embed-friendly / external
/// presentation, a forced re-present (the airspace fix), and show/hide/close.
///
/// <para><b>Threading.</b> Construct + <see cref="PrepareForEmbedding"/> + read <see cref="Handle"/> on
/// the Avalonia UI thread (the plugin wraps them in <see cref="EmbeddedAvaloniaHost.Invoke"/>).
/// <see cref="ForceRender"/> / <see cref="SetVisible"/> / <see cref="Close"/> self-marshal, so they are
/// safe to call from any thread.</para>
/// </summary>
public class EmbeddedAvaloniaView
{
    private readonly Window _window;
    private bool _reallyClose;
    private bool _preparedForEmbedding;
    private bool _externalPresented;
    private ChildContentPin? _contentPin;
    private readonly DeferredRepaint _repaint;

    /// <summary>Raised (on the UI thread) when the user closes the EXTERNAL window via its OS close (X):
    /// we hide it instead of destroying it (mirroring FL's View-menu windows) so a toggle can re-show it.
    /// Not raised in the embedded case (there FL's native close is handled by the bridge).</summary>
    public event Action? HiddenByUser;

    /// <summary>
    /// Must be constructed on the Avalonia UI thread (via <see cref="EmbeddedAvaloniaHost.Invoke"/>),
    /// wrapping an already-constructed (but not yet shown) window. Show it via
    /// <see cref="PrepareForEmbedding"/> or <see cref="ShowExternal"/>.
    /// </summary>
    public EmbeddedAvaloniaView(Window window)
    {
        _window = window ?? throw new ArgumentNullException(nameof(window));
        _repaint = new DeferredRepaint(action => Dispatcher.UIThread.Post(action, DispatcherPriority.Render), InvalidateSurface);
        _window.Closing += (_, e) =>
        {
            if (_reallyClose) return;    // owner is tearing down → let it really close
            e.Cancel = true;
            try { _window.Hide(); } catch { /* best-effort */ }
            HiddenByUser?.Invoke();
        };
    }

    /// <summary>The wrapped window (UI-thread access only, like any Avalonia control).</summary>
    public Window Window => _window;

    /// <summary>
    /// The native window handle (HWND) to hand to the bridge for reparenting. Valid only after the
    /// window has been shown (<see cref="PrepareForEmbedding"/> / <see cref="ShowExternal"/>). Call on
    /// the UI thread.
    /// </summary>
    public IntPtr Handle => _window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;

    /// <summary>Convert the current requested/minimum client dimensions from DIPs to physical pixels.
    /// Read this on the UI thread after preparing the window, so the platform's current scale is known.</summary>
    public FlWindowOptions GetWindowOptions(string caption)
    {
        double scale = double.IsFinite(_window.RenderScaling) && _window.RenderScaling > 0 ? _window.RenderScaling : 1;
        int minimumWidth = Pixels(_window.MinWidth, scale, 1);
        int minimumHeight = Pixels(_window.MinHeight, scale, 1);
        return new FlWindowOptions(caption, Math.Max(minimumWidth, Pixels(_window.Width, scale, 640)),
            Math.Max(minimumHeight, Pixels(_window.Height, scale, 480)), minimumWidth, minimumHeight);
    }

    private static int Pixels(double value, double scale, int fallback)
        => double.IsFinite(value) && value > 0 ? (int)Math.Clamp(Math.Ceiling(value * scale), 1, 20000) : fallback;

    /// <summary>Keep toolkit position/DPI updates inside the native parent's content area. Call on
    /// the child's UI thread only after embedding succeeds. Removed when the view is closed/detached.</summary>
    public void PinToHostContent(int insetX, int insetY)
    {
        _contentPin?.Dispose();
        _contentPin = new ChildContentPin(Handle, insetX, insetY, _repaint.Request);
    }

    /// <summary>
    /// Make the window child-embed-friendly BEFORE the reparent: no OS chrome (FL draws the chrome), no
    /// taskbar button, no activation steal, parked off-screen so the pre-embed <see cref="Window.Show()"/>
    /// (which realizes the HWND + forces the first Skia paint) never flashes on the desktop. Call on the
    /// UI thread; returns once the HWND exists.
    /// </summary>
    public void PrepareForEmbedding()
    {
        _preparedForEmbedding = true;
        _window.SystemDecorations = SystemDecorations.None;
        _window.ShowInTaskbar = false;
        _window.ShowActivated = false;
        _window.CanResize = false;
        _window.WindowStartupLocation = WindowStartupLocation.Manual;
        _window.Position = new PixelPoint(-32000, -32000);
        _window.Show();                 // realizes the Win32 HWND + first software paint
        _repaint.Request();
    }

    /// <summary>External top-level fallback (no bridge / reparent failed): normal chrome, on-screen.</summary>
    public void ShowExternal()
    {
        void Apply()
        {
            _window.SystemDecorations = SystemDecorations.Full;
            _window.ShowInTaskbar = true;
            _window.ShowActivated = true;
            _window.CanResize = true;
            _window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            _window.Show();
            // PrepareForEmbedding already showed the window at -32000. StartupLocation applies
            // only to its first show, so explicitly recover an already-realized fallback window.
            _contentPin?.Dispose();
            _contentPin = null;
            if (!_externalPresented && _preparedForEmbedding) CenterOnScreen();
            _externalPresented = true;
            _window.Activate();
        }
        UiThread.RunOrPost(Apply);
    }

    private void CenterOnScreen()
    {
        var screen = _window.Screens.ScreenFromWindow(_window) ?? _window.Screens.Primary;
        if (screen is null) { _window.Position = new PixelPoint(0, 0); return; }
        PixelRect area = screen.WorkingArea;
        int width = (int)Math.Ceiling(_window.Bounds.Width * screen.Scaling);
        int height = (int)Math.Ceiling(_window.Bounds.Height * screen.Scaling);
        _window.Position = new PixelPoint(area.X + Math.Max(0, (area.Width - width) / 2),
            area.Y + Math.Max(0, (area.Height - height) / 2));
    }

    /// <summary>
    /// Force the embedded child to actually re-present. In software/redirection mode Avalonia paints via
    /// the redirection surface, but after a host hide→show (or an initial reparent) it can stay blank
    /// until an input event. Queue one visual and full-surface invalidation after native layout returns;
    /// Avalonia's normal WM_PAINT path then requests a complete compositor redraw without background erase.
    /// Safe from any thread.
    /// </summary>
    public void ForceRender() => UiThread.RunOrPost(_repaint.Request);

    private void InvalidateSurface()
    {
        try
        {
            _window.InvalidateVisual();
            IntPtr h = Handle;
            if (h != IntPtr.Zero)
                InvalidateRect(h, IntPtr.Zero, erase: false);
        }
        catch { /* best-effort */ }
    }

    /// <summary>Show/hide the window (used by the external, non-embedded fallback path). Any thread.</summary>
    public void SetVisible(bool visible)
        => UiThread.RunOrPost(() => { try { if (visible) { _window.Show(); } else { _window.Hide(); } } catch { } });

    /// <summary>Close (destroy) the window. The Avalonia THREAD keeps running for a later re-enable.</summary>
    public void Close()
    {
        if (Dispatcher.UIThread.CheckAccess()) CloseCore();
        else ObserveCloseAsync();
    }

    /// <summary>Close on the owning UI thread and report cleanup failures before plugin unload.</summary>
    public async Task CloseAsync() => await Dispatcher.UIThread.InvokeAsync(CloseCore);

    private void CloseCore()
    {
        _contentPin?.Dispose();
        _contentPin = null;
        _reallyClose = true;
        _window.Close();
        _repaint.Dispose();
    }

    private async void ObserveCloseAsync()
    {
        try { await CloseAsync().ConfigureAwait(false); }
        catch { /* A failed live subclass remains rooted; callers needing confirmation use CloseAsync. */ }
    }

    [DllImport("user32.dll")]
    private static extern bool InvalidateRect(IntPtr hWnd, IntPtr rectangle, bool erase);
}
