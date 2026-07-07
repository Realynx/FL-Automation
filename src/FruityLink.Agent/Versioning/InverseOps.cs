using FruityLink.Core.Abstractions;
using static FruityLink.Agent.Versioning.JournalValue;

namespace FruityLink.Agent.Versioning;

/// <summary>
/// The Phase-1 (value-set) inverse-op catalog: the op-id constants, the FL tool names that are fully
/// journaled (so the coordinator can taint a turn that also ran a not-yet-invertible tool), and the
/// factory that builds a populated <see cref="InverseOpRegistry"/>. Every op here reads its BEFORE-state
/// through a symmetric getter that mirrors the setter it undoes, so undo restores exactly what changed.
///
/// <para>Deliberately NOT here (kept <c>.flp</c>-fallback until a follow-up phase): mixer send / EQ gain
/// (no symmetric command-bus GET — send is poked into the routing matrix), and channel/mixer plugin
/// params (the raw read-back scale differs native vs. hosted-VST, so a normalized round-trip can't be
/// trusted without a live verify). Their tools are absent from <see cref="GranularToolNames"/>, so any
/// turn using them falls back to the whole-commit <c>.flp</c> — safe, just not granular.</para>
/// </summary>
public static class InverseOps
{
    // ── op ids (registry keys, also written into ops.json) ───────────────────────────────────────
    public const string Tempo = "tempo";
    public const string MasterVolume = "master_volume";
    public const string MasterPitch = "master_pitch";
    public const string Shuffle = "shuffle";
    public const string MixerVolume = "mixer_volume";
    public const string MixerPan = "mixer_pan";
    public const string ChannelVolume = "channel_volume";
    public const string ChannelPan = "channel_pan";
    public const string ChannelPitch = "channel_pitch";
    public const string ChannelMuted = "channel_muted";
    public const string ChannelRoute = "channel_route";
    public const string TrackName = "track_name";
    public const string TrackColor = "track_color";
    public const string TrackMute = "track_mute";
    public const string TrackCollapsed = "track_collapsed";
    public const string SongMode = "song_mode";
    public const string ArrangementName = "arrangement_name";
    public const string ClipMove = "clip_move";
    public const string ClipResize = "clip_resize";

    // ── Phase 2 (create / delete inverses, identity-addressed) ───────────────────────────────────
    /// <summary>Clip mute/unmute (SetScalar on a clip's mute bit, addressed by pattern+track+start).</summary>
    public const string ClipMute = "clip_mute";
    /// <summary>A pattern clip's existence. Create records (add / duplicate) carry the spec in <c>New</c>
    /// and delete by identity on undo; Delete records (delete_clips) carry the spec in <c>Old</c> and
    /// re-add on undo. One apply serves both: <c>value != null</c> ⇒ create-from-spec, else delete-by-target.</summary>
    public const string PatternClip = "pattern_clip";
    /// <summary>An automation point's existence, addressed by (channel, time-in-beats). Same
    /// create-or-delete apply as <see cref="PatternClip"/>.</summary>
    public const string AutomationPoint = "automation_point";
    /// <summary>An arrangement's existence, addressed by index. Create-only this phase (undo = delete);
    /// <c>native_delete_arrangement</c> stays <c>.flp</c>-only (deep content can't be re-created).</summary>
    public const string Arrangement = "arrangement";

    /// <summary>
    /// The FL <c>[KernelFunction]</c> names whose mutations are FULLY captured by the journal this phase.
    /// The coordinator treats a turn as granular only if every mutating tool it ran is in this set; any
    /// other mutating tool (e.g. add_note, add_channel, delete_arrangement) taints the commit to
    /// <c>.flp</c>-only. Keep in lock-step with the ops registered in <see cref="CreateRegistry"/>.
    /// </summary>
    public static IReadOnlySet<string> GranularToolNames { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "native_set_tempo",
        "native_set_master_pitch",
        "native_set_shuffle",
        "native_set_mixer_volume",
        "native_set_mixer_pan",
        "native_set_channel_volume",
        "native_set_channel_pan",
        "native_set_channel_pitch",
        "native_set_channel_muted",
        "native_route_channel_to_mixer",
        "native_set_track_name",
        "native_set_track_color",
        "native_set_track_mute",
        "native_set_track_collapsed",
        "native_set_song_mode",
        "native_rename_arrangement",
        "native_move_clips",
        "native_resize_clips",
        // ── Phase 2 (create / delete inverses) ──
        "native_mute_clips",
        "native_add_pattern_clips",
        "native_delete_clips",
        "native_duplicate_clip",
        "native_add_automation_point",
        "native_delete_automation_point",
        "native_make_arrangement",
        "native_clone_arrangement",
    };

