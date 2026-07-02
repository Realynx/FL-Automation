using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FruityLink.Installer.Core;

/// <summary>A file we moved aside so uninstall can put it back.</summary>
public sealed class BackupEntry
{
    public string OriginalPath { get; set; } = string.Empty;
    public string BackupPath { get; set; } = string.Empty;
}

/// <summary>
/// Records exactly what an install did so uninstall is precise and reversible: the files we wrote,
/// the directories we created, and the originals we backed up. Written into the FL dir as
/// <c>FruityLink.install.json</c> and mirrored to <c>%LocalAppData%\FruityLink\install-record.json</c>
/// so uninstall can find it even without reading the (protected) FL dir.
/// </summary>
public sealed class InstallRecord
{
    public string SchemaVersion { get; set; } = "1";
    public DateTimeOffset InstalledUtc { get; set; } = DateTimeOffset.UtcNow;
    public string InstallerVersion { get; set; } = string.Empty;
    public string FlPath { get; set; } = string.Empty;

    /// <summary>Absolute paths of every file we wrote (deleted on uninstall).</summary>
    public List<string> FilesWritten { get; set; } = new();

    /// <summary>Absolute paths of directories we created, parent-first (removed child-first if empty).</summary>
    public List<string> DirectoriesCreated { get; set; } = new();

    /// <summary>Originals we moved aside (restored on uninstall).</summary>
    public List<BackupEntry> Backups { get; set; } = new();

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOpts);

    public static InstallRecord FromJson(string json) =>
        JsonSerializer.Deserialize<InstallRecord>(json, JsonOpts)
        ?? throw new InvalidDataException("Install record JSON deserialized to null.");

    /// <summary>The mirror copy under %LocalAppData% used as a fallback for uninstall discovery.</summary>
    public static string LocalAppDataRecordPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FruityLink",
            "install-record.json");
}
