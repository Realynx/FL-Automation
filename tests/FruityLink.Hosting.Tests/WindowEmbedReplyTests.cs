using FruityLink.FlStudio.Windowing;
using Xunit;

namespace FruityLink.Hosting.Tests;

public sealed class WindowEmbedReplyTests
{
    [Fact]
    public void ParsesWhitespaceFieldOrderAndContentInsets()
    {
        const string json = """
            { "host": "0X5678", "content" : "0x1234", "cy" : 24, "cx": -2, "cw":640, "ch":480, "ok" : 1 }
            """;
        Assert.True(WindowEmbedReply.TryParse(json, out var reply));
        Assert.Equal(new WindowEmbedReply((IntPtr)0x1234, (IntPtr)0x5678, -2, 24, 640, 480), reply);
    }

    [Theory]
    [InlineData("err:unsupported")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{\"diag\":\"\\\"ok\\\":1\",\"ok\":0}")]
    [InlineData("{\"ok\":\"1\"}")]
    [InlineData("{\"ok\":10}")]
    public void RejectsMalformedRepliesAndNonSuccessValues(string json)
        => Assert.False(WindowEmbedReply.TryParse(json, out _));

    [Fact]
    public void InvalidHandleAndOversizedDimensionsUseFallbackValues()
    {
        Assert.True(WindowEmbedReply.TryParse("""{"ok":1,"content":"0x12zz","host":8,"cw":2147483648} """, out var reply));
        Assert.Equal(IntPtr.Zero, reply.Content);
        Assert.Equal(IntPtr.Zero, reply.Host);
        Assert.Equal(0, reply.Width);
    }
}
