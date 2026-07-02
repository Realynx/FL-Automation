using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using FruityLink.Installer.Cli;
using FruityLink.Installer.Core;

namespace FruityLink.Installer.Gui;

/// <summary>
/// The installer GUI (Avalonia, hero-themed). Functionally the same two pages as the previous
/// WPF window — start (path + log + install/uninstall) and finish (outcome + details) — plus an
/// in-window glass confirm overlay standing in for WPF's MessageBox (Avalonia has none).
/// </summary>
public partial class MainWindow : Window
{
    private readonly InstallManifest _manifest;
    private readonly string _payloadRoot;
    private bool _busy;

    /// <summary>Completes the pending ConfirmAsync when the overlay's OK/Cancel is clicked.</summary>
    private TaskCompletionSource<bool>? _confirmTcs;

    /// <summary>FL dir to relaunch from when "Launch FL Studio on close" is checked (successful install only).</summary>
    private string? _launchFlDir;

    public MainWindow() : this(null) { }

    public MainWindow(string? initialFlPath)
    {
        InitializeComponent();

        _manifest = InstallManifest.Default();
        _payloadRoot = InstallerInfo.ResolvePayloadRoot(null);

        var path = initialFlPath
                   ?? FlStudioLocator.DetectBest()
                   ?? FlStudioLocator.DefaultPath;
        PathBox.Text = path;

        AppendLine(LogLevel.Info, $"FL Automate installer {InstallerInfo.Version}");
        AppendLine(LogLevel.Info, $"Payload root: {_payloadRoot}");
        if (!Directory.Exists(_payloadRoot))
            AppendLine(LogLevel.Warn, "Payload folder not found next to the installer — Install will fail until the payload is present (Dry run still works).");
        AppendLine(LogLevel.Info, "Choose a folder, then Install or Uninstall. Use Dry run to preview.");
    }

    // ------------------------------------------------------------ custom chrome ----

