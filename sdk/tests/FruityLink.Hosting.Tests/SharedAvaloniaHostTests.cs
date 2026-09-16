using Avalonia;
using Avalonia.Controls;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using FruityLink.Ui.Avalonia.Hosting;
using Xunit;

namespace FruityLink.Hosting.Tests;

[Collection("Windows hosting")]
public sealed class SharedAvaloniaHostTests
{
    [Fact]
    public async Task IndependentWindowThemesAndReenableKeepOneDispatcherAlive()
    {
        EmbeddedAvaloniaHost host = EmbeddedAvaloniaHost.Instance;
        await Task.Run(() => host.EnsureStarted(() => AppBuilder.Configure<Application>()
            .UsePlatformDetect().WithInterFont())).WaitAsync(TimeSpan.FromSeconds(15));
        int originalThread = host.Invoke(() => Environment.CurrentManagedThreadId);
        host.EnsureStarted(() => throw new InvalidOperationException("A second plugin must not restart Avalonia"));
        foreach (bool reverse in new[] { false, true })
        {
            host.Invoke(() =>
            {
                var first = CreateWindow(reverse ? "editor" : "agent");
                var second = CreateWindow(reverse ? "agent" : "editor");
                try
                {
                    Assert.NotSame(first.Resources, second.Resources);
                    Assert.NotEqual(first.Resources["plugin-theme"], second.Resources["plugin-theme"]);
                    first.Close();
                    Assert.Equal(originalThread, Environment.CurrentManagedThreadId);
                    var enabledAgain = CreateWindow("enabled again");
                    enabledAgain.Close();
                    Assert.NotNull(Application.Current);
                }
                finally { first.Close(); second.Close(); }
            });
        }
        Assert.Equal(originalThread, host.Invoke(() => Environment.CurrentManagedThreadId));
        Assert.IsType<Application>(host.Invoke(() => Application.Current));
    }

    private static Window CreateWindow(string theme)
    {
        var window = new Window { RequestedThemeVariant = ThemeVariant.Dark };
        window.Resources["plugin-theme"] = theme;
        window.Styles.Add(new FluentTheme());
        return window;
    }
}
