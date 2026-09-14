using Avalonia.Controls;
using Avalonia.Media;
using FruityLink.Installer.Core;
using FruityLink.Installer.Core.Mcp;

namespace FruityLink.Installer.Gui;

public partial class MainWindow
{
    private readonly List<(CheckBox Box, McpClientTarget Target)> _mcpClientRows = [];
    private readonly McpUserPaths _mcpUserPaths = InstallerApp.InitialMcpUserPaths ?? McpUserPaths.Capture();

    private void InitializeMcpClients()
    {
        McpTemplateBox.Text = InstallerApp.InitialMcpTemplate;
        McpWorkspaceBox.Text = InstallerApp.InitialMcpWorkspace;
        McpSectionContainer.IsVisible = _mcpSelection is not null;
        if (_mcpSelection is null) return;
        _mcpSelection.IsCheckedChanged += (_, _) => UpdateMcpAvailability();
        try
        {
            foreach (var target in McpClientSetup.Discover(_mcpUserPaths))
            {
                var label = target.Detected ? "detected" : "not detected; can still configure";
                var box = new CheckBox
                {
                    IsChecked = InstallerApp.InitialMcpClientIds.Contains(target.Id, StringComparer.OrdinalIgnoreCase),
                    Content = new TextBlock { Text = $"{target.Name} — {label}", TextWrapping = TextWrapping.Wrap },
                };
                ToolTip.SetTip(box, target.ConfigPath);
                _mcpClientRows.Add((box, target));
                McpClientList.Children.Add(box);
            }
        }
        catch (Exception ex)
        {
            AppendLine(LogLevel.Error, "MCP client detection failed: " + ex.Message);
        }
        AddUnrecognizedMcpSelections();
        UpdateMcpAvailability();
    }

    private void AddUnrecognizedMcpSelections()
    {
        var known = _mcpClientRows.Select(row => row.Target.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var id in InstallerApp.InitialMcpClientIds.Where(id => !known.Contains(id)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var box = new CheckBox
            {
                IsChecked = true,
                Content = new TextBlock
                {
                    Text = $"{id} — target unavailable; uncheck to skip this connection",
                    TextWrapping = TextWrapping.Wrap,
                },
            };
            _mcpClientRows.Add((box, new McpClientTarget(id, id, "", Detected: false)));
            McpClientList.Children.Add(box);
        }
    }

    private void UpdateMcpAvailability()
    {
        var included = _mcpSelection?.IsChecked == true;
        McpSection.IsEnabled = included && !_busy;
        McpSection.Opacity = included ? 1 : 0.55;
        McpClientStatus.Text = !included
            ? "Select the bundled FLMCP plugin above to connect AI apps."
            : "Only checked apps are updated. Uninstall removes their matching FLMCP connection before removing files.";
    }

    private McpSetupOptions SelectedMcpOptions()
    {
        var ids = _mcpSelection?.IsChecked == true
            ? _mcpClientRows.Where(row => row.Box.IsChecked == true).Select(row => row.Target.Id).ToArray()
            : [];
        return new(ids, _mcpUserPaths, InstallerApp.InitialMcpPythonRuntime,
            OptionalPath(McpTemplateBox.Text), OptionalPath(McpWorkspaceBox.Text));
    }

    private static string? OptionalPath(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private McpOperationOutcome ExecuteWithMcp(bool install, bool dryRun, string flPath,
        InstallManifest manifest, McpSetupOptions options, IProgressLog log)
    {
        Func<McpSetupResult>? clients = options.ClientIds.Count == 0 ? null : () => install
            ? McpClientSetup.Configure(flPath, options, dryRun, log)
            : McpClientSetup.Remove(flPath, options, dryRun, log);
        return McpOperationCoordinator.Run(install, dryRun, () => install
            ? InstallerOperations.RunInstall(flPath, manifest, _payloadRoot, dryRun, log,
                "Aborting: required payload files are missing.", missingPayloadDryRunWarning: null, out _)!
            : InstallerOperations.RunUninstall(flPath, manifest, dryRun, log, out _)!, clients);
    }

    private void AppendMcpOutcome(McpSetupResult? clients)
    {
        if (clients is null) return;
        foreach (var detail in clients.Details) AppendLine(LogLevel.Info, detail);
        foreach (var error in clients.Errors) AppendLine(LogLevel.Error, "MCP client setup: " + error);
    }

    private static string McpFinishSummary(McpSetupResult? clients, bool install, bool dryRun, bool filesSkipped)
    {
        if (clients is null) return "";
        if (dryRun)
            return clients.Success
                ? install ? "\n\nPreview: enable FLMCP and connect selected AI apps. No client settings were changed."
                    : "\n\nPreview: remove selected AI app connections for this FL Studio installation. No client settings were changed."
                : "\n\nThe MCP client preview found problems. No client settings were changed; see the details log.";
        if (clients.Success)
            return install ? "\n\nFLMCP setup is complete. " + McpSetupGuidance.NextSteps
                : "\n\nSelected AI app connections for this FL Studio installation were removed.";
        return filesSkipped
            ? "\n\nMCP client cleanup needs attention. Installer files were retained. See the details log, resolve the client configuration errors, and retry."
            : "\n\nInstaller files are present, but MCP client setup needs attention. See the details log for the affected apps and retry setup.";
    }
}
