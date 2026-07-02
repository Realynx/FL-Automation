using System;
using Avalonia;
using Avalonia.Controls;
using FruityLink.Installer.Cli;
using FruityLink.Installer.Core;
using FruityLink.Installer.Gui;

namespace FruityLink.Installer;

/// <summary>
/// Single entry point for both modes. No args (or --gui) => Avalonia window. Any headless
/// verb/flag => CLI: attach to the parent console, run, return an exit code. WinExe means the
/// GUI never flashes a console, and the CLI path never initializes Avalonia.
/// </summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        var opts = CliOptions.Parse(args);

        if (opts.RunGui)
            return RunGui(opts);

        ConsoleHost.EnsureConsole(opts.Silent);

        using var fileLog = new FileLog();
        var log = new CompositeLog(new ConsoleLog(), fileLog);

        try
        {
            return CliRunner.Run(opts, log);
        }
        catch (Exception ex)
        {
            log.Error("Unhandled error: " + ex.Message);
            return ExitCodes.Error;
        }
    }

    private static int RunGui(CliOptions opts)
    {
        InstallerApp.InitialFlPath = opts.FlPath;
        return AppBuilder.Configure<InstallerApp>()
            .UsePlatformDetect()
            .WithInterFont()   // guaranteed body-font fallback (Avalonia.Fonts.Inter)
            .StartWithClassicDesktopLifetime(Array.Empty<string>(), ShutdownMode.OnMainWindowClose);
    }
}
