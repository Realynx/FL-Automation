using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace FruityLink.Installer.Gui;

/// <summary>
/// The GUI-mode Avalonia application (hero-themed; see Gui/App.axaml). Program.cs only builds
/// this when no headless verb was given, so the CLI path never touches Avalonia.
/// </summary>
public sealed class InstallerApp : Application
{
    /// <summary>--fl-path handed through from the CLI parse, applied to the window on startup.</summary>
    public static string? InitialFlPath { get; set; }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new MainWindow(InitialFlPath);
        base.OnFrameworkInitializationCompleted();
    }
}
