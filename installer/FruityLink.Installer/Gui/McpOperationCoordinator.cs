using FruityLink.Installer.Core;
using FruityLink.Installer.Core.Mcp;

namespace FruityLink.Installer.Gui;

/// <summary>Keeps client configuration ordered around file installation without hiding partial success.</summary>
public sealed record McpOperationOutcome(OperationResult Files, McpSetupResult? Clients, bool FilesSkipped = false);

public static class McpOperationCoordinator
{
    public static McpOperationOutcome Run(bool install, bool dryRun,
        Func<OperationResult> runFiles, Func<McpSetupResult>? updateClients)
    {
        McpSetupResult? clients = null;
        if (!install && updateClients is not null)
        {
            clients = RunClientSetup(updateClients);
            if (!clients.Success)
                return new(new OperationResult { DryRun = dryRun }, clients, FilesSkipped: true);
        }

        var files = runFiles();
        if (install && files.Outcome == OperationOutcome.Success && updateClients is not null)
            clients = RunClientSetup(updateClients);
        return new(files, clients);
    }

    private static McpSetupResult RunClientSetup(Func<McpSetupResult> updateClients)
    {
        try { return updateClients(); }
        catch (Exception ex) { return new McpSetupResult { Errors = [ex.Message] }; }
    }
}
