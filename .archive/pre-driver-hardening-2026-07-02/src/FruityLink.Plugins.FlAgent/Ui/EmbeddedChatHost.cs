using System;
using System.Runtime.InteropServices;
using System.Text;

namespace FruityLink.Plugins.FlAgent.Ui;

/// <summary>
/// Thin first-party access to the native bridge for the window-host embed (task #22, Phase 1).
///
/// <para><b>Thread model (the crux — 2026-07-01 VST-embed RE).</b> FL only ever <c>SetParent</c>s a window
/// from that window's OWN owning thread; when threads differ it POSTs, never blocks (this is how FL embeds
/// VST editor windows). Our chat is a WPF window on its own UI thread, so we mirror FL exactly:
/// <list type="number">
/// <item>The bridge command <c>winhost_embed</c> runs on FL's MAIN thread but ONLY creates + realizes the FL
/// host form and returns its HWND — it never touches our child, so its blocking <c>SendMessage</c> is safe.</item>
/// <item>WE then do the <c>SetParent</c> + restyle + position + show HERE, on the child's own thread, via Win32.
/// FL's main thread is just pumping, so it completes with no deadlock. We do NOT issue any further blocking
/// bridge call while the child is parented, so no cross-thread op can target our (possibly blocked) thread.</item>
/// </list>
/// The earlier hangs were the opposite: our thread blocked in the bridge <c>SendMessage</c> while FL's main
/// thread tried to <c>SetParent</c>/activate our window — classic cross-thread deadlock.</para>
///
/// <para>Fully fail-safe: any failure or a missing bridge leaves the caller on its external top-level window.</para>
/// </summary>
internal static class EmbeddedChatHost
{
    // Cdecl, identical signature to InProcBridge.FlBridge_Command — the module is already loaded in the
    // FL process (by full path) so this resolves to the same bridge regardless of which ALC we run in.
    [DllImport("FlBridge.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int FlBridge_Command(byte[] req, byte[] outBuf, int outLen);

    // --- Win32 (user32): all child/host window ops run on the child's own thread, mirroring FL ---
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);
    [DllImport("user32.dll")] private static extern IntPtr GetParent(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int left, top, right, bottom; }

    private const int GWL_STYLE = -16, GWL_EXSTYLE = -20;
    private const long WS_CHILD = 0x40000000, WS_VISIBLE = 0x10000000, WS_POPUP = 0x80000000,
                       WS_CLIPSIBLINGS = 0x04000000, WS_CAPTION = 0x00C00000, WS_THICKFRAME = 0x00040000,
                       WS_SYSMENU = 0x00080000, WS_MINIMIZEBOX = 0x00020000, WS_MAXIMIZEBOX = 0x00010000,
                       WS_OVERLAPPEDWINDOW = 0x00CF0000;
    private const long WS_EX_APPWINDOW = 0x00040000, WS_EX_TOOLWINDOW = 0x00000080;
    private const uint SWP_NOACTIVATE = 0x0010, SWP_SHOWWINDOW = 0x0040;
    private const int SW_HIDE = 0, SW_SHOWNOACTIVATE = 4;

    // Embed state (owned by the child's UI thread). Kept so SetVisible/Close operate via Win32 without any
    // further blocking bridge round-trip.
    private static IntPtr _hostHwnd = IntPtr.Zero;
    private static IntPtr _embeddedChild = IntPtr.Zero;
    private static IntPtr _savedStyle = IntPtr.Zero, _savedExStyle = IntPtr.Zero;

    private static string Raw(string message)
    {
        byte[] req = Encoding.UTF8.GetBytes(message + "\0"); // null-terminated for the C side
        byte[] buf = new byte[4096];
        int n = FlBridge_Command(req, buf, buf.Length);
        if (n < 0) return "err:inproc";
        if (n > buf.Length)
        {
            buf = new byte[n];
            n = FlBridge_Command(req, buf, buf.Length);
            if (n < 0 || n > buf.Length) return "err:inproc";
        }
        return Encoding.UTF8.GetString(buf, 0, n);
    }

    /// <summary>
    /// Set FL's status/hint bar text (the plugin-host loading indicator) via the native <c>hint</c> command.
    /// Fire-and-forget on a threadpool thread so it never blocks the caller and never issues a blocking bridge
    /// call on the child's own (possibly parented) UI thread. Best-effort: silently no-ops if the bridge/FL
    /// isn't ready. The native side routes this to FL's own hint setter on FL's main thread.
    /// </summary>
    internal static void SetStatusHint(string text)
    {
        System.Threading.Tasks.Task.Run(() => { try { Raw("hint " + text); } catch { /* best-effort */ } });
    }

    /// <summary>True only when FlBridge.dll is loaded in this process and answers (the in-FL case).</summary>
    public static bool IsBridgeAvailable()
    {
        try { return Raw("ping") == "pong"; }
        catch { return false; }   // DllNotFound / any failure → no bridge → caller uses external window
    }

    /// <summary>
    /// The raw JSON the bridge returned from the last <see cref="TryEmbed"/> call (includes the native
    /// per-step "diag" field). Surfaced so the caller can log WHY an embed failed. Empty until first attempt.
    /// </summary>
    public static string LastEmbedReply { get; private set; } = "";

    /// <summary>The FL content-rect inset (border/titlebar) from the last embed — the caller pins the child below it.</summary>
    public static int LastInsetX { get; private set; }
    public static int LastInsetY { get; private set; }

    /// <summary>
    /// Embed <paramref name="childHwnd"/> into an FL host form. MUST be called on the thread that OWNS
    /// <paramref name="childHwnd"/> (the WPF UI thread) — the reparent is performed here, on that thread.
    /// Returns true only when the child is confirmed reparented; any failure keeps the external window.
    /// </summary>
    public static bool TryEmbed(IntPtr childHwnd, bool show)
    {
        try
        {
            // Phase A — FL MAIN THREAD (bridge): create + realize the FL host form, return its HWND. This
            // never touches our child, so the bridge's blocking SendMessage cannot deadlock.
            string r = Raw($"winhost_embed {childHwnd.ToInt64():x} {(show ? 1 : 0)}");
            LastEmbedReply = r;
            if (!r.Contains("\"ok\":1")) return false;
            // Parent target = the FL host's CONTENT control HWND (FL keeps it laid out inside the chrome);
            // "host" = the FL form's top-level window (for show/hide).
            IntPtr content  = ParseHexField(r, "content");
            IntPtr formHwnd = ParseHexField(r, "host");
            if (content == IntPtr.Zero || !IsWindow(content)) { LastEmbedReply = r + " | bad-content"; return false; }

            // Phase B — THIS thread (owns the child), all via Win32: restyle → SetParent → fill. FL's main
            // thread is only pumping, so SetParent's cross-thread notifications complete (no deadlock).
            _savedStyle   = GetWindowLongPtr(childHwnd, GWL_STYLE);
            _savedExStyle = GetWindowLongPtr(childHwnd, GWL_EXSTYLE);

            long s = _savedStyle.ToInt64();
            s &= ~(WS_POPUP | WS_OVERLAPPEDWINDOW | WS_CAPTION | WS_THICKFRAME | WS_SYSMENU | WS_MINIMIZEBOX | WS_MAXIMIZEBOX);
            s |=  (WS_CHILD | WS_VISIBLE | WS_CLIPSIBLINGS);
            SetWindowLongPtr(childHwnd, GWL_STYLE, new IntPtr(s));
            SetWindowLongPtr(childHwnd, GWL_EXSTYLE, new IntPtr(_savedExStyle.ToInt64() & ~(WS_EX_APPWINDOW | WS_EX_TOOLWINDOW)));

            SetParent(childHwnd, content);
            if (GetParent(childHwnd) != content)   // reparent did not take → restore + external fallback
            {
                SetWindowLongPtr(childHwnd, GWL_STYLE, _savedStyle);
                SetWindowLongPtr(childHwnd, GWL_EXSTYLE, _savedExStyle);
                LastEmbedReply = r + " | setparent-failed";
                return false;
            }
            _hostHwnd = (formHwnd != IntPtr.Zero && IsWindow(formHwnd)) ? formHwnd : content;
            _embeddedChild = childHwnd;

            // Inset the child to the FL CONTENT rect (below the skinned titlebar) the bridge reported. The
            // bridge already showed the FL form WITH chrome, so we do NOT show the form here.
            LastInsetX = ParseIntField(r, "cx"); LastInsetY = ParseIntField(r, "cy");
            int cw = ParseIntField(r, "cw"), chh = ParseIntField(r, "ch");
            if (cw > 0 && chh > 0)
                SetWindowPos(childHwnd, IntPtr.Zero, LastInsetX, LastInsetY, cw, chh, SWP_SHOWWINDOW | SWP_NOACTIVATE);
            else if (GetClientRect(content, out RECT rc))
                SetWindowPos(childHwnd, IntPtr.Zero, 0, 0, rc.right - rc.left, rc.bottom - rc.top, SWP_SHOWWINDOW | SWP_NOACTIVATE);
            return true;
        }
        catch (Exception ex) { LastEmbedReply = "err:exception " + ex.Message; return false; }
    }

    /// <summary>True if the FL host form is currently shown (so the View ✓ + toggle track the real state,
    /// incl. after the user clicks the native close (X), which hides — not destroys — the form).</summary>
    public static bool IsHostVisible()
    {
        try { return _hostHwnd != IntPtr.Zero && IsWindow(_hostHwnd) && IsWindowVisible(_hostHwnd); }
        catch { return false; }
    }

    /// <summary>Show/hide the FL HOST form (the View ▸ FL Agent toggle drives this when embedded).</summary>
    public static void SetVisible(bool visible)
    {
        try
        {
            // Win32 directly on the host (SW_SHOWNOACTIVATE so toggling never steals FL's focus). No blocking
            // bridge call → no chance of a cross-thread block on our parented child.
            if (_hostHwnd != IntPtr.Zero && IsWindow(_hostHwnd))
                ShowWindow(_hostHwnd, visible ? SW_SHOWNOACTIVATE : SW_HIDE);
            else
                Raw($"winhost_show {(visible ? 1 : 0)}");   // fallback if we somehow lost the host handle
        }
        catch { /* best-effort */ }
    }

    /// <summary>True when the chat is currently embedded inside an FL host form (so min/max/dock apply).</summary>
    public static bool IsEmbedded => _hostHwnd != IntPtr.Zero && _embeddedChild != IntPtr.Zero;

    /// <summary>
    /// Minimize / toggle-maximize the FL HOST form — like FL's own plugin-editor windows. The bridge runs
    /// FL's native SetWindowState on FL's MAIN thread, re-fits our embedded child, and re-presents it; returns
    /// false when not embedded or the op was a no-op. Best-effort (never throws).
    ///
    /// <para><b>Thread note.</b> These issue a blocking bridge round-trip, so prefer calling them from FL's
    /// own UI thread (e.g. a native menu/caption handler) rather than the WPF child's thread. FL's work here
    /// only resizes the HOST form (our child is repositioned asynchronously), so unlike the initial embed
    /// there is no synchronous cross-thread op targeting our child.</para>
    /// </summary>
    public static bool Minimize()       => WinHostOp("winhost_min");
    public static bool ToggleMaximize() => WinHostOp("winhost_max");

    /// <summary>
    /// In-workspace docking is DEFERRED (always returns false). FL's dock reparent recreates the host form's
    /// Win32 handle, which orphans our embedded child; the window stays a solid, movable floating plugin-style
    /// window with working min/max. Supporting true docking needs a full cross-thread re-embed after the
    /// toggle (re-acquire the new handle, re-subclass, re-parent the child) — a documented follow-up.
    /// </summary>
    public static bool ToggleDock()     => WinHostOp("winhost_dock");

    private static bool WinHostOp(string cmd)
    {
        try
        {
            if (!IsEmbedded) return false;
            return Raw(cmd).Contains("\"ok\":1");
        }
        catch { return false; }
    }

    /// <summary>
    /// Detach our child (on its own thread) + hide the host — reversible, used on plugin disable before the
    /// WPF window is closed. Mirrors FL's teardown order (park the child first, then drop the host).
    /// </summary>
    public static void Close()
    {
        try
        {
            IntPtr child = _embeddedChild;
            if (child != IntPtr.Zero && IsWindow(child))
            {
                SetParent(child, IntPtr.Zero);                          // detach on the child's own thread
                if (_savedStyle   != IntPtr.Zero) SetWindowLongPtr(child, GWL_STYLE,   _savedStyle);
                if (_savedExStyle != IntPtr.Zero) SetWindowLongPtr(child, GWL_EXSTYLE, _savedExStyle);
            }
            if (_hostHwnd != IntPtr.Zero && IsWindow(_hostHwnd)) ShowWindow(_hostHwnd, SW_HIDE);
            _embeddedChild = IntPtr.Zero; _hostHwnd = IntPtr.Zero;
            Raw("winhost_close");                                        // reset the bridge's embed state
        }
        catch { /* best-effort */ }
    }

    /// <summary>Parse a <c>"field":"0x...."</c> hex handle out of the bridge's JSON reply.</summary>
    private static IntPtr ParseHexField(string json, string field)
    {
        try
        {
            string key = "\"" + field + "\":\"0x";
            int i = json.IndexOf(key, StringComparison.Ordinal);
            if (i < 0) return IntPtr.Zero;
            i += key.Length;
            int j = i;
            while (j < json.Length && Uri.IsHexDigit(json[j])) j++;
            if (j == i) return IntPtr.Zero;
            return new IntPtr(Convert.ToInt64(json.Substring(i, j - i), 16));
        }
        catch { return IntPtr.Zero; }
    }

    /// <summary>Parse an integer <c>"field":123</c> (or negative) out of the bridge's JSON reply.</summary>
    private static int ParseIntField(string json, string field)
    {
        try
        {
            string key = "\"" + field + "\":";
            int i = json.IndexOf(key, StringComparison.Ordinal);
            if (i < 0) return 0;
            i += key.Length;
            int j = i;
            if (j < json.Length && (json[j] == '-' || json[j] == '+')) j++;
            while (j < json.Length && char.IsDigit(json[j])) j++;
            if (j == i || (j == i + 1 && !char.IsDigit(json[i]))) return 0;
            return int.TryParse(json.Substring(i, j - i), out int v) ? v : 0;
        }
        catch { return 0; }
    }
}
