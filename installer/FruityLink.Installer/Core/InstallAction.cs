namespace FruityLink.Installer.Core;

public enum ActionKind
{
    /// <summary>Create a directory (and record it for cleanup if it didn't already exist).</summary>
    CreateDirectory,
    /// <summary>Move an existing original file out of the way (Source -> Target).</summary>
    BackupFile,
    /// <summary>Copy a payload file in (Source -> Target).</summary>
    CopyFile,
    /// <summary>Serialize the install record to Target (and its LocalAppData mirror).</summary>
    WriteRecord,
    /// <summary>Delete a file we previously wrote (Target).</summary>
    DeleteFile,
    /// <summary>Restore a backed-up original (Source backup -> Target original).</summary>
    RestoreBackup,
    /// <summary>Remove a directory we created, only if empty (Target).</summary>
    RemoveDirectory,
}

/// <summary>
/// One planned filesystem step. A plan is a list of these; <see cref="InstallEngine"/> can print
/// them (dry-run) or execute them. Keeping the plan as data makes both dry-run and unit testing
/// trivial.
/// </summary>
public sealed class InstallAction
{
    public ActionKind Kind { get; init; }
    public string? Source { get; init; }
    public string Target { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;

    /// <summary>For copies: a missing source is a warning (skip) rather than a hard error.</summary>
    public bool Optional { get; init; }

    /// <summary>
    /// For <see cref="ActionKind.BackupFile"/>: record the backup in the install record but do NOT
    /// move anything (the backup already exists from a prior install — preserve the true original).
    /// </summary>
    public bool RecordOnly { get; init; }

    public PayloadKind Payload { get; init; } = PayloadKind.Other;

    /// <summary>Short one-line form used in dry-run / progress output.</summary>
    public string Format(bool dryRun)
    {
        var tag = Kind switch
        {
            ActionKind.CreateDirectory => "mkdir  ",
            ActionKind.BackupFile      => "backup ",
            ActionKind.CopyFile        => "copy   ",
            ActionKind.WriteRecord     => "record ",
            ActionKind.DeleteFile      => "delete ",
            ActionKind.RestoreBackup   => "restore",
            ActionKind.RemoveDirectory => "rmdir  ",
            _ => "?      ",
        };
        var prefix = dryRun ? "[dry] " : string.Empty;
        var body = Source is null ? Target : $"{Source}  ->  {Target}";
        return $"{prefix}[{tag}] {body}";
    }
}
