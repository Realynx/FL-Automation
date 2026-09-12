using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using FruityLink.Ui.Avalonia.ViewModels;
using FruityLink.Ui.Avalonia.Views;

namespace FruityLink.Ui.Avalonia;

public partial class App : Application
{
    /// <summary>
    /// The product's <see cref="AppBuilder"/> factory for the EMBEDDED in-FL host: handed to the SDK's
    /// <c>EmbeddedAvaloniaHost.EnsureStarted</c> on first start. Configures the product App + platform +
    /// the guaranteed Inter fallback font; the SDK host appends the embed-safe software-rendering /
    /// redirection-surface options and calls <c>SetupWithoutStarting</c> on its own STA thread.
    /// (The standalone dev head has its own factory in FruityLink.Ui.Avalonia.Desktop's Program.)
    /// </summary>
    public static AppBuilder BuildEmbeddedAppBuilder() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont();   // guaranteed default body font even if bundled assets are absent

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Seam for later wiring: the ChatViewModel currently self-seeds sample content.
            // When the agent is connected, hand it in here (or via a DI-provided factory).
            desktop.MainWindow = new ChatWindow
            {
                DataContext = new ChatViewModel(),
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
