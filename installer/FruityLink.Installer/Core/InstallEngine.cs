using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace FruityLink.Installer.Core;

/// <summary>Three-state result of an operation, used to drive honest headlines / exit codes.</summary>
public enum OperationOutcome
{
    /// <summary>Everything completed; nothing left behind.</summary>
    Success,
    /// <summary>One or more files were locked and scheduled for removal/restore on next reboot.</summary>
    RebootPending,
    /// <summary>A hard error or an un-removable leftover; the operation did not fully succeed.</summary>
    Failed,
}

/// <summary>Outcome of an install or uninstall run.</summary>
public sealed class OperationResult
{
    /// <summary>
    /// True only for a clean run: no hard errors, nothing left behind, and nothing deferred to a
    /// reboot. Equivalent to <see cref="Outcome"/> == <see cref="OperationOutcome.Success"/>.
    /// </summary>
    public bool Success => Outcome == OperationOutcome.Success;
    public bool DryRun { get; set; }
    public int ActionsPlanned { get; set; }
    public int ActionsExecuted { get; set; }
    public List<string> Errors { get; } = new();
    public InstallRecord? Record { get; set; }

    /// <summary>Files actually copied (install) or removed (uninstall). Dry-run: files that would be.</summary>
    public int FilesAffected { get; set; }

    /// <summary>How many running FL Studio instances we closed before mutating files.</summary>
    public int FlProcessesClosed { get; set; }

    /// <summary>Files that were locked and scheduled to be removed/restored on the next reboot.</summary>
    public List<string> RebootPending { get; } = new();

    /// <summary>Files we could neither remove/restore now nor schedule for reboot (true leftovers).</summary>
    public List<string> Leftover { get; } = new();

    /// <summary>Honest three-state outcome derived from errors / leftovers / reboot-pending.</summary>
    public OperationOutcome Outcome =>
        Errors.Count > 0 || Leftover.Count > 0 ? OperationOutcome.Failed
        : RebootPending.Count > 0 ? OperationOutcome.RebootPending
        : OperationOutcome.Success;
}

/// <summary>
/// Plans and executes the install / uninstall. Pure-logic: it talks only to <see cref="IFileSystem"/>
/// and <see cref="IProgressLog"/>, so it is unit-testable (see InMemoryFileSystem) and dry-run-clean
/// (planning never mutates; dry-run executes nothing).
/// </summary>
public sealed class InstallEngine
{
    private readonly IFileSystem _fs;
    private readonly string _installerVersion;
    private readonly IProcessManager _processes;
    private readonly int _deleteRetries;
    private readonly int _deleteRetryDelayMs;
    private readonly string _mirrorRecordPath;

    /// <param name="processManager">
    /// Closes FL Studio before mutating its files. Defaults to <see cref="NoOpProcessManager"/> so the
    /// unit tests and <c>--self-test</c> never kill a real FL instance; the GUI/CLI pass a
    /// <see cref="RealProcessManager"/>.
    /// </param>
    /// <param name="deleteRetries">How many times to retry a locked delete/restore before scheduling it for reboot.</param>
    /// <param name="deleteRetryDelayMs">Delay between delete/restore retries (0 in tests).</param>
    /// <param name="mirrorRecordPath">Fallback install-record location. Defaults to the user's
    /// LocalAppData record; self-tests supply a path inside their isolated workspace.</param>
    public InstallEngine(
        IFileSystem fs,
        string installerVersion,
        IProcessManager? processManager = null,
        int deleteRetries = 5,
        int deleteRetryDelayMs = 300,
        string? mirrorRecordPath = null)
    {
        _fs = fs;
        _installerVersion = installerVersion;
        _processes = processManager ?? new NoOpProcessManager();
        _deleteRetries = Math.Max(1, deleteRetries);
        _deleteRetryDelayMs = Math.Max(0, deleteRetryDelayMs);
        _mirrorRecordPath = mirrorRecordPath ?? InstallRecord.LocalAppDataRecordPath;
    }

