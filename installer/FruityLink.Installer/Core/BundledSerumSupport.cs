namespace FruityLink.Installer.Core;

/// <summary>Optional open-source Python helpers for Serum preset inventory and audition analysis.</summary>
public static class BundledSerumSupport
{
    public const string Id = "serum-support";
    public const string SourceDirectory = "optional-plugins/serum-support";
    public const string SelectionLabel = "Serum support — preset inventory and audition analysis (MIT; Serum not included)";

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
            Destination = "FruityLink/python/extensions/serum-support",
            IsDirectory = true,
            Required = true,
            Description = "FruityLink Serum support Python wheel, documentation, and MIT license",
        });
        return manifest;
    }
}
