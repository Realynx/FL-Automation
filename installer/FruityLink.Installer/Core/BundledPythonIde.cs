namespace FruityLink.Installer.Core;

/// <summary>The independently selectable editor; Python itself belongs to the base framework.</summary>
public static class BundledPythonIde
{
    public const string Id = "fl-python-ide";
    public const string SourceDirectory = "optional-plugins/fl-python-ide";
    public const string SelectionLabel = "FL Python IDE — write and run Python inside FL Studio (MIT)";

    public static bool IsAvailable(string payloadRoot) =>
        Directory.Exists(Path.Combine(payloadRoot, SourceDirectory));

    public static InstallManifest Select(InstallManifest basis, string payloadRoot, bool include = true)
    {
        var manifest = InstallManifest.FromJson(basis.ToJson());
        if (!include || !IsAvailable(payloadRoot)) return manifest;
        manifest.Items.Add(new PayloadItem
        {
            Kind = PayloadKind.ManagedDir,
            Source = SourceDirectory,
            Destination = "FruityLink/plugins/fl-python-ide",
            IsDirectory = true,
            Required = true,
            Description = "FL Python IDE editor and Avalonia dependencies",
        });
        return manifest;
    }
}
