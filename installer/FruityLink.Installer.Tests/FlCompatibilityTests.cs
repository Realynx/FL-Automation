using System.IO;
using FruityLink.Installer.Core;
using Shouldly;
using Xunit;

namespace FruityLink.Installer.Tests;

/// <summary>
/// The verified-build + integrity gate (Core/FlCompatibility.cs): generation excludes our own
/// footprint, verification catches tampered/missing binaries, the packed list round-trips, and a
/// missing list fails CLOSED. Uses throwaway temp dirs with fake binaries (hash logic doesn't care
/// that they aren't real PE files); version parsing of a real FL64.exe is covered by the CLI paths.
/// </summary>
public sealed class FlCompatibilityTests : System.IDisposable
{
    private readonly string _flDir;

    public FlCompatibilityTests()
    {
        _flDir = Path.Combine(Path.GetTempPath(), "fl-compat-test-" + Path.GetRandomFileName());
        Directory.CreateDirectory(Path.Combine(_flDir, "Plugins", "Fruity"));
        Directory.CreateDirectory(Path.Combine(_flDir, "FruityLink"));

        File.WriteAllText(Path.Combine(_flDir, "FL64.exe"), "fl shell");
        File.WriteAllText(Path.Combine(_flDir, "FLEngine_x64.dll"), "fl engine");
        File.WriteAllText(Path.Combine(_flDir, "Plugins", "Fruity", "Edison_x64.dll"), "edison");
        File.WriteAllText(Path.Combine(_flDir, "Readme.txt"), "not a binary");        // ignored (extension)
        File.WriteAllText(Path.Combine(_flDir, "version.dll"), "OUR proxy");          // exempt
        File.WriteAllText(Path.Combine(_flDir, "version.dll.flbak"), "backup");       // exempt
        File.WriteAllText(Path.Combine(_flDir, "FruityLink", "FlBridge.dll"), "ours"); // exempt (our dir)
    }

    public void Dispose()
    {
        try { Directory.Delete(_flDir, recursive: true); } catch { /* best effort */ }
    }

    private VerifiedFlBuild Generate() =>
        FlIntegrity.GenerateEntry(_flDir, note: "test", versionOverride: "25.9.9.9999");

    [Fact]
    public void GenerateEntry_HashesBinariesOnly_AndExcludesOurFootprint()
    {
        var entry = Generate();

        entry.Version.ShouldBe("25.9.9.9999");
        entry.Files.Keys.ShouldBe(new[]
        {
            "FL64.exe",
            "FLEngine_x64.dll",
            @"Plugins\Fruity\Edison_x64.dll",
        }, ignoreOrder: true);
    }

    [Fact]
    public void Verify_CleanInstall_ReportsNoProblems()
    {
        var entry = Generate();

        FlIntegrity.Verify(_flDir, entry).ShouldBeEmpty();
    }

    [Fact]
    public void Verify_IgnoresExtraFilesUsersAdded()
    {
        var entry = Generate();
        File.WriteAllText(Path.Combine(_flDir, "UserAdded_x64.dll"), "third-party plugin");

        FlIntegrity.Verify(_flDir, entry).ShouldBeEmpty();
    }

    [Fact]
    public void Verify_TamperedBinary_IsReportedAsModified()
    {
        var entry = Generate();
        File.WriteAllText(Path.Combine(_flDir, "FLEngine_x64.dll"), "cracked engine");

        var problems = FlIntegrity.Verify(_flDir, entry);

        problems.ShouldHaveSingleItem().ShouldBe("modified: FLEngine_x64.dll");
    }

    [Fact]
    public void Verify_DeletedBinary_IsReportedAsMissing()
    {
        var entry = Generate();
        File.Delete(Path.Combine(_flDir, @"Plugins\Fruity\Edison_x64.dll"));

        var problems = FlIntegrity.Verify(_flDir, entry);

        problems.ShouldHaveSingleItem().ShouldBe(@"missing: Plugins\Fruity\Edison_x64.dll");
    }

