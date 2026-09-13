using System.Runtime.InteropServices;
using FruityLink.FlStudio.Windowing;
using Xunit;

namespace FruityLink.Hosting.Tests;

[Collection("Windows hosting")]
public sealed class NativeWindowVisibilityTests
{
    [Fact]
    public void OnlyMatchingNativeCloseNotifiesAndDisposalRemovesCallback()
    {
        using var host = new NativeWindow();
        using var child = new NativeWindow(host.Handle, 0x40000000);
        int hidden = 0;
        using var listener = new NativeWindowVisibilityListener(child.Handle, host.Handle, host.Handle, () => hidden++);
        SendMessage(child.Handle, 0x0018, IntPtr.Zero, IntPtr.Zero); // Generic hide is not a user preference.
        SendMessage(child.Handle, NativeWindowVisibilityListener.Message, IntPtr.Zero, host.Handle);
        Assert.Equal(0, hidden);
        SendMessage(child.Handle, NativeWindowVisibilityListener.Message, host.Handle, host.Handle);
        Assert.Equal(1, hidden);
        listener.Dispose();
        SendMessage(child.Handle, NativeWindowVisibilityListener.Message, host.Handle, host.Handle);
        Assert.Equal(1, hidden);
    }

    [Fact]
    public void DestroyedWindowReleasesListenerAndRepeatedDisposalIsHarmless()
    {
        using var host = new NativeWindow();
        using var child = new NativeWindow(host.Handle, 0x40000000);
        using var listener = new NativeWindowVisibilityListener(child.Handle, host.Handle, host.Handle, () => { });
        child.Dispose();
        listener.Dispose();
        listener.Dispose();
    }

    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr w, IntPtr l);
}
