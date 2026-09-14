using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using FruityLink.Ui.Avalonia.Theme;
using FruityLink.Ui.Avalonia.ViewModels;
using FruityLink.Ui.Avalonia.Views;

namespace FruityLink.Ui.Avalonia;

public partial class App : Application
{
    /// <summary>
    /// The product's <see cref="AppBuilder"/> factory for the EMBEDDED in-FL host: handed to the SDK's
    /// <c>EmbeddedAvaloniaHost.EnsureStarted</c> on first start. Configures a neutral application + platform +
    /// the guaranteed Inter fallback font; the SDK host appends the embed-safe software-rendering /
    /// redirection-surface options and calls <c>SetupWithoutStarting</c> on its own STA thread.
    /// (The standalone dev head has its own factory in FruityLink.Ui.Avalonia.Desktop's Program.)
    /// </summary>
    public static AppBuilder BuildEmbeddedAppBuilder() =>
        AppBuilder.Configure<Application>()
            .UsePlatformDetect()
            .WithInterFont();   // guaranteed default body font even if bundled assets are absent

    /// <summary>Keep product resources on its window when another plugin owns shared startup.</summary>
    internal static void ApplyEmbeddedWindowTheme(Window window)
    {
        if (Current is App) return; // The standalone head already supplies these application resources.
        window.RequestedThemeVariant = ThemeVariant.Dark;
        window.Resources.MergedDictionaries.Add(new ProductTokens());
        window.Styles.Add(new FluentTheme());
        window.Styles.Add(new ProductStyles());
    }

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
