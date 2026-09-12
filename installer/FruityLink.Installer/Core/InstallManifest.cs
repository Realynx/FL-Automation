using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FruityLink.Installer.Core;

/// <summary>What kind of payload a manifest entry is (informational + drives a couple of defaults).</summary>
public enum PayloadKind
{
    /// <summary>The proxy <c>version.dll</c> (or other sideload target). Backs up FL's original.</summary>
    Proxy,
    /// <summary>CLR host DLL that boots .NET in-process.</summary>
    ClrHost,
    /// <summary>Native C++ control SDK (FlBridge.dll).</summary>
    NativeSdk,
    /// <summary>A single managed assembly / config file.</summary>
    ManagedFile,
    /// <summary>A whole directory of managed assemblies + *.runtimeconfig.json / *.deps.json.</summary>
    ManagedDir,
    /// <summary>A file copied from a system location (e.g. the renamed original from System32).</summary>
    SystemCopy,
    Other,
}

/// <summary>
/// One payload entry: a file (or directory) to copy from the payload root into the FL Studio dir.
/// The manifest is intentionally a plain data shape so it round-trips to JSON and can be synced to
/// the proxy contract (re/integration-pending-proxy.md) without code changes.
/// </summary>
public sealed class PayloadItem
{
    public PayloadKind Kind { get; set; } = PayloadKind.Other;

    /// <summary>
    /// Source location. For most kinds this is a path relative to the payload root. For
    /// <see cref="PayloadKind.SystemCopy"/> it is an (env-expandable) absolute path, e.g.
    /// <c>%SystemRoot%\System32\version.dll</c>. For a directory, the folder name under the payload root.
    /// </summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>
    /// Destination relative to the FL Studio root. Empty = same file name as the source, in the
    /// FL root. For a directory this is the destination sub-folder (e.g. <c>FruityLink</c>).
    /// </summary>
    public string Destination { get; set; } = string.Empty;

    /// <summary>Copy the whole folder recursively rather than a single file.</summary>
    public bool IsDirectory { get; set; }

    /// <summary>If true, a missing source aborts a real install; if false it is a warning.</summary>
    public bool Required { get; set; } = true;

    /// <summary>Back up an existing destination file before overwriting it (the proxy needs this).</summary>
    public bool BackupExisting { get; set; }

    /// <summary>Suffix appended to the original to form the backup name (e.g. <c>.flbak</c>).</summary>
    public string BackupSuffix { get; set; } = ".flbak";

    public string? Description { get; set; }

    /// <summary>Destination file/dir name relative to FL root (resolves the "empty = same name" rule).</summary>
    [JsonIgnore]
    public string EffectiveDestination =>
        string.IsNullOrWhiteSpace(Destination)
            ? Path.GetFileName(Source.Replace('/', '\\').TrimEnd('\\'))
            : Destination;
}

/// <summary>
/// The authoritative payload list. <see cref="Default"/> embodies the RealLoader / proxy-version.dll
/// model described in re/15-proxy-install.md; once re/integration-pending-proxy.md exists, sync it
/// either by editing <see cref="Default"/> or by shipping a <c>manifest.json</c> next to the EXE
/// (loaded via <see cref="Load"/>), no recompile required.
/// </summary>
public sealed class InstallManifest
{
    public int Version { get; set; } = 1;

    public string Name { get; set; } = "FruityLink";

    /// <summary>Install record file name, written into the FL dir (and mirrored to LocalAppData).</summary>
    public string RecordFileName { get; set; } = "FruityLink.install.json";

    public List<PayloadItem> Items { get; set; } = new();

    public string ToJson() => JsonSerializer.Serialize(this, InstallerJson.WithEnumStrings);

    public static InstallManifest FromJson(string json) =>
        JsonSerializer.Deserialize<InstallManifest>(json, InstallerJson.WithEnumStrings)
        ?? throw new InvalidDataException("Manifest JSON deserialized to null.");

    public static InstallManifest Load(string path) => FromJson(File.ReadAllText(path));

    /// <summary>
    /// Default payload (RealLoader / version.dll proxy). This is the contract until
    /// re/integration-pending-proxy.md supersedes it.
    /// </summary>
    public static InstallManifest Default() => new()
    {
        Version = 1,
        Name = "FruityLink",
        RecordFileName = "FruityLink.install.json",
        Items = new List<PayloadItem>
        {
            new()
            {
                Kind = PayloadKind.Proxy,
                Source = "version.dll",
                Destination = "version.dll",
                Required = true,
                BackupExisting = true,
                BackupSuffix = ".flbak",
                Description = "Proxy version.dll: forwards the version.dll exports to System32 and " +
                              "bootstraps FruityLink (FlClrHost -> CoreCLR) on a worker thread. Any " +
                              "existing version.dll is backed up first.",
            },
            new()
            {
                Kind = PayloadKind.ManagedDir,
                Source = "FruityLink",
                Destination = "FruityLink",
                IsDirectory = true,
                Required = true,
                Description = "The in-process payload copied into FL\\FruityLink\\: FlClrHost.dll (CLR host), " +
                              "FlBridge.dll (native SDK), FruityLink.Host + Core/FlStudio/Plugins.* assemblies " +
                              "(+ runtimeconfig/deps), and plugins\\fl-agent\\ (the FL Agent plugin closure). " +
                              "Mirrors bootstrap\\dist\\ exactly (synced by build-and-stage.ps1).",
            },
        },
    };
}
