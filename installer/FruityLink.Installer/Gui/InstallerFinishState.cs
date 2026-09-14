using FruityLink.Installer.Core;

namespace FruityLink.Installer.Gui;

/// <summary>Presentation decisions are independent of controls so preview and partial success stay testable.</summary>
public sealed record InstallerFinishState(string Headline, string ColorResource,
    bool OfferLaunch = false, bool LaunchInitiallyChecked = false)
{
    public static InstallerFinishState Create(McpOperationOutcome outcome, bool install, bool dryRun,
        string productName, bool flExecutableExists)
    {
        if (outcome.Clients is { Success: false })
            return new(dryRun ? "⚠ Preview found MCP client setup problems"
                : outcome.FilesSkipped ? "⚠ Client cleanup needs attention — files retained"
                : "⚠ Files installed — AI app setup needs attention", "WarnBrush");
        if (dryRun)
            return outcome.Files.Success ? new("✓ Dry run complete — no changes were made", "OkBrush")
                : new("⚠ Preview found installation problems", "WarnBrush");
        var verb = install ? "Install" : "Uninstall";
        if (outcome.Files.Outcome == OperationOutcome.RebootPending)
            return new($"⚠ {verb} incomplete — {outcome.Files.RebootPending.Count} file(s) pending reboot", "WarnBrush");
        if (!outcome.Files.Success)
            return new($"✕ {verb} failed", "DangerBrush");
        if (install)
            return new($"✓ {productName} is installed", "HeadlineGradientBrush", OfferLaunch: true,
                LaunchInitiallyChecked: outcome.Clients is null && flExecutableExists);
        return new(outcome.Files.FilesAffected == 0
            ? "✓ Nothing to remove — FL Studio is already stock"
            : "✓ Uninstall complete — FL Studio restored to stock", "HeadlineGradientBrush");
    }
}