    // ---------------------------------------------------------------- INSTALL ----

    /// <summary>
    /// Build the ordered install plan. <paramref name="errors"/> collects blocking problems
    /// (e.g. a required payload file is missing) that would make a real install fail.
    /// </summary>
    public List<InstallAction> PlanInstall(
        string flPath, InstallManifest manifest, string payloadRoot, out List<string> errors)
    {
        // Local list because the out parameter cannot be captured by the local functions below.
        var errs = new List<string>();
        var actions = new List<InstallAction>();
        var dirsPlanned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void PlanDir(string dir)
        {
            // Emit parent-first CreateDirectory actions for any directory not already present
            // and not already planned. The FL root itself is assumed to exist (validated earlier).
            var chain = new List<string>();
            var cur = dir;
            while (!string.IsNullOrEmpty(cur)
                   && !string.Equals(cur, flPath.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)
                   && !_fs.DirectoryExists(cur)
                   && !dirsPlanned.Contains(cur))
            {
                chain.Add(cur);
                cur = Path.GetDirectoryName(cur) ?? string.Empty;
            }
            chain.Reverse();
            foreach (var d in chain)
            {
                if (dirsPlanned.Add(d))
                    actions.Add(new InstallAction
                    {
                        Kind = ActionKind.CreateDirectory,
                        Target = d,
                        Description = "create directory",
                    });
            }
        }

        void PlanCopy(string source, string dest, PayloadItem item)
        {
            var sourceExists = _fs.FileExists(source);
            if (!sourceExists)
            {
                var msg = $"payload missing: {source}";
                if (item.Required)
                    errs.Add(msg + " (required)");
                // Still emit the action (Optional flag) so dry-run shows intent; executor skips/aborts.
            }

            var destDir = Path.GetDirectoryName(dest);
            if (!string.IsNullOrEmpty(destDir))
                PlanDir(destDir);

            // Back up an existing original before we overwrite it (proxy version.dll).
            if (item.BackupExisting && _fs.FileExists(dest))
            {
                var backup = dest + item.BackupSuffix;
                if (!_fs.FileExists(backup))
                {
                    actions.Add(new InstallAction
                    {
                        Kind = ActionKind.BackupFile,
                        Source = dest,
                        Target = backup,
                        Description = "back up existing original",
                    });
                }
                else
                {
                    // A backup already exists (re-install) -> keep the true original untouched, but
                    // still record it so a later uninstall restores it.
                    actions.Add(new InstallAction
                    {
                        Kind = ActionKind.BackupFile,
                        Source = dest,
                        Target = backup,
                        Description = "preserve existing backup of original",
                        RecordOnly = true,
                    });
                }
            }

            actions.Add(new InstallAction
            {
                Kind = ActionKind.CopyFile,
                Source = source,
                Target = dest,
                Description = item.Description ?? item.Kind.ToString(),
                Optional = !item.Required,
            });
        }

        foreach (var item in manifest.Items)
        {
            var source = ResolveSource(item, payloadRoot);

            if (item.IsDirectory)
            {
                var destDir = Path.Combine(flPath, item.EffectiveDestination);
                if (!_fs.DirectoryExists(source))
                {
                    var msg = $"payload directory missing: {source}";
                    if (item.Required) errs.Add(msg + " (required)");
                    // Nothing to enumerate; in dry-run we still note the intended directory.
                    PlanDir(destDir);
                    continue;
                }

                var files = _fs.EnumerateFiles(source, recursive: true).ToList();
                foreach (var file in files)
                {
                    var rel = Path.GetRelativePath(source, file);
                    var dest = Path.Combine(destDir, rel);
                    PlanCopy(file, dest, item);
                }
            }
            else
            {
                var dest = Path.Combine(flPath, item.EffectiveDestination);
                PlanCopy(source, dest, item);
            }
        }

        // Final step: persist the record so uninstall is exact.
        var recordPath = Path.Combine(flPath, manifest.RecordFileName);
        actions.Add(new InstallAction
        {
            Kind = ActionKind.WriteRecord,
            Target = recordPath,
            Description = "write install record",
        });

        errors = errs;
        return actions;
    }

