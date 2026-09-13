using System.Text.Json;
using FruityLink.FlStudio.Windowing;
using Xunit;

namespace FruityLink.Hosting.Tests;

[Collection("Windows hosting")]
public sealed class FlWindowHostTests
{
    [Fact]
    public void RepeatedEmbedRestoresOriginalStylesAndOwnerWithoutAnotherBridgeCall()
    {
        using var owner = new NativeWindow();
        using var child = new NativeWindow(owner.Handle, style: 0x80000000);
        using var content = new NativeWindow();
        child.ExStyle = IntPtr.Zero;
        IntPtr style = child.Style;
        var calls = new List<string>();
        var host = CreateHost(content.Handle, calls);
        try
        {
            Assert.True(host.TryEmbed(child.Handle, false), host.LastEmbedReply);
            Assert.False(content.Visible);
            Assert.True(host.TryEmbed(child.Handle, true));
            Assert.Equal(content.Handle, child.Parent);
            Assert.True(content.Visible);
            Assert.Single(calls);
            // Simulate toolkit extended-style changes while embedded. Zero is a saved value too.
            child.ExStyle = (IntPtr)0x80;
        }
        finally { host.Close(); }
        Assert.Equal(style, child.Style);
        Assert.Equal(IntPtr.Zero, child.ExStyle);
        Assert.Equal(owner.Handle, child.Parent);
        Assert.Equal("winhost_close", calls[^1]);
        Assert.False(content.Visible);
    }

    [Fact]
    public void AnotherWindowCannotReplaceTheLiveNativeSlot()
    {
        using var content = new NativeWindow();
        using var first = new NativeWindow();
        using var second = new NativeWindow();
        var calls = new List<string>();
        var host = CreateHost(content.Handle, calls);
        try
        {
            Assert.True(host.TryEmbed(first.Handle, false), host.LastEmbedReply);
            Assert.False(host.TryEmbed(second.Handle, false));
            Assert.Equal(content.Handle, first.Parent);
            Assert.Equal(IntPtr.Zero, second.Parent);
            Assert.Single(calls);
        }
        finally { host.Close(); }
    }

    [Fact]
    public void WrongThreadCannotEmbedOrDetachWindow()
    {
        using var content = new NativeWindow();
        using var child = new NativeWindow();
        var calls = new List<string>();
        var host = CreateHost(content.Handle, calls);
        bool? result = null;
        var thread = new Thread(() => result = host.TryEmbed(child.Handle, false));
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(5)));
        Assert.False(result);
        Assert.Empty(calls);
        try
        {
            Assert.True(host.TryEmbed(child.Handle, false), host.LastEmbedReply);
            thread = new Thread(host.Close);
            thread.Start();
            Assert.True(thread.Join(TimeSpan.FromSeconds(5)));
            Assert.Equal(content.Handle, child.Parent);
            Assert.Single(calls);
        }
        finally { host.Close(); }
    }

    [Fact]
    public void FailedReplyLeavesExternalWindowUnchanged()
    {
        using var child = new NativeWindow();
        IntPtr style = child.Style;
        var host = new FlWindowHost(_ => "{\"ok\":0,\"error\":\"unsupported-build\"}");
        Assert.False(host.TryEmbed(child.Handle, true));
        Assert.Equal(IntPtr.Zero, child.Parent);
        Assert.Equal(style, child.Style);
        Assert.Equal(0, host.LastInsetY);
    }

    [Fact]
    public void FailedReparentRestoresTopLevelStylesAndReleasesTheNativeSlot()
    {
        using var child = new NativeWindow(style: 0x80000000);
        IntPtr style = child.Style;
        IntPtr extendedStyle = child.ExStyle;
        var calls = new List<string>();
        // Win32 rejects using the child itself as its parent. Exercise rollback after styles
        // have already changed, without requiring a live FL bridge or DPI-dependent failures.
        var host = CreateHost(child.Handle, calls);
        try
        {
            Assert.False(host.TryEmbed(child.Handle, false));
            Assert.Equal(IntPtr.Zero, child.Parent);
            Assert.Equal(style, child.Style);
            Assert.Equal(extendedStyle, child.ExStyle);
            Assert.Equal("winhost_close", calls[^1]);
            Assert.Contains("setparent-failed", host.LastEmbedReply);
        }
        finally { host.Close(); }
    }

    [Fact]
    public void VisibilityWithoutEmbedNeverCallsTheBridge()
    {
        var host = new FlWindowHost(_ => throw new InvalidOperationException("Unexpected bridge call"));
        host.SetVisible(true);
        host.Close();
        Assert.False(host.IsHostVisible());
    }

    private static FlWindowHost CreateHost(IntPtr content, List<string> calls)
        => new(command =>
        {
            calls.Add(command);
            return JsonSerializer.Serialize(new
            {
                ok = 1, host = $"0x{content.ToInt64():x}", content = $"0x{content.ToInt64():x}",
                cx = 2, cy = 24, cw = 600, ch = 400,
            });
        });
}
