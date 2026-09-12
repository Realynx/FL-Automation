using System;
using System.IO;
using FruityLink.Installer.Core;

namespace FruityLink.Installer.Cli;

/// <summary>Process exit codes (documented in <see cref="CliOptions.Usage"/>).</summary>
public static class ExitCodes
{
    public const int Success = 0;
    public const int Error = 1;
    public const int BadArgs = 2;
    public const int FlNotFound = 3;
    public const int PayloadMissing = 4;
    public const int NeedsElevation = 5;
    public const int RebootRequired = 6;
    public const int BuildNotVerified = 7;
    public const int IntegrityFailed = 8;
}

/// <summary>Headless entry point. Dispatches verbs and returns a process exit code.</summary>
public static class CliRunner
{
    public static int Run(CliOptions opts, IProgressLog log)
    {
        if (opts.Help)
        {
            log.Info(CliOptions.Usage);
            return ExitCodes.Success;
        }

        if (opts.ShowVersion)
        {
            log.Info($"FruityLink.Installer {InstallerInfo.Version}");
            return ExitCodes.Success;
        }

        if (opts.Unknown.Count > 0)
        {
            log.Error("Unknown argument(s): " + string.Join(" ", opts.Unknown));
            log.Info("Run with --help for usage.");
            return ExitCodes.BadArgs;
        }

        if (opts.Install && opts.Uninstall)
        {
            log.Error("Specify only one of --install or --uninstall.");
            return ExitCodes.BadArgs;
        }

        if (opts.PrintManifest)
        {
            log.Info(LoadManifest(opts, log).ToJson());
            return ExitCodes.Success;
        }

        if (opts.SelfTest)
        {
            log.Info("Running FruityLink installer self-test (temp dir, real file ops)...");
            return SelfTest.Run(log, InstallerInfo.Version) ? ExitCodes.Success : ExitCodes.Error;
        }

        if (opts.PrintFlHashes)
            return RunPrintFlHashes(opts, log);

        if (opts.Install) return RunInstall(opts, log);
        if (opts.Uninstall) return RunUninstall(opts, log);

        log.Error("Nothing to do. Run with --help for usage.");
        return ExitCodes.BadArgs;
    }

    private static int RunInstall(CliOptions opts, IProgressLog log)
    {
        var manifest = LoadManifest(opts, log);
        var payloadRoot = InstallerInfo.ResolvePayloadRoot(opts.PayloadRoot);

        if (!TryResolveFlPath(opts, log, out var flPath))
            return ExitCodes.FlNotFound;

        log.Info($"FL Studio:    {flPath}");
        log.Info($"Payload root: {payloadRoot}");
        log.Info($"Mode:         {(opts.DryRun ? "DRY RUN (no changes)" : "INSTALL")}");
        log.Info("");

        // Verified-build + integrity gates (see FlCompatibilityList). Dry-run previews past a
        // failure; --force is the documented dev/power-user override. Uninstall is never gated —
        // restoring FL to stock must always be possible.
        if (GateOnCompatibility(flPath, opts, log) is int blocked)
            return blocked;

        var elevationCode = ExitCodes.Success;
        var result = InstallerOperations.RunInstall(
            flPath, manifest, payloadRoot, opts.DryRun, log,
            "Aborting: required payload files are missing. (Use --dry-run to preview anyway.)",
            "Continuing dry-run despite missing payload (nothing will be written).",
            out var abortedOnMissingPayload,
            beforeExecute: () => opts.DryRun || EnsureWritable(flPath, opts, log, install: true, out elevationCode));

        if (abortedOnMissingPayload)
            return ExitCodes.PayloadMissing;
        if (result is null)
            return elevationCode;

        log.Info("");
        return Report(result, log, install: true, flPath);
    }

    private static int RunUninstall(CliOptions opts, IProgressLog log)
    {
        var manifest = LoadManifest(opts, log);

        if (!TryResolveFlPath(opts, log, out var flPath))
            return ExitCodes.FlNotFound;

        log.Info($"FL Studio: {flPath}");
        log.Info($"Mode:      {(opts.DryRun ? "DRY RUN (no changes)" : "UNINSTALL")}");
        log.Info("");

        var elevationCode = ExitCodes.Success;
        var result = InstallerOperations.RunUninstall(
            flPath, manifest, opts.DryRun, log,
            out var nothingToUninstall,
            beforeExecute: () => opts.DryRun || EnsureWritable(flPath, opts, log, install: false, out elevationCode));

        if (nothingToUninstall)
            return ExitCodes.Success;
        if (result is null)
            return elevationCode;

        log.Info("");
        return Report(result, log, install: false, flPath);
    }

