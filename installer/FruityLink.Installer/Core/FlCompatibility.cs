using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FruityLink.Installer.Core;

/// <summary>
/// One manually verified FL Studio build: its exact 4-part version (FL64.exe FileVersion) plus the
/// SHA-256 of every binary (*.exe / *.dll) in the FL directory, keyed by FL-root-relative path.
/// Entries are produced by <c>--print-fl-hashes</c> on a machine where the team has confirmed the
/// full FruityLink stack works, then pasted into <c>compatibility.json</c>.
/// </summary>
public sealed class VerifiedFlBuild
{
    /// <summary>Exact FL64.exe FileVersion, e.g. "25.2.5.5319".</summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>Free-form provenance note ("verified 2026-07-02, bridge r123").</summary>
    public string? Note { get; set; }

    /// <summary>FL-root-relative path ('\' separators) → lowercase SHA-256 hex. Populate (not
    /// replace) on deserialize, so the case-insensitive comparer survives the JSON round-trip —
    /// Windows paths compare case-insensitively.</summary>
    [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
    public Dictionary<string, string> Files { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// The packed compatibility list (<c>compatibility.json</c> next to the installer EXE). Two gates,
/// both deliberate product decisions:
///  1. BUILD gate — FruityLink drives FL through reverse-engineered offsets, so only builds the team
///     has manually verified are installable. Unknown build = refuse (even though it might work):
///     an unstable install is worse for users and for the brand than a clear "not yet supported".
///  2. INTEGRITY gate — we are pro Image-Line: if FL's own binaries don't hash-match the verified
///     official build (cracked/patched/unofficial copies), we refuse to install. The one exception
///     is the file WE install over (<c>version.dll</c>) + our own FruityLink footprint, so
///     re-installing over a previous FruityLink install still passes.
/// Missing/corrupt compatibility.json fails CLOSED (packaging bug ≠ skip the gates).
/// </summary>
public sealed class FlCompatibilityList
{
    public int SchemaVersion { get; set; } = 1;

    /// <summary>Informational; the verifier always computes SHA-256.</summary>
    public string HashAlgorithm { get; set; } = "SHA-256";

    public List<VerifiedFlBuild> VerifiedBuilds { get; set; } = new();

    public const string FileName = "compatibility.json";

    /// <summary>The packed list next to the installer EXE.</summary>
    public static string DefaultPath => Path.Combine(AppContext.BaseDirectory, FileName);

    public string ToJson() => JsonSerializer.Serialize(this, InstallerJson.Default);

    public static FlCompatibilityList FromJson(string json) =>
        JsonSerializer.Deserialize<FlCompatibilityList>(json, InstallerJson.Default)
        ?? throw new InvalidDataException("Compatibility JSON deserialized to null.");

    public static FlCompatibilityList Load(string path) => FromJson(File.ReadAllText(path));

    /// <summary>Exact-version lookup (ordinal; a build is verified or it isn't — no ranges).</summary>
    public VerifiedFlBuild? FindByVersion(string version) =>
        VerifiedBuilds.FirstOrDefault(b => string.Equals(b.Version, version, StringComparison.Ordinal));
}

/// <summary>Outcome of the pre-install compatibility + integrity check.</summary>
/// <param name="GateBypassed">True when the check WOULD have blocked but the gate is currently
/// disabled (see <see cref="FlIntegrity.GateEnforced"/>), so <see cref="Ok"/> was forced true and
/// <see cref="BlockReason"/>/<see cref="Problems"/> describe what would have blocked — for logging
/// and future drip-release data, never to stop the install.</param>
public sealed record CompatCheckResult(
    bool Ok,
    string? FlVersion,
    string? BlockReason,
    IReadOnlyList<string> Problems,
    bool GateBypassed = false)
{
    public static CompatCheckResult Pass(string version) =>
        new(true, version, null, Array.Empty<string>());

    public static CompatCheckResult Block(string? version, string reason, IReadOnlyList<string>? problems = null) =>
        new(false, version, reason, problems ?? Array.Empty<string>());

    /// <summary>A would-be block downgraded to a non-blocking pass because the gate is disabled.
    /// Carries the reason/problems so callers can log what would have been refused.</summary>
    public static CompatCheckResult Bypass(string? version, string reason, IReadOnlyList<string>? problems = null) =>
        new(true, version, reason, problems ?? Array.Empty<string>(), GateBypassed: true);
}

/// <summary>
/// Reads the FL build version, generates verified-build entries, and verifies an FL directory
/// against one. Binary scope: *.exe / *.dll recursively — that is the surface piracy cracks patch;
/// content files (samples/presets/projects) are user-mutable and would only false-positive.
/// </summary>
public static class FlIntegrity
{
    /// <summary>
    /// MASTER SWITCH for the pre-install gates (build-verify AND SHA-256 integrity).
    /// <para><b>TEMPORARILY <c>false</c> for launch.</b> During onboarding a verified-build or
    /// hash-mismatch refusal is a worse look than letting an unusual install through, and it was
    /// causing intermittent "can't install" reports. While this is false, <see cref="Check"/> still
    /// runs the full evaluation and reports (via <see cref="CompatCheckResult.GateBypassed"/> + the
    /// installer log) what it WOULD have blocked — so we collect field data — but it never stops an
    /// install.</para>
    /// <para>Flip this ONE line back to <c>true</c> to re-arm anti-piracy + build-stability
    /// enforcement once a customer base is onboarded (the planned "drip release"), then ship a new
    /// installer. It is a compile-time const on purpose: an end user must not be able to toggle an
    /// anti-piracy gate at runtime.</para>
    /// </summary>
    public const bool GateEnforced = false;

    /// <summary>Extensions covered by the integrity hash list.</summary>
    private static readonly string[] BinaryExtensions = { ".exe", ".dll" };

    /// <summary>
    /// FL-root-relative paths exempt from hashing/verification: the proxy target we install over,
    /// its backup, our payload directory, and our install record.
    /// </summary>
    internal static bool IsExempt(string relativePath)
    {
        if (relativePath.Equals("version.dll", StringComparison.OrdinalIgnoreCase)) return true;
        if (relativePath.StartsWith("version.dll.", StringComparison.OrdinalIgnoreCase)) return true;   // .flbak etc.
        if (relativePath.StartsWith("FruityLink\\", StringComparison.OrdinalIgnoreCase)) return true;
        if (relativePath.Equals("FruityLink.install.json", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>FL64.exe's 4-part FileVersion in <paramref name="flDir"/>, or null if unreadable.</summary>
    public static string? GetFlVersion(string flDir)
    {
        try
        {
            var exe = Path.Combine(flDir, FlStudioLocator.ExeName);
            if (!File.Exists(exe)) return null;
            var info = FileVersionInfo.GetVersionInfo(exe);
            return string.IsNullOrWhiteSpace(info.FileVersion) ? null : info.FileVersion!.Trim();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Hashes every non-exempt binary under <paramref name="flDir"/> into a verified-build entry.
    /// Run this ONLY on an FL install the team has confirmed end-to-end (see --print-fl-hashes).
    /// </summary>
    public static VerifiedFlBuild GenerateEntry(string flDir, string? note = null, string? versionOverride = null)
    {
        var version = versionOverride
                      ?? GetFlVersion(flDir)
                      ?? throw new InvalidOperationException($"Cannot read {FlStudioLocator.ExeName} version in '{flDir}'.");

        var entry = new VerifiedFlBuild { Version = version, Note = note };
        foreach (var file in EnumerateBinaries(flDir).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            entry.Files[file] = HashFile(Path.Combine(flDir, file));
        return entry;
    }

    /// <summary>
    /// Verifies <paramref name="flDir"/> against a verified build's hash list. Returns problem lines
    /// (empty = clean). Extra files are allowed (users add plugins/content); listed binaries must
    /// exist and hash-match exactly.
    /// </summary>
    public static IReadOnlyList<string> Verify(string flDir, VerifiedFlBuild build)
    {
        var problems = new List<string>();
        foreach (var (relPath, expected) in build.Files.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (IsExempt(relPath)) continue;   // tolerate exempt entries in hand-edited lists
            var full = Path.Combine(flDir, relPath);
            if (!File.Exists(full))
            {
                problems.Add($"missing: {relPath}");
                continue;
            }
            var actual = HashFile(full);
            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                problems.Add($"modified: {relPath}");
        }
        return problems;
    }

    /// <summary>
    /// The full pre-install gate: load the packed list, match the FL build, verify the hashes.
    /// Fails CLOSED when the list is missing/corrupt — a broken package must not skip the gates.
    /// <para>When enforcement is off (<see cref="GateEnforced"/> false, overridable via
    /// <paramref name="enforce"/> for tests) every would-be block is downgraded to a non-blocking
    /// <see cref="CompatCheckResult.Bypass"/> that still carries the reason — the install proceeds
    /// but the log records what would have been refused.</para>
    /// </summary>
    public static CompatCheckResult Check(string flDir, string? listPath = null, bool? enforce = null)
    {
        bool enforced = enforce ?? GateEnforced;

        // When the gate is disabled, a would-be block becomes a Bypass (Ok=true) instead of a Block,
        // so a single site here controls both the CLI and GUI. The reason still rides along for the log.
        CompatCheckResult Refuse(string? version, string reason, IReadOnlyList<string>? problems = null) =>
            enforced
                ? CompatCheckResult.Block(version, reason, problems)
                : CompatCheckResult.Bypass(version, reason, problems);

        FlCompatibilityList list;
        var path = listPath ?? FlCompatibilityList.DefaultPath;
        try
        {
            if (!File.Exists(path))
                return Refuse(null,
                    $"compatibility list not found ({path}) — this installer package is incomplete.");
            list = FlCompatibilityList.Load(path);
        }
        catch (Exception ex)
        {
            return Refuse(null, $"compatibility list unreadable: {ex.Message}");
        }

        var version = GetFlVersion(flDir);
        if (version is null)
            return Refuse(null,
                $"could not read the FL Studio version from {FlStudioLocator.ExeName}.");

        var build = list.FindByVersion(version);
        if (build is null)
        {
            var known = string.Join(", ", list.VerifiedBuilds.Select(b => b.Version));
            return Refuse(version,
                $"FL Studio {version} has not been verified with FL Automate yet. " +
                $"Verified build(s): {(known.Length > 0 ? known : "none")}. " +
                "An update is usually verified within days of an FL release — please wait for it rather than risk an unstable install.");
        }

        var problems = Verify(flDir, build);
        if (problems.Count > 0)
            return Refuse(version,
                $"FL Studio {version} does not match the official verified build — " +
                $"{problems.Count} file(s) are modified or missing. FL Automate only installs on unmodified, " +
                "genuine FL Studio installs.", problems);

        return CompatCheckResult.Pass(version);
    }

    private static IEnumerable<string> EnumerateBinaries(string flDir)
    {
        var root = Path.GetFullPath(flDir);
        foreach (var full in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var ext = Path.GetExtension(full);
            if (!BinaryExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase)) continue;
            var rel = Path.GetRelativePath(root, full);
            if (IsExempt(rel)) continue;
            yield return rel;
        }
    }

    private static string HashFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}

/// <summary>
/// Front-end plumbing shared by the CLI and GUI pre-install gates: runs
/// <see cref="FlIntegrity.Check"/> and logs the shared lines (the verified-build success line, and
/// each per-file problem on failure). Callers decide what a failure means (abort, dry-run warning,
/// or the CLI's --force override) and log the block reason in their own words.
/// </summary>
public static class FlIntegrityGate
{
    public static CompatCheckResult CheckAndLog(string flPath, IProgressLog log)
    {
        var check = FlIntegrity.Check(flPath);
        if (check.Ok)
        {
            if (check.GateBypassed)
            {
                // Gate disabled for launch: proceed, but leave a clear trail of what it WOULD have
                // refused (each per-file problem too), so we can size the impact before re-arming.
                log.Warn("Compatibility/integrity checks are currently DISABLED — installing anyway.");
                log.Warn($"  would have blocked: {check.BlockReason}");
                foreach (var p in check.Problems)
                    log.Warn("    " + p);
            }
            else
            {
                log.Success($"FL Studio {check.FlVersion} is a verified build; all binaries match.");
            }
            return check;
        }

        foreach (var p in check.Problems)
            log.Error("  " + p);
        return check;
    }
}
