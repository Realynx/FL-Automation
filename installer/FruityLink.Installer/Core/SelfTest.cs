using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace FruityLink.Installer.Core;

/// <summary>
/// End-to-end verification that does NOT touch the real FL Studio: it builds a throwaway fake FL
/// directory + payload under a temp folder, runs a real install then a real uninstall through the
/// engine (with <see cref="RealFileSystem"/>), and asserts FL is byte-for-byte restored. Exposed as
/// the <c>--self-test</c> CLI command and exercised by the unit tests too.
/// </summary>
public static class SelfTest
{
    public static bool Run(IProgressLog log, string installerVersion)
    {
        var root = Path.Combine(Path.GetTempPath(), "FruityLink.Installer.selftest-" + Guid.NewGuid().ToString("N"));
        var flDir = Path.Combine(root, "FL Studio 2025");
        var payloadDir = Path.Combine(root, "payload");
        var localAppRecord = InstallRecord.LocalAppDataRecordPath;

        var failures = new List<string>();
        var checks = 0;
        void Check(bool cond, string what)
        {
            checks++;
            if (cond) { log.Success("  PASS  " + what); }
            else { log.Error("  FAIL  " + what); failures.Add(what); }
        }

        try
        {
            log.Info($"self-test workspace: {root}");

            // --- Arrange: a fake FL dir with FL64.exe and a pre-existing (real) version.dll. ---
            Directory.CreateDirectory(flDir);
            File.WriteAllText(Path.Combine(flDir, "FL64.exe"), "FAKE-FL64");
            const string originalVersionDll = "WINDOWS-ORIGINAL-VERSION-DLL";
            File.WriteAllText(Path.Combine(flDir, "version.dll"), originalVersionDll);
            // A few unrelated FL files that must be left completely untouched.
            File.WriteAllText(Path.Combine(flDir, "FLEngine_x64.dll"), "FAKE-ENGINE");
            Directory.CreateDirectory(Path.Combine(flDir, "Shared"));
            File.WriteAllText(Path.Combine(flDir, "Shared", "keep.txt"), "KEEP-ME");

            var before = SnapshotDir(flDir);

            // --- Arrange: a fake payload matching the default manifest. ---
            // The proxy version.dll sits in the payload root; everything else lives under the
            // FruityLink\ payload sub-folder (copied to FL\FruityLink\) — mirrors bootstrap\dist\.
            Directory.CreateDirectory(payloadDir);
            File.WriteAllText(Path.Combine(payloadDir, "version.dll"), "FRUITYLINK-PROXY-VERSION-DLL");
            var managed = Path.Combine(payloadDir, "FruityLink");
            Directory.CreateDirectory(managed);
            File.WriteAllText(Path.Combine(managed, "FlBridge.dll"), "FRUITYLINK-NATIVE-BRIDGE");
            File.WriteAllText(Path.Combine(managed, "FlClrHost.dll"), "FRUITYLINK-CLR-HOST");
            File.WriteAllText(Path.Combine(managed, "Payload.Sample.dll"), "MANAGED-APP");
            File.WriteAllText(Path.Combine(managed, "Payload.Sample.runtimeconfig.json"), "{}");
            File.WriteAllText(Path.Combine(managed, "Payload.Sample.deps.json"), "{}");
            Directory.CreateDirectory(Path.Combine(managed, "plugins", "fl-agent"));
            File.WriteAllText(Path.Combine(managed, "plugins", "fl-agent", "plugin.json"), "{}");

            var fs = new RealFileSystem();
            var engine = new InstallEngine(fs, installerVersion);
            var manifest = InstallManifest.Default();

            // --- Act 1: install. ---
            log.Info("");
            log.Info("=== INSTALL ===");
            var plan = engine.PlanInstall(flDir, manifest, payloadDir, out var planErrors);
            foreach (var e in planErrors) log.Warn("plan note: " + e);
            var install = engine.ExecuteInstall(plan, flDir, dryRun: false, log);
            Check(install.Success, "install reported success");

            // --- Assert install state. ---
            Check(File.Exists(Path.Combine(flDir, "version.dll")), "proxy version.dll present");
            Check(File.ReadAllText(Path.Combine(flDir, "version.dll")) == "FRUITYLINK-PROXY-VERSION-DLL",
                "version.dll is our proxy");
            Check(File.Exists(Path.Combine(flDir, "version.dll.flbak")), "original backed up to version.dll.flbak");
            Check(File.Exists(Path.Combine(flDir, "version.dll.flbak")) &&
                  File.ReadAllText(Path.Combine(flDir, "version.dll.flbak")) == originalVersionDll,
                "backup holds the true original bytes");
            Check(File.Exists(Path.Combine(flDir, "FruityLink", "FlBridge.dll")), "native bridge present");
            Check(File.Exists(Path.Combine(flDir, "FruityLink", "FlClrHost.dll")), "CLR host present");
            Check(File.Exists(Path.Combine(flDir, "FruityLink", "Payload.Sample.dll")), "managed assembly present");
            Check(File.Exists(Path.Combine(flDir, "FruityLink", "plugins", "fl-agent", "plugin.json")),
                "nested managed file present");
            Check(File.Exists(Path.Combine(flDir, manifest.RecordFileName)), "install record written");

            // --- Act 2: uninstall. ---
            log.Info("");
            log.Info("=== UNINSTALL ===");
            var record = engine.TryLoadRecord(flDir, manifest);
            Check(record is not null, "install record reloads");
            var uplan = engine.PlanUninstall(flDir, manifest, record, log);
            var uninstall = engine.ExecuteUninstall(uplan, dryRun: false, log);
            Check(uninstall.Success, "uninstall reported success");

            // --- Assert restored state. ---
            Check(!File.Exists(Path.Combine(flDir, "version.dll.flbak")), "backup file removed after restore");
            Check(!File.Exists(Path.Combine(flDir, "FruityLink", "FlBridge.dll")), "native bridge removed");
            Check(!Directory.Exists(Path.Combine(flDir, "FruityLink")), "managed sub-folder removed");
            Check(!File.Exists(Path.Combine(flDir, manifest.RecordFileName)), "install record removed");
            Check(File.Exists(Path.Combine(flDir, "version.dll")), "version.dll exists again");
            Check(File.ReadAllText(Path.Combine(flDir, "version.dll")) == originalVersionDll,
                "version.dll restored to the original bytes");

            var after = SnapshotDir(flDir);
            Check(SnapshotsEqual(before, after, out var diff),
                "FL directory is byte-for-byte identical to before install" + (diff is null ? "" : $" ({diff})"));

            log.Info("");
            if (failures.Count == 0)
                log.Success($"SELF-TEST PASSED ({checks} checks).");
            else
                log.Error($"SELF-TEST FAILED: {failures.Count}/{checks} check(s) failed.");

            return failures.Count == 0;
        }
        catch (Exception ex)
        {
            log.Error("SELF-TEST CRASHED: " + ex);
            return false;
        }
        finally
        {
            TryDelete(root);
            // The self-test mirrors a record into LocalAppData; clean it so we leave nothing behind.
            try { if (File.Exists(localAppRecord)) File.Delete(localAppRecord); } catch { /* ignore */ }
        }
    }

    private static Dictionary<string, string> SnapshotDir(string dir)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(dir, f);
            map[rel] = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(f)));
        }
        return map;
    }

    private static bool SnapshotsEqual(
        Dictionary<string, string> a, Dictionary<string, string> b, out string? diff)
    {
        diff = null;
        var onlyInA = a.Keys.Except(b.Keys, StringComparer.OrdinalIgnoreCase).ToList();
        var onlyInB = b.Keys.Except(a.Keys, StringComparer.OrdinalIgnoreCase).ToList();
        if (onlyInA.Count > 0) { diff = "missing now: " + string.Join(", ", onlyInA); return false; }
        if (onlyInB.Count > 0) { diff = "leftover: " + string.Join(", ", onlyInB); return false; }
        foreach (var k in a.Keys)
            if (a[k] != b[k]) { diff = "changed: " + k; return false; }
        return true;
    }

    private static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch { /* temp; OS will reclaim */ }
    }
}
