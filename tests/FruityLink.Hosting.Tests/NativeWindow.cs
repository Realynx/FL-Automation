using System.ComponentModel;
using System.Runtime.InteropServices;

namespace FruityLink.Hosting.Tests;

/// <summary>Hidden Win32 windows let the managed adapter run without FL or its native bridge.</summary>
internal sealed class NativeWindow : IDisposable
{
    internal IntPtr Handle { get; }

    internal NativeWindow(IntPtr owner = default, uint style = 0x00CF0000)
    {
        Handle = CreateWindowEx(0, "STATIC", "FruityLink hosting regression test", style,
            -32000, -32000, 640, 480, owner, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (Handle == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    internal IntPtr Style => GetWindowLongPtr(Handle, -16);
    internal IntPtr ExStyle
    {
        get => GetWindowLongPtr(Handle, -20);
        set => SetWindowLongPtr(Handle, -20, value);
    }
    internal IntPtr Parent => GetParent(Handle);
    internal bool Visible => IsWindowVisible(Handle);

    public void Dispose() => DestroyWindow(Handle);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(uint exStyle, string className, string title, uint style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr GetParent(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value);
}