    [Fact]
    public void Verify_ChangingOurOwnFiles_NeverFlags()
    {
        var entry = Generate();
        // Re-install scenario: our proxy replaced version.dll and our payload dir changed.
        File.WriteAllText(Path.Combine(_flDir, "version.dll"), "NEWER proxy build");
        File.WriteAllText(Path.Combine(_flDir, "FruityLink", "FlBridge.dll"), "newer bridge");

        FlIntegrity.Verify(_flDir, entry).ShouldBeEmpty();
    }

    [Fact]
    public void CompatibilityList_RoundTripsThroughJson()
    {
        var list = new FlCompatibilityList { VerifiedBuilds = { Generate() } };

        var reloaded = FlCompatibilityList.FromJson(list.ToJson());

        reloaded.SchemaVersion.ShouldBe(1);
        reloaded.HashAlgorithm.ShouldBe("SHA-256");
        var build = reloaded.FindByVersion("25.9.9.9999").ShouldNotBeNull();
        build.Files.Count.ShouldBe(3);
        build.Files["fl64.exe"].ShouldBe(list.VerifiedBuilds[0].Files["FL64.exe"]); // case-insensitive keys
    }

    [Fact]
    public void FindByVersion_IsExactMatchOnly()
    {
        var list = new FlCompatibilityList { VerifiedBuilds = { Generate() } };

        list.FindByVersion("25.9.9.9999").ShouldNotBeNull();
        list.FindByVersion("25.9.9").ShouldBeNull();      // no prefix/range matching, by design
        list.FindByVersion("25.9.9.10000").ShouldBeNull();
    }

    [Fact]
    public void Check_MissingCompatibilityList_FailsClosed_WhenEnforced()
    {
        var result = FlIntegrity.Check(
            _flDir, listPath: Path.Combine(_flDir, "no-such-list.json"), enforce: true);

        result.Ok.ShouldBeFalse();
        result.BlockReason.ShouldNotBeNull();
        result.BlockReason.ShouldContain("compatibility list not found");
    }

    [Fact]
    public void Check_UnreadableList_FailsClosed_WhenEnforced()
    {
        var listPath = Path.Combine(_flDir, "corrupt.json");
        File.WriteAllText(listPath, "{ not json !!");

        var result = FlIntegrity.Check(_flDir, listPath, enforce: true);

        result.Ok.ShouldBeFalse();
        result.BlockReason.ShouldNotBeNull();
        result.BlockReason.ShouldContain("unreadable");
    }

    // --- gate DISABLED (launch mode): every would-be block becomes a non-blocking bypass, but the
    //     reason still rides along so the installer log can record what would have been refused. ---

    [Fact]
    public void Check_GateDisabled_MissingList_BypassesInsteadOfBlocking()
    {
        var result = FlIntegrity.Check(
            _flDir, listPath: Path.Combine(_flDir, "no-such-list.json"), enforce: false);

        result.Ok.ShouldBeTrue();                 // install proceeds
        result.GateBypassed.ShouldBeTrue();
        result.BlockReason.ShouldNotBeNull();     // ...but we know what it would have blocked
        result.BlockReason.ShouldContain("compatibility list not found");
    }

    [Fact]
    public void Check_GateDisabled_SameRefusalIsAlsoBypassed_WhenVersionUnreadable()
    {
        // Present a valid (empty) list so the check gets past the list load and reaches the FL-version
        // read, which the fake FL64.exe can't satisfy — enforced would Block there, disabled Bypasses.
        var listPath = Path.Combine(_flDir, FlCompatibilityList.FileName);
        File.WriteAllText(listPath, new FlCompatibilityList().ToJson());

        var enforced = FlIntegrity.Check(_flDir, listPath, enforce: true);
        enforced.Ok.ShouldBeFalse();

        var disabled = FlIntegrity.Check(_flDir, listPath, enforce: false);
        disabled.Ok.ShouldBeTrue();               // install proceeds despite the same condition
        disabled.GateBypassed.ShouldBeTrue();
        disabled.BlockReason.ShouldBe(enforced.BlockReason);   // identical reason, just non-blocking
    }
}
