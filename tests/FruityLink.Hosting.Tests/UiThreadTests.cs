using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using FruityLink.Ui.Avalonia.Hosting;
using FruityLink.Ui.Wpf.Hosting;
using Xunit;

namespace FruityLink.Hosting.Tests;

[Collection("Windows hosting")]
public sealed class UiThreadTests
{
    [Fact]
    public async Task ConcurrentWpfStartsReuseOneDispatcherAndStopDoesNotWaitOnUiWork()
    {
        var ui = new WpfUiThread("FruityLink test dispatcher");
        try
        {
            Dispatcher[] dispatchers = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
            {
                ui.Start();
                return ui.Dispatcher;
            })));
            Assert.All(dispatchers, dispatcher => Assert.Same(dispatchers[0], dispatcher));
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            Dispatcher dispatcher = ui.Dispatcher;
            var work = dispatcher.InvokeAsync(() => { entered.Set(); release.Wait(TimeSpan.FromSeconds(10)); });
            try
            {
                Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
                await Task.Run(ui.Stop).WaitAsync(TimeSpan.FromSeconds(2));
                Assert.Throws<InvalidOperationException>(() => ui.Dispatcher);
            }
            finally { release.Set(); }
            await work.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { ui.Stop(); }
    }

    [Fact]
    public void WpfFallbackHandlesAutomaticDimensionsAndForcesAnOpaqueBackdrop()
    {
        var ui = new WpfUiThread("FruityLink fallback test");
        ui.Start();
        try
        {
            ui.Dispatcher.Invoke(() =>
            {
                var window = new Window { Background = new SolidColorBrush(Color.FromArgb(20, 30, 40, 50)) };
                try
                {
                    var view = new EmbeddedWpfView(window);
                    view.PrepareForEmbedding();
                    var source = HwndSource.FromHwnd(view.Handle);
                    Assert.Equal((byte)255, source.CompositionTarget.BackgroundColor.A);
                    view.RestoreExternalChrome();
                    Assert.True(double.IsFinite(window.Left));
                    Assert.True(double.IsFinite(window.Top));
                    Assert.True(window.Left >= 0);
                    Assert.True(window.Top >= 0);
                    Assert.True(window.ShowActivated);
                }
                finally { window.Close(); }
            });
        }
        finally { ui.Stop(); }
    }

    [Fact]
    public async Task AvaloniaFactoryReentrancyFailsInsteadOfWaitingForItsOwnStartup()
    {
        var host = new EmbeddedAvaloniaHost();
        InvalidOperationException error = await Task.Run(() => Assert.Throws<InvalidOperationException>(() =>
            host.EnsureStarted(() => { host.EnsureStarted(); throw new InvalidOperationException("Unreachable"); })))
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains("cannot wait for itself", error.Message);
    }

    [Fact]
    public async Task AvaloniaStartupFailureIsSharedByConcurrentCallersAndInvoke()
    {
        var host = new EmbeddedAvaloniaHost();
        int starts = 0;
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            var error = Assert.Throws<InvalidOperationException>(() => host.EnsureStarted(() =>
            {
                Interlocked.Increment(ref starts);
                throw new InvalidOperationException("missing native dependency");
            }));
            Assert.Contains("missing native dependency", error.Message);
        }))).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, starts);
        Assert.Throws<InvalidOperationException>(() => host.Invoke(() => { }));
    }
}
