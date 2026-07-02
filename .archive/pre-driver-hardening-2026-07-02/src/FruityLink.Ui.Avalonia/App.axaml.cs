using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using FruityLink.Ui.Avalonia.ViewModels;
using FruityLink.Ui.Avalonia.Views;

namespace FruityLink.Ui.Avalonia;

public partial class App : Application
{
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