    public OperationResult ExecuteInstall(
        IReadOnlyList<InstallAction> plan, string flPath, bool dryRun, IProgressLog log)
    {
        var result = new OperationResult { DryRun = dryRun, ActionsPlanned = plan.Count };
        var record = new InstallRecord
        {
            InstallerVersion = _installerVersion,
            FlPath = flPath,
            InstalledUtc = DateTimeOffset.UtcNow,
        };
        result.Record = record;

        // FL's version.dll (and the other in-process DLLs) are LOADED while FL runs, so they can't be
        // backed up / overwritten until every FL instance exits. Close FL before touching any file.
        if (!dryRun)
            CloseFlStudio(result, log);

        foreach (var action in plan)
        {
            log.Action(action.Format(dryRun));
            try
            {
                switch (action.Kind)
                {
                    case ActionKind.CreateDirectory:
                        if (!dryRun) _fs.CreateDirectory(action.Target);
                        record.DirectoriesCreated.Add(action.Target);
                        break;

                    case ActionKind.BackupFile:
                        if (!action.RecordOnly && !dryRun)
                            _fs.MoveFile(action.Source!, action.Target, overwrite: false);
                        record.Backups.Add(new BackupEntry
                        {
                            // For RecordOnly the original currently lives at action.Target (the backup);
                            // action.Source is where it must be restored to.
                            OriginalPath = action.Source!,
                            BackupPath = action.Target,
                        });
                        break;

                    case ActionKind.CopyFile:
                        if (!_fs.FileExists(action.Source!))
                        {
                            if (action.Optional)
                            {
                                log.Warn($"  skipped (optional, source missing): {action.Source}");
                                continue;
                            }
                            throw new FileNotFoundException("Required payload missing", action.Source);
                        }
                        // If we're overwriting a file we didn't back up and there's no backup for it,
                        // it's one of our own files (re-install) — safe to overwrite.
                        if (!dryRun) _fs.CopyFile(action.Source!, action.Target, overwrite: true);
                        record.FilesWritten.Add(action.Target);
                        result.FilesAffected++;
                        break;

                    case ActionKind.WriteRecord:
                        if (!dryRun)
                        {
                            _fs.WriteAllText(action.Target, record.ToJson());
                            TryWriteMirror(record, log);
                        }
                        break;

                    default:
                        throw new InvalidOperationException($"Unexpected install action: {action.Kind}");
                }

                result.ActionsExecuted++;
            }
            catch (Exception ex)
            {
                var msg = $"FAILED: {action.Format(dryRun)} :: {ex.Message}";
                log.Error(msg);
                result.Errors.Add(msg);
                return result; // stop on first failure; user can uninstall to roll back partials
            }
        }

        return result;
    }

    private void TryWriteMirror(InstallRecord record, IProgressLog log)
    {
        try
        {
            _fs.WriteAllText(_mirrorRecordPath, record.ToJson());
        }
        catch (Exception ex)
        {
            log.Warn($"  could not write install record mirror: {ex.Message}");
        }
    }

    // -------------------------------------------------------------- UNINSTALL ----