    private void TitleBar_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginMoveDrag(e);
    }

    private void Minimize_Click(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void CloseButton_Click(object? sender, RoutedEventArgs e) => Close();

    // ----------------------------------------------------------- path field ----

    private void PathBox_TextChanged(object? sender, TextChangedEventArgs e) => UpdatePathStatus();

    private void UpdatePathStatus()
    {
        var v = FlStudioLocator.Validate(PathBox.Text ?? string.Empty);
        if (v.IsValid)
        {
            PathStatus.Text = $"✓ FL Studio detected ({FlStudioLocator.ExeName} found).";
            PathStatus.Foreground = Res("OkBrush");
        }
        else
        {
            PathStatus.Text = $"⚠ {v.Reason} You can still preview with Dry run.";
            PathStatus.Foreground = Res("DangerBrush");
        }
    }

    private async void Browse_Click(object? sender, RoutedEventArgs e)
    {
        var options = new FolderPickerOpenOptions
        {
            Title = $"Select the FL Studio folder (contains {FlStudioLocator.ExeName})",
            AllowMultiple = false,
        };
        try
        {
            if (Directory.Exists(PathBox.Text))
                options.SuggestedStartLocation = await StorageProvider.TryGetFolderFromPathAsync(PathBox.Text!);
        }
        catch { /* ignore bad initial dir */ }

        var picked = await StorageProvider.OpenFolderPickerAsync(options);
        if (picked.Count > 0)
            PathBox.Text = picked[0].Path.LocalPath;
    }

    private void Detect_Click(object? sender, RoutedEventArgs e)
    {
        var all = FlStudioLocator.Detect();
        if (all.Count == 0)
        {
            AppendLine(LogLevel.Warn, "No FL Studio installation detected. Set the folder manually.");
            return;
        }
        PathBox.Text = all[0];
        AppendLine(LogLevel.Info, $"Detected {all.Count} FL install(s); using: {all[0]}");
        for (var i = 1; i < all.Count; i++)
            AppendLine(LogLevel.Info, $"   also: {all[i]}");
    }

    // ------------------------------------------------------------ confirm overlay ----

    /// <summary>Shows the modal glass overlay and resolves true on OK / false on Cancel.</summary>
    private Task<bool> ConfirmAsync(string title, string message, string okText = "OK", string cancelText = "Cancel")
    {
        _confirmTcs?.TrySetResult(false);   // defensive: never leave an older await hanging
        _confirmTcs = new TaskCompletionSource<bool>();

        ConfirmTitle.Text = title;
        ConfirmMessage.Text = message;
        ConfirmOkBtn.Content = okText;
        ConfirmCancelBtn.Content = cancelText;
        ConfirmOverlay.IsVisible = true;
        ConfirmOkBtn.Focus();
        return _confirmTcs.Task;
    }

    /// <summary>Single-button variant of the overlay (blocked-install notices etc.).</summary>
    private async Task AlertAsync(string title, string message, string okText = "OK")
    {
        ConfirmCancelBtn.IsVisible = false;
        try { await ConfirmAsync(title, message, okText); }
        finally { ConfirmCancelBtn.IsVisible = true; }
    }

    private void ConfirmOk_Click(object? sender, RoutedEventArgs e) => ResolveConfirm(true);

    private void ConfirmCancel_Click(object? sender, RoutedEventArgs e) => ResolveConfirm(false);

    private void ResolveConfirm(bool result)
    {
        ConfirmOverlay.IsVisible = false;
        _confirmTcs?.TrySetResult(result);
    }

    // -------------------------------------------------------------- actions ----

    private async void Install_Click(object? sender, RoutedEventArgs e) => await RunOperation(install: true);

    private async void Uninstall_Click(object? sender, RoutedEventArgs e) => await RunOperation(install: false);

    private async Task RunOperation(bool install)
    {
        if (_busy) return;

        var flPath = (PathBox.Text ?? string.Empty).Trim().TrimEnd('"');
        var dryRun = DryRunCheck.IsChecked == true;
        var verbName = install ? "Install" : "Uninstall";

        var validation = FlStudioLocator.Validate(flPath);
        if (!validation.IsValid && !dryRun)
        {
            var proceed = await ConfirmAsync(
                "FL Studio not validated",
                $"{FlStudioLocator.ExeName} was not found in:\n{flPath}\n\n{validation.Reason}\n\nProceed anyway?",
                "Proceed anyway");
            if (!proceed) return;
        }

        // Verified-build + integrity gates before any install (uninstall is never gated so FL can
        // always be restored to stock). Hashing ~1 GB of binaries takes a moment — run it off the
        // UI thread behind the busy state. Dry run downgrades a failure to a log warning; the GUI
        // has no override on purpose (CLI --force is the unsupported dev escape hatch).
        if (install && validation.IsValid)
        {
            SetBusy(true);
            AppendLine(LogLevel.Info, "Checking FL Studio build + file integrity against the verified list…");
            CompatCheckResult check;
            try { check = await Task.Run(() => FlIntegrity.Check(flPath)); }
            finally { SetBusy(false); }

            if (check.Ok)
            {
                AppendLine(LogLevel.Success, $"FL Studio {check.FlVersion} is a verified build; all binaries match.");
            }
            else
            {
                foreach (var p in check.Problems) AppendLine(LogLevel.Error, "  " + p);
                AppendLine(LogLevel.Error, "Compatibility check failed: " + check.BlockReason);
                if (dryRun)
                {
                    AppendLine(LogLevel.Warn, "Continuing dry-run preview despite the failed check (nothing will be written).");
                }
                else
                {
                    var title = check.Problems.Count > 0
                        ? "FL Studio install doesn't match the official build"
                        : "This FL Studio build isn't verified yet";
                    await AlertAsync(title, check.BlockReason ?? "Compatibility check failed.", "Close");
                    return;
                }
            }
        }

        if (!dryRun)
        {
            var confirmed = await ConfirmAsync(
                $"Confirm {verbName.ToLowerInvariant()}",
                install
                    ? $"Install FL Automate into:\n{flPath}\n\nFL Studio's version.dll will be backed up first.\n\nAny running FL Studio instances will be closed first (its files are locked while it runs)."
                    : $"Uninstall FL Automate from:\n{flPath}\n\nThe original version.dll will be restored.\n\nAny running FL Studio instances will be closed first (its files are locked while it runs).",
                verbName);
            if (!confirmed) return;

            // Program Files needs admin: offer to relaunch elevated.
            var writableCheck = new RealFileSystem();
            if (!writableCheck.IsDirectoryWritable(flPath) && !Elevation.IsAdministrator())
            {
                var elevate = await ConfirmAsync(
                    "Administrator required",
                    $"'{flPath}' requires administrator rights to modify.\n\nRelaunch the installer as administrator?",
                    "Relaunch as admin");
                if (elevate)
                {
                    var args = new List<string>
                    {
                        install ? "--install" : "--uninstall",
                        "--gui", "--fl-path", flPath,
                    };
                    if (Elevation.RelaunchAsAdmin(args.ToArray()))
                    {
                        ShutdownApp();
                        return;
                    }
                    AppendLine(LogLevel.Error, "Elevation was declined; cannot modify the FL Studio folder.");
                }
                return;
            }
        }

        SetBusy(true);
        AppendLine(LogLevel.Info, "");
        AppendLine(LogLevel.Info, $"=== {verbName}{(dryRun ? " (dry run)" : "")} ===");

        var log = new UiThreadLog(this);
        OperationResult result;
        try
        {
            result = await Task.Run(() =>
            {
                var fs = new RealFileSystem();
                var engine = new InstallEngine(fs, InstallerInfo.Version, new RealProcessManager());
                if (install)
                {
                    var plan = engine.PlanInstall(flPath, _manifest, _payloadRoot, out var errors);
                    foreach (var err in errors) log.Error("payload problem: " + err);
                    if (errors.Count > 0 && !dryRun)
                    {
                        log.Error("Aborting: required payload files are missing.");
                        // Populate Errors so the finish page reports FAILED (not a false success).
                        var aborted = new OperationResult { Success = false, DryRun = dryRun };
                        foreach (var err in errors) aborted.Errors.Add(err);
                        return aborted;
                    }
                    return engine.ExecuteInstall(plan, flPath, dryRun, log);
                }
                else
                {
                    var record = engine.TryLoadRecord(flPath, _manifest);
                    var plan = engine.PlanUninstall(flPath, _manifest, record, log);
                    if (plan.Count == 0)
                    {
                        log.Warn("Nothing to uninstall (no FruityLink files found).");
                        return new OperationResult { Success = true, DryRun = dryRun };
                    }
                    return engine.ExecuteUninstall(plan, dryRun, log);
                }
            });
        }
        catch (Exception ex)
        {
            AppendLine(LogLevel.Error, "Unhandled error: " + ex.Message);
            SetBusy(false);
            return;
        }

        SetBusy(false);
        ShowFinish(result, install, dryRun, flPath);
    }

    // ------------------------------------------------------------- finish page ----

    /// <summary>Routes to the finish page with an honest headline + summary for the real outcome.</summary>
    private void ShowFinish(OperationResult result, bool install, bool dryRun, string flPath)
    {
        var verb = install ? "Install" : "Uninstall";
        var offerLaunch = false;
        string headline;
        IBrush color;

        if (dryRun)
        {
            headline = "✓ Dry run complete — no changes were made";
            color = Res("OkBrush");
        }
        else
        {
            switch (result.Outcome)
            {
                case OperationOutcome.Success:
                    if (install)
                    {
                        headline = "✓ FL Automate is installed";
                        offerLaunch = true; // ONLY after a successful real install
                    }
                    else
                    {
                        headline = result.FilesAffected == 0
                            ? "✓ Nothing to remove — FL Studio is already stock"
                            : "✓ Uninstall complete — FL Studio restored to stock";
                    }
                    // Success gets the hero headline gradient rather than a flat status colour.
                    color = Res("HeadlineGradientBrush");
                    break;

                case OperationOutcome.RebootPending:
                    headline = $"⚠ {verb} incomplete — {result.RebootPending.Count} file(s) pending reboot";
                    color = Res("WarnBrush");
                    break;

                default: // Failed
                    headline = $"✕ {verb} failed";
                    color = Res("DangerBrush");
                    break;
            }
        }

        FinishHeadline.Text = headline;
        FinishHeadline.Foreground = color;
        FinishSummary.Text = BuildSummary(result, install, dryRun, flPath);

        _launchFlDir = offerLaunch ? flPath : null;
        LaunchCheck.IsVisible = offerLaunch;
        if (offerLaunch)
            LaunchCheck.IsChecked = File.Exists(Path.Combine(flPath, FlStudioLocator.ExeName));

        FinishLogBox.Text = LogBox.Text;
        ScrollLogToEnd(FinishLogBox);
        DetailsExpander.IsExpanded = result.Outcome != OperationOutcome.Success && !dryRun;

        StartPanel.IsVisible = false;
        FinishPanel.IsVisible = true;
    }

    private static string BuildSummary(OperationResult result, bool install, bool dryRun, string flPath)
    {
        var verb = install ? "Install" : "Uninstall";
        var noun = install ? "copied" : "removed";
        var sb = new StringBuilder();

        sb.AppendLine($"Action:   {verb}{(dryRun ? "  (dry run — preview only)" : "")}");
        sb.AppendLine($"Target:   {flPath}");
        sb.Append($"Files {noun}: {result.FilesAffected}{(dryRun ? "  (would be)" : "")}");

        if (!dryRun && result.FlProcessesClosed > 0)
            sb.Append($"\nFL Studio instances closed before the operation: {result.FlProcessesClosed}");

        if (result.RebootPending.Count > 0)
        {
            sb.Append($"\n\n⚠ {result.RebootPending.Count} file(s) were locked and are scheduled to be " +
                      $"{(install ? "applied" : "removed/restored")} on the NEXT REBOOT. " +
                      "Restart Windows to finish:");
            foreach (var f in result.RebootPending)
                sb.Append($"\n   • {f}");
        }

        if (result.Leftover.Count > 0)
        {
            sb.Append($"\n\n✕ {result.Leftover.Count} file(s) could NOT be removed (still locked):");
            foreach (var f in result.Leftover)
                sb.Append($"\n   • {f}");
            sb.Append("\nClose FL Studio (and any process using these files), then run uninstall again.");
        }

        if (result.Errors.Count > 0)
        {
            sb.Append($"\n\nErrors ({result.Errors.Count}): see the details log below.");
        }

        return sb.ToString();
    }

    private void Back_Click(object? sender, RoutedEventArgs e)
    {
        _launchFlDir = null;
        LaunchCheck.IsVisible = false;
        FinishPanel.IsVisible = false;
        StartPanel.IsVisible = true;
        UpdatePathStatus();
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// Honors "Launch FL Studio on close" — fires for both the Finish button and the window's ✕,
    /// but only when the checkbox is actually visible (successful install) and checked.
    /// </summary>
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (LaunchCheck.IsVisible
            && LaunchCheck.IsChecked == true
            && !string.IsNullOrEmpty(_launchFlDir))
        {
            TryLaunchFl(_launchFlDir);
        }
    }

    private static void TryLaunchFl(string flDir)
    {
        try
        {
            var exe = Path.Combine(flDir, FlStudioLocator.ExeName);
            if (File.Exists(exe))
                Process.Start(new ProcessStartInfo
                {
                    FileName = exe,
                    WorkingDirectory = flDir,
                    UseShellExecute = true,
                });
        }
        catch { /* best-effort launch; nothing actionable if FL fails to start */ }
    }

    // ---------------------------------------------------------------- helpers ----

    private IBrush Res(string key) =>
        this.FindResource(key) as IBrush ?? Brushes.White;

    private static void ShutdownApp()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.Shutdown();
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        Progress.IsVisible = busy;
        InstallBtn.IsEnabled = !busy;
        UninstallBtn.IsEnabled = !busy;
        BrowseBtn.IsEnabled = !busy;
        DetectBtn.IsEnabled = !busy;
        PathBox.IsEnabled = !busy;
        DryRunCheck.IsEnabled = !busy;
    }

    private void AppendLine(LogLevel level, string message)
    {
        // Color is conveyed by a prefix glyph since the log is a single TextBox; keeps it simple/clean.
        var prefix = level switch
        {
            LogLevel.Error => "  [x] ",
            LogLevel.Warn => "  [!] ",
            LogLevel.Success => "  [✓] ",
            LogLevel.Action => "      ",
            _ => "  ",
        };
        LogBox.Text += prefix + message + Environment.NewLine;
        ScrollLogToEnd(LogBox);
    }

    /// <summary>Avalonia's TextBox has no ScrollToEnd; moving the caret to the tail scrolls there.</summary>
    private static void ScrollLogToEnd(TextBox box) => box.CaretIndex = box.Text?.Length ?? 0;

    /// <summary>Marshals engine log lines (raised on a worker thread) onto the UI thread.</summary>
    private sealed class UiThreadLog : IProgressLog
    {
        private readonly MainWindow _w;
        public UiThreadLog(MainWindow w) => _w = w;

        public void Log(LogLevel level, string message)
            => Dispatcher.UIThread.Post(() => _w.AppendLine(level, message));
    }
}
