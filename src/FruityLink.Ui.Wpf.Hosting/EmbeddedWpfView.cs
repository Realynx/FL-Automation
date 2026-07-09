using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace FruityLink.Ui.Wpf.Hosting;

/// <summary>
/// Makes any WPF <see cref="Window"/> embeddable inside an FL Studio window host
/// (<c>IFlWindowHost.TryEmbed</c>). This is the WPF counterpart of the Avalonia hosting package's
/// <c>EmbeddedAvaloniaView</c>: it applies the live-verified workarounds a WPF window needs to
/// survive as a <c>WS_CHILD</c> of FL's non-WPF host form — software rendering with an opaque
/// backdrop (DWM "airspace" bug), a <c>WM_WINDOWPOSCHANGING</c> pin so WPF's window-as-child
/// coordinate bug can't move the window off the host, and a forced re-present after resize/re-show.
///
/// Typical flow, ALL on the window's own UI thread (see <see cref="WpfUiThread"/>):
/// <code>
/// var view = new EmbeddedWpfView(window);
/// view.PrepareForEmbedding();
/// window.Show();                                   // force a WPF layout/render pass before reparenting
/// bool embedded = context.Windows.TryEmbed(view.EnsureNativeHandle(), show: true);
/// if (embedded)
/// {
///     view.PinToHostContent(context.Windows.LastInsetX, context.Windows.LastInsetY);
///     view.ForceRerender();                        // synchronous first paint (else blank until a click)
/// }
/// else
/// {
///     view.RestoreExternalChrome();                // external top-level fallback (always works)
///     window.Show();
///     window.Activate();
/// }
/// </code>
/// The interop in here is finicky and verified live inside FL — do NOT casually restructure it.
/// </summary>
public sealed class EmbeddedWpfView
{
    private readonly Window _window;
    private readonly Color? _explicitBackdrop;
    private HwndSource? _pinSrc;
    private int _insetX, _insetY;   // FL content inset: border (left/right/bottom) + titlebar (top)

    /// <param name="window">The window to make embeddable. The view does not take ownership.</param>
    /// <param name="opaqueBackdrop">
    /// Backdrop color for the software <see cref="HwndTarget"/>. Should match your window's
    /// background. Without an OPAQUE backdrop, areas of the child not covered by a control stay
    /// transparent in software mode inside the foreign FL parent — FL's own content bleeds through
    /// them. When omitted, the window's <see cref="Control.Background"/> is used if it is a solid
    /// color, else black.
    /// </param>
    public EmbeddedWpfView(Window window, Color? opaqueBackdrop = null)
    {
        _window = window ?? throw new ArgumentNullException(nameof(window));
        _explicitBackdrop = opaqueBackdrop;
    }

    /// <summary>The wrapped window.</summary>
    public Window Window => _window;

    private Color Backdrop =>
        _explicitBackdrop
        ?? (_window.Background as SolidColorBrush)?.Color
        ?? Colors.Black;

    /// <summary>Ensure the Win32 HWND exists (without requiring a prior Show) and return it — the handle
    /// you hand to <c>IFlWindowHost.TryEmbed</c> to reparent this window into an FL host form.</summary>
    public IntPtr EnsureNativeHandle() => new WindowInteropHelper(_window).EnsureHandle();

    /// <summary>The window's HWND, or <see cref="IntPtr.Zero"/> if not yet created.</summary>
    public IntPtr Handle => new WindowInteropHelper(_window).Handle;

