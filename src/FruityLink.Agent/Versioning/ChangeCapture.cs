using FruityLink.Core.Abstractions;
using FruityLink.Core.Domain;

namespace FruityLink.Agent.Versioning;

/// <summary>
/// The single read-before-write seam every mutating Phase-1 tool routes through, so capture lives in ONE
/// code path instead of being bolted onto each tool. It captures the OLD value via the registry, runs the
/// mutation, then appends the record — using the SAME registry entry the undo executor replays, so the two
/// directions can never diverge.
///
/// <para>Inert when disabled (no journal/registry wired, e.g. tests or a host-provided agent): the
/// mutation runs and nothing is recorded, so behavior is identical to before the journal existed. Any
/// mutation it cannot capture cleanly <see cref="IChangeJournal.Taint"/>s the turn, which makes the whole
/// commit fall back to its <c>.flp</c> — FL is never left half-inverted.</para>
/// </summary>
public sealed class ChangeCapture
{
    private readonly INativeFlControl _fl;
    private readonly IChangeJournal? _journal;
    private readonly IInverseOpRegistry? _registry;

    public ChangeCapture(INativeFlControl fl, IChangeJournal? journal, IInverseOpRegistry? registry)
    {
        _fl = fl ?? throw new ArgumentNullException(nameof(fl));
        _journal = journal;
        _registry = registry;
    }

    /// <summary>An inert seam (no journaling) — the Phase-0 / test / host-agent default.</summary>
    public static ChangeCapture Disabled(INativeFlControl fl) => new(fl, null, null);

    /// <summary>True when a journal + registry are wired.</summary>
    public bool Enabled => _journal is not null && _registry is not null;

    /// <summary>
    /// Capture-and-mutate a value-set op: read OLD via the registry, run <paramref name="mutate"/>, then
    /// journal a <see cref="ChangeOpKind.SetScalar"/> record (old→undo, new→redo). If capture fails (op
    /// unregistered or read threw) the mutation still runs but the turn is tainted → <c>.flp</c> fallback.
    /// </summary>
    public async Task ScalarAsync(
        string op,
        IReadOnlyDictionary<string, object?> target,
        IReadOnlyDictionary<string, object?> newValue,
        Func<CancellationToken, Task> mutate,
        CancellationToken ct)
    {
        if (!Enabled)
        {
            await mutate(ct).ConfigureAwait(false);
            return;
        }

        IReadOnlyDictionary<string, object?>? old = null;
        if (_registry!.TryGet(op, out var inv))
        {
            try { old = await inv.ReadAsync(_fl, target, ct).ConfigureAwait(false); }
            catch { old = null; }
        }

        await mutate(ct).ConfigureAwait(false);

        if (old is null)
        {
            _journal!.Taint();   // mutated but couldn't capture → this turn can't be granularly inverted
            return;
        }
        _journal!.Append(new ChangeRecord(0, ChangeOpKind.SetScalar, op, target, old, newValue));
    }

    /// <summary>Capture-and-mutate a batch of clip MOVES. Each moved clip becomes its own record addressed
    /// by identity (pattern+track+start), so reverse replay is element-exact.</summary>
    public Task ClipMovesAsync(IReadOnlyList<ClipMove> moves, Func<CancellationToken, Task> mutate, CancellationToken ct)
        => RecordBatchAsync(() => BuildMoveRecordsAsync(moves, ct), mutate, ct);

    /// <summary>Capture-and-mutate a batch of clip RESIZES (one identity-addressed record per clip).</summary>
    public Task ClipResizesAsync(IReadOnlyList<ClipResize> resizes, Func<CancellationToken, Task> mutate, CancellationToken ct)
        => RecordBatchAsync(() => BuildResizeRecordsAsync(resizes, ct), mutate, ct);

    /// <summary>The shared capture-and-mutate spine: build the per-element records (a read-before-write
    /// snapshot for delete/mute, or the known specs for create) BEFORE the mutation runs, run the mutation,
    /// then append the records — or, when the builder returns null (a change we can't cleanly invert),
    /// <see cref="IChangeJournal.Taint"/> the turn so the whole commit falls back to its <c>.flp</c>.</summary>
    private async Task RecordBatchAsync(
        Func<Task<List<ChangeRecord>?>> build, Func<CancellationToken, Task> mutate, CancellationToken ct)
    {
        if (!Enabled)
        {
            await mutate(ct).ConfigureAwait(false);
            return;
        }

        List<ChangeRecord>? records;
        try { records = await build().ConfigureAwait(false); }
        catch { records = null; }

        await mutate(ct).ConfigureAwait(false);

        if (records is null)
        {
            _journal!.Taint();   // a change we can't address/invert by identity → whole-commit .flp fallback
            return;
        }
        foreach (var rec in records) _journal!.Append(rec);
    }

