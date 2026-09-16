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
public sealed class FlWindowHost : IFlWindowHost, IFlWindowHostFactory
{
    // The raw bridge transport: InProcBridge.Raw in this same assembly P/Invokes the identical
    // FlBridge_Command export (the module is already loaded in the FL process by full path, so it
    // resolves to the same bridge regardless of which ALC we run in) with the same
    // null-terminated-request / resize-and-retry protocol the plugin-local copy used.
    private readonly Func<string, string> _send;

    /// <summary>Create the process-wide native window-host adapter.</summary>
    public FlWindowHost() : this(InProcBridge.Raw) { }

    internal FlWindowHost(Func<string, string> send) => _send = send;

    /// <inheritdoc/>
    public IFlWindowHost CreateWindowHost(string windowId, string caption) => new FlWindowSession(_send, caption);

    private string Raw(string message) => _send(message);

    // --- Win32 (user32): all child/host window ops run on the child's own thread, mirroring FL. ---
    private const int GWL_STYLE = -16, GWL_EXSTYLE = -20, GWLP_HWNDPARENT = -8;
    private const long WS_CHILD = 0x40000000, WS_VISIBLE = 0x10000000, WS_POPUP = 0x80000000,
                       WS_CLIPSIBLINGS = 0x04000000, WS_CAPTION = 0x00C00000, WS_THICKFRAME = 0x00040000,
                       WS_SYSMENU = 0x00080000, WS_MINIMIZEBOX = 0x00020000, WS_MAXIMIZEBOX = 0x00010000,
                       WS_OVERLAPPEDWINDOW = 0x00CF0000;
    private const long WS_EX_APPWINDOW = 0x00040000, WS_EX_TOOLWINDOW = 0x00000080;
    private const uint SWP_NOACTIVATE = 0x0010, SWP_SHOWWINDOW = 0x0040,
                       SWP_FRAMECHANGED = 0x0020, SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOZORDER = 0x0004;
    private const int SW_HIDE = 0, SW_SHOWNOACTIVATE = 4;

