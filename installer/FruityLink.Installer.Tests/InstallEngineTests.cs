using System.IO;
using System.Linq;
using FruityLink.Installer.Core;
using Shouldly;
using Xunit;

namespace FruityLink.Installer.Tests;

/// <summary>
/// Core install/uninstall logic, driven through the in-memory file system so backup/restore is
/// asserted deterministically with no disk I/O. The on-disk path is covered separately by
/// <see cref="SelfTest"/> (also asserted here via <see cref="SelfTest_RoundTrips"/>).
/// </summary>
public class InstallEngineTests
{
    private const string FlPath = @"C:\Program Files\Image-Line\FL Studio 2025";
    private const string PayloadRoot = @"C:\payload";

    private sealed class NullLog : IProgressLog
    {
        public void Log(LogLevel level, string message) { }
    }

    /// <summary>Records how many times the engine asked FL Studio to close (and reports a count).</summary>
    private sealed class FakeProcessManager : IProcessManager
    {
        private readonly int _closed;
        public FakeProcessManager(int closed = 0) => _closed = closed;
        public int Calls { get; private set; }
        public FlCloseResult CloseFlStudio(IProgressLog log)
        {
            Calls++;
            return new FlCloseResult { ClosedCount = _closed, ExitedCount = _closed };
        }
    }

    // Fast settings for the locked-file retry path: a couple of retries, no sleeping.
    private static InstallEngine NewEngine(InMemoryFileSystem fs, IProcessManager? procs = null) =>
        new(fs, "test", procs, deleteRetries: 2, deleteRetryDelayMs: 0);

    private static InMemoryFileSystem MakeFlAndPayload(out InstallManifest manifest)
    {
        var fs = new InMemoryFileSystem();
        // Fake FL dir with FL64.exe and an existing original version.dll.
        fs.WriteAllText(Path.Combine(FlPath, "FL64.exe"), "FL");
        fs.WriteAllText(Path.Combine(FlPath, "version.dll"), "ORIGINAL");

        // Payload matching the default manifest: proxy version.dll in the payload root, everything
        // else under the FruityLink\ payload dir (copied to FL\FruityLink\).
        fs.WriteAllText(Path.Combine(PayloadRoot, "version.dll"), "PROXY");
        fs.WriteAllText(Path.Combine(PayloadRoot, "FruityLink", "FlBridge.dll"), "BRIDGE");
        fs.WriteAllText(Path.Combine(PayloadRoot, "FruityLink", "FlClrHost.dll"), "HOST");
        fs.WriteAllText(Path.Combine(PayloadRoot, "FruityLink", "Payload.Sample.dll"), "APP");
        fs.WriteAllText(Path.Combine(PayloadRoot, "FruityLink", "Payload.Sample.runtimeconfig.json"), "{}");

        manifest = InstallManifest.Default();
        return fs;
    }

    /// <summary>The installed path of one of our in-process DLLs (FL\FruityLink\&lt;name&gt;).</summary>
    private static string Installed(string name) => Path.Combine(FlPath, "FruityLink", name);

    [Fact]
    public void Install_BacksUpOriginal_AndCopiesPayload()
    {
        var fs = MakeFlAndPayload(out var manifest);
        var engine = new InstallEngine(fs, "test");

        var plan = engine.PlanInstall(FlPath, manifest, PayloadRoot, out var errors);
        errors.ShouldBeEmpty();

        var result = engine.ExecuteInstall(plan, FlPath, dryRun: false, new NullLog());
        result.Success.ShouldBeTrue();

        fs.ReadAllText(Path.Combine(FlPath, "version.dll")).ShouldBe("PROXY");
        fs.FileExists(Path.Combine(FlPath, "version.dll.flbak")).ShouldBeTrue();
        fs.ReadAllText(Path.Combine(FlPath, "version.dll.flbak")).ShouldBe("ORIGINAL");
        fs.FileExists(Installed("FlBridge.dll")).ShouldBeTrue();
        fs.FileExists(Installed("Payload.Sample.dll")).ShouldBeTrue();
        fs.FileExists(Path.Combine(FlPath, manifest.RecordFileName)).ShouldBeTrue();
    }