    private async Task<List<ChangeRecord>?> BuildMoveRecordsAsync(IReadOnlyList<ClipMove> moves, CancellationToken ct)
    {
        if (moves is null || moves.Count == 0) return new List<ChangeRecord>();
        var clips = ClipList.Parse(await _fl.ListClipsAsync(0, -1, ct).ConfigureAwait(false));
        var records = new List<ChangeRecord>(moves.Count);
        foreach (var m in moves)
        {
            var c = ClipList.AtSlot(clips, m.Index);
            if (c is null || c.Value.Pattern < 0) return null;   // not an identity-addressable pattern clip
            int pattern = c.Value.Pattern, fromStart = c.Value.Start, fromTrack = c.Value.Track;
            int toTrack = m.Track > 0 ? m.Track : fromTrack;      // Track <= 0 means "keep current"
            int toStart = m.StartTick;

            // old (undo): the clip currently sits at (toStart,toTrack) → move it back to (fromStart,fromTrack).
            var old = JournalDict.Of(
                "pattern", pattern, "fromStart", toStart, "fromTrack", toTrack, "toStart", fromStart, "toTrack", fromTrack);
            // new (redo): the clip sits at (fromStart,fromTrack) → move it to (toStart,toTrack).
            var @new = JournalDict.Of(
                "pattern", pattern, "fromStart", fromStart, "fromTrack", fromTrack, "toStart", toStart, "toTrack", toTrack);
            records.Add(new ChangeRecord(0, ChangeOpKind.SetScalar, InverseOps.ClipMove,
                JournalDict.Of("pattern", pattern, "start", fromStart, "track", fromTrack), old, @new));
        }
        return records;
    }

    private async Task<List<ChangeRecord>?> BuildResizeRecordsAsync(IReadOnlyList<ClipResize> resizes, CancellationToken ct)
    {
        if (resizes is null || resizes.Count == 0) return new List<ChangeRecord>();
        var clips = ClipList.Parse(await _fl.ListClipsAsync(0, -1, ct).ConfigureAwait(false));
        var records = new List<ChangeRecord>(resizes.Count);
        foreach (var r in resizes)
        {
            var c = ClipList.AtSlot(clips, r.Index);
            if (c is null || c.Value.Pattern < 0) return null;
            int pattern = c.Value.Pattern, start = c.Value.Start, track = c.Value.Track;
            var target = JournalDict.Of("pattern", pattern, "start", start, "track", track);
            // resize changes only length; the (pattern,start,track) locator is stable both directions.
            var old = JournalDict.Of("pattern", pattern, "start", start, "track", track, "length", c.Value.Length);
            var @new = JournalDict.Of("pattern", pattern, "start", start, "track", track, "length", r.LengthTick);
            records.Add(new ChangeRecord(0, ChangeOpKind.SetScalar, InverseOps.ClipResize, target, old, @new));
        }
        return records;
    }

    // ── Phase 2: create / delete captures ─────────────────────────────────────────────────────────

    /// <summary>Capture-and-mutate a batch of clip MUTE toggles. Each pattern clip becomes a SetScalar record
    /// on its mute bit, addressed by identity; an audio/automation clip (no identity) taints → <c>.flp</c>.</summary>
    public Task ClipMutesAsync(IReadOnlyList<int> indices, bool muted, Func<CancellationToken, Task> mutate, CancellationToken ct)
        => RecordBatchAsync(() => BuildMuteRecordsAsync(indices, muted, ct), mutate, ct);