    /// <summary>
    /// Build the uninstall plan. Prefers the recorded truth; if no record is found, falls back to a
    /// manifest-derived best-effort (delete known files, restore known backups).
    /// </summary>
    public List<InstallAction> PlanUninstall(
        string flPath, InstallManifest manifest, InstallRecord? record, IProgressLog log)
    {
        var actions = new List<InstallAction>();

        if (record is null || !RecordBelongsTo(record, flPath))
        {
            log.Warn("No install record for the selected FL directory; using manifest-derived best-effort uninstall.");
            return PlanUninstallFromManifest(flPath, manifest);
        }

        // 1. Delete files we wrote.
        foreach (var file in record.FilesWritten)
            actions.Add(new InstallAction
            {
                Kind = ActionKind.DeleteFile,
                Target = file,
                Description = "remove installed file",
            });

        // 2. Restore originals we moved aside.
        foreach (var backup in record.Backups)
            actions.Add(new InstallAction
            {
                Kind = ActionKind.RestoreBackup,
                Source = backup.BackupPath,
                Target = backup.OriginalPath,
                Description = "restore original",
            });

        // 3. Remove directories we created (child-first).
        foreach (var dir in record.DirectoriesCreated.AsEnumerable().Reverse())
            actions.Add(new InstallAction
            {
                Kind = ActionKind.RemoveDirectory,
                Target = dir,
                Description = "remove created directory (if empty)",
            });

        // 4. Remove the record itself + this engine's configured fallback mirror.
        var recordPath = Path.Combine(flPath, manifest.RecordFileName);
        actions.Add(new InstallAction
        {
            Kind = ActionKind.DeleteFile,
            Target = recordPath,
            Description = "remove install record",
        });
        AddMatchingMirrorRemoval(actions, flPath);

        return actions;
    }

    private List<InstallAction> PlanUninstallFromManifest(string flPath, InstallManifest manifest)
    {
        var actions = new List<InstallAction>();
        var dirsToRemove = new List<string>();

        foreach (var item in manifest.Items)
        {
            if (item.IsDirectory)
            {
                var destDir = Path.Combine(flPath, item.EffectiveDestination);
                if (_fs.DirectoryExists(destDir))
                {
                    foreach (var file in _fs.EnumerateFiles(destDir, recursive: true))
                        actions.Add(new InstallAction
                        {
                            Kind = ActionKind.DeleteFile, Target = file, Description = "remove installed file",
                        });
                    dirsToRemove.Add(destDir);
                }
            }
            else
            {
                var dest = Path.Combine(flPath, item.EffectiveDestination);
                if (_fs.FileExists(dest))
                    actions.Add(new InstallAction
                    {
                        Kind = ActionKind.DeleteFile, Target = dest, Description = "remove installed file",
                    });

                if (item.BackupExisting)
                {
                    var backup = dest + item.BackupSuffix;
                    if (_fs.FileExists(backup))
                        actions.Add(new InstallAction
                        {
                            Kind = ActionKind.RestoreBackup, Source = backup, Target = dest,
                            Description = "restore original",
                        });
                }
            }
        }

        foreach (var dir in dirsToRemove)
            actions.Add(new InstallAction
            {
                Kind = ActionKind.RemoveDirectory, Target = dir, Description = "remove created directory (if empty)",
            });

        var recordPath = Path.Combine(flPath, manifest.RecordFileName);
        if (_fs.FileExists(recordPath))
            actions.Add(new InstallAction { Kind = ActionKind.DeleteFile, Target = recordPath, Description = "remove install record" });
        AddMatchingMirrorRemoval(actions, flPath);

        return actions;
    }