    [Fact]
    public void Uninstall_RestoresOriginal_AndRemovesEverything()
    {
        var fs = MakeFlAndPayload(out var manifest);
        var engine = new InstallEngine(fs, "test");

        var installPlan = engine.PlanInstall(FlPath, manifest, PayloadRoot, out _);
        engine.ExecuteInstall(installPlan, FlPath, dryRun: false, new NullLog());

        var record = engine.TryLoadRecord(FlPath, manifest);
        record.ShouldNotBeNull();

        var uninstallPlan = engine.PlanUninstall(FlPath, manifest, record, new NullLog());
        var result = engine.ExecuteUninstall(uninstallPlan, dryRun: false, new NullLog());
        result.Success.ShouldBeTrue();

        // Original restored, our files gone.
        fs.ReadAllText(Path.Combine(FlPath, "version.dll")).ShouldBe("ORIGINAL");
        fs.FileExists(Path.Combine(FlPath, "version.dll.flbak")).ShouldBeFalse();
        fs.FileExists(Installed("FlBridge.dll")).ShouldBeFalse();
        fs.DirectoryExists(Path.Combine(FlPath, "FruityLink")).ShouldBeFalse();
        fs.FileExists(Path.Combine(FlPath, manifest.RecordFileName)).ShouldBeFalse();
        // FL's own file untouched.
        fs.FileExists(Path.Combine(FlPath, "FL64.exe")).ShouldBeTrue();
    }

    [Fact]
    public void Reinstall_PreservesTrueOriginalBackup()
    {
        var fs = MakeFlAndPayload(out var manifest);
        var engine = new InstallEngine(fs, "test");

        // First install.
        var plan1 = engine.PlanInstall(FlPath, manifest, PayloadRoot, out _);
        engine.ExecuteInstall(plan1, FlPath, dryRun: false, new NullLog());

        // Second install without uninstalling: the .flbak must still hold the TRUE original,
        // not our proxy, and uninstall must still restore "ORIGINAL".
        var plan2 = engine.PlanInstall(FlPath, manifest, PayloadRoot, out _);
        var r2 = engine.ExecuteInstall(plan2, FlPath, dryRun: false, new NullLog());
        r2.Success.ShouldBeTrue();
        fs.ReadAllText(Path.Combine(FlPath, "version.dll.flbak")).ShouldBe("ORIGINAL");
        r2.Record!.Backups.ShouldContain(b => b.OriginalPath.EndsWith("version.dll"));

        var record = engine.TryLoadRecord(FlPath, manifest);
        var uplan = engine.PlanUninstall(FlPath, manifest, record, new NullLog());
        engine.ExecuteUninstall(uplan, dryRun: false, new NullLog());
        fs.ReadAllText(Path.Combine(FlPath, "version.dll")).ShouldBe("ORIGINAL");
    }

    [Fact]
    public void DryRun_WritesNothing()
    {
        var fs = MakeFlAndPayload(out var manifest);
        var engine = new InstallEngine(fs, "test");

        var plan = engine.PlanInstall(FlPath, manifest, PayloadRoot, out _);
        engine.ExecuteInstall(plan, FlPath, dryRun: true, new NullLog());

        fs.ReadAllText(Path.Combine(FlPath, "version.dll")).ShouldBe("ORIGINAL");
        fs.FileExists(Path.Combine(FlPath, "version.dll.flbak")).ShouldBeFalse();
        fs.FileExists(Installed("FlBridge.dll")).ShouldBeFalse();
        fs.FileExists(Path.Combine(FlPath, manifest.RecordFileName)).ShouldBeFalse();
    }

    [Fact]
    public void MissingRequiredPayload_IsReportedAsError()
    {
        var fs = new InMemoryFileSystem();
        fs.WriteAllText(Path.Combine(FlPath, "FL64.exe"), "FL");
        var manifest = InstallManifest.Default();
        manifest.Items.RemoveAll(i => i.Kind == PayloadKind.SystemCopy);
        var engine = new InstallEngine(fs, "test");

        engine.PlanInstall(FlPath, manifest, PayloadRoot, out var errors);
        errors.ShouldNotBeEmpty();
        errors.ShouldContain(e => e.Contains("version.dll"));
    }

