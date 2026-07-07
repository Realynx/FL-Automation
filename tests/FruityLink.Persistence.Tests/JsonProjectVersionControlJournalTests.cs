using FruityLink.Agent.Versioning;
using FruityLink.Core.Domain;
using FruityLink.Persistence;
using Shouldly;
using Xunit;

namespace FruityLink.Persistence.Tests;

/// <summary>
/// End-to-end coverage of the inverse-journal path in <see cref="JsonProjectVersionControl"/>: a granular
/// commit writes <c>{commitId}.ops.json</c>; undo/redo replay it through the registry (writing the OLD /
/// NEW value straight back, NOT reopening a project); and a commit with no/ non-invertible journal (or no
/// registry at all — the Phase-0 inert case) falls back to opening the <c>.flp</c>, exactly as before.
/// </summary>
public sealed class JsonProjectVersionControlJournalTests : TempStorageFixture
{
    private const string Session = "s1";

    [Fact]
    public void ProjectOpsFile_IsAlongsideTheFlp()
    {
        string dir = Paths.ProjectVersionDir(Session);
        Paths.ProjectOpsFile(Session, "abc").ShouldBe(System.IO.Path.Combine(dir, "abc.ops.json"));
    }

    [Fact]
    public async Task GranularCommit_WritesOpsJson_AndUndoRedoReplayInverseOps()
    {
        var fl = new RecordingFlControl { ChannelVolume = 6400 };
        var vc = new JsonProjectVersionControl(Paths, fl, InverseOps.CreateRegistry());
        await vc.OpenSessionAsync(Session);

        // Baseline commit (no journal), then a granular edit: channel 2 volume 10000 → 6400.
        await vc.CommitAsync("base", trigger: CommitTrigger.Initial);
        var edit = await vc.CommitAsync("vol", trigger: CommitTrigger.Auto, changes: new[] { ChannelVolume(2, old: 10000, @new: 6400) });
        edit.ShouldNotBeNull();

        System.IO.File.Exists(Paths.ProjectOpsFile(Session, edit!.Id)).ShouldBeTrue("ops.json written for a granular commit");

        // Undo → replay OLD (10000) via the setter; no .flp reopen.
        fl.Calls.Clear();
        var afterUndo = await vc.UndoAsync();
        afterUndo.ShouldNotBeNull();
        vc.Head!.Label.ShouldBe("base");
        fl.Calls.ShouldContain("SetChannelVolumeAsync(2,10000)");
        fl.Calls.ShouldNotContain(c => c.StartsWith("OpenProjectAsync"), "granular undo must NOT reopen a .flp");

        // Redo → replay NEW (6400) forward.
        fl.Calls.Clear();
        var afterRedo = await vc.RedoAsync();
        afterRedo.ShouldNotBeNull();
        vc.Head!.Label.ShouldBe("vol");
        fl.Calls.ShouldContain("SetChannelVolumeAsync(2,6400)");
        fl.Calls.ShouldNotContain(c => c.StartsWith("OpenProjectAsync"));
    }

    [Fact]
    public async Task CommitWithoutJournal_FallsBackToFlpOnUndo()
    {
        var fl = new RecordingFlControl();
        var vc = new JsonProjectVersionControl(Paths, fl, InverseOps.CreateRegistry());
        await vc.OpenSessionAsync(Session);

        var baseCommit = await vc.CommitAsync("base", trigger: CommitTrigger.Initial);
        await vc.CommitAsync("opaque edit", trigger: CommitTrigger.Auto);   // no changes ⇒ no ops.json

        fl.Calls.Clear();
        await vc.UndoAsync();

        // No journal ⇒ authoritative .flp reopen of the parent (today's behavior).
        fl.Calls.ShouldContain($"OpenProjectAsync({baseCommit!.FlpBackupPath})");
    }

    [Fact]
    public async Task NoRegistry_IsInert_EvenWhenChangesArePassed()
    {
        // Phase-0 inert: with no registry the ops.json is marked invertible:false, so undo uses the .flp —
        // behaviorally identical to before the journal existed.
        var fl = new RecordingFlControl();
        var vc = new JsonProjectVersionControl(Paths, fl, registry: null);
        await vc.OpenSessionAsync(Session);

        var baseCommit = await vc.CommitAsync("base", trigger: CommitTrigger.Initial);
        await vc.CommitAsync("vol", trigger: CommitTrigger.Auto, changes: new[] { ChannelVolume(2, old: 10000, @new: 6400) });

        fl.Calls.Clear();
        await vc.UndoAsync();

        fl.Calls.ShouldContain($"OpenProjectAsync({baseCommit!.FlpBackupPath})");
        fl.Calls.ShouldNotContain(c => c.StartsWith("SetChannelVolumeAsync"));
    }

    private static ChangeRecord ChannelVolume(int channel, int old, int @new) =>
        new(0, ChangeOpKind.SetScalar, InverseOps.ChannelVolume,
            JournalDict.Of("channel", channel), JournalDict.Of("value", old), JournalDict.Of("value", @new));
}