    public OperationResult ExecuteUninstall(
        IReadOnlyList<InstallAction> plan, bool dryRun, IProgressLog log)
    {
        var result = new OperationResult { DryRun = dryRun, ActionsPlanned = plan.Count };

        // The proxy version.dll + the in-process FruityLink DLLs stay LOCKED while FL runs (this is the
        // bug we hit: an uninstall "succeeded" but left them behind because FL was never closed). Close
        // every FL instance and wait for the locks to release before deleting anything.
        if (!dryRun)
            CloseFlStudio(result, log);

        foreach (var action in plan)
        {
            log.Action(action.Format(dryRun));
            try
            {
                switch (action.Kind)
                {
                    case ActionKind.DeleteFile:
                        if (!_fs.FileExists(action.Target))
                        {
                            log.Warn($"  already gone: {action.Target}");
                            break;
                        }
                        if (dryRun) { result.FilesAffected++; break; }
                        RobustDelete(action.Target, result, log);
                        break;

                    case ActionKind.RestoreBackup:
                        if (!_fs.FileExists(action.Source!))
                        {
                            log.Warn($"  backup missing, cannot restore: {action.Source}");
                            break;
                        }
                        if (dryRun) { result.FilesAffected++; break; }
                        RobustRestore(action.Source!, action.Target, result, log);
                        break;

                    case ActionKind.RemoveDirectory:
                        if (!dryRun) _fs.DeleteDirectoryIfEmpty(action.Target);
                        break;

                    default:
                        throw new InvalidOperationException($"Unexpected uninstall action: {action.Kind}");
                }

                result.ActionsExecuted++;
            }
            catch (Exception ex)
            {
                var msg = $"FAILED: {action.Format(dryRun)} :: {ex.Message}";
                log.Error(msg);
                result.Errors.Add(msg);
                // Uninstall is best-effort: keep going so we remove as much as we can.
            }
        }

        return result;
    }

    // ------------------------------------------------------- FL + locked-file handling ----

    /// <summary>Closes every running FL Studio instance and records how many, so files unlock.</summary>
    private void CloseFlStudio(OperationResult result, IProgressLog log)
    {
        var close = _processes.CloseFlStudio(log);
        result.FlProcessesClosed = close.ClosedCount;
        if (close.ClosedCount > 0 && !close.AllExited)
            log.Warn(
                $"  {close.ClosedCount - close.ExitedCount} FL Studio process(es) may still be running; " +
                "locked files will be scheduled for removal on reboot.");
    }

    /// <summary>
    /// Deletes a file, retrying a few times if it's locked; if it stays locked it is scheduled for
    /// deletion on the next reboot (MoveFileEx) and tracked, so we never falsely report success.
    /// </summary>
    private void RobustDelete(string path, OperationResult result, IProgressLog log)
    {
        var deleted = TryWithRetries(
            () =>
            {
                _fs.DeleteFile(path);
                if (!_fs.FileExists(path))
                {
                    result.FilesAffected++;
                    return true;
                }
                return false;
            },
            (attempt, ex) => log.Warn(
                $"  still locked after {attempt} attempt(s) ({ex.Message}); scheduling for reboot: {path}"));
        if (deleted)
            return;

        ScheduleOnRebootOrFail(
            () => _fs.ScheduleDeleteOnReboot(path), path,
            pendingWarn: $"  locked; scheduled for deletion on next reboot: {path}",
            schedulingFailedPrefix: $"  could not schedule reboot-delete for {path}: ",
            leftoverError: $"could not remove (file locked): {path}",
            failedLog: $"  FAILED to remove (locked, reboot-scheduling failed): {path}",
            result, log);
    }

    /// <summary>
    /// Restores a backed-up original over the current (possibly locked) file, retrying; if the target
    /// stays locked the restore is scheduled for the next reboot and tracked.
    /// </summary>
    private void RobustRestore(string backup, string original, OperationResult result, IProgressLog log)
    {
        var restored = TryWithRetries(
            () =>
            {
                _fs.MoveFile(backup, original, overwrite: true);
                return true;
            },
            (attempt, ex) => log.Warn(
                $"  restore blocked after {attempt} attempt(s) ({ex.Message}); scheduling for reboot: {original}"));
        if (restored)
            return;

        ScheduleOnRebootOrFail(
            () => _fs.ScheduleMoveOnReboot(backup, original), original,
            pendingWarn: $"  locked; the original will be restored on next reboot: {original}",
            schedulingFailedPrefix: $"  could not schedule reboot-restore for {original}: ",
            leftoverError: $"could not restore original (file locked): {original}",
            failedLog: $"  FAILED to restore (locked, reboot-scheduling failed): {original}",
            result, log);
    }

