using System;
using System.Runtime.InteropServices;

namespace FruityLink.Ui.Wpf.Hosting;

/// <summary>
/// user32 P/Invoke surface for the WPF embed machinery. Declarations are copied verbatim from the
/// live-verified embed code — do NOT alter signatures, EntryPoints, or marshaling (native-interop
/// gotcha: a subtly wrong signature works in tests and corrupts state in production).
/// </summary>
internal static class Win32
{
    [StructLayout(LayoutKind.Sequential)] internal struct RECT { public int left, top, right, bottom; }
    [StructLayout(LayoutKind.Sequential)] internal struct WINDOWPOS { public IntPtr hwnd, hwndInsertAfter; public int x, y, cx, cy; public uint flags; }

    [DllImport("user32.dll", SetLastError = true)] internal static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);
    [DllImport("user32.dll")] internal static extern IntPtr GetParent(IntPtr hWnd);
    [DllImport("user32.dll")] internal static extern bool IsWindow(IntPtr hWnd);
    [DllImport("user32.dll")] internal static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
    [DllImport("user32.dll")] internal static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll")] internal static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] internal static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] internal static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
    [DllImport("user32.dll")] internal static extern bool RedrawWindow(IntPtr hWnd, IntPtr lprc, IntPtr hrgn, uint flags);
    [DllImport("user32.dll")] internal static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
}