    /// <summary>Builds the registry with every Phase-1 inverse op wired.</summary>
    public static InverseOpRegistry CreateRegistry()
    {
        var r = new InverseOpRegistry();

        // ── global scalars (target is empty) ─────────────────────────────────────────────────────
        r.Register(Tempo, Scalar(
            async (fl, _, ct) => Value(await fl.GetTempoAsync(ct)),
            (fl, _, v, ct) => fl.SetTempoAsync(AsDouble(v), ct)));

        r.Register(MasterVolume, Scalar(
            async (fl, _, ct) => Value(await fl.GetMasterVolumeAsync(ct)),
            (fl, _, v, ct) => fl.SetMasterVolumeAsync(AsInt(v), ct)));

        r.Register(MasterPitch, Scalar(
            async (fl, _, ct) => Value(await fl.GetMasterPitchAsync(ct)),
            (fl, _, v, ct) => fl.SetMasterPitchAsync(AsInt(v), ct)));

        r.Register(Shuffle, Scalar(
            async (fl, _, ct) => Value(await fl.GetShuffleAsync(ct)),
            (fl, _, v, ct) => fl.SetShuffleAsync(AsInt(v), ct)));

        // ── mixer (target = { track }) ───────────────────────────────────────────────────────────
        r.Register(MixerVolume, Scalar(
            async (fl, t, ct) => Value(await fl.GetMixerVolumeAsync(Track(t), ct)),
            (fl, t, v, ct) => fl.SetMixerVolumeAsync(Track(t), AsInt(v), ct)));

        r.Register(MixerPan, Scalar(
            async (fl, t, ct) => Value(await fl.GetMixerPanAsync(Track(t), ct)),
            (fl, t, v, ct) => fl.SetMixerPanAsync(Track(t), AsInt(v), ct)));

        // ── channel rack (target = { channel }) ──────────────────────────────────────────────────
        r.Register(ChannelVolume, Scalar(
            async (fl, t, ct) => Value(await fl.GetChannelVolumeAsync(Channel(t), ct)),
            (fl, t, v, ct) => fl.SetChannelVolumeAsync(Channel(t), AsInt(v), ct)));

        r.Register(ChannelPan, Scalar(
            async (fl, t, ct) => Value(await fl.GetChannelPanAsync(Channel(t), ct)),
            (fl, t, v, ct) => fl.SetChannelPanAsync(Channel(t), AsInt(v), ct)));

        r.Register(ChannelPitch, Scalar(
            async (fl, t, ct) => Value(await fl.GetChannelPitchAsync(Channel(t), ct)),
            (fl, t, v, ct) => fl.SetChannelPitchAsync(Channel(t), AsInt(v), ct)));

        r.Register(ChannelMuted, Scalar(
            async (fl, t, ct) => Value(await fl.GetChannelMutedAsync(Channel(t), ct)),
            (fl, t, v, ct) => fl.SetChannelMutedAsync(Channel(t), AsBool(v), ct)));

        r.Register(ChannelRoute, Scalar(
            async (fl, t, ct) => Value(await fl.GetChannelFxRouteAsync(Channel(t), ct)),
            (fl, t, v, ct) => fl.SetChannelFxRouteAsync(Channel(t), AsInt(v), ct)));

        // ── playlist tracks (target = { track }) ─────────────────────────────────────────────────
        r.Register(TrackName, Scalar(
            async (fl, t, ct) => Value(await fl.GetTrackNameAsync(Track(t), ct)),
            (fl, t, v, ct) => fl.SetTrackNameAsync(Track(t), AsString(v), ct)));

        r.Register(TrackColor, Scalar(
            async (fl, t, ct) => Value(await fl.GetTrackColorAsync(Track(t), ct)),
            (fl, t, v, ct) => fl.SetTrackColorAsync(Track(t), AsInt(v), ct)));

        r.Register(TrackMute, Scalar(
            async (fl, t, ct) => Value(await fl.GetTrackMuteAsync(Track(t), ct)),
            (fl, t, v, ct) => fl.SetTrackMuteAsync(Track(t), AsBool(v), ct)));

        r.Register(TrackCollapsed, Scalar(
            async (fl, t, ct) => Value(await fl.GetTrackCollapsedAsync(Track(t), ct)),
            (fl, t, v, ct) => fl.SetTrackCollapsedAsync(Track(t), AsBool(v), ct)));

        // ── song mode (target empty) ─────────────────────────────────────────────────────────────
        r.Register(SongMode, Scalar(
            async (fl, _, ct) => Value(await fl.GetSongModeAsync(ct)),
            (fl, _, v, ct) => fl.SetSongModeAsync(AsBool(v), ct)));

        // ── arrangement name (target = { arrangement }) ──────────────────────────────────────────
        r.Register(ArrangementName, Scalar(
            async (fl, t, ct) => Value(await fl.GetArrangementNameAsync(AsInt(Field(t, "arrangement")), ct)),
            (fl, t, v, ct) => fl.RenameArrangementAsync(AsInt(Field(t, "arrangement")), AsString(v), ct)));

        // ── clips: addressed by identity (pattern+track+start), resolved to a live slot at replay ──
        r.Register(ClipMove, new DelegateInverseOp(ReadClipGeometry, ApplyClipMove));
        r.Register(ClipResize, new DelegateInverseOp(ReadClipGeometry, ApplyClipResize));

        // ── Phase 2: clip mute (SetScalar on the mute bit, identity-addressed) ──
        r.Register(ClipMute, Scalar(
            async (fl, t, ct) => Value(await ReadClipMuted(fl, t, ct)),
            (fl, t, v, ct) => ApplyClipMuted(fl, t, AsBool(v), ct)));

        // ── Phase 2: create/delete of pattern clips, automation points, arrangements ──
        // ReadAsync is unused for these (capture builds the create/delete records directly in the plugin);
        // the version-control replay only ever calls ApplyAsync. value != null ⇒ create, value == null ⇒ delete.
        r.Register(PatternClip, new DelegateInverseOp(NoRead, ApplyClipPresence));
        r.Register(AutomationPoint, new DelegateInverseOp(NoRead, ApplyAutomationPresence));
        r.Register(Arrangement, new DelegateInverseOp(NoRead, ApplyArrangementPresence));

        return r;
    }

