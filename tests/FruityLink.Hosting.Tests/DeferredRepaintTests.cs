using FruityLink.Ui.Avalonia.Hosting;
using Xunit;

namespace FruityLink.Hosting.Tests;

public sealed class DeferredRepaintTests
{
    [Fact]
    public void LayoutBurstProducesOneRepaintAfterTheNativeCallbackReturns()
    {
        var posted = new Queue<Action>();
        int paints = 0;
        using var repaint = new DeferredRepaint(posted.Enqueue, () => paints++);
        for (int index = 0; index < 100; index++) repaint.Request();
        Assert.Equal(0, paints);
        Assert.Single(posted);
        posted.Dequeue()();
        Assert.Equal(1, paints);
        repaint.Request();
        Assert.Single(posted);
        posted.Dequeue()();
        Assert.Equal(2, paints);
    }

    [Fact]
    public void ClosingTheViewCancelsQueuedAndFutureRepaints()
    {
        var posted = new Queue<Action>();
        int paints = 0;
        var repaint = new DeferredRepaint(posted.Enqueue, () => paints++);
        repaint.Request();
        repaint.Dispose();
        posted.Dequeue()();
        repaint.Request();
        Assert.Equal(0, paints);
        Assert.Empty(posted);
    }

    [Fact]
    public void FailedDispatcherPostDoesNotSuppressAllLaterRepaints()
    {
        var posted = new Queue<Action>();
        bool fail = true;
        int paints = 0;
        using var repaint = new DeferredRepaint(action =>
        {
            if (fail) throw new InvalidOperationException("dispatcher unavailable");
            posted.Enqueue(action);
        }, () => paints++);
        Assert.Throws<InvalidOperationException>(repaint.Request);
        fail = false;
        repaint.Request();
        posted.Dequeue()();
        Assert.Equal(1, paints);
    }
}
