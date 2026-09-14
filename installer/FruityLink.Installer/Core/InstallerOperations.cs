using System;
using FruityLink.Installer.Cli;

namespace FruityLink.Installer.Core;

/// <summary>
/// The install/uninstall pipeline shared by the CLI and the GUI: build the real engine, plan, log
/// every plan problem, and execute. Front-ends keep their own confirmation flow, elevation
/// handling, exit-code mapping, and finish reporting.
/// </summary>
public static class InstallerOperations
{
    private static InstallEngine CreateEngine() =>
        new(new RealFileSystem(), InstallerInfo.Version, new RealProcessManager());

    /// <summary>
    /// Plans and executes an install. Every plan problem is logged as "payload problem: ...". On a
    /// real run with required payload missing, <paramref name="missingPayloadAbortMessage"/> is
    /// logged and a failed result (Errors populated, so callers report FAILED rather than a false
    /// success) is returned with <paramref name="abortedOnMissingPayload"/> set; a dry-run
    /// continues past missing payload, logging <paramref name="missingPayloadDryRunWarning"/> when
    /// provided. <paramref name="beforeExecute"/> (the CLI's writability/elevation check) runs
    /// between planning and execution; returning false aborts with a null result.
    /// </summary>
    public static OperationResult? RunInstall(
        string flPath,
        InstallManifest manifest,
        string payloadRoot,
        bool dryRun,
        IProgressLog log,
        string missingPayloadAbortMessage,
        string? missingPayloadDryRunWarning,
        out bool abortedOnMissingPayload,
        Func<bool>? beforeExecute = null)
    {
        abortedOnMissingPayload = false;

        var engine = CreateEngine();
        var plan = engine.PlanInstall(flPath, manifest, payloadRoot, out var errors);
        foreach (var err in errors) log.Error("payload problem: " + err);

        if (errors.Count > 0)
        {
            if (!dryRun)
            {
                log.Error(missingPayloadAbortMessage);
                var aborted = new OperationResult { DryRun = dryRun };
                foreach (var err in errors) aborted.Errors.Add(err);
                abortedOnMissingPayload = true;
                return aborted;
            }
            if (missingPayloadDryRunWarning is not null)
                log.Warn(missingPayloadDryRunWarning);
        }

        if (beforeExecute is not null && !beforeExecute())
            return null;

        return engine.ExecuteInstall(plan, flPath, dryRun, log);
    }

    /// <summary>
    /// Plans and executes an uninstall. An empty plan logs the shared "Nothing to uninstall"
    /// warning, sets <paramref name="nothingToUninstall"/>, and returns a clean success.
    /// <paramref name="beforeExecute"/> runs between planning and execution; returning false
    /// aborts with a null result.
    /// </summary>
    public static OperationResult? RunUninstall(
        string flPath,
        InstallManifest manifest,
        bool dryRun,
        IProgressLog log,
        out bool nothingToUninstall,
        Func<bool>? beforeExecute = null)
    {
        nothingToUninstall = false;

        var engine = CreateEngine();
        var record = engine.TryLoadRecord(flPath, manifest);
        var plan = engine.PlanUninstall(flPath, manifest, record, log);

        if (plan.Count == 0)
        {
            log.Warn("Nothing to uninstall (no FruityLink files found).");
            nothingToUninstall = true;
            return new OperationResult { DryRun = dryRun };
        }

        if (beforeExecute is not null && !beforeExecute())
            return null;

        return engine.ExecuteUninstall(plan, dryRun, log, flPath);
    }
}
