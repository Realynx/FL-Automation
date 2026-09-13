using System.Runtime.InteropServices;
using FruityLink.FlStudio.Windowing;
using FruityLink.Ui.Avalonia.Hosting;
using Xunit;

namespace FruityLink.Hosting.Tests;

[Collection("Windows hosting")]
public sealed class ChildContentPinTests
{
    [Fact]
    public void ToolkitMoveCannotEscapeParentAndDestroyReleasesTheSubclass()
    {
        using var parent = new NativeWindow();
        using var child = new NativeWindow();
        var native = new NativeChildWindow(child.Handle);
        Assert.True(native.Attach(new WindowEmbedReply(parent.Handle, parent.Handle, 0, 0, 500, 300), out _));
        using var pin = new ChildContentPin(child.Handle, 0, 0, () => { });
        Assert.True(SetWindowPos(child.Handle, IntPtr.Zero, -32000, -32000, 3, 4, 0x0010 | 0x0004));
        Assert.True(GetClientRect(parent.Handle, out Rect parentBounds));
        Assert.True(GetClientRect(child.Handle, out Rect childBounds));
        Assert.Equal(parentBounds.Right, childBounds.Right);
        Assert.Equal(parentBounds.Bottom, childBounds.Bottom);
        child.Dispose();
        Assert.False(pin.IsInstalled);
        pin.Dispose();
        Assert.True(native.Detach(out _));
        Assert.False(NativeChildWindow.CurrentThreadHasEmbeddedWindow);
    }

    [Fact]
    public void AlreadyAlignedChildKeepsNoSizeAndNoMoveFlagsDuringRepeatedParentLayout()
    {
        using var parent = new NativeWindow();
        using var child = new NativeWindow();
        var native = new NativeChildWindow(child.Handle);
        Assert.True(GetClientRect(parent.Handle, out Rect bounds));
        Assert.True(native.Attach(new WindowEmbedReply(parent.Handle, parent.Handle, 0, 0, bounds.Right, bounds.Bottom), out _));
        using var pin = new ChildContentPin(child.Handle, 0, 0, () => { });
        try
        {
            for (int index = 0; index < 100; index++)
            {
                WindowPosition result = ChangePosition(child.Handle, new WindowPosition
                { Window = child.Handle, X = -32000, Y = -32000, Width = 3, Height = 4, Flags = 0x0014 });
                Assert.Equal(0x0003u, result.Flags & 0x0003u);
                Assert.Equal(0, result.X);
                Assert.Equal(0, result.Y);
                Assert.Equal(bounds.Right, result.Width);
                Assert.Equal(bounds.Bottom, result.Height);
            }
        }
        finally { pin.Dispose(); Assert.True(native.Detach(out _)); }
    }

    [Fact]
    public void NativeLayoutMessagesDeferAndCoalescePaintingWithoutAPaintFeedbackLoop()
    {
        using var child = new NativeWindow();
        var posted = new Queue<Action>();
        int paints = 0;
        using var repaint = new DeferredRepaint(posted.Enqueue, () => paints++);
        using var pin = new ChildContentPin(child.Handle, 0, 0, repaint.Request);
        for (int index = 0; index < 100; index++) SendMessage(child.Handle, 0x0005, IntPtr.Zero, IntPtr.Zero);
        Assert.Equal(0, paints);
        Assert.Single(posted);
        posted.Dequeue()();
        Assert.Equal(1, paints);
        SendMessage(child.Handle, 0x000F, IntPtr.Zero, IntPtr.Zero);
        Assert.Empty(posted);
    }

    private static WindowPosition ChangePosition(IntPtr child, WindowPosition value)
    {
        IntPtr memory = Marshal.AllocHGlobal(Marshal.SizeOf<WindowPosition>());
        try
        {
            Marshal.StructureToPtr(value, memory, false);
            SendMessage(child, 0x0046, IntPtr.Zero, memory);
            return Marshal.PtrToStructure<WindowPosition>(memory);
        }
        finally { Marshal.FreeHGlobal(memory); }
    }

    [StructLayout(LayoutKind.Sequential)] private struct Rect { internal int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct WindowPosition { internal IntPtr Window, InsertAfter; internal int X, Y, Width, Height; internal uint Flags; }
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr window, out Rect rectangle);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", EntryPoint = "SendMessageW")] private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
}
