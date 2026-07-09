using System;
using System.Runtime.InteropServices;
using FruityLink.FlStudio.Inject;
using FruityLink.Plugins.Abstractions;

namespace FruityLink.FlStudio.Windowing;

/// <summary>
/// The SDK implementation of <see cref="IFlWindowHost"/>: thin first-party access to the native bridge
/// for the FL window-host embed (originally the FL Agent plugin's <c>EmbeddedChatHost</c>, task #22,
/// Phase 1 — promoted verbatim into the SDK).
///
/// <para><b>Thread model (the crux — 2026-07-01 VST-embed RE).</b> FL only ever <c>SetParent</c>s a window
/// from that window's OWN owning thread; when threads differ it POSTs, never blocks (this is how FL embeds
/// VST editor windows). The embedding plugin's window lives on its own UI thread, so we mirror FL exactly:
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
///
/// <para>One instance per process is constructed by the host; the embed state is static (there is exactly
/// one bridge window-host slot per process), so every instance sees the same embed.</para>
/// </summary>
public sealed class FlWindowHost : IFlWindowHost
{
    // The raw bridge transport: InProcBridge.Raw in this same assembly P/Invokes the identical
    // FlBridge_Command export (the module is already loaded in the FL process by full path, so it
    // resolves to the same bridge regardless of which ALC we run in) with the same
    // null-terminated-request / resize-and-retry protocol the plugin-local copy used.
    private static string Raw(string message) => InProcBridge.Raw(message);

    // --- Win32 (user32): all child/host window ops run on the child's own thread, mirroring FL. ---
    private const int GWL_STYLE = -16, GWL_EXSTYLE = -20;
    private const long WS_CHILD = 0x40000000, WS_VISIBLE = 0x10000000, WS_POPUP = 0x80000000,
                       WS_CLIPSIBLINGS = 0x04000000, WS_CAPTION = 0x00C00000, WS_THICKFRAME = 0x00040000,
                       WS_SYSMENU = 0x00080000, WS_MINIMIZEBOX = 0x00020000, WS_MAXIMIZEBOX = 0x00010000,
                       WS_OVERLAPPEDWINDOW = 0x00CF0000;
    private const long WS_EX_APPWINDOW = 0x00040000, WS_EX_TOOLWINDOW = 0x00000080;
    private const uint SWP_NOACTIVATE = 0x0010, SWP_SHOWWINDOW = 0x0040;
    private const int SW_HIDE = 0, SW_SHOWNOACTIVATE = 4;

    // Embed state (owned by the child's UI thread). Kept so SetVisible/Close operate via Win32 without any
    // further blocking bridge round-trip. Static: one bridge window-host slot per process.
    private static IntPtr _hostHwnd = IntPtr.Zero;
    private static IntPtr _embeddedChild = IntPtr.Zero;
    private static IntPtr _savedStyle = IntPtr.Zero, _savedExStyle = IntPtr.Zero;
    private static string _lastEmbedReply = "";
    private static int _lastInsetX, _lastInsetY;

    /// <inheritdoc/>
    public void SetStatusHint(string text)
    {
        // Fire-and-forget on a threadpool thread so it never blocks the caller and never issues a blocking
        // bridge call on the child's own (possibly parented) UI thread. Best-effort: silently no-ops if the
        // bridge/FL isn't ready. The native side routes this to FL's own hint setter on FL's main thread.
        System.Threading.Tasks.Task.Run(() => { try { Raw("hint " + text); } catch { /* best-effort */ } });
    }

    /// <inheritdoc/>
    public bool IsBridgeAvailable()
    {
        try { return Raw("ping") == "pong"; }
        catch { return false; }   // DllNotFound / any failure → no bridge → caller uses external window
    }

    /// <inheritdoc/>
    public string LastEmbedReply => _lastEmbedReply;

    /// <inheritdoc/>
    public int LastInsetX => _lastInsetX;

    /// <inheritdoc/>
    public int LastInsetY => _lastInsetY;

    /// <inheritdoc/>
    public bool TryEmbed(IntPtr childHwnd, bool show)
    {
        try
        {
            // Phase A — FL MAIN THREAD (bridge): create + realize the FL host form, return its HWND. This
            // never touches our child, so the bridge's blocking SendMessage cannot deadlock.
            string r = Raw($"winhost_embed {childHwnd.ToInt64():x} {(show ? 1 : 0)}");
            _lastEmbedReply = r;
            if (!r.Contains("\"ok\":1")) return false;
            // Parent target = the FL host's CONTENT control HWND (FL keeps it laid out inside the chrome);
            // "host" = the FL form's top-level window (for show/hide).
            IntPtr content  = ParseHexField(r, "content");
            IntPtr formHwnd = ParseHexField(r, "host");
            if (content == IntPtr.Zero || !IsWindow(content)) { _lastEmbedReply = r + " | bad-content"; return false; }

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
                _lastEmbedReply = r + " | setparent-failed";
                return false;
            }
            _hostHwnd = (formHwnd != IntPtr.Zero && IsWindow(formHwnd)) ? formHwnd : content;
            _embeddedChild = childHwnd;

            // Inset the child to the FL CONTENT rect (below the skinned titlebar) the bridge reported. The
            // bridge already showed the FL form WITH chrome, so we do NOT show the form here.
            _lastInsetX = ParseIntField(r, "cx"); _lastInsetY = ParseIntField(r, "cy");
            int cw = ParseIntField(r, "cw"), chh = ParseIntField(r, "ch");
            if (cw > 0 && chh > 0)
                SetWindowPos(childHwnd, IntPtr.Zero, _lastInsetX, _lastInsetY, cw, chh, SWP_SHOWWINDOW | SWP_NOACTIVATE);
            else if (GetClientRect(content, out RECT rc))
                SetWindowPos(childHwnd, IntPtr.Zero, 0, 0, rc.right - rc.left, rc.bottom - rc.top, SWP_SHOWWINDOW | SWP_NOACTIVATE);
            return true;
        }
        catch (Exception ex) { _lastEmbedReply = "err:exception " + ex.Message; return false; }
    }

    /// <inheritdoc/>
    public bool IsHostVisible()
    {
        try { return _hostHwnd != IntPtr.Zero && IsWindow(_hostHwnd) && IsWindowVisible(_hostHwnd); }
        catch { return false; }
    }

    /// <inheritdoc/>
    public void SetVisible(bool visible)
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

    /// <inheritdoc/>
    public void Close()
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

    // --- user32 P/Invokes (private to this class; copied verbatim from the plugin's shared Win32 class —
    //     do NOT alter signatures, EntryPoints, or marshaling: native-interop gotcha). ---
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int left, top, right, bottom; }

    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);
    [DllImport("user32.dll")] private static extern IntPtr GetParent(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
}