    // Embed state (owned by the child's UI thread). Kept so SetVisible/Close operate via Win32 without any
    // further blocking bridge round-trip. Static: one bridge window-host slot per process.
    private static IntPtr _hostHwnd = IntPtr.Zero;
    private static IntPtr _embeddedChild = IntPtr.Zero;
    private static IntPtr _savedStyle = IntPtr.Zero, _savedExStyle = IntPtr.Zero;
    private static IntPtr _savedOwner, _contentHwnd;
    private static int _operationInProgress;
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
        if (NativeChildWindow.CurrentThreadHasEmbeddedWindow || IsOwnedWindow(_embeddedChild)) return true;
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
        if (Interlocked.CompareExchange(ref _operationInProgress, 1, 0) != 0) return false;
        try
        {
            if (!IsOwnedWindow(childHwnd)) return Fail("err:invalid-child-or-thread");
            if (_embeddedChild != IntPtr.Zero && IsWindow(_embeddedChild))
            {
                if (_embeddedChild != childHwnd) return Fail("err:window-host-in-use");
                if (GetParent(childHwnd) != _contentHwnd) return Fail("err:child-detached-call-close");
                // Re-show an existing embed without overwriting its saved styles or blocking the
                // embedded UI thread on another bridge request.
                SetVisible(show);
                return true;
            }
            ResetState();
            return EmbedNewChild(childHwnd, show);
        }
        catch (Exception ex)
        {
            RollBackEmbed();
            return Fail("err:exception " + ex.Message);
        }
        finally { Volatile.Write(ref _operationInProgress, 0); }
    }

    private bool EmbedNewChild(IntPtr child, bool show)
    {
        if (NativeChildWindow.CurrentThreadHasEmbeddedWindow) return Fail("err:async-window-host-required");
        if ((GetWindowLongPtr(child, GWL_STYLE).ToInt64() & WS_CHILD) != 0)
            return Fail("err:child-must-be-top-level");
        _lastEmbedReply = Raw($"winhost_embed {child.ToInt64():x} {(show ? 1 : 0)}");
        if (!WindowEmbedReply.TryParse(_lastEmbedReply, out var reply)) return false;
        if (reply.Content == IntPtr.Zero || !IsWindow(reply.Content))
            return Fail(_lastEmbedReply + " | bad-content");

        _savedStyle = GetWindowLongPtr(child, GWL_STYLE);
        _savedExStyle = GetWindowLongPtr(child, GWL_EXSTYLE);
        _savedOwner = GetWindowLongPtr(child, GWLP_HWNDPARENT);
        _embeddedChild = child;
        _contentHwnd = reply.Content;
        _hostHwnd = reply.Host != IntPtr.Zero && IsWindow(reply.Host) ? reply.Host : reply.Content;
        long style = _savedStyle.ToInt64() & ~(WS_POPUP | WS_OVERLAPPEDWINDOW | WS_CAPTION
            | WS_THICKFRAME | WS_SYSMENU | WS_MINIMIZEBOX | WS_MAXIMIZEBOX);
        SetWindowLongPtr(child, GWL_STYLE, new IntPtr(style | WS_CHILD | WS_VISIBLE | WS_CLIPSIBLINGS));
        SetWindowLongPtr(child, GWL_EXSTYLE, new IntPtr(_savedExStyle.ToInt64() & ~(WS_EX_APPWINDOW | WS_EX_TOOLWINDOW)));
        SetParent(child, reply.Content);
        int parentError = Marshal.GetLastPInvokeError();
        if (GetParent(child) != reply.Content)
        {
            RollBackEmbed();
            return Fail(_lastEmbedReply + " | setparent-failed:" + parentError);
        }
        PositionChild(child, reply);
        SetVisible(show);
        return true;
    }

    private static void PositionChild(IntPtr child, WindowEmbedReply reply)
    {
        _lastInsetX = reply.X;
        _lastInsetY = reply.Y;
        int width = reply.Width, height = reply.Height;
        if ((width <= 0 || height <= 0) && GetClientRect(reply.Content, out RECT rc))
        {
            _lastInsetX = _lastInsetY = 0;
            width = rc.right - rc.left;
            height = rc.bottom - rc.top;
        }
        SetWindowPos(child, IntPtr.Zero, _lastInsetX, _lastInsetY, Math.Max(1, width), Math.Max(1, height),
            SWP_SHOWWINDOW | SWP_NOACTIVATE | SWP_NOZORDER | SWP_FRAMECHANGED);
    }

    private static bool IsOwnedWindow(IntPtr window)
        => window != IntPtr.Zero && IsWindow(window)
            && GetWindowThreadProcessId(window, out _) == GetCurrentThreadId();

    private static bool Fail(string reason) { _lastEmbedReply = reason; return false; }

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
        }
        catch { /* best-effort */ }
    }

    /// <inheritdoc/>
    public void Close()
    {
        if (Interlocked.CompareExchange(ref _operationInProgress, 1, 0) != 0) return;
        try
        {
            if (_embeddedChild != IntPtr.Zero && IsWindow(_embeddedChild) && !IsOwnedWindow(_embeddedChild))
            {
                Fail("err:close-on-child-thread-required");
                return;
            }
            if (_embeddedChild == IntPtr.Zero && _hostHwnd == IntPtr.Zero) return;
            RollBackEmbed();
        }
        catch { /* best-effort */ }
        finally { Volatile.Write(ref _operationInProgress, 0); }
    }

    private void RollBackEmbed()
    {
        try
        {
            if (!RestoreChild()) return;
            if (_hostHwnd != IntPtr.Zero && IsWindow(_hostHwnd)) ShowWindow(_hostHwnd, SW_HIDE);
            ResetState();
            // Safe only after the child was confirmed detached and can pump independently of FL.
            if (NativeChildWindow.CurrentThreadHasEmbeddedWindow)
                System.Threading.Tasks.Task.Run(() => { try { Raw("winhost_close"); } catch { } });
            else Raw("winhost_close");
        }
        catch { /* best-effort; preserve child state if detaching failed */ }
    }

    private static bool RestoreChild()
    {
        IntPtr child = _embeddedChild;
        if (child == IntPtr.Zero || !IsWindow(child)) return true;
        SetParent(child, IntPtr.Zero);
        // GetParent can return a top-level window's OWNER after detach. GA_PARENT reports
        // only the actual parent, so an original owner does not look like a failed detach.
        if (GetAncestor(child, 1 /* GA_PARENT */) != GetDesktopWindow()) return false;
        SetWindowLongPtr(child, GWL_STYLE, _savedStyle);
        SetWindowLongPtr(child, GWL_EXSTYLE, _savedExStyle);
        SetWindowLongPtr(child, GWLP_HWNDPARENT, _savedOwner);
        SetWindowPos(child, IntPtr.Zero, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
        return true;
    }

    private static void ResetState()
    {
        _embeddedChild = _hostHwnd = _contentHwnd = IntPtr.Zero;
        _savedStyle = _savedExStyle = _savedOwner = IntPtr.Zero;
        _lastInsetX = _lastInsetY = 0;
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
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hWnd, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr GetDesktopWindow();
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
}
