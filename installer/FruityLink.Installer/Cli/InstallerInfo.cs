using System;
using System.IO;
using System.Reflection;

namespace FruityLink.Installer.Cli;

/// <summary>Version + payload-root resolution shared by CLI and GUI.</summary>
public static class InstallerInfo
{
    public static string Version =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

    public static string ResolvePayloadRoot(string? overrideRoot) =>
        string.IsNullOrWhiteSpace(overrideRoot)
            ? Path.Combine(AppContext.BaseDirectory, "payload")
            : overrideRoot;

    /// <summary>
    /// True when this artifact ships the sold FL Automate plugin (the packaged edition sold on
    /// fl-automate.com); false for the community edition, which includes the plugin system and
    /// optional FLMCP bundle. One exe serves both — the edition is simply whether the fl-agent
    /// plugin closure is present in the payload next to it.
    /// </summary>
    public static bool IsPackagedEdition(string payloadRoot) =>
        Directory.Exists(Path.Combine(payloadRoot, "FruityLink", "plugins", "fl-agent"));
}
