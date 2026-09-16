using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace FruityLink.FlStudio.Windowing;

/// <summary>Child-thread Win32 state, separate from FL-thread form creation and release.</summary>
internal sealed class NativeChildWindow
{
    private static readonly ConcurrentDictionary<uint, int> AttachedThreads = new();
    private readonly IntPtr _style, _extendedStyle, _owner;
    private readonly Rect _bounds;
    private readonly uint _thread;
    private bool _attached;
    private bool _modified;

    internal NativeChildWindow(IntPtr handle)
    {
        Handle = handle;
        _thread = GetWindowThreadProcessId(handle, out _);
        _style = GetWindowLongPtr(handle, -16);
        _extendedStyle = GetWindowLongPtr(handle, -20);
        _owner = GetWindowLongPtr(handle, -8);
        GetWindowRect(handle, out _bounds);
    }

    internal IntPtr Handle { get; }
    internal static bool CurrentThreadHasEmbeddedWindow => AttachedThreads.TryGetValue(GetCurrentThreadId(), out int count) && count > 0;
    internal static bool IsOwned(IntPtr handle) => IsWindow(handle) && GetWindowThreadProcessId(handle, out _) == GetCurrentThreadId();
    internal static bool IsLocal(IntPtr handle) => IsWindow(handle) && GetWindowThreadProcessId(handle, out uint pid) != 0 && pid == Environment.ProcessId;
    internal static bool IsVisible(IntPtr handle) => IsWindow(handle) && IsWindowVisible(handle);
    internal bool IsAttachedTo(IntPtr parent) => _attached && IsWindow(Handle) && GetParent(Handle) == parent;

    internal bool Attach(WindowEmbedReply reply, out string diagnostic)
    {
        diagnostic = "err:invalid-window-or-thread";
        if (!IsOwned(Handle) || !IsLocal(reply.Content)) return false;
        if ((_style.ToInt64() & 0x40000000) != 0) { diagnostic = "err:child-must-be-top-level"; return false; }
        long style = _style.ToInt64() & ~(0x80000000L | 0x00CF0000L);
        _modified = true;
        SetWindowLongPtr(Handle, -16, (IntPtr)(style | 0x40000000L | 0x04000000L));
        SetWindowLongPtr(Handle, -20, (IntPtr)(_extendedStyle.ToInt64() & ~(0x40000L | 0x80L)));
        Marshal.SetLastPInvokeError(0);
        SetParent(Handle, reply.Content);
        int error = Marshal.GetLastPInvokeError();
        _attached = GetParent(Handle) == reply.Content;
        if (!_attached) { diagnostic = $"err:setparent-failed:{error}"; return false; }
        AttachedThreads.AddOrUpdate(_thread, 1, (_, count) => count + 1);
        bool positioned = SetWindowPos(Handle, IntPtr.Zero, reply.X, reply.Y, Math.Max(1, reply.Width), Math.Max(1, reply.Height),
            0x0010 | 0x0004 | 0x0040 | 0x0020);
        diagnostic = positioned ? "ok" : $"err:position-failed:{Marshal.GetLastPInvokeError()}";
        return positioned;
    }

    internal bool Detach(out string diagnostic)
    {
        diagnostic = "ok";
        if (!_modified) return true;
        if (!IsWindow(Handle)) { Unregister(); return true; }
        if (!IsOwned(Handle)) { diagnostic = "err:close-on-child-thread-required"; return false; }
        SetParent(Handle, IntPtr.Zero);
        if (GetAncestor(Handle, 1) != GetDesktopWindow())
        {
            diagnostic = $"err:detach-failed:{Marshal.GetLastPInvokeError()}";
            return false;
        }
        SetWindowLongPtr(Handle, -16, _style);
        SetWindowLongPtr(Handle, -20, _extendedStyle);
        SetWindowLongPtr(Handle, -8, _owner);
        SetWindowPos(Handle, IntPtr.Zero, _bounds.Left, _bounds.Top, Math.Max(1, _bounds.Right - _bounds.Left),
            Math.Max(1, _bounds.Bottom - _bounds.Top), 0x0010 | 0x0004 | 0x0020);
        Unregister();
        _modified = false;
        return true;
    }

    private void Unregister()
    {
        if (!_attached) return;
        _attached = false;
        AttachedThreads.AddOrUpdate(_thread, 0, (_, count) => Math.Max(0, count - 1));
    }

    [StructLayout(LayoutKind.Sequential)] private struct Rect { internal int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out Rect rectangle);
    [DllImport("user32.dll")] private static extern IntPtr GetParent(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr GetDesktopWindow();
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetParent(IntPtr child, IntPtr parent);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)] private static extern IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
}
