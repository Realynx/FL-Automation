using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
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
using FruityLink.Installer.Core.Mcp;

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
    private readonly bool _isPackaged;
    private readonly List<(CheckBox Box, CommunityPlugin Plugin)> _communityRows = new();
    private CheckBox? _mcpSelection;
    private CheckBox? _pythonIdeSelection;
    private CheckBox? _serumSupportSelection;
    private bool _busy;

    /// <summary>What we call the thing being installed: the sold product vs the open-source system.</summary>
    private string ProductName => _isPackaged ? "FL Automate" : "FruityLink";

    /// <summary>Completes the pending ConfirmAsync when the overlay's OK/Cancel is clicked.</summary>
    private TaskCompletionSource<bool>? _confirmTcs;

    /// <summary>FL dir to relaunch from when "Launch FL Studio on close" is checked (successful install only).</summary>
    private string? _launchFlDir;

    public MainWindow() : this(null) { }

    public MainWindow(string? initialFlPath)
    {
        InitializeComponent();

        _manifest = InstallManifest.Default();
        _payloadRoot = InstallerInfo.ResolvePayloadRoot(InstallerApp.InitialPayloadRoot);
        _isPackaged = InstallerInfo.IsPackagedEdition(_payloadRoot);

        var path = initialFlPath
                   ?? FlStudioLocator.DetectBest()
                   ?? FlStudioLocator.DefaultPath;
        PathBox.Text = path;

        ApplyEdition();
        AddBundledPlugins();
        InitializeMcpClients();

        AppendLine(LogLevel.Info, $"FL Automate installer {InstallerInfo.Version} ({(_isPackaged ? "packaged" : "community")} edition)");
        AppendLine(LogLevel.Info, $"Payload root: {_payloadRoot}");
        if (!Directory.Exists(_payloadRoot))
            AppendLine(LogLevel.Warn, "Payload folder not found next to the installer — Install will fail until the payload is present (Dry run still works).");
        AppendLine(LogLevel.Info, "Choose a folder, then Install or Uninstall. Use Dry run to preview.");

        _ = LoadCommunityPluginsAsync(InstallerApp.InitialCommunityPlugins);
    }

    // ------------------------------------------------------------ editions & plugins ----

    /// <summary>
    /// Packaged edition (sold on fl-automate.com): the FL Automate plugin ships in the payload and
    /// is always installed — shown as a permanently checked, read-only row. Community edition
    /// (open-source GitHub artifact): omits the sold plugin. Both editions offer bundled FLMCP.
    /// </summary>
    private void ApplyEdition()
    {
        if (_isPackaged)
        {
            var soldRow = new CheckBox
            {
                IsChecked = true,
                IsEnabled = false,
                Content = "FL Automate — AI assistant (included with your purchase)",
            };
            PluginList.Children.Insert(0, soldRow);
        }
        else
        {
            HeadlineProduct.Text = "FruityLink";
            SubtitleText.Text =
                "FruityLink is the open-source C# plugin system for FL Studio. Installs the FruityLink " +
                "bridge and plugin host into the FL Studio folder — the original version.dll is backed " +
                "up, and uninstalling restores it.";
        }
    }

    /// <summary>
    /// Fetches the community plugin catalog from the open-source repo and renders one optional
    /// checkbox per plugin. Fully best-effort: offline or an empty catalog never blocks the
    /// base install.
    /// </summary>
    private async Task LoadCommunityPluginsAsync(IReadOnlyList<string> precheckIds)
    {
        try
        {
            var plugins = (await CommunityPluginCatalog.FetchAsync())
                .Where(plugin => _mcpSelection is null || !plugin.Id.Equals(BundledMcp.Id, StringComparison.OrdinalIgnoreCase))
                .Where(plugin => _pythonIdeSelection is null || !plugin.Id.Equals(BundledPythonIde.Id, StringComparison.OrdinalIgnoreCase))
                .Where(plugin => _serumSupportSelection is null || !plugin.Id.Equals(BundledSerumSupport.Id, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (plugins.Count == 0)
            {
                CommunityStatus.Text = "No additional community plugins are available.";
                return;
            }

            CommunityStatus.Text = "Additional community plugins (download required):";
            foreach (var plugin in plugins)
            {
                var box = new CheckBox
                {
                    IsChecked = precheckIds.Contains(plugin.Id, StringComparer.OrdinalIgnoreCase),
                    Content = string.IsNullOrWhiteSpace(plugin.Description)
                        ? plugin.Name
                        : $"{plugin.Name} — {plugin.Description}",
                };
                _communityRows.Add((box, plugin));
                PluginList.Children.Add(box);
            }
        }
        catch (Exception ex)
        {
            CommunityStatus.Text = "Additional plugin catalog unavailable. Included plugins can still be installed offline.";
            AppendLine(LogLevel.Warn, "Community plugin catalog unavailable: " + ex.Message);
        }
    }

    private List<CommunityPlugin> SelectedCommunityPlugins() =>
        _communityRows.Where(r => r.Box.IsChecked == true).Select(r => r.Plugin).ToList();

    private void AddBundledPlugins()
    {
        int index = _isPackaged ? 1 : 0;
        if (BundledMcp.IsAvailable(_payloadRoot))
        {
            _mcpSelection = new CheckBox
            {
                Content = BundledMcp.SelectionLabel,
                IsChecked = !InstallerApp.WithoutMcp,
            };
            PluginList.Children.Insert(index++, _mcpSelection);
        }
        if (BundledPythonIde.IsAvailable(_payloadRoot))
        {
            _pythonIdeSelection = new CheckBox
            {
                Content = BundledPythonIde.SelectionLabel,
                IsChecked = !InstallerApp.WithoutPythonIde,
            };
            PluginList.Children.Insert(index++, _pythonIdeSelection);
        }
        if (BundledSerumSupport.IsAvailable(_payloadRoot))
        {
            _serumSupportSelection = new CheckBox
            {
                Content = BundledSerumSupport.SelectionLabel,
                IsChecked = !InstallerApp.WithoutSerumSupport,
            };
            PluginList.Children.Insert(index, _serumSupportSelection);
        }
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
        // always be restored to stock).
        if (install && validation.IsValid && !await PassesCompatGateAsync(flPath, dryRun))
            return;

        if (!dryRun && !await ConfirmAndCheckElevationAsync(flPath, install))
            return;

        // Selected community plugins download + stage BEFORE the engine runs, so the install
        // plan already contains them (and the install record covers them for uninstall).
        var manifest = BundledMcp.Select(_manifest, _payloadRoot, include: _mcpSelection?.IsChecked == true);
        manifest = BundledPythonIde.Select(manifest, _payloadRoot, include: _pythonIdeSelection?.IsChecked == true);
        manifest = BundledSerumSupport.Select(manifest, _payloadRoot, include: _serumSupportSelection?.IsChecked == true);
        string? stagingRoot = null;
        if (install)
        {
            var selected = SelectedCommunityPlugins();
            if (selected.Count > 0)
            {
                if (dryRun)
                {
                    foreach (var plugin in selected)
                        AppendLine(LogLevel.Info, $"[dry run] would download and install community plugin: {plugin.Name}");
                }
                else
                {
                    var staged = await StageCommunityPluginsAsync(selected, manifest);
                    if (staged is null)
                        return; // a download failed; the user was told — let them retry/uncheck
                    stagingRoot = staged.Value.Root;
                    manifest = staged.Value.Manifest;
                }
            }
        }

        SetBusy(true);
        AppendLine(LogLevel.Info, "");
        AppendLine(LogLevel.Info, $"=== {verbName}{(dryRun ? " (dry run)" : "")} ===");

        var log = new UiThreadLog(this);
        var mcpOptions = SelectedMcpOptions();
        McpOperationOutcome outcome;
        try
        {
            outcome = await Task.Run(() => ExecuteWithMcp(install, dryRun, flPath, manifest, mcpOptions, log));
        }
        catch (Exception ex)
        {
            AppendLine(LogLevel.Error, "Unhandled error: " + ex.Message);
            SetBusy(false);
            return;
        }
        finally
        {
            CleanUpStaging(stagingRoot);
        }

        SetBusy(false);
        AppendMcpOutcome(outcome.Clients);
        ShowFinish(outcome, install, dryRun, flPath);
    }

    /// <summary>
    /// Downloads the selected community plugins into a temp staging dir and returns the base
    /// manifest extended with one directory item per plugin. Null when a download fails (the
    /// user gets an alert and stays on the start page — nothing has touched FL Studio yet).
    /// </summary>
    private async Task<(string Root, InstallManifest Manifest)?> StageCommunityPluginsAsync(
        List<CommunityPlugin> selected, InstallManifest manifest)
    {
        var root = Path.Combine(Path.GetTempPath(), "FLAutomateInstaller",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        SetBusy(true);
        try
        {
            foreach (var plugin in selected)
            {
                AppendLine(LogLevel.Info, $"Downloading community plugin: {plugin.Name}…");
                var stagedDir = await CommunityPluginStager(plugin, root);
                manifest.Items.Add(CommunityPluginCatalog.ToPayloadItem(plugin, stagedDir));
                AppendLine(LogLevel.Success, $"Ready to install: {plugin.Name}");
            }
            return (root, manifest);
        }
        catch (Exception ex)
        {
            AppendLine(LogLevel.Error, "Community plugin download failed: " + ex.Message);
            CleanUpStaging(root);
            await AlertAsync(
                "Community plugin download failed",
                $"{ex.Message}\n\nCheck your connection, or uncheck the plugin and install without it. Nothing was changed.",
                "Close");
            return null;
        }
        finally
        {
            SetBusy(false);
        }
    }

    private static Task<string> CommunityPluginStager(CommunityPlugin plugin, string root) =>
        CommunityPluginCatalog.StageAsync(plugin, root);

    private static void CleanUpStaging(string? stagingRoot)
    {
        if (stagingRoot is null) return;
        try { Directory.Delete(stagingRoot, recursive: true); }
        catch { /* best-effort temp cleanup */ }
    }

    /// <summary>
    /// Verified-build + integrity gate. Hashing ~1 GB of binaries takes a moment — run it off the
    /// UI thread behind the busy state. Dry run downgrades a failure to a log warning; the GUI has
    /// no override on purpose (CLI --force is the unsupported dev escape hatch). True = proceed.
    /// </summary>
    private async Task<bool> PassesCompatGateAsync(string flPath, bool dryRun)
    {
        SetBusy(true);
        AppendLine(LogLevel.Info, "Checking FL Studio build + file integrity against the verified list…");
        var log = new UiThreadLog(this);
        CompatCheckResult check;
        try { check = await Task.Run(() => FlIntegrityGate.CheckAndLog(flPath, log)); }
        finally { SetBusy(false); }

        if (check.Ok)
            return true;

        AppendLine(LogLevel.Error, "Compatibility check failed: " + check.BlockReason);
        if (dryRun)
        {
            AppendLine(LogLevel.Warn, "Continuing dry-run preview despite the failed check (nothing will be written).");
            return true;
        }

        var title = check.Problems.Count > 0
            ? "FL Studio install doesn't match the official build"
            : "This FL Studio build isn't verified yet";
        await AlertAsync(title, check.BlockReason ?? "Compatibility check failed.", "Close");
        return false;
    }

    /// <summary>
    /// The operation confirm dialog plus Program Files elevation handling (offer to relaunch as
    /// admin). True = proceed with the operation in this process.
    /// </summary>
    private async Task<bool> ConfirmAndCheckElevationAsync(string flPath, bool install)
    {
        var verbName = install ? "Install" : "Uninstall";
        var confirmed = await ConfirmAsync(
            $"Confirm {verbName.ToLowerInvariant()}",
            install
                ? $"Install {ProductName} into:\n{flPath}\n\nFL Studio's version.dll will be backed up first.\n\nAny running FL Studio instances will be closed first (its files are locked while it runs)."
                : $"Uninstall {ProductName} from:\n{flPath}\n\nThe original version.dll will be restored.\n\nAny running FL Studio instances will be closed first (its files are locked while it runs).",
            verbName);
        if (!confirmed) return false;

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
                    "--payload-root", _payloadRoot,
                };
                if (_mcpSelection?.IsChecked != true) args.Add("--without-mcp");
                if (_pythonIdeSelection?.IsChecked != true) args.Add("--without-python-ide");
                if (_serumSupportSelection?.IsChecked != true) args.Add("--without-serum-support");
                args.AddRange(McpElevationArguments.Create(SelectedMcpOptions()));
                var selectedIds = SelectedCommunityPlugins().Select(p => p.Id).ToList();
                if (selectedIds.Count > 0)
                {
                    args.Add("--community-plugins");
                    args.Add(string.Join(',', selectedIds));
                }
                if (Elevation.RelaunchAsAdmin(args.ToArray()))
                {
                    ShutdownApp();
                    return false;
                }
                AppendLine(LogLevel.Error, "Elevation was declined; cannot modify the FL Studio folder.");
            }
            return false;
        }

        return true;
    }

    // ------------------------------------------------------------- finish page ----

    /// <summary>Routes to the finish page with an honest headline + summary for the real outcome.</summary>
    private void ShowFinish(McpOperationOutcome outcome, bool install, bool dryRun, string flPath)
    {
        var result = outcome.Files;
        var finish = InstallerFinishState.Create(outcome, install, dryRun, ProductName,
            File.Exists(Path.Combine(flPath, FlStudioLocator.ExeName)));

        FinishHeadline.Text = finish.Headline;
        FinishHeadline.Foreground = Res(finish.ColorResource);
        FinishSummary.Text = BuildSummary(result, install, dryRun, flPath)
            + McpFinishSummary(outcome.Clients, install, dryRun, outcome.FilesSkipped);

        _launchFlDir = finish.OfferLaunch ? flPath : null;
        LaunchCheck.IsVisible = finish.OfferLaunch;
        LaunchCheck.IsChecked = finish.LaunchInitiallyChecked;

        FinishLogBox.Text = LogBox.Text;
        ScrollLogToEnd(FinishLogBox);
        DetailsExpander.IsExpanded = (result.Outcome != OperationOutcome.Success && !dryRun)
            || outcome.Clients is { Success: false };

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
        if (_mcpSelection is not null) _mcpSelection.IsEnabled = !busy;
        if (_pythonIdeSelection is not null) _pythonIdeSelection.IsEnabled = !busy;
        if (_serumSupportSelection is not null) _serumSupportSelection.IsEnabled = !busy;
        UpdateMcpAvailability();
        foreach (var (box, _) in _communityRows)
            box.IsEnabled = !busy;
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