    // ── Phase 2 apply/read helpers ─────────────────────────────────────────────────────────────────

    /// <summary>Create/Delete ops don't read a BEFORE-scalar (their spec is known at the tool), so this
    /// no-op read keeps the registry entry uniform; the replay path only calls ApplyAsync.</summary>
    private static Task<IReadOnlyDictionary<string, object?>> NoRead(
        INativeFlControl fl, IReadOnlyDictionary<string, object?> target, CancellationToken ct)
        => Task.FromResult(JournalDict.Empty);

    /// <summary>Resolve a clip's live slot from its stable identity (pattern+track+start) against LIVE
    /// state, or -1 when it isn't present.</summary>
    private static async Task<int> ResolveClipSlot(
        INativeFlControl fl, int pattern, int start, int track, CancellationToken ct)
    {
        var clips = ClipList.Parse(await fl.ListClipsAsync(0, -1, ct));
        return ClipList.FindSlot(clips, pattern, start, track);
    }

    private static async Task<bool> ReadClipMuted(
        INativeFlControl fl, IReadOnlyDictionary<string, object?> target, CancellationToken ct)
    {
        int pattern = AsInt(Field(target, "pattern")), start = AsInt(Field(target, "start")), track = AsInt(Field(target, "track"));
        int slot = await ResolveClipSlot(fl, pattern, start, track, ct);
        if (slot < 0) throw new InvalidOperationException($"clip_mute: no pattern-{pattern} clip at track {track} start {start}");
        return await fl.GetClipMutedAsync(slot, ct);
    }

