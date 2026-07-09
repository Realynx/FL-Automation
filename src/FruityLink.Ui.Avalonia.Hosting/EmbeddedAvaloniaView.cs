using System;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;

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

    /// <summary>
    /// Make the window child-embed-friendly BEFORE the reparent: no OS chrome (FL draws the chrome), no
    /// taskbar button, no activation steal, parked off-screen so the pre-embed <see cref="Window.Show()"/>
    /// (which realizes the HWND + forces the first Skia paint) never flashes on the desktop. Call on the
    /// UI thread; returns once the HWND exists.
    /// </summary>
    public void PrepareForEmbedding()
    {
        _window.SystemDecorations = SystemDecorations.None;
        _window.ShowInTaskbar = false;
        _window.ShowActivated = false;
        _window.CanResize = false;
        _window.WindowStartupLocation = WindowStartupLocation.Manual;
        _window.Position = new PixelPoint(-32000, -32000);
        _window.Show();                 // realizes the Win32 HWND + first software paint
        ForceRenderCore();
    }

    /// <summary>External top-level fallback (no bridge / reparent failed): normal chrome, on-screen.</summary>
    public void ShowExternal()
    {
        void Apply()
        {
            _window.SystemDecorations = SystemDecorations.Full;
            _window.ShowInTaskbar = true;
            _window.CanResize = true;
            _window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            _window.Show();
            _window.Activate();
        }
        UiThread.RunOrPost(Apply);
    }

    /// <summary>
    /// Force the embedded child to actually re-present. In software/redirection mode Avalonia paints via
    /// the redirection surface, but after a host hide→show (or an initial reparent) it can stay blank
    /// until an input event — so we invalidate the visual tree AND drive a synchronous native repaint.
    /// Safe from any thread.
    /// </summary>
    public void ForceRender() => UiThread.RunOrPost(ForceRenderCore);

    private void ForceRenderCore()
    {
        try
        {
            _window.InvalidateVisual();
            IntPtr h = Handle;
            if (h != IntPtr.Zero)
                RedrawWindow(h, IntPtr.Zero, IntPtr.Zero,
                    RDW_INVALIDATE | RDW_ERASE | RDW_UPDATENOW | RDW_ALLCHILDREN);
        }
        catch { /* best-effort */ }
    }

    /// <summary>Show/hide the window (used by the external, non-embedded fallback path). Any thread.</summary>
    public void SetVisible(bool visible)
        => UiThread.RunOrPost(() => { try { if (visible) { _window.Show(); } else { _window.Hide(); } } catch { } });

    /// <summary>Close (destroy) the window. The Avalonia THREAD keeps running for a later re-enable.</summary>
    public void Close()
        => UiThread.RunOrPost(() => { try { _reallyClose = true; _window.Close(); } catch { } });

    private const uint RDW_INVALIDATE = 0x0001, RDW_ERASE = 0x0004, RDW_ALLCHILDREN = 0x0080, RDW_UPDATENOW = 0x0100;

    [DllImport("user32.dll")]
    private static extern bool RedrawWindow(IntPtr hWnd, IntPtr lprcUpdate, IntPtr hrgnUpdate, uint flags);
}