    [Fact]
    public void Manifest_RoundTripsThroughJson()
    {
        var original = InstallManifest.Default();
        var copy = InstallManifest.FromJson(original.ToJson());

        copy.Items.Count.ShouldBe(original.Items.Count);
        copy.Items[0].Kind.ShouldBe(PayloadKind.Proxy);
        copy.Items[0].BackupExisting.ShouldBeTrue();
        copy.Items.ShouldContain(i => i.Kind == PayloadKind.ManagedDir && i.IsDirectory);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void InjectedMirror_IsUsedForInstallFallbackAndUninstall_WithoutTouchingUserRecord(bool useRecordedUninstall)
    {
        var fs = MakeFlAndPayload(out var manifest);
        const string userRecord = "EXISTING-USER-INSTALL-RECORD";
        const string isolatedMirror = @"C:\self-test-workspace\install-record-mirror.json";
        fs.WriteAllText(InstallRecord.LocalAppDataRecordPath, userRecord);
        var engine = new InstallEngine(fs, "test", mirrorRecordPath: isolatedMirror);

        var plan = engine.PlanInstall(FlPath, manifest, PayloadRoot, out var errors);
        errors.ShouldBeEmpty();
        engine.ExecuteInstall(plan, FlPath, dryRun: false, new NullLog()).Success.ShouldBeTrue();
        fs.ReadAllText(InstallRecord.LocalAppDataRecordPath).ShouldBe(userRecord);
        fs.FileExists(isolatedMirror).ShouldBeTrue();

        // Force the fallback load path: only the injected mirror contains this install's record.
        fs.DeleteFile(Path.Combine(FlPath, manifest.RecordFileName));
        var record = engine.TryLoadRecord(FlPath, manifest);
        record.ShouldNotBeNull();
        record.FlPath.ShouldBe(FlPath);
        var uninstall = engine.PlanUninstall(FlPath, manifest,
            useRecordedUninstall ? record : null, new NullLog());
        uninstall.ShouldContain(action => action.Target == isolatedMirror);
        uninstall.ShouldNotContain(action => action.Target == InstallRecord.LocalAppDataRecordPath);
        engine.ExecuteUninstall(uninstall, dryRun: false, new NullLog()).Success.ShouldBeTrue();

        fs.FileExists(isolatedMirror).ShouldBeFalse();
        fs.ReadAllText(InstallRecord.LocalAppDataRecordPath).ShouldBe(userRecord);
        fs.ReadAllText(Path.Combine(FlPath, "version.dll")).ShouldBe("ORIGINAL");
    }

    [Fact]
    public void InjectedMirror_DoesNotLoadTheUsersDefaultFallbackRecord()
    {
        var fs = MakeFlAndPayload(out var manifest);
        var userRecord = new InstallRecord { FlPath = @"C:\UsersInstallation\FL Studio 2025" }.ToJson();
        fs.WriteAllText(InstallRecord.LocalAppDataRecordPath, userRecord);
        var engine = new InstallEngine(fs, "test", mirrorRecordPath: @"C:\self-test-workspace\missing-mirror.json");

        engine.TryLoadRecord(FlPath, manifest).ShouldBeNull();

        fs.ReadAllText(InstallRecord.LocalAppDataRecordPath).ShouldBe(userRecord);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void Uninstall_LeavesAnotherFlInstallAndItsMirrorUntouched(bool keepPrimary, bool supplyForeignRecord)
    {
        const string otherFl = @"C:\Program Files\Image-Line\FL Studio 2026";
        var fs = MakeFlAndPayload(out var manifest);
        fs.WriteAllText(Path.Combine(otherFl, "FL64.exe"), "OTHER FL");
        fs.WriteAllText(Path.Combine(otherFl, "version.dll"), "OTHER ORIGINAL");
        var engine = new InstallEngine(fs, "test");
        InstallAt(engine, FlPath, manifest);
        InstallAt(engine, otherFl, manifest);
        var foreignRecord = engine.TryLoadRecord(otherFl, manifest);
        foreignRecord.ShouldNotBeNull();
        var otherFiles = fs.EnumerateFiles(otherFl, recursive: true).ToDictionary(path => path, fs.ReadAllText);
        var mirror = fs.ReadAllText(InstallRecord.LocalAppDataRecordPath);
        if (!keepPrimary) fs.DeleteFile(Path.Combine(FlPath, manifest.RecordFileName));

        var targetRecord = engine.TryLoadRecord(FlPath, manifest);
        (targetRecord is not null).ShouldBe(keepPrimary);
        var suppliedRecord = supplyForeignRecord ? foreignRecord : targetRecord;
        var plan = engine.PlanUninstall(FlPath, manifest, suppliedRecord, new NullLog());
        plan.ShouldNotContain(action => action.Target == InstallRecord.LocalAppDataRecordPath);
        engine.ExecuteUninstall(plan, dryRun: false, new NullLog()).Success.ShouldBeTrue();

        fs.ReadAllText(Path.Combine(FlPath, "version.dll")).ShouldBe("ORIGINAL");
        fs.FileExists(Installed("FlBridge.dll")).ShouldBeFalse();
        fs.ReadAllText(InstallRecord.LocalAppDataRecordPath).ShouldBe(mirror);
        foreach (var file in otherFiles) fs.ReadAllText(file.Key).ShouldBe(file.Value);
    }

    [Fact]
    public void PrimaryRecordForAnotherInstallation_IsIgnoredInFavorOfMatchingMirror()
    {
        var fs = MakeFlAndPayload(out var manifest);
        var matching = new InstallRecord { FlPath = FlPath, InstallerVersion = "correct" };
        var foreign = new InstallRecord { FlPath = @"C:\Other FL", InstallerVersion = "wrong" };
        fs.WriteAllText(Path.Combine(FlPath, manifest.RecordFileName), foreign.ToJson());
        fs.WriteAllText(InstallRecord.LocalAppDataRecordPath, matching.ToJson());

        new InstallEngine(fs, "test").TryLoadRecord(FlPath, manifest)!.InstallerVersion.ShouldBe("correct");
    }

    [Theory]
    [InlineData(@"c:\program files\image-line\fl studio 2025\")]
    [InlineData("C:/Program Files/Image-Line/FL Studio 2025/./")]
    public void RecordIdentity_NormalizesCaseAndDirectorySeparators(string recordedPath)
    {
        var fs = MakeFlAndPayload(out var manifest);
        fs.WriteAllText(InstallRecord.LocalAppDataRecordPath, new InstallRecord { FlPath = recordedPath }.ToJson());

        new InstallEngine(fs, "test").TryLoadRecord(FlPath, manifest).ShouldNotBeNull();
    }

    private static void InstallAt(InstallEngine engine, string flPath, InstallManifest manifest)
    {
        var plan = engine.PlanInstall(flPath, manifest, PayloadRoot, out var errors);
        errors.ShouldBeEmpty();
        engine.ExecuteInstall(plan, flPath, dryRun: false, new NullLog()).Success.ShouldBeTrue();
    }

    [Fact]
    public void SelfTest_RoundTrips()
    {
        SelfTest.Run(new NullLog(), "test").ShouldBeTrue();
    }

    // ---------------------------------------------- close-FL + locked-file handling ----

    [Fact]
    public void RealRun_ClosesFlStudio_ForBothInstallAndUninstall()
    {
        var fs = MakeFlAndPayload(out var manifest);
        var procs = new FakeProcessManager(closed: 2);
        var engine = NewEngine(fs, procs);

        var installPlan = engine.PlanInstall(FlPath, manifest, PayloadRoot, out _);
        var install = engine.ExecuteInstall(installPlan, FlPath, dryRun: false, new NullLog());
        procs.Calls.ShouldBe(1);                 // install closes FL too
        install.FlProcessesClosed.ShouldBe(2);

        var record = engine.TryLoadRecord(FlPath, manifest);
        var uplan = engine.PlanUninstall(FlPath, manifest, record, new NullLog());
        var uninstall = engine.ExecuteUninstall(uplan, dryRun: false, new NullLog());
        procs.Calls.ShouldBe(2);                 // uninstall closes FL
        uninstall.FlProcessesClosed.ShouldBe(2);
        uninstall.Success.ShouldBeTrue();
    }

    [Fact]
    public void DryRun_DoesNotCloseFlStudio()
    {
        var fs = MakeFlAndPayload(out var manifest);
        var procs = new FakeProcessManager(closed: 1);
        var engine = NewEngine(fs, procs);

        var plan = engine.PlanInstall(FlPath, manifest, PayloadRoot, out _);
        engine.ExecuteInstall(plan, FlPath, dryRun: true, new NullLog());

        procs.Calls.ShouldBe(0);
    }

    [Fact]
    public void Uninstall_LockedFile_IsScheduledForRebootAndReportedHonestly()
    {
        var fs = MakeFlAndPayload(out var manifest);
        var engine = NewEngine(fs, new FakeProcessManager(closed: 1));

        var installPlan = engine.PlanInstall(FlPath, manifest, PayloadRoot, out _);
        engine.ExecuteInstall(installPlan, FlPath, dryRun: false, new NullLog());

        // FlBridge.dll stays loaded/locked even after closing FL.
        var locked = Installed("FlBridge.dll");
        fs.Lock(locked);

        var record = engine.TryLoadRecord(FlPath, manifest);
        var uplan = engine.PlanUninstall(FlPath, manifest, record, new NullLog());
        var result = engine.ExecuteUninstall(uplan, dryRun: false, new NullLog());

        // Honest reporting: NOT a clean success, surfaced as reboot-pending (never silently "done").
        result.Outcome.ShouldBe(OperationOutcome.RebootPending);
        result.Success.ShouldBeFalse();
        result.RebootPending.ShouldContain(p => p.EndsWith("FlBridge.dll"));
        fs.RebootScheduledDeletes.ShouldContain(p => p.EndsWith("FlBridge.dll"));
        result.Leftover.ShouldBeEmpty();
    }

    [Fact]
    public void Uninstall_LockedVersionDll_SchedulesRebootRestore()
    {
        var fs = MakeFlAndPayload(out var manifest);
        var engine = NewEngine(fs, new FakeProcessManager(closed: 1));

        var installPlan = engine.PlanInstall(FlPath, manifest, PayloadRoot, out _);
        engine.ExecuteInstall(installPlan, FlPath, dryRun: false, new NullLog());

        // The proxy version.dll is the file that stays mapped for the whole FL session.
        fs.Lock(Path.Combine(FlPath, "version.dll"));

        var record = engine.TryLoadRecord(FlPath, manifest);
        var uplan = engine.PlanUninstall(FlPath, manifest, record, new NullLog());
        var result = engine.ExecuteUninstall(uplan, dryRun: false, new NullLog());

        result.Outcome.ShouldBe(OperationOutcome.RebootPending);
        result.RebootPending.ShouldContain(p => p.EndsWith("version.dll"));
        fs.RebootScheduledMoves.ShouldContain(m => m.EndsWith("version.dll"));
    }

    [Fact]
    public void Uninstall_LockedAndRebootSchedulingFails_ReportedAsLeftoverFailure()
    {
        var fs = MakeFlAndPayload(out var manifest);
        var engine = NewEngine(fs, new FakeProcessManager(closed: 1));

        var installPlan = engine.PlanInstall(FlPath, manifest, PayloadRoot, out _);
        engine.ExecuteInstall(installPlan, FlPath, dryRun: false, new NullLog());

        var locked = Installed("FlBridge.dll");
        fs.Lock(locked);
        fs.FailRebootScheduling = true;   // e.g. not elevated: even the reboot fallback fails

        var record = engine.TryLoadRecord(FlPath, manifest);
        var uplan = engine.PlanUninstall(FlPath, manifest, record, new NullLog());
        var result = engine.ExecuteUninstall(uplan, dryRun: false, new NullLog());

        // Never claim success with leftovers.
        result.Outcome.ShouldBe(OperationOutcome.Failed);
        result.Success.ShouldBeFalse();
        result.Leftover.ShouldContain(p => p.EndsWith("FlBridge.dll"));
        result.Errors.ShouldNotBeEmpty();
    }
}