    private static async Task ApplyClipMuted(
        INativeFlControl fl, IReadOnlyDictionary<string, object?> target, bool muted, CancellationToken ct)
    {
        int pattern = AsInt(Field(target, "pattern")), start = AsInt(Field(target, "start")), track = AsInt(Field(target, "track"));
        int slot = await ResolveClipSlot(fl, pattern, start, track, ct);
        if (slot < 0) throw new InvalidOperationException($"clip_mute: no pattern-{pattern} clip at track {track} start {start}");
        await fl.SetClipMutedAsync(slot, muted, ct);
    }

    /// <summary>Create (value = spec) or delete (value = null) a pattern clip by identity. Create re-adds via
    /// <c>AddPatternClipsAsync</c> and, if the captured spec was muted, restores the mute; delete resolves the
    /// identity to a live slot and removes it. Throws when a delete target can't be located → <c>.flp</c> fallback.</summary>
    private static async Task ApplyClipPresence(
        INativeFlControl fl, IReadOnlyDictionary<string, object?> target,
        IReadOnlyDictionary<string, object?>? value, CancellationToken ct)
    {
        if (value is null)   // delete by identity
        {
            int pattern = AsInt(Field(target, "pattern")), start = AsInt(Field(target, "start")), track = AsInt(Field(target, "track"));
            int slot = await ResolveClipSlot(fl, pattern, start, track, ct);
            if (slot < 0)
                throw new InvalidOperationException($"pattern_clip: no pattern-{pattern} clip at track {track} start {start} to delete");
            await fl.DeleteClipsAsync(new[] { slot }, ct);
            return;
        }

        // create from the captured spec
        int p = AsInt(Field(value, "pattern")), t = AsInt(Field(value, "track"));
        int s = AsInt(Field(value, "start")), len = AsInt(Field(value, "length"));
        await fl.AddPatternClipsAsync(new[] { new PatternClipSpec(p, t, s, len) }, ct);
        if (AsBool(Field(value, "muted")))
        {
            int slot = await ResolveClipSlot(fl, p, s, t, ct);
            if (slot >= 0) await fl.SetClipMutedAsync(slot, true, ct);
        }
    }

    /// <summary>Create (value = spec) or delete (value = null) an automation point, addressed by (channel,
    /// time-in-beats). Delete resolves the time to a live index; throws when it can't → <c>.flp</c> fallback.</summary>
    private static async Task ApplyAutomationPresence(
        INativeFlControl fl, IReadOnlyDictionary<string, object?> target,
        IReadOnlyDictionary<string, object?>? value, CancellationToken ct)
    {
        int channel = AsInt(Field(target, "channel"));
        if (value is null)   // delete by identity (channel, time)
        {
            double time = AsDouble(Field(target, "time"));
            var pts = AutomationPointList.Parse(await fl.ListAutomationPointsAsync(channel, ct));
            int idx = AutomationPointList.FindIndex(pts, time);
            if (idx < 0)
                throw new InvalidOperationException($"automation_point: no point near beat {time} on channel {channel} to delete");
            await fl.DeleteAutomationPointAsync(channel, idx, ct);
            return;
        }
        await fl.AddAutomationPointAsync(
            channel, AsDouble(Field(value, "time")), AsDouble(Field(value, "value")), AsDouble(Field(value, "tension")), ct);
    }

    /// <summary>Create (value = spec) or delete (value = null) an arrangement, addressed by index. Redo re-runs
    /// the original make/clone (a clone re-copies from its captured concrete source); undo deletes the index.</summary>
    private static async Task ApplyArrangementPresence(
        INativeFlControl fl, IReadOnlyDictionary<string, object?> target,
        IReadOnlyDictionary<string, object?>? value, CancellationToken ct)
    {
        int index = AsInt(Field(target, "arrangement"));
        if (value is null)   // undo of a create → delete the arrangement
        {
            await fl.DeleteArrangementAsync(index, ct);
            return;
        }
        string name = AsString(Field(value, "name"));
        string? nm = string.IsNullOrEmpty(name) ? null : name;
        if (AsString(Field(value, "kind")) == "clone")
            await fl.CloneArrangementAsync(AsInt(Field(value, "src")), nm, ct);
        else
            await fl.AddArrangementAsync(nm, ct);
    }