    private async Task<List<ChangeRecord>?> BuildMuteRecordsAsync(IReadOnlyList<int> indices, bool muted, CancellationToken ct)
    {
        if (indices is null || indices.Count == 0) return new List<ChangeRecord>();
        var clips = ClipList.Parse(await _fl.ListClipsAsync(0, -1, ct).ConfigureAwait(false));
        var records = new List<ChangeRecord>(indices.Count);
        var seen = new HashSet<int>();
        foreach (int idx in indices)
        {
            if (!seen.Add(idx)) continue;
            var c = ClipList.AtSlot(clips, idx);
            if (c is null || c.Value.Pattern < 0) return null;   // can't identity-address audio/automation clips
            var target = JournalDict.Of("pattern", c.Value.Pattern, "start", c.Value.Start, "track", c.Value.Track);
            records.Add(new ChangeRecord(0, ChangeOpKind.SetScalar, InverseOps.ClipMute,
                target, JournalDict.Of("value", c.Value.Muted), JournalDict.Of("value", muted)));
        }
        return records;
    }

    /// <summary>Capture the CREATE of pattern clips (add): each placed spec is one Create record whose
    /// identity + spec are known up front (undo deletes by identity, redo re-adds from the spec).</summary>
    public Task PatternClipsAddedAsync(IReadOnlyList<PatternClipSpec> placed, Func<CancellationToken, Task> mutate, CancellationToken ct)
        => RecordBatchAsync(() => Task.FromResult(BuildAddedClipRecords(placed)), mutate, ct);

    private static List<ChangeRecord>? BuildAddedClipRecords(IReadOnlyList<PatternClipSpec> placed)
    {
        if (placed is null || placed.Count == 0) return new List<ChangeRecord>();
        var records = new List<ChangeRecord>(placed.Count);
        foreach (var s in placed)
            records.Add(new ChangeRecord(0, ChangeOpKind.Create, InverseOps.PatternClip,
                JournalDict.Of("pattern", s.Pattern, "start", s.StartTick, "track", s.Track),
                null,
                JournalDict.Of("pattern", s.Pattern, "track", s.Track, "start", s.StartTick, "length", s.LengthTick)));
        return records;
    }

    /// <summary>Capture the DELETE of clips: snapshot each clip's full spec (incl. mute) BEFORE the delete so
    /// undo can re-add it. Only pattern clips are re-addable; any audio/automation clip in the batch taints
    /// the turn → the whole commit falls back to its <c>.flp</c>.</summary>
    public Task ClipsDeletedAsync(IReadOnlyList<int> indices, Func<CancellationToken, Task> mutate, CancellationToken ct)
        => RecordBatchAsync(() => BuildDeletedClipRecords(indices, ct), mutate, ct);

    private async Task<List<ChangeRecord>?> BuildDeletedClipRecords(IReadOnlyList<int> indices, CancellationToken ct)
    {
        if (indices is null || indices.Count == 0) return new List<ChangeRecord>();
        var clips = ClipList.Parse(await _fl.ListClipsAsync(0, -1, ct).ConfigureAwait(false));
        var records = new List<ChangeRecord>(indices.Count);
        var seen = new HashSet<int>();
        foreach (int idx in indices)
        {
            if (!seen.Add(idx)) continue;
            var c = ClipList.AtSlot(clips, idx);
            if (c is null || c.Value.Pattern < 0) return null;   // audio/automation clips can't be re-added → .flp
            records.Add(new ChangeRecord(0, ChangeOpKind.Delete, InverseOps.PatternClip,
                JournalDict.Of("pattern", c.Value.Pattern, "start", c.Value.Start, "track", c.Value.Track),
                JournalDict.Of("pattern", c.Value.Pattern, "track", c.Value.Track, "start", c.Value.Start,
                    "length", c.Value.Length, "muted", c.Value.Muted),
                null));
        }
        return records;
    }

    /// <summary>Capture the CREATE from a duplicate: diff the clip list before vs. after and record every NEW
    /// pattern-clip identity (mute carried so a muted source's copy restores muted). A duplicate that produced
    /// no identity-addressable pattern clip (e.g. an audio clip) taints the turn → <c>.flp</c>.</summary>
    public async Task ClipDuplicatedAsync(Func<CancellationToken, Task> mutate, CancellationToken ct)
    {
        if (!Enabled) { await mutate(ct).ConfigureAwait(false); return; }

        List<ClipInfo>? before;
        try { before = ClipList.Parse(await _fl.ListClipsAsync(0, -1, ct).ConfigureAwait(false)); }
        catch { before = null; }

        await mutate(ct).ConfigureAwait(false);

        List<ChangeRecord>? records = null;
        if (before is not null)
        {
            try { records = BuildDuplicateRecords(before, ClipList.Parse(await _fl.ListClipsAsync(0, -1, ct).ConfigureAwait(false))); }
            catch { records = null; }
        }

        if (records is null) { _journal!.Taint(); return; }
        foreach (var rec in records) _journal!.Append(rec);
    }

