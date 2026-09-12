using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace FruityLink.Plugins.FlAgent.Ui;

/// <summary>
/// The Win32 embed/pinning machinery of <see cref="FlAgentChatWindow"/> (task #22, Phase 1): making
/// the WPF window child-embed-friendly, pinning it over the FL host form's content control, and the
/// live-verified airspace-bug workarounds (software rendering, forced re-present, synthetic
/// mouse-move). Kept in its own partial file so the finicky, verified-in-FL interop is separated
/// from the chat presentation code — do NOT casually restructure anything in here.
/// </summary>
internal sealed partial class FlAgentChatWindow
{
    // ---- window-host embed support (task #22, Phase 1) ----

    /// <summary>Ensure the Win32 HWND exists (without requiring a prior Show) and return it — the handle
    /// we hand to the native bridge to reparent this window into an FL host form.</summary>
    public IntPtr EnsureNativeHandle() => new WindowInteropHelper(this).EnsureHandle();

    /// <summary>
    /// Make the window child-embed-friendly BEFORE it is shown: drop OS chrome + taskbar presence, don't
    /// steal activation, and park it off-screen so the brief pre-embed <see cref="Window.Show"/> (which
    /// forces a WPF layout/render pass) never flashes on the desktop. The native side also strips the
    /// top-level styles during the reparent; setting them here keeps WPF's own state consistent.
    /// </summary>
    public void PrepareForEmbedding()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = -32000;
        Top = -32000;

        // Force SOFTWARE rendering BEFORE the first Show()/reparent. As a WS_CHILD of FL's non-WPF parent,
        // hardware/DWM composition hits the airspace bug (blank until an input event). Setting it in
        // PinToHostContent (AFTER the reparent) is too late for the first paint → blank on load.
        try
        {
            IntPtr h = new WindowInteropHelper(this).EnsureHandle();
            _pinSrc ??= HwndSource.FromHwnd(h);
            if (_pinSrc?.CompositionTarget is HwndTarget ht)
            {
                ht.RenderMode = RenderMode.SoftwareOnly;
                // Composite onto an OPAQUE backdrop. Without this, areas of the child not covered by a control
                // stay transparent in software mode inside the foreign FL parent — so FL content behind (e.g.
                // the animated mascot it shows in the MAXIMIZED script-dialog) bleeds through the empty chat
                // area. An opaque backdrop matching the window bg makes the whole child cover the FL form.
                ht.BackgroundColor = OpaqueBackdrop;
            }
        }
        catch { /* best-effort; PinToHostContent also sets it */ }
    }

    /// <summary>Undo <see cref="PrepareForEmbedding"/> for the external fallback: normal chrome, on-screen,
    /// roughly centred. Safe to call whether or not embedding was attempted.</summary>
    public void RestoreExternalChrome()
    {
        WindowStyle = WindowStyle.SingleBorderWindow;
        ResizeMode = ResizeMode.CanResize;
        ShowInTaskbar = true;
        Left = Math.Max(0, (SystemParameters.PrimaryScreenWidth - Width) / 2);
        Top = Math.Max(0, (SystemParameters.PrimaryScreenHeight - Height) / 2);
    }

    // --- Embed positioning: pin our HWND to fill the FL host's content control ---
    // Once we're a WS_CHILD, WPF keeps re-applying Window.Left/Top and lands us OFF the parent (the classic
    // WPF-window-as-child coord bug). We intercept WM_WINDOWPOSCHANGING at the Win32 level (WPF can't override
    // it) and force x=0,y=0 filling the parent's client, so the chat always sits exactly over the FL content
    // control regardless of what WPF wants.
    private HwndSource? _pinSrc;
    private int _insetX, _insetY;   // FL content inset: border (left/right/bottom) + titlebar (top)
    public void PinToHostContent(int insetX, int insetY)
    {
        _insetX = insetX; _insetY = insetY;
        try
        {
            IntPtr h = new WindowInteropHelper(this).EnsureHandle();
            _pinSrc ??= HwndSource.FromHwnd(h);
            _pinSrc?.AddHook(PinHook);
            // As a WS_CHILD of a NON-WPF (FL) parent, WPF's default DWM/hardware composition hits the classic
            // "airspace" bug: the child's redirection surface isn't presented reliably — it goes blank on
            // re-show and only repaints the strip under a moving cursor. Forcing SOFTWARE rendering makes WPF
            // paint straight through WM_PAINT/GDI, which composites correctly inside a foreign parent. This is
            // the real fix for the blank/lazy-render bug (the earlier size-nudge only masked it). Perf is a
            // non-issue for a small text chat.
            if (_pinSrc?.CompositionTarget is HwndTarget ht) { ht.RenderMode = RenderMode.SoftwareOnly; ht.BackgroundColor = OpaqueBackdrop; }
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
                // scroll/typing). Coords (10,10) sit over the transcript → harmless (no button state).
                Win32.PostMessage(hwnd, WM_MOUSEMOVE, IntPtr.Zero, (IntPtr)((10 << 16) | 10));
        }
        return IntPtr.Zero;
    }
    /// <summary>
    /// Force the embedded WPF child to actually re-present after the FL host form was hidden→re-shown. With
    /// software rendering (see <see cref="PinToHostContent"/>) WPF paints via WM_PAINT, so we invalidate the
    /// visual tree AND immediately drive a synchronous native repaint of the whole client — otherwise the
    /// child can stay blank until the next input event. Self-dispatches to the UI thread.
    /// </summary>
    public void ForceRerender()
    {
        try
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    InvalidateVisual();
                    UpdateLayout();
                    IntPtr h = new WindowInteropHelper(this).Handle;
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
