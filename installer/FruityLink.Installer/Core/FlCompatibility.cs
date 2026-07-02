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

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOpts);

    public static FlCompatibilityList FromJson(string json) =>
        JsonSerializer.Deserialize<FlCompatibilityList>(json, JsonOpts)
        ?? throw new InvalidDataException("Compatibility JSON deserialized to null.");

    public static FlCompatibilityList Load(string path) => FromJson(File.ReadAllText(path));

    /// <summary>Exact-version lookup (ordinal; a build is verified or it isn't — no ranges).</summary>
    public VerifiedFlBuild? FindByVersion(string version) =>
        VerifiedBuilds.FirstOrDefault(b => string.Equals(b.Version, version, StringComparison.Ordinal));
}

/// <summary>Outcome of the pre-install compatibility + integrity check.</summary>
public sealed record CompatCheckResult(
    bool Ok,
    string? FlVersion,
    string? BlockReason,
    IReadOnlyList<string> Problems)
{
    public static CompatCheckResult Pass(string version) =>
        new(true, version, null, Array.Empty<string>());

    public static CompatCheckResult Block(string? version, string reason, IReadOnlyList<string>? problems = null) =>
        new(false, version, reason, problems ?? Array.Empty<string>());
}

/// <summary>
/// Reads the FL build version, generates verified-build entries, and verifies an FL directory
/// against one. Binary scope: *.exe / *.dll recursively — that is the surface piracy cracks patch;
/// content files (samples/presets/projects) are user-mutable and would only false-positive.
/// </summary>
public static class FlIntegrity
{
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
    /// </summary>
    public static CompatCheckResult Check(string flDir, string? listPath = null)
    {
        FlCompatibilityList list;
        var path = listPath ?? FlCompatibilityList.DefaultPath;
        try
        {
            if (!File.Exists(path))
                return CompatCheckResult.Block(null,
                    $"compatibility list not found ({path}) — this installer package is incomplete.");
            list = FlCompatibilityList.Load(path);
        }
        catch (Exception ex)
        {
            return CompatCheckResult.Block(null, $"compatibility list unreadable: {ex.Message}");
        }

        var version = GetFlVersion(flDir);
        if (version is null)
            return CompatCheckResult.Block(null,
                $"could not read the FL Studio version from {FlStudioLocator.ExeName}.");

        var build = list.FindByVersion(version);
        if (build is null)
        {
            var known = string.Join(", ", list.VerifiedBuilds.Select(b => b.Version));
            return CompatCheckResult.Block(version,
                $"FL Studio {version} has not been verified with FL Automate yet. " +
                $"Verified build(s): {(known.Length > 0 ? known : "none")}. " +
                "An update is usually verified within days of an FL release — please wait for it rather than risk an unstable install.");
        }

        var problems = Verify(flDir, build);
        if (problems.Count > 0)
            return CompatCheckResult.Block(version,
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