    /// <summary>
    /// Runs <paramref name="op"/> up to the retry limit (sleeping between attempts). True as soon
    /// as it succeeds; false when every attempt failed or threw (a locked file), after
    /// <paramref name="onFinalFailure"/> logged the last attempt's exception.
    /// </summary>
    private bool TryWithRetries(Func<bool> op, Action<int, Exception> onFinalFailure)
    {
        for (var attempt = 1; attempt <= _deleteRetries; attempt++)
        {
            try
            {
                if (op())
                    return true;
            }
            catch (Exception ex) when (attempt == _deleteRetries)
            {
                onFinalFailure(attempt, ex);
            }
            catch
            {
                // transient lock — fall through to retry
            }

            if (attempt < _deleteRetries && _deleteRetryDelayMs > 0)
                System.Threading.Thread.Sleep(_deleteRetryDelayMs);
        }

        return false;
    }

    /// <summary>
    /// Last-resort bookkeeping for a file that stayed locked: schedule the reboot-time op and track
    /// it as pending, or record a true leftover + error, so we never falsely report success.
    /// </summary>
    private static void ScheduleOnRebootOrFail(
        Func<bool> schedule, string path, string pendingWarn, string schedulingFailedPrefix,
        string leftoverError, string failedLog, OperationResult result, IProgressLog log)
    {
        try
        {
            if (schedule())
            {
                result.RebootPending.Add(path);
                log.Warn(pendingWarn);
                return;
            }
        }
        catch (Exception ex)
        {
            log.Error(schedulingFailedPrefix + ex.Message);
        }

        result.Leftover.Add(path);
        result.Errors.Add(leftoverError);
        log.Error(failedLog);
    }

    // ----------------------------------------------------------------- shared ----

    /// <summary>Absolute source path for a payload item (env-expanded for SystemCopy).</summary>
    public static string ResolveSource(PayloadItem item, string payloadRoot)
    {
        if (item.Kind == PayloadKind.SystemCopy)
            return Environment.ExpandEnvironmentVariables(item.Source);

        var s = Environment.ExpandEnvironmentVariables(item.Source);
        return Path.IsPathRooted(s) ? s : Path.Combine(payloadRoot, s);
    }

    /// <summary>Loads a record belonging to the selected FL directory (primary first, then the
    /// configured fallback mirror). Records for another installation are ignored.</summary>
    public InstallRecord? TryLoadRecord(string flPath, InstallManifest manifest)
    {
        var primary = Path.Combine(flPath, manifest.RecordFileName);
        return TryLoadMatchingRecord(primary, flPath) ?? TryLoadMatchingRecord(_mirrorRecordPath, flPath);
    }

    private InstallRecord? TryLoadMatchingRecord(string path, string flPath)
    {
        if (!_fs.FileExists(path)) return null;
        try
        {
            var record = InstallRecord.FromJson(_fs.ReadAllText(path));
            return RecordBelongsTo(record, flPath) ? record : null;
        }
        catch { return null; }
    }

    private static bool RecordBelongsTo(InstallRecord record, string flPath)
    {
        if (string.IsNullOrWhiteSpace(record.FlPath) || string.IsNullOrWhiteSpace(flPath)) return false;
        try
        {
            var recorded = Path.TrimEndingDirectorySeparator(Path.GetFullPath(record.FlPath));
            var selected = Path.TrimEndingDirectorySeparator(Path.GetFullPath(flPath));
            return recorded.Equals(selected, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private void AddMatchingMirrorRemoval(List<InstallAction> actions, string flPath)
    {
        if (TryLoadMatchingRecord(_mirrorRecordPath, flPath) is null) return;
        actions.Add(new InstallAction
        {
            Kind = ActionKind.DeleteFile,
            Target = _mirrorRecordPath,
            Description = "remove install record mirror",
        });
    }
}