    // ------------------------------------------------- compatibility / integrity ----

    /// <summary>
    /// Runs the packed verified-build + hash gates against <paramref name="flPath"/>.
    /// Returns null to proceed, or the exit code to abort with. Never blocks dry-run or --force
    /// (both downgrade to loud warnings).
    /// </summary>
    private static int? GateOnCompatibility(string flPath, CliOptions opts, IProgressLog log)
    {
        log.Info("Checking FL Studio build + file integrity against the verified list...");
        var check = FlIntegrityGate.CheckAndLog(flPath, log);
        if (check.Ok)
            return null;

        var code = check.Problems.Count > 0 ? ExitCodes.IntegrityFailed : ExitCodes.BuildNotVerified;

        if (opts.DryRun || opts.Force)
        {
            log.Warn($"Compatibility check FAILED: {check.BlockReason}");
            log.Warn($"Proceeding anyway ({(opts.DryRun ? "dry-run" : "--force")}).");
            return null;
        }

        log.Error($"Aborting: {check.BlockReason}");
        return code;
    }

    /// <summary>
    /// Dev tool: hash the given (team-verified!) FL install into a compatibility.json-shaped
    /// document, printed to stdout. Redirect into compatibility.json for a first entry, or merge
    /// the entry into the existing list when verifying an additional FL build.
    /// </summary>
    private static int RunPrintFlHashes(CliOptions opts, IProgressLog log)
    {
        if (!TryResolveFlPath(opts, log, out var flPath))
            return ExitCodes.FlNotFound;

        try
        {
            log.Info($"Hashing binaries under: {flPath} (run this ONLY on a build the team verified end-to-end)");
            var entry = FlIntegrity.GenerateEntry(flPath, note: $"verified {DateTime.Now:yyyy-MM-dd}");
            var list = new FlCompatibilityList { VerifiedBuilds = { entry } };
            log.Info(list.ToJson());
            log.Success($"FL {entry.Version}: {entry.Files.Count} binaries hashed.");
            return ExitCodes.Success;
        }
        catch (Exception ex)
        {
            log.Error("Hashing failed: " + ex.Message);
            return ExitCodes.Error;
        }
    }

    // -------------------------------------------------------------- helpers ----

    public static InstallManifest LoadManifest(CliOptions opts, IProgressLog log)
    {
        // Precedence: explicit --manifest, then manifest.json next to the EXE, then built-in default.
        var candidate = opts.ManifestPath;
        if (string.IsNullOrWhiteSpace(candidate))
        {
            var beside = Path.Combine(AppContext.BaseDirectory, "manifest.json");
            if (File.Exists(beside)) candidate = beside;
        }

        if (!string.IsNullOrWhiteSpace(candidate))
        {
            try
            {
                log.Info($"Using manifest: {candidate}");
                return InstallManifest.Load(candidate);
            }
            catch (Exception ex)
            {
                log.Warn($"Failed to load manifest '{candidate}' ({ex.Message}); using built-in default.");
            }
        }

        return BundledMcp.Select(InstallManifest.Default(),
            InstallerInfo.ResolvePayloadRoot(opts.PayloadRoot), include: !opts.WithoutMcp);
    }

    /// <summary>Resolves FL path: flag, else auto-detect, else default. Validates the result.</summary>
    public static bool TryResolveFlPath(CliOptions opts, IProgressLog log, out string flPath)
    {
        flPath = string.Empty;

        string? chosen = opts.FlPath;
        if (string.IsNullOrWhiteSpace(chosen))
        {
            chosen = FlStudioLocator.DetectBest();
            if (chosen is not null) log.Info($"Auto-detected FL Studio at: {chosen}");
        }
        chosen ??= FlStudioLocator.DefaultPath;

        var v = FlStudioLocator.Validate(chosen);
        if (!v.IsValid)
        {
            if (opts.DryRun || opts.Force)
            {
                log.Warn($"FL Studio not validated at '{chosen}' ({v.Reason}). Proceeding ({(opts.DryRun ? "dry-run" : "--force")}).");
                flPath = chosen.TrimEnd('"').Trim();
                return true;
            }
            log.Error($"FL Studio not found / invalid at '{chosen}': {v.Reason}");
            log.Info("Pass --fl-path \"<FL Studio dir>\" or install FL Studio first.");
            return false;
        }

        flPath = v.Path;
        return true;
    }