    private static List<ChangeRecord>? BuildDuplicateRecords(List<ClipInfo> before, List<ClipInfo> after)
    {
        var had = new HashSet<(int, int, int)>();
        foreach (var c in before) if (c.Pattern >= 0) had.Add((c.Pattern, c.Track, c.Start));
        var records = new List<ChangeRecord>();
        foreach (var c in after)
        {
            if (c.Pattern < 0 || had.Contains((c.Pattern, c.Track, c.Start))) continue;
            records.Add(new ChangeRecord(0, ChangeOpKind.Create, InverseOps.PatternClip,
                JournalDict.Of("pattern", c.Pattern, "start", c.Start, "track", c.Track),
                null,
                JournalDict.Of("pattern", c.Pattern, "track", c.Track, "start", c.Start, "length", c.Length, "muted", c.Muted)));
        }
        return records.Count == 0 ? null : records;   // nothing identity-addressable appeared → taint (fall back)
    }

    /// <summary>Capture the CREATE of an automation point (undo deletes by (channel,time), redo re-adds).</summary>
    public Task AutomationAddedAsync(
        int channel, double timeBeats, double value, double tension, Func<CancellationToken, Task> mutate, CancellationToken ct)
        => RecordBatchAsync(() => Task.FromResult<List<ChangeRecord>?>(new List<ChangeRecord>
        {
            new(0, ChangeOpKind.Create, InverseOps.AutomationPoint,
                JournalDict.Of("channel", channel, "time", timeBeats),
                null,
                JournalDict.Of("channel", channel, "time", timeBeats, "value", value, "tension", tension)),
        }), mutate, ct);

    /// <summary>Capture the DELETE of an automation point: snapshot (time,value,tension) BEFORE deleting so
    /// undo can re-add it. A missing point, or one with a non-zero curve (which <c>AddAutomationPointAsync</c>
    /// can't reproduce — it only writes linear), taints the turn → <c>.flp</c> rather than a lossy undo.</summary>
    public Task AutomationDeletedAsync(int channel, int index, Func<CancellationToken, Task> mutate, CancellationToken ct)
        => RecordBatchAsync(() => BuildDeletedAutomationRecords(channel, index, ct), mutate, ct);

    private async Task<List<ChangeRecord>?> BuildDeletedAutomationRecords(int channel, int index, CancellationToken ct)
    {
        var pts = AutomationPointList.Parse(await _fl.ListAutomationPointsAsync(channel, ct).ConfigureAwait(false));
        AutoPointInfo? found = null;
        foreach (var p in pts) if (p.Index == index) { found = p; break; }
        if (found is null || found.Value.Curve != 0) return null;   // absent, or a curve we can't restore → .flp
        var pt = found.Value;
        return new List<ChangeRecord>
        {
            new(0, ChangeOpKind.Delete, InverseOps.AutomationPoint,
                JournalDict.Of("channel", channel, "time", pt.Time),
                JournalDict.Of("channel", channel, "time", pt.Time, "value", pt.Value, "tension", pt.Tension),
                null),
        };
    }

    /// <summary>Run an arrangement CREATE (make/clone) and record it as a Create keyed by the returned index
    /// (undo deletes that index, redo re-runs the make/clone). Returns the new index for the tool's report.
    /// When journaling is off it just runs the create; if the create throws it taints and rethrows so a
    /// partial mutation can't leave an un-journaled commit.</summary>
    public async Task<int> ArrangementCreatedAsync(
        Func<CancellationToken, Task<int>> create, bool clone, int src, string? name, CancellationToken ct)
    {
        if (!Enabled) return await create(ct).ConfigureAwait(false);

        int index;
        try { index = await create(ct).ConfigureAwait(false); }
        catch { _journal!.Taint(); throw; }

        var @new = clone
            ? JournalDict.Of("kind", "clone", "src", src, "name", name ?? string.Empty)
            : JournalDict.Of("kind", "make", "name", name ?? string.Empty);
        _journal!.Append(new ChangeRecord(0, ChangeOpKind.Create, InverseOps.Arrangement,
            JournalDict.Of("arrangement", index), null, @new));
        return index;
    }
}