    // ── clip inverse ops ─────────────────────────────────────────────────────────────────────────

    /// <summary>Reads the current geometry of the clip identified by target { pattern, start, track }.
    /// (The move/resize capture path builds its directional tokens inline in the plugin; this keeps the
    /// registry entry self-contained and testable for the identity-address case.)</summary>
    private static async Task<IReadOnlyDictionary<string, object?>> ReadClipGeometry(
        INativeFlControl fl, IReadOnlyDictionary<string, object?> target, CancellationToken ct)
    {
        int pattern = AsInt(Field(target, "pattern"));
        int start = AsInt(Field(target, "start"));
        int track = AsInt(Field(target, "track"));
        var clips = ClipList.Parse(await fl.ListClipsAsync(0, -1, ct));
        int slot = ClipList.FindSlot(clips, pattern, start, track);
        var c = slot >= 0 ? ClipList.AtSlot(clips, slot) : null;
        return JournalDict.Of(
            "pattern", pattern, "start", start, "track", track,
            "length", c?.Length ?? 0, "toStart", start);
    }

    /// <summary>Locate the clip at value { pattern, fromStart, fromTrack } and move it to
    /// { toStart, toTrack }. Throws when the clip can't be located → caller falls back to the .flp.</summary>
    private static async Task ApplyClipMove(
        INativeFlControl fl, IReadOnlyDictionary<string, object?> target,
        IReadOnlyDictionary<string, object?>? value, CancellationToken ct)
    {
        int pattern = AsInt(Field(value, "pattern"));
        int fromStart = AsInt(Field(value, "fromStart"));
        int fromTrack = AsInt(Field(value, "fromTrack"));
        int toStart = AsInt(Field(value, "toStart"));
        int toTrack = AsInt(Field(value, "toTrack"));
        var clips = ClipList.Parse(await fl.ListClipsAsync(0, -1, ct));
        int slot = ClipList.FindSlot(clips, pattern, fromStart, fromTrack);
        if (slot < 0)
            throw new InvalidOperationException(
                $"clip_move: no pattern-{pattern} clip at track {fromTrack} start {fromStart} to move");
        await fl.MoveClipAsync(slot, toStart, toTrack, ct);
    }

    /// <summary>Locate the clip at value { pattern, start, track } and set its length. Throws when the
    /// clip can't be located → caller falls back to the .flp.</summary>
    private static async Task ApplyClipResize(
        INativeFlControl fl, IReadOnlyDictionary<string, object?> target,
        IReadOnlyDictionary<string, object?>? value, CancellationToken ct)
    {
        int pattern = AsInt(Field(value, "pattern"));
        int start = AsInt(Field(value, "start"));
        int track = AsInt(Field(value, "track"));
        int length = AsInt(Field(value, "length"));
        var clips = ClipList.Parse(await fl.ListClipsAsync(0, -1, ct));
        int slot = ClipList.FindSlot(clips, pattern, start, track);
        if (slot < 0)
            throw new InvalidOperationException(
                $"clip_resize: no pattern-{pattern} clip at track {track} start {start} to resize");
        await fl.ResizeClipAsync(slot, length, ct);
    }

    // ── tiny builders / accessors ─────────────────────────────────────────────────────────────────
    // The apply half receives the UNWRAPPED scalar (the token's "value" field), so each op reads it with
    // one As* call instead of unwrapping the { "value": … } envelope itself.
    private static DelegateInverseOp Scalar(
        Func<INativeFlControl, IReadOnlyDictionary<string, object?>, CancellationToken, Task<IReadOnlyDictionary<string, object?>>> read,
        Func<INativeFlControl, IReadOnlyDictionary<string, object?>, object?, CancellationToken, Task> applyValue)
        => new(read, (fl, target, value, ct) => applyValue(fl, target, Field(value, "value"), ct));

    private static IReadOnlyDictionary<string, object?> Value(object? v) => JournalDict.Of("value", v);
    private static int Track(IReadOnlyDictionary<string, object?> t) => AsInt(Field(t, "track"));
    private static int Channel(IReadOnlyDictionary<string, object?> t) => AsInt(Field(t, "channel"));
}