    /// <summary>
    /// Make the window child-embed-friendly BEFORE it is shown: drop OS chrome + taskbar presence, don't
    /// steal activation, and park it off-screen so the brief pre-embed <see cref="Window.Show"/> (which
    /// forces a WPF layout/render pass) never flashes on the desktop. The native side also strips the
    /// top-level styles during the reparent; setting them here keeps WPF's own state consistent.
    /// </summary>
    public void PrepareForEmbedding()
    {
        _window.WindowStyle = WindowStyle.None;
        _window.ResizeMode = ResizeMode.NoResize;
        _window.ShowInTaskbar = false;
        _window.ShowActivated = false;
        _window.WindowStartupLocation = WindowStartupLocation.Manual;
        _window.Left = -32000;
        _window.Top = -32000;

        // Force SOFTWARE rendering BEFORE the first Show()/reparent. As a WS_CHILD of FL's non-WPF parent,
        // hardware/DWM composition hits the airspace bug (blank until an input event). Setting it in
        // PinToHostContent (AFTER the reparent) is too late for the first paint → blank on load.
        try
        {
            IntPtr h = new WindowInteropHelper(_window).EnsureHandle();
            _pinSrc ??= HwndSource.FromHwnd(h);
            if (_pinSrc?.CompositionTarget is HwndTarget ht)
            {
                ht.RenderMode = RenderMode.SoftwareOnly;
                // Composite onto an OPAQUE backdrop — see the ctor doc for why transparency bleeds FL
                // content through empty child areas inside the foreign parent.
                ht.BackgroundColor = Backdrop;
            }
        }
        catch { /* best-effort; PinToHostContent also sets it */ }
    }

    /// <summary>Undo <see cref="PrepareForEmbedding"/> for the external fallback: normal chrome, on-screen,
    /// roughly centred. Safe to call whether or not embedding was attempted.</summary>
    public void RestoreExternalChrome()
    {
        _window.WindowStyle = WindowStyle.SingleBorderWindow;
        _window.ResizeMode = ResizeMode.CanResize;
        _window.ShowInTaskbar = true;
        _window.Left = Math.Max(0, (SystemParameters.PrimaryScreenWidth - _window.Width) / 2);
        _window.Top = Math.Max(0, (SystemParameters.PrimaryScreenHeight - _window.Height) / 2);
    }

    // --- Embed positioning: pin the HWND to fill the FL host's content control ---
    // Once the window is a WS_CHILD, WPF keeps re-applying Window.Left/Top and lands it OFF the parent (the
    // classic WPF-window-as-child coord bug). We intercept WM_WINDOWPOSCHANGING at the Win32 level (WPF can't
    // override it) and force x/y to the FL content inset, sized to fill the parent's client — so the view
    // always sits exactly over the FL content control regardless of what WPF wants.

    /// <summary>
    /// Call after a successful <c>TryEmbed</c>, passing the host's content insets
    /// (<c>IFlWindowHost.LastInsetX</c>/<c>LastInsetY</c>). Installs the position pin and the
    /// resize re-present workaround, and (re)applies software rendering.
    /// </summary>
    public void PinToHostContent(int insetX, int insetY)
    {
        _insetX = insetX; _insetY = insetY;
        try
        {
            IntPtr h = new WindowInteropHelper(_window).EnsureHandle();
            _pinSrc ??= HwndSource.FromHwnd(h);
            _pinSrc?.AddHook(PinHook);
            // As a WS_CHILD of a NON-WPF (FL) parent, WPF's default DWM/hardware composition hits the classic
            // "airspace" bug: the child's redirection surface isn't presented reliably — it goes blank on
            // re-show and only repaints the strip under a moving cursor. Forcing SOFTWARE rendering makes WPF
            // paint straight through WM_PAINT/GDI, which composites correctly inside a foreign parent. This is
            // the real fix for the blank/lazy-render bug (a size-nudge only masks it). Perf is a non-issue
            // for typical plugin UI.
            if (_pinSrc?.CompositionTarget is HwndTarget ht) { ht.RenderMode = RenderMode.SoftwareOnly; ht.BackgroundColor = Backdrop; }
            SnapToContent(h);
        }
        catch { /* best-effort */ }
    }

