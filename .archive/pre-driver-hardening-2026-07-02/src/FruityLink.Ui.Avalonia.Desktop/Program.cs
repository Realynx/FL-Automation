using Avalonia;
using FruityLink.Ui.Avalonia;

namespace FruityLink.Ui.Avalonia.Desktop;

/// <summary>
/// Standalone desktop entry point (dev / preview only). Launches the shared
/// <see cref="App"/> from the FruityLink.Ui.Avalonia library with a classic desktop
/// lifetime. The FL Agent plugin does NOT use this Main — it hosts the same library
/// in-process on its own Avalonia thread (see the library's Hosting/EmbeddedAvaloniaHost).
/// </summary>
internal static class Program
{
    // Avalonia configuration; also referenced by the visual designer.
    // Keep this a simple static method (do not use fields / async) — the designer invokes it.
    [STAThread]
    public static void Main(string[] args) =>
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont() // guaranteed default body font even if bundled assets are absent
            .LogToTrace();
}
