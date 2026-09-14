namespace FruityLink.Installer.Core.Mcp;

/// <summary>Shared next steps for GUI and command-line client setup.</summary>
public static class McpSetupGuidance
{
    public const string NextSteps = "Restart configured AI apps; import exported settings for other clients if selected. "
        + "Restart FL Studio after installing updates. To connect an existing session, enable FLMCP in its Plugins menu, "
        + "use fl_instances to list sessions, then fl_attach to choose one. fl_detach disconnects without closing FL. "
        + "For a new managed project, close other FL sessions and use fl_project_start. Client tool approvals remain under your control.";
}
