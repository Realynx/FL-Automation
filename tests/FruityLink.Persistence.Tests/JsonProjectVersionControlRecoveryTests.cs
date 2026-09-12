using System.Linq;
using FruityLink.Agent.Versioning;
using FruityLink.Core.Domain;
using FruityLink.Persistence;
using Shouldly;
using Xunit;

namespace FruityLink.Persistence.Tests;

/// <summary>
/// Crash-recovery detection in <see cref="JsonProjectVersionControl.OpenSessionAsync"/>. The default FL
/// template loads UNTITLED and a fresh session auto-creates a "Session start" baseline, so the old
/// <c>if (untitled) recoverable = true</c> offered that baseline as a recovery candidate on EVERY normal
/// launch ("crash recovery available" after a clean close). Recovery must only surface for a session that
/// already had real history on disk before this run.
/// </summary>
public sealed class JsonProjectVersionControlRecoveryTests : TempStorageFixture
{
    private const string Session = "s1";

    [Fact]
    public async Task FreshSession_UntitledProject_DoesNotOfferRecovery()
    {
        // RecordingFlControl.GetProjectInfoAsync → "Untitled (never saved)"; a fresh session auto-commits
        // the "Session start" baseline. That baseline is NOT crash-recovery.
        var fl = new RecordingFlControl();
        var vc = new JsonProjectVersionControl(Paths, fl, InverseOps.CreateRegistry());

        await vc.OpenSessionAsync(Session);

        vc.History.ShouldNotBeEmpty();                                            // baseline was created
        vc.History.All(c => c.Trigger == CommitTrigger.Initial).ShouldBeTrue();   // only the baseline
        vc.RecoveryCandidate.ShouldBeNull("a fresh session's own baseline is not crash recovery");
    }

    [Fact]
    public async Task PersistedSession_WithUnsavedEdit_OffersRecoveryOnReopen()
    {
        // A prior run committed a real (non-baseline) edit that was never persisted. Reopening the SAME
        // session — which now has history on disk — must still surface that edit as recoverable, so the fix
        // suppresses only the false positive, not genuine cross-run recovery.
        var fl = new RecordingFlControl();
        var first = new JsonProjectVersionControl(Paths, fl, InverseOps.CreateRegistry());
        await first.OpenSessionAsync(Session);
        await first.CommitAsync("ai edit", trigger: CommitTrigger.Auto);   // real unsaved AI work

        var reopened = new JsonProjectVersionControl(Paths, fl, InverseOps.CreateRegistry());
        await reopened.OpenSessionAsync(Session);

        reopened.RecoveryCandidate.ShouldNotBeNull("an existing session's real unsaved edit is recoverable");
        reopened.RecoveryCandidate!.Label.ShouldBe("ai edit");
    }
}
