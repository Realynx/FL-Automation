using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using FruityLink.Installer.Core.Mcp;

namespace FruityLink.Installer.Gui;

/// <summary>
/// The GUI-mode Avalonia application (hero-themed; see Gui/App.axaml). Program.cs only builds
/// this when no headless verb was given, so the CLI path never touches Avalonia.
/// </summary>
public sealed class InstallerApp : Application
{
    /// <summary>--fl-path handed through from the CLI parse, applied to the window on startup.</summary>
    public static string? InitialFlPath { get; set; }
    public static string? InitialPayloadRoot { get; set; }
    public static bool WithoutMcp { get; set; }
    public static bool WithoutPythonIde { get; set; }
    public static bool WithoutSerumSupport { get; set; }
    public static IReadOnlyList<string> InitialMcpClientIds { get; set; } = [];
    public static McpUserPaths? InitialMcpUserPaths { get; set; }
    public static string? InitialMcpPythonRuntime { get; set; }
    public static string? InitialMcpTemplate { get; set; }
    public static string? InitialMcpWorkspace { get; set; }

    /// <summary>--community-plugins ids to re-check after an elevation relaunch.</summary>
    public static IReadOnlyList<string> InitialCommunityPlugins { get; set; } = [];

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new MainWindow(InitialFlPath);
        base.OnFrameworkInitializationCompleted();
    }
}
