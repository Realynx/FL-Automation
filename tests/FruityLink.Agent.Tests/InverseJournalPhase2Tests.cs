using System.Text.Json;
using System.Text.Json.Serialization;
using FruityLink.Agent.Plugins;
using FruityLink.Agent.Versioning;
using FruityLink.Core.Abstractions;
using FruityLink.Core.Domain;
using Shouldly;
using Xunit;

namespace FruityLink.Agent.Tests;

/// <summary>
/// Phase-2 (create/delete inverse ops) coverage: each mutating tool routes through the capture seam, and
/// the drained journal — replayed through the registry exactly as <c>JsonProjectVersionControl</c> does
/// (undo = OLD reversed, redo = NEW forward) — restores state via FL's own setters, never a <c>.flp</c>
/// reopen. Also covers the taint/fallback paths (a change that can't be cleanly inverted marks the turn so
/// the whole commit falls back to its <c>.flp</c>). The model-backed <see cref="FakeNativeFlControl"/> is
/// the oracle: identity resolution + create/delete actually mutate its clip/automation state.
/// </summary>
public sealed class InverseJournalPhase2Tests
{
    private static readonly JsonSerializerOptions Json = BuildJson();

    private static JsonSerializerOptions BuildJson()
    {
        var o = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
        };
        o.Converters.Add(new JsonStringEnumConverter());
        return o;
    }

    private static (FakeNativeFlControl Fl, NativeControlPlugin Plugin, ChangeJournal Journal, InverseOpRegistry Reg) Wire()
    {
        var fl = new FakeNativeFlControl();
        var journal = new ChangeJournal();
        var reg = InverseOps.CreateRegistry();
        var plugin = new NativeControlPlugin(fl, new ChangeCapture(fl, journal, reg));
        return (fl, plugin, journal, reg);
    }

    // Replay a drained op list exactly as the version-control store does.
    private static async Task Undo(FakeNativeFlControl fl, IInverseOpRegistry reg, IReadOnlyList<ChangeRecord> ops)
    {
        foreach (var r in ops.OrderByDescending(o => o.Seq))
        { reg.TryGet(r.Op, out var inv).ShouldBeTrue(); await inv.ApplyAsync(fl, r.Target, r.Old); }
    }

    private static async Task Redo(FakeNativeFlControl fl, IInverseOpRegistry reg, IReadOnlyList<ChangeRecord> ops)
    {
        foreach (var r in ops.OrderBy(o => o.Seq))
        { reg.TryGet(r.Op, out var inv).ShouldBeTrue(); await inv.ApplyAsync(fl, r.Target, r.New); }
    }

    // ── clip mute (SetScalar, identity-addressed) ────────────────────────────────────────────────

    [Fact]
    public async Task MuteClips_CapturesPriorMute_UndoRestores_RedoReapplies()
    {
        var (fl, plugin, journal, reg) = Wire();   // slot0 = pattern1@track1 start0 (unmuted)

        await plugin.MuteClipsAsync("0", muted: true);
        (await fl.GetClipMutedAsync(0)).ShouldBeTrue();     // the mute actually happened

        var ops = journal.Drain();
        ops.Count.ShouldBe(1);
        ops[0].Kind.ShouldBe(ChangeOpKind.SetScalar);
        ops[0].Op.ShouldBe(InverseOps.ClipMute);
        JournalValue.AsInt(ops[0].Target["pattern"]).ShouldBe(1);   // identity, not the volatile slot
        JournalValue.AsBool(ops[0].Old!["value"]).ShouldBeFalse();  // was unmuted
        JournalValue.AsBool(ops[0].New!["value"]).ShouldBeTrue();

        await Undo(fl, reg, ops);
        (await fl.GetClipMutedAsync(0)).ShouldBeFalse();    // OLD (unmuted) restored via the setter

        await Redo(fl, reg, ops);
        (await fl.GetClipMutedAsync(0)).ShouldBeTrue();     // NEW (muted) re-applied
    }

    [Fact]
    public async Task MuteClips_AudioClip_TaintsTurn_ForFlpFallback()
    {
        var (fl, plugin, journal, _) = Wire();
        fl.SeedClip(track: 3, start: 0, length: 384, pattern: -1);   // slot2 = audio clip (no identity)

        await plugin.MuteClipsAsync("2", muted: true);

        journal.IsTainted.ShouldBeTrue();     // can't identity-address → whole-commit .flp fallback
        journal.Drain().ShouldBeEmpty();
    }

    // ── add_pattern_clips → Create ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task AddPatternClips_Create_UndoDeletesByIdentity_RedoReAdds()
    {
        var (fl, plugin, journal, reg) = Wire();

        await plugin.AddPatternClipsAsync("[{\"pattern\":3,\"track\":1,\"start\":1920,\"length\":0}]");
        (await fl.ListClipsAsync(0, -1)).ShouldContain("pattern 3");   // placed

        var ops = journal.Drain();
        ops.Count.ShouldBe(1);
        ops[0].Kind.ShouldBe(ChangeOpKind.Create);
        ops[0].Op.ShouldBe(InverseOps.PatternClip);
        ops[0].Old.ShouldBeNull();
        JournalValue.AsInt(ops[0].New!["pattern"]).ShouldBe(3);

        await Undo(fl, reg, ops);
        (await fl.ListClipsAsync(0, -1)).ShouldNotContain("pattern 3");   // deleted by identity

        await Redo(fl, reg, ops);
        (await fl.ListClipsAsync(0, -1)).ShouldContain("pattern 3");      // re-created from the spec
    }

    // ── delete_clips → Delete ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task DeleteClips_Delete_UndoReAddsFromSnapshot_RedoDeletesAgain()
    {
        var (fl, plugin, journal, reg) = Wire();   // slot0 = pattern1@track1 start0 len384

        await plugin.DeleteClipsAsync("0");
        (await fl.ListClipsAsync(0, -1)).ShouldNotContain("pattern 1");

        var ops = journal.Drain();
        ops.Count.ShouldBe(1);
        ops[0].Kind.ShouldBe(ChangeOpKind.Delete);
        ops[0].Op.ShouldBe(InverseOps.PatternClip);
        ops[0].New.ShouldBeNull();
        JournalValue.AsInt(ops[0].Old!["pattern"]).ShouldBe(1);
        JournalValue.AsInt(ops[0].Old!["length"]).ShouldBe(384);   // full spec snapshotted for re-add

        await Undo(fl, reg, ops);
        (await fl.ListClipsAsync(0, -1)).ShouldContain("pattern 1");   // re-added from the snapshot

        await Redo(fl, reg, ops);
        (await fl.ListClipsAsync(0, -1)).ShouldNotContain("pattern 1");
    }

    [Fact]
    public async Task DeleteClips_AudioClip_TaintsTurn()
    {
        var (fl, plugin, journal, _) = Wire();
        fl.SeedClip(track: 3, start: 0, length: 384, pattern: -1);   // slot2 = audio clip (can't re-add)

        await plugin.DeleteClipsAsync("2");

        journal.IsTainted.ShouldBeTrue();
        journal.Drain().ShouldBeEmpty();
    }

    [Fact]
    public async Task DeleteClips_UndoRestoresMutedState()
    {
        var (fl, plugin, journal, reg) = Wire();
        fl.SeedClip(track: 5, start: 100, length: 200, pattern: 4, muted: true);   // slot2, muted

        await plugin.DeleteClipsAsync("2");
        var ops = journal.Drain();
        JournalValue.AsBool(ops[0].Old!["muted"]).ShouldBeTrue();

        await Undo(fl, reg, ops);
        // re-added AND its mute restored: the new slot for (pattern4,start100,track5) is muted again.
        var listing = await fl.ListClipsAsync(0, -1);
        listing.ShouldContain("pattern 4");
        listing.ShouldContain("muted");
    }

    // ── duplicate_clip → Create (before/after diff) ──────────────────────────────────────────────

    [Fact]
    public async Task DuplicateClip_Create_UndoDeletesTheCopy_RedoReAdds()
    {
        var (fl, plugin, journal, reg) = Wire();   // slot0 = pattern1@track1 start0 len384

        await plugin.DuplicateClipAsync(0);         // copy → pattern1@track1 start384
        var ops = journal.Drain();
        ops.Count.ShouldBe(1);
        ops[0].Kind.ShouldBe(ChangeOpKind.Create);
        JournalValue.AsInt(ops[0].Target["start"]).ShouldBe(384);   // the NEW copy's identity, not the source

        await Undo(fl, reg, ops);
        (await fl.ListClipsAsync(0, 1)).ShouldNotContain("start=384");   // copy removed, source untouched

        await Redo(fl, reg, ops);
        (await fl.ListClipsAsync(0, 1)).ShouldContain("start=384");
    }

    [Fact]
    public async Task DuplicateClip_AudioClip_TaintsTurn()
    {
        var (fl, plugin, journal, _) = Wire();
        fl.SeedClip(track: 3, start: 0, length: 384, pattern: -1);   // slot2 = audio clip

        await plugin.DuplicateClipAsync(2);

        journal.IsTainted.ShouldBeTrue();   // the copy has no re-addable pattern identity → .flp
        journal.Drain().ShouldBeEmpty();
    }

    // ── automation add/delete points → Create / Delete (identity = channel + time) ────────────────

    [Fact]
    public async Task AddAutomationPoint_Create_UndoDeletesByTime_RedoReAdds()
    {
        var (fl, plugin, journal, reg) = Wire();

        await plugin.AddAutomationPointAsync(channel: 5, timeBeats: 4.0, value: 0.75, tension: 0);
        (await fl.ListAutomationPointsAsync(5)).ShouldContain("t=4");

        var ops = journal.Drain();
        ops.Count.ShouldBe(1);
        ops[0].Kind.ShouldBe(ChangeOpKind.Create);
        ops[0].Op.ShouldBe(InverseOps.AutomationPoint);
        JournalValue.AsDouble(ops[0].New!["value"]).ShouldBe(0.75, 0.0001);

        await Undo(fl, reg, ops);
        (await fl.ListAutomationPointsAsync(5)).ShouldBe("(no points)");   // deleted by (channel,time)

        await Redo(fl, reg, ops);
        (await fl.ListAutomationPointsAsync(5)).ShouldContain("t=4");
    }

    [Fact]
    public async Task DeleteAutomationPoint_Delete_UndoReAddsSnapshot_RedoDeletesAgain()
    {
        var (fl, plugin, journal, reg) = Wire();
        fl.SeedAutomationPoint(channel: 5, time: 4.0, value: 0.75, tension: 0.25, curve: 0);

        await plugin.DeleteAutomationPointAsync(channel: 5, index: 0);
        (await fl.ListAutomationPointsAsync(5)).ShouldBe("(no points)");

        var ops = journal.Drain();
        ops.Count.ShouldBe(1);
        ops[0].Kind.ShouldBe(ChangeOpKind.Delete);
        JournalValue.AsDouble(ops[0].Old!["tension"]).ShouldBe(0.25, 0.0001);

        await Undo(fl, reg, ops);
        (await fl.ListAutomationPointsAsync(5)).ShouldContain("t=4");   // re-added from the snapshot

        await Redo(fl, reg, ops);
        (await fl.ListAutomationPointsAsync(5)).ShouldBe("(no points)");
    }

    [Fact]
    public async Task DeleteAutomationPoint_NonZeroCurve_TaintsTurn()
    {
        var (fl, plugin, journal, _) = Wire();
        fl.SeedAutomationPoint(channel: 5, time: 4.0, value: 0.75, tension: 0, curve: 2);   // curve AddPoint can't restore

        await plugin.DeleteAutomationPointAsync(channel: 5, index: 0);

        journal.IsTainted.ShouldBeTrue();
        journal.Drain().ShouldBeEmpty();
    }

    // ── make / clone arrangement → Create (index identity; undo = delete) ─────────────────────────

    [Fact]
    public async Task MakeArrangement_Create_UndoDeletes_RedoReMakes()
    {
        var (fl, plugin, journal, reg) = Wire();   // AddArrangement returns 1

        await plugin.MakeArrangementAsync("Verse");
        var ops = journal.Drain();
        ops.Count.ShouldBe(1);
        ops[0].Kind.ShouldBe(ChangeOpKind.Create);
        ops[0].Op.ShouldBe(InverseOps.Arrangement);
        JournalValue.AsInt(ops[0].Target["arrangement"]).ShouldBe(1);

        fl.Calls.Clear();
        await Undo(fl, reg, ops);
        fl.Calls.ShouldContain("DeleteArrangementAsync(1)");

        fl.Calls.Clear();
        await Redo(fl, reg, ops);
        fl.Calls.ShouldContain("AddArrangementAsync(Verse)");
    }

    [Fact]
    public async Task CloneArrangement_Create_CapturesConcreteSource_UndoDeletes_RedoReClones()
    {
        var (fl, plugin, journal, reg) = Wire();   // ListArrangements marks index 0 current; Clone returns 2

        await plugin.CloneArrangementAsync(srcIndex: -1, name: "Copy");   // -1 = current → resolve to 0
        var ops = journal.Drain();
        ops.Count.ShouldBe(1);
        JournalValue.AsInt(ops[0].Target["arrangement"]).ShouldBe(2);
        JournalValue.AsString(ops[0].New!["kind"]).ShouldBe("clone");
        JournalValue.AsInt(ops[0].New!["src"]).ShouldBe(0);   // concrete, not "-1 = current"

        fl.Calls.Clear();
        await Undo(fl, reg, ops);
        fl.Calls.ShouldContain("DeleteArrangementAsync(2)");

        fl.Calls.Clear();
        await Redo(fl, reg, ops);
        fl.Calls.ShouldContain("CloneArrangementAsync(0,Copy)");   // re-clones from the captured concrete source
    }

    // ── disk round-trip: a create/delete replays from JsonElement values, exactly as undo reloads them ──

    [Fact]
    public async Task PatternClipCreate_ReplaysAfterJsonRoundTrip()
    {
        var (fl, _, _, reg) = Wire();
        var record = new ChangeRecord(0, ChangeOpKind.Create, InverseOps.PatternClip,
            JournalDict.Of("pattern", 3, "start", 1920, "track", 1),
            null,
            JournalDict.Of("pattern", 3, "track", 1, "start", 1920, "length", 0));
        var reloaded = JsonSerializer.Deserialize<ChangeRecord>(JsonSerializer.Serialize(record, Json), Json)!;

        reg.TryGet(reloaded.Op, out var inv).ShouldBeTrue();
        await inv.ApplyAsync(fl, reloaded.Target, reloaded.New);   // redo = create from reloaded JsonElement spec
        (await fl.ListClipsAsync(0, -1)).ShouldContain("pattern 3");
    }

    // ── taint contract for the Phase-2 ops ───────────────────────────────────────────────────────

    [Fact]
    public void Registry_CanInvert_TrueForPhase2Ops()
    {
        var reg = InverseOps.CreateRegistry();
        foreach (var op in new[] { InverseOps.ClipMute, InverseOps.PatternClip, InverseOps.AutomationPoint, InverseOps.Arrangement })
            reg.CanInvert(new ChangeRecord(0, ChangeOpKind.Create, op, JournalDict.Empty, null, JournalDict.Empty))
               .ShouldBeTrue($"{op} must be registered");
    }

    [Fact]
    public void GranularToolNames_IncludeEveryPhase2Tool()
    {
        foreach (var tool in new[]
        {
            "native_mute_clips", "native_add_pattern_clips", "native_delete_clips", "native_duplicate_clip",
            "native_add_automation_point", "native_delete_automation_point", "native_make_arrangement", "native_clone_arrangement",
        })
            InverseOps.GranularToolNames.ShouldContain(tool);
    }
}
