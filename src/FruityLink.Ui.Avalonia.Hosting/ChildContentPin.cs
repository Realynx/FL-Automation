using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace FruityLink.Ui.Avalonia.Hosting;

/// <summary>Owned-thread subclass: let FL determine the parent geometry, never a toolkit's saved screen position.</summary>
internal sealed class ChildContentPin : IDisposable
{
    private readonly IntPtr _child;
    private readonly int _insetX, _insetY;
    private readonly Action _repaint;
    private readonly SubclassProc _callback;
    private readonly UIntPtr _id;
    private static long _nextId;
    private static readonly ConcurrentDictionary<UIntPtr, ChildContentPin> Roots = new();
    private bool _installed;
    internal bool IsInstalled => _installed;

    internal ChildContentPin(IntPtr child, int insetX, int insetY, Action repaint)
    {
        if (child == IntPtr.Zero || GetWindowThreadProcessId(child, out _) != GetCurrentThreadId())
            throw new InvalidOperationException("Content pinning requires the window's owning UI thread.");
        _child = child;
        _insetX = Math.Max(0, insetX);
        _insetY = Math.Max(0, insetY);
        _repaint = repaint;
        _callback = HandleMessage;
        _id = (UIntPtr)System.Threading.Interlocked.Increment(ref _nextId);
        _installed = SetWindowSubclass(child, _callback, _id, UIntPtr.Zero);
        if (!_installed) throw new InvalidOperationException("Could not install the embedded content position handler.");
        Roots[_id] = this;
    }

    private IntPtr HandleMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam, UIntPtr id, UIntPtr data)
    {
        if (message == 0x0082) MarkDestroyed(); // The subclass is removed by the window's destruction.
        try { if (message == 0x0046 && lParam != IntPtr.Zero) PinPosition(lParam); } // WM_WINDOWPOSCHANGING
        catch { /* Keep the native message chain running even if a stale layout cannot be read. */ }
        IntPtr result = DefSubclassProc(window, message, wParam, lParam);
        try
        {
            if (message is 0x0005 or 0x02E0 or 0x0018 or 0x0047) _repaint(); // size/DPI/show/position; caller queues outside this callback
        }
        catch { /* Never dispatch a message to the next subclass twice after it has completed. */ }
        return result;
    }

    private void PinPosition(IntPtr position)
    {
        if ((GetWindowLongPtr(_child, -16).ToInt64() & 0x40000000) == 0) return;
        IntPtr parent = GetParent(_child);
        if (parent == IntPtr.Zero || !GetClientRect(parent, out Rect rectangle)) return;
        WindowPosition value = Marshal.PtrToStructure<WindowPosition>(position);
        value.X = _insetX;
        value.Y = _insetY;
        value.Width = Math.Max(1, rectangle.Right - rectangle.Left - _insetX * 2);
        value.Height = Math.Max(1, rectangle.Bottom - rectangle.Top - _insetY - _insetX);
        UpdateChangeFlags(ref value, parent);
        Marshal.StructureToPtr(value, position, false);
    }

    private void UpdateChangeFlags(ref WindowPosition value, IntPtr parent)
    {
        if (!GetWindowRect(_child, out Rect current))
        {
            value.Flags &= ~0x0003u;
            return;
        }
        MapWindowPoints(IntPtr.Zero, parent, ref current, 2);
        bool samePosition = current.Left == value.X && current.Top == value.Y;
        bool sameSize = current.Right - current.Left == value.Width && current.Bottom - current.Top == value.Height;
        value.Flags = sameSize ? value.Flags | 0x0001u : value.Flags & ~0x0001u;
        value.Flags = samePosition ? value.Flags | 0x0002u : value.Flags & ~0x0002u;
    }

    public void Dispose()
    {
        if (!_installed) return;
        if (!IsWindow(_child)) { MarkDestroyed(); return; }
        if (GetWindowThreadProcessId(_child, out _) != GetCurrentThreadId())
            throw new InvalidOperationException("Content pin removal requires the window's owning UI thread.");
        if (!RemoveWindowSubclass(_child, _callback, _id))
            throw new InvalidOperationException("The embedded content handler is still installed; keep its view alive and retry cleanup.");
        MarkDestroyed();
    }

    private void MarkDestroyed() { _installed = false; Roots.TryRemove(_id, out _); }

    [StructLayout(LayoutKind.Sequential)] private struct Rect { internal int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct WindowPosition { internal IntPtr Window, InsertAfter; internal int X, Y, Width, Height; internal uint Flags; }
    private delegate IntPtr SubclassProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam, UIntPtr id, UIntPtr data);
    [DllImport("comctl32.dll", SetLastError = true)] private static extern bool SetWindowSubclass(IntPtr window, SubclassProc callback, UIntPtr id, UIntPtr data);
    [DllImport("comctl32.dll")] private static extern bool RemoveWindowSubclass(IntPtr window, SubclassProc callback, UIntPtr id);
    [DllImport("comctl32.dll")] private static extern IntPtr DefSubclassProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern IntPtr GetParent(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr window, out Rect rectangle);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out Rect rectangle);
    [DllImport("user32.dll")] private static extern int MapWindowPoints(IntPtr from, IntPtr to, ref Rect rectangle, uint points);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);
}