    private void SnapToContent(IntPtr h)
    {
        IntPtr parent = Win32.GetParent(h);
        if (parent == IntPtr.Zero || !Win32.GetClientRect(parent, out Win32.RECT rc)) return;
        int w = (rc.right - rc.left) - 2 * _insetX, hh = (rc.bottom - rc.top) - _insetY - _insetX;
        if (w < 1) w = 1; if (hh < 1) hh = 1;
        Win32.SetWindowPos(h, IntPtr.Zero, _insetX, _insetY, w, hh, 0x0014 /*NOZORDER|NOACTIVATE*/);
    }

    private IntPtr PinHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_WINDOWPOSCHANGING = 0x0046;
        if (msg == WM_WINDOWPOSCHANGING)
        {
            IntPtr parent = Win32.GetParent(hwnd);
            if (parent != IntPtr.Zero && Win32.GetClientRect(parent, out Win32.RECT rc))
            {
                int w = (rc.right - rc.left) - 2 * _insetX, hh = (rc.bottom - rc.top) - _insetY - _insetX;
                if (w < 1) w = 1; if (hh < 1) hh = 1;
                var wp = Marshal.PtrToStructure<Win32.WINDOWPOS>(lParam);
                wp.x = _insetX; wp.y = _insetY; wp.cx = w; wp.cy = hh;   // stay below the FL titlebar
                wp.flags &= ~0x0003u;   // clear SWP_NOSIZE(0x1)|SWP_NOMOVE(0x2) so our x/y/cx/cy apply
                Marshal.StructureToPtr(wp, lParam, false);
            }
        }
        const int WM_WINDOWPOSCHANGED = 0x0047;
        if (msg == WM_WINDOWPOSCHANGED)
        {
            // The FL host was minimized / maximized / docked, so the host subclass just resized+repositioned
            // this child. In SOFTWARE-render mode inside a foreign (FL) parent, WPF does NOT re-present on its
            // own after such a change — the airspace bug leaves the child transparent/blank (FL's form shows
            // through) until an input event.
            ForceRerender();
            const uint SWP_NOSIZE = 0x0001, WM_MOUSEMOVE = 0x0200;
            var wp = Marshal.PtrToStructure<Win32.WINDOWPOS>(lParam);
            if ((wp.flags & SWP_NOSIZE) == 0)
                // A SIZE change (maximize / restore / dock). ForceRerender alone presents the OLD frame
                // (present-before-rerender), so the newly-exposed area stays blank until real input. A
                // synthetic mouse-move — POSTED, so it does NOT move the OS cursor and never clicks — kicks
                // WPF's full render+present cycle at the new size. Verified live: this is the reliable
                // re-present after a resize inside the FL parent. Only fires on actual resizes (not moves/
                // scroll/typing). Coords (10,10) sit near the top-left of the client area — no button state.
                Win32.PostMessage(hwnd, WM_MOUSEMOVE, IntPtr.Zero, (IntPtr)((10 << 16) | 10));
        }
        return IntPtr.Zero;
    }

    /// <summary>
    /// Force the embedded WPF child to actually re-present after the FL host form was hidden→re-shown. With
    /// software rendering (see <see cref="PinToHostContent"/>) WPF paints via WM_PAINT, so we invalidate the
    /// visual tree AND immediately drive a synchronous native repaint of the whole client — otherwise the
    /// child can stay blank until the next input event. Self-dispatches to the window's UI thread.
    /// Call this after every re-show of the host (e.g. from your show/hide toggle).
    /// </summary>
    public void ForceRerender()
    {
        try
        {
            _window.Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    _window.InvalidateVisual();
                    _window.UpdateLayout();
                    IntPtr h = new WindowInteropHelper(_window).Handle;
                    if (h != IntPtr.Zero)
                        Win32.RedrawWindow(h, IntPtr.Zero, IntPtr.Zero,
                            RDW_INVALIDATE | RDW_ERASE | RDW_UPDATENOW | RDW_ALLCHILDREN);
                }
                catch { }
            }), DispatcherPriority.Render);
        }
        catch { }
    }

    private const uint RDW_INVALIDATE = 0x0001, RDW_ERASE = 0x0004, RDW_ALLCHILDREN = 0x0080, RDW_UPDATENOW = 0x0100;
}
