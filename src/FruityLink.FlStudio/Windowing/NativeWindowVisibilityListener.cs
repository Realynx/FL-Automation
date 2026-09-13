using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace FruityLink.FlStudio.Windowing;

/// <summary>Receives only the bridge's explicit native-close notification on the child's UI thread.</summary>
internal sealed class NativeWindowVisibilityListener : IDisposable
{
    internal static readonly uint Message = RegisterWindowMessage("FruityLink.Window.UserHidden.v1");
    private static readonly ConcurrentDictionary<UIntPtr, NativeWindowVisibilityListener> Roots = new();
    private static long _nextId;
    private readonly IntPtr _child, _host, _content;
    private readonly Action _hidden;
    private readonly SubclassProc _callback;
    private readonly UIntPtr _id;
    private bool _installed;

    internal NativeWindowVisibilityListener(IntPtr child, IntPtr host, IntPtr content, Action hidden)
    {
        if (!NativeChildWindow.IsOwned(child)) throw new InvalidOperationException("Visibility tracking requires the child's UI thread.");
        _child = child;
        _host = host;
        _content = content;
        _hidden = hidden;
        _callback = OnMessage;
        _id = (UIntPtr)Interlocked.Increment(ref _nextId);
        _installed = Message != 0 && SetWindowSubclass(child, _callback, _id, UIntPtr.Zero);
        if (!_installed) throw new InvalidOperationException("Could not register native window visibility tracking.");
        Roots[_id] = this;
    }

    private IntPtr OnMessage(IntPtr window, uint message, IntPtr w, IntPtr l, UIntPtr id, UIntPtr data)
    {
        if (message == 0x0082) ReleaseRoot(); // WM_NCDESTROY removes the native subclass.
        if (message == Message && w == _host && l == _content && GetParent(window) == _content &&
            (GetWindowLongPtr(_host, -16).ToInt64() & 0x10000000) == 0)
        {
            try { _hidden(); } catch { /* Never let a persistence subscriber unwind through Win32. */ }
        }
        return DefSubclassProc(window, message, w, l);
    }

    public void Dispose()
    {
        if (!_installed) return;
        if (!IsWindow(_child)) { ReleaseRoot(); return; }
        if (!NativeChildWindow.IsOwned(_child)) throw new InvalidOperationException("Visibility cleanup requires the child's UI thread.");
        if (!RemoveWindowSubclass(_child, _callback, _id))
            throw new InvalidOperationException("Native visibility tracking remains installed; retain the plugin and retry cleanup.");
        ReleaseRoot();
    }

    private void ReleaseRoot() { _installed = false; Roots.TryRemove(_id, out _); }

    private delegate IntPtr SubclassProc(IntPtr window, uint message, IntPtr w, IntPtr l, UIntPtr id, UIntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string message);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr GetParent(IntPtr window);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);
    [DllImport("comctl32.dll")] private static extern bool SetWindowSubclass(IntPtr window, SubclassProc callback, UIntPtr id, UIntPtr data);
    [DllImport("comctl32.dll")] private static extern bool RemoveWindowSubclass(IntPtr window, SubclassProc callback, UIntPtr id);
    [DllImport("comctl32.dll")] private static extern IntPtr DefSubclassProc(IntPtr window, uint message, IntPtr w, IntPtr l);
}
