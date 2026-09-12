namespace FruityLink.Installer.Core;

/// <summary>Local FLMCP payload selection, shared by GUI and unattended installs.</summary>
public static class BundledMcp
{
    public const string Id = "fl-mcp";
    public const string SourceDirectory = "optional-plugins/fl-mcp";
    public const string SelectionLabel = "FLMCP — MCP server and Python SDK (included; PolyForm NonCommercial)";

    /// <summary>A missing bundle keeps older standalone installer payloads usable.</summary>
    public static bool IsAvailable(string payloadRoot) =>
        Directory.Exists(Path.Combine(payloadRoot, SourceDirectory));

    /// <summary>Copies the base manifest and adds the selected local payload. The optional bundle
    /// stays outside the base FruityLink directory, so deselection actually excludes its files.</summary>
    public static InstallManifest Select(InstallManifest basis, string payloadRoot, bool include = true)
    {
        var manifest = InstallManifest.FromJson(basis.ToJson());
        if (!include || !IsAvailable(payloadRoot)) return manifest;
        manifest.Items.Add(Component("plugin", "FruityLink/plugins/fl-mcp", "FLMCP plugin"));
        manifest.Items.Add(Component("companion", "FruityLink/tools/fl-mcp", "FLMCP server, Python SDK, documentation, and licenses"));
        return manifest;
    }

    private static PayloadItem Component(string source, string destination, string description) => new()
    {
        Kind = PayloadKind.ManagedDir,
        Source = Path.Combine(SourceDirectory, source),
        Destination = destination,
        IsDirectory = true,
        Required = true,
        Description = description,
    };
}
