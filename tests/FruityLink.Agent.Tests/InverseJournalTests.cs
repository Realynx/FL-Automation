using System.Text.Json;
using System.Text.Json.Serialization;
using FruityLink.Agent.Versioning;
using FruityLink.Core.Abstractions;
using FruityLink.Core.Domain;
using Shouldly;
using Xunit;

namespace FruityLink.Agent.Tests;

/// <summary>
/// Covers the inverse-journal engine + Phase-1 registry: the per-turn recorder, the on-disk shape's
/// JSON round-trip (values arrive back as <c>JsonElement</c>, exactly as undo reloads them), and the
/// undo-applies-old contract — an inverse op reads the OLD value from the bridge and, on apply, writes
/// it straight back. The recording <see cref="FakeNativeFlControl"/> is the oracle.
/// </summary>
public sealed class InverseJournalTests
{
    private static readonly JsonSerializerOptions Json = BuildJson();

    private static JsonSerializerOptions BuildJson()
    {
        // Mirrors FruityLink.Persistence.JsonDefaults so the round-trip matches the real on-disk format.
        var o = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
        };
        o.Converters.Add(new JsonStringEnumConverter());
        return o;
    }

    // ── ChangeJournal (per-turn recorder) ───────────────────────────────────────────────────────

    [Fact]
    public void Journal_AssignsSeqInAppendOrder_AndDrainClears()
    {
        var journal = new ChangeJournal();
        journal.HasChanges.ShouldBeFalse();

        journal.Append(Rec("channel_volume", JournalDict.Of("channel", 0)));
        journal.Append(Rec("channel_volume", JournalDict.Of("channel", 1)));
        journal.Append(Rec("channel_volume", JournalDict.Of("channel", 2)));
        journal.HasChanges.ShouldBeTrue();

        var drained = journal.Drain();
        drained.Select(r => r.Seq).ShouldBe(new[] { 0, 1, 2 });          // append order, re-stamped
        drained.Select(r => JournalValue.AsInt(r.Target["channel"])).ShouldBe(new[] { 0, 1, 2 });

        journal.HasChanges.ShouldBeFalse();                              // drain cleared the buffer
        journal.Drain().ShouldBeEmpty();
    }

    [Fact]
    public void Journal_Taint_IsResetByDrain()
    {
        var journal = new ChangeJournal();
        journal.IsTainted.ShouldBeFalse();
        journal.Taint();
        journal.IsTainted.ShouldBeTrue();
        journal.Drain();
        journal.IsTainted.ShouldBeFalse();                              // a fresh turn starts clean
    }

    // ── on-disk shape round-trips (values return as JsonElement, as undo reloads them) ───────────

    [Fact]
    public void CommitJournal_JsonRoundTrips_PreservingValues()
    {
        var ops = new List<ChangeRecord>
        {
            new(0, ChangeOpKind.SetScalar, "channel_volume",
                JournalDict.Of("channel", 2), JournalDict.Of("value", 10000), JournalDict.Of("value", 6400)),
            new(1, ChangeOpKind.SetScalar, "track_name",
                JournalDict.Of("track", 3), JournalDict.Of("value", "Old"), JournalDict.Of("value", "Drums")),
        };
        var journal = new CommitJournal(1, "abc123", DateTimeOffset.UtcNow, true, ops);

        string json = JsonSerializer.Serialize(journal, Json);
        var back = JsonSerializer.Deserialize<CommitJournal>(json, Json)!;

        back.SchemaVersion.ShouldBe(1);
        back.CommitId.ShouldBe("abc123");
        back.Invertible.ShouldBeTrue();
        back.Ops.Count.ShouldBe(2);
        back.Ops[0].Kind.ShouldBe(ChangeOpKind.SetScalar);
        back.Ops[0].Op.ShouldBe("channel_volume");
        // Values come back as JsonElement — the tolerant accessors must still read them.
        JournalValue.AsInt(back.Ops[0].Target["channel"]).ShouldBe(2);
        JournalValue.AsInt(back.Ops[0].Old!["value"]).ShouldBe(10000);
        JournalValue.AsInt(back.Ops[0].New!["value"]).ShouldBe(6400);
        JournalValue.AsString(back.Ops[1].Old!["value"]).ShouldBe("Old");
        JournalValue.AsString(back.Ops[1].New!["value"]).ShouldBe("Drums");
    }

    // ── registry: read-before-write + undo-applies-old ──────────────────────────────────────────

    [Fact]
    public async Task ChannelVolume_ReadsOld_ThenUndoAppliesIt()
    {
        var fl = new FakeNativeFlControl();
        var reg = InverseOps.CreateRegistry();
        reg.TryGet(InverseOps.ChannelVolume, out var op).ShouldBeTrue();

        // read-before: captures the OLD scalar off the bridge (fake canned 10000).
        var old = await op.ReadAsync(fl, JournalDict.Of("channel", 2));
        JournalValue.AsInt(old["value"]).ShouldBe(10000);
        fl.Calls.ShouldContain("GetChannelVolumeAsync(2)");

        // undo: applying OLD writes it straight back through the setter.
        fl.Calls.Clear();
        await op.ApplyAsync(fl, JournalDict.Of("channel", 2), JournalDict.Of("value", 9000));
        fl.Calls.ShouldBe(new[] { "SetChannelVolumeAsync(2,9000)" });
    }

    [Theory]
    [MemberData(nameof(ScalarApplyCases))]
    public async Task ScalarOp_Apply_WritesValueThroughTheSetter(
        string op, IReadOnlyDictionary<string, object?> target, IReadOnlyDictionary<string, object?> value, string expected)
    {
        var fl = new FakeNativeFlControl();
        var reg = InverseOps.CreateRegistry();
        reg.TryGet(op, out var inv).ShouldBeTrue();

        await inv.ApplyAsync(fl, target, value);

        fl.Calls.ShouldBe(new[] { expected });
    }

    public static TheoryData<string, IReadOnlyDictionary<string, object?>, IReadOnlyDictionary<string, object?>, string> ScalarApplyCases() => new()
    {
        { InverseOps.Tempo, JournalDict.Empty, JournalDict.Of("value", 140.0), "SetTempoAsync(140)" },
        { InverseOps.MasterPitch, JournalDict.Empty, JournalDict.Of("value", -300), "SetMasterPitchAsync(-300)" },
        { InverseOps.Shuffle, JournalDict.Empty, JournalDict.Of("value", 64), "SetShuffleAsync(64)" },
        { InverseOps.MixerVolume, JournalDict.Of("track", 5), JournalDict.Of("value", 8000), "SetMixerVolumeAsync(5,8000)" },
        { InverseOps.MixerPan, JournalDict.Of("track", 5), JournalDict.Of("value", 6400), "SetMixerPanAsync(5,6400)" },
        { InverseOps.ChannelPan, JournalDict.Of("channel", 1), JournalDict.Of("value", 4000), "SetChannelPanAsync(1,4000)" },
        { InverseOps.ChannelPitch, JournalDict.Of("channel", 1), JournalDict.Of("value", -1200), "SetChannelPitchAsync(1,-1200)" },
        { InverseOps.ChannelMuted, JournalDict.Of("channel", 1), JournalDict.Of("value", true), "SetChannelMutedAsync(1,True)" },
        { InverseOps.ChannelRoute, JournalDict.Of("channel", 1), JournalDict.Of("value", 3), "SetChannelFxRouteAsync(1,3)" },
        { InverseOps.TrackName, JournalDict.Of("track", 3), JournalDict.Of("value", "Verse"), "SetTrackNameAsync(3,Verse)" },
        { InverseOps.TrackColor, JournalDict.Of("track", 3), JournalDict.Of("value", 0xFF8800), "SetTrackColorAsync(3,16746496)" },
        { InverseOps.TrackMute, JournalDict.Of("track", 3), JournalDict.Of("value", true), "SetTrackMuteAsync(3,True)" },
        { InverseOps.TrackCollapsed, JournalDict.Of("track", 3), JournalDict.Of("value", false), "SetTrackCollapsedAsync(3,False)" },
        { InverseOps.SongMode, JournalDict.Empty, JournalDict.Of("value", true), "SetSongModeAsync(True)" },
        { InverseOps.ArrangementName, JournalDict.Of("arrangement", 1), JournalDict.Of("value", "B"), "RenameArrangementAsync(1,B)" },
    };

    [Fact]
    public async Task ScalarApply_ReadsValuesReloadedFromDisk_AsJsonElement()
    {
        // The apply path runs on records reloaded from ops.json (JsonElement values), not raw CLR — prove it.
        var record = new ChangeRecord(0, ChangeOpKind.SetScalar, InverseOps.ChannelVolume,
            JournalDict.Of("channel", 4), JournalDict.Of("value", 10000), JournalDict.Of("value", 6400));
        string json = JsonSerializer.Serialize(record, Json);
        var reloaded = JsonSerializer.Deserialize<ChangeRecord>(json, Json)!;

        var fl = new FakeNativeFlControl();
        var reg = InverseOps.CreateRegistry();
        reg.TryGet(reloaded.Op, out var inv).ShouldBeTrue();

        await inv.ApplyAsync(fl, reloaded.Target, reloaded.Old);         // undo = apply OLD
        fl.Calls.ShouldBe(new[] { "SetChannelVolumeAsync(4,10000)" });
    }

    // ── clips addressed by stable identity (resolved to a live slot at replay) ───────────────────

    [Fact]
    public async Task ClipMove_LocatesByIdentity_ThenMovesToDestination()
    {
        var fl = new FakeNativeFlControl();   // ListClips canned: [0] pattern 1 @track1 start0; [1] pattern 2 @track2 start768
        var reg = InverseOps.CreateRegistry();
        reg.TryGet(InverseOps.ClipMove, out var op).ShouldBeTrue();

        // Move the clip currently at (pattern 1, start 0, track 1) → (start 1920, track 3).
        var value = JournalDict.Of("pattern", 1, "fromStart", 0, "fromTrack", 1, "toStart", 1920, "toTrack", 3);
        await op.ApplyAsync(fl, JournalDict.Of("pattern", 1, "start", 0, "track", 1), value);

        fl.Calls.ShouldContain("ListClipsAsync(0,-1)");                  // resolved identity → slot
        fl.Calls.ShouldContain("MoveClipAsync(0,1920,3)");              // slot 0 moved
    }

    [Fact]
    public async Task ClipResize_LocatesByIdentity_ThenResizes()
    {
        var fl = new FakeNativeFlControl();
        var reg = InverseOps.CreateRegistry();
        reg.TryGet(InverseOps.ClipResize, out var op).ShouldBeTrue();

        var value = JournalDict.Of("pattern", 2, "start", 768, "track", 2, "length", 1920);
        await op.ApplyAsync(fl, JournalDict.Of("pattern", 2, "start", 768, "track", 2), value);

        fl.Calls.ShouldContain("ResizeClipAsync(1,1920)");             // slot 1 (the pattern-2 clip)
    }

    [Fact]
    public async Task ClipMove_UnlocatableIdentity_Throws_SoCallerFallsBackToFlp()
    {
        var fl = new FakeNativeFlControl();
        var reg = InverseOps.CreateRegistry();
        reg.TryGet(InverseOps.ClipMove, out var op).ShouldBeTrue();

        // No clip matches (pattern 9) → apply must throw so the VC opens the .flp instead of half-inverting.
        var value = JournalDict.Of("pattern", 9, "fromStart", 0, "fromTrack", 1, "toStart", 100, "toTrack", 1);
        await Should.ThrowAsync<InvalidOperationException>(
            op.ApplyAsync(fl, JournalDict.Of("pattern", 9), value));
    }

    // ── registry taint contract ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Registry_CanInvert_TrueForRegistered_FalseForUnknown()
    {
        var reg = InverseOps.CreateRegistry();
        reg.CanInvert(Rec(InverseOps.ChannelVolume, JournalDict.Of("channel", 0))).ShouldBeTrue();
        // A not-yet-invertible op (e.g. add_note) is unregistered ⇒ its commit taints to the .flp.
        reg.CanInvert(Rec("add_note", JournalDict.Empty)).ShouldBeFalse();
    }

    private static ChangeRecord Rec(string op, IReadOnlyDictionary<string, object?> target) =>
        new(0, ChangeOpKind.SetScalar, op, target, JournalDict.Of("value", 0), JournalDict.Of("value", 0));
}