    /// <summary>
    /// Ensures the FL dir is writable. If not, the dir likely needs admin: in silent mode we fail
    /// with guidance (the GitHub script elevates); interactively we attempt a UAC self-elevation.
    /// </summary>
    private static bool EnsureWritable(
        string flPath, CliOptions opts, IProgressLog log, bool install, out int exitCode)
    {
        exitCode = ExitCodes.Success;
        var fs = new RealFileSystem();
        if (fs.IsDirectoryWritable(flPath))
            return true;

        log.Warn($"'{flPath}' is not writable by the current user (needs administrator).");

        if (opts.Silent || Elevation.IsAdministrator())
        {
            log.Error("Re-run elevated (right-click ▸ Run as administrator), or use install.ps1 which elevates for you.");
            exitCode = ExitCodes.NeedsElevation;
            return false;
        }

        log.Info("Requesting administrator rights (UAC)...");
        var verb = install ? "--install" : "--uninstall";
        var args = BuildElevatedArgs(opts, verb, flPath);
        if (Elevation.RelaunchAsAdmin(args))
        {
            log.Info("An elevated installer instance was started. This window can be closed.");
            exitCode = ExitCodes.Success;
            return false; // let the elevated instance do the work
        }

        log.Error("Elevation was declined. Cannot write to the FL Studio directory.");
        exitCode = ExitCodes.NeedsElevation;
        return false;
    }

    private static string[] BuildElevatedArgs(CliOptions opts, string verb, string flPath)
    {
        var args = new System.Collections.Generic.List<string> { verb, "--silent", "--fl-path", flPath };
        if (opts.DryRun) args.Add("--dry-run");
        if (opts.WithoutMcp) args.Add("--without-mcp");
        if (!string.IsNullOrWhiteSpace(opts.ManifestPath)) { args.Add("--manifest"); args.Add(opts.ManifestPath!); }
        if (!string.IsNullOrWhiteSpace(opts.PayloadRoot)) { args.Add("--payload-root"); args.Add(opts.PayloadRoot!); }
        return args.ToArray();
    }

    private static int Report(OperationResult result, IProgressLog log, bool install, string flPath)
    {
        var verb = install ? "Install" : "Uninstall";
        var isUninstall = !install;
        var tag = result.DryRun ? $"{verb} (dry-run)" : verb;
        var noun = isUninstall ? "removed" : "copied";

        switch (result.Outcome)
        {
            case OperationOutcome.Success:
                log.Success($"{tag} complete.");
                log.Info($"  Target:          {flPath}");
                log.Info($"  Files {noun}:    {result.FilesAffected}{(result.DryRun ? " (would)" : "")}");
                if (!result.DryRun && result.FlProcessesClosed > 0)
                    log.Info($"  FL closed:       {result.FlProcessesClosed} instance(s)");
                if (isUninstall && !result.DryRun)
                    log.Success(result.FilesAffected == 0
                        ? "  FL Studio is already stock (nothing to remove)."
                        : "  FL Studio restored to stock.");
                return ExitCodes.Success;

            case OperationOutcome.RebootPending:
                log.Warn($"{tag} INCOMPLETE — {result.RebootPending.Count} file(s) are locked and will be " +
                         (isUninstall ? "removed/restored" : "applied") + " on the next reboot:");
                foreach (var f in result.RebootPending) log.Warn($"    pending reboot: {f}");
                log.Warn("  Restart Windows to finish. (Files were locked — FL Studio was likely still running.)");
                return ExitCodes.RebootRequired;

            default: // Failed
                log.Error($"{tag} finished with problems: {result.Errors.Count} error(s), " +
                          $"{result.Leftover.Count} file(s) left behind.");
                foreach (var f in result.Leftover) log.Error($"    left behind: {f}");
                foreach (var f in result.RebootPending) log.Warn($"    pending reboot: {f}");
                if (isUninstall)
                    log.Error("  FL Studio was NOT fully restored to stock.");
                return ExitCodes.Error;
        }
    }
}
