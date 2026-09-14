using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Text.Json;
using FruityLink.Core.Abstractions;

namespace FruityLink.FlStudio.Inject;

// Patterns + piano-roll notes: pattern select/create/clear/list, note add/read, pattern refresh, pattern index validation.
// Partial of the FlInjectBridge god-class split; see FlInjectBridge.cs for the class doc.
public sealed partial class FlInjectBridge
{
    // ---- refresh helpers (safe; no audio side effects) -----------------------
    /// <summary>Repaint the channel rack + piano roll (FUN_0107EAD0).</summary>
    private Task RefreshRackAsync(CancellationToken ct) => CallAsync("107ead0", Array.Empty<ulong>(), ct);

    /// <summary>Safe pattern refresh after note edits: rebuild pattern + notify changed + refresh editors + repaint.
    /// Never use FUN_0107EB90 (corrupts audio) or d51810 (markers only — won't repaint notes).</summary>
    private async Task RefreshPatternAsync(int patIdx, CancellationToken ct)
    {
        await CallAsync("11d4140", new ulong[] { (uint)patIdx, 1 }, ct);  // rebuild pattern (length + playlist block)
        await CallAsync("f53d30", new ulong[] { (uint)patIdx }, ct);     // notify pattern changed
        await CallAsync("d421c0", Array.Empty<ulong>(), ct);             // refresh editor views
        await RefreshRackAsync(ct);
    }

    // ---- piano-roll notes (current pattern) ----------------------------------
    // Current pattern note store: arr = *(*(0x14ABA80)+0xC40)+0x57C ; arr is a vtable object whose
    // Add(NoteRec*) is at vtbl+8. NoteRec = 24 bytes (FLP note format). Refresh via 0xD37800 + 0xD51810.

    /// <summary>
    /// Add a note to a pattern's piano roll via the per-pattern note recorder (auto-creates it —
    /// no UI needed). <paramref name="pattern"/> is the 1-based pattern index, or &lt;= 0 for the
    /// current pattern. key = MIDI 0..131 (60 = middle C), startTick/lengthTick in PPQ ticks
    /// (see <see cref="GetPpqAsync"/>), velocity 0..127. Live-verified.
    /// </summary>
    public Task AddNoteAsync(int pattern, int channel, int key, int startTick, int lengthTick, int velocity, CancellationToken ct = default)
        => AddNotesAsync(pattern, new[] { new NoteSpec(channel, key, startTick, lengthTick, velocity) }, ct);

    /// <summary>
    /// Batch note authoring: resolve the pattern's note store and refresh the editor ONCE for the whole
    /// set (one complete-record append per note). Invalid input rejects the entire batch before mutation.
    /// </summary>
    public async Task AddNotesAsync(int pattern, IReadOnlyList<NoteSpec> notes, CancellationToken ct = default)
    {
        if (notes is null || notes.Count == 0) return;
        NoteSpec[] validated = await ValidateNoteBatchAsync(notes, ct);
        LogOp("AddNotes", $"pattern={pattern} notes={notes.Count}");

        int patIdx = pattern;
        if (patIdx <= 0)
        {
            patIdx = await GetCurrentPatternAsync(ct);
            if (patIdx == 0) throw new InvalidOperationException("No project open in FL Studio.");
        }
        ValidatePattern(patIdx);

        // FLpat_GetOrCreateNoteRecorder(patternIdx, 1) -> the pattern's note store (creates if absent).
        ulong rec = await CallAsync("11d4080", new ulong[] { (uint)patIdx, 1UL }, ct);
        if (rec == 0) throw new InvalidOperationException($"Could not get/create the note store for pattern {patIdx} (open a project in FL).");

        await AppendCompleteNotesAsync(patIdx, rec, validated, ct);
    }

    private async Task<NoteSpec[]> ValidateNoteBatchAsync(IReadOnlyList<NoteSpec> notes, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        NoteSpec[] snapshot = notes.ToArray();
        foreach (NoteSpec note in snapshot) ValidateNoteValues(note);
        int channels = await GetChannelCountAsync(ct);
        if (channels is < 1 or > 65536)
            throw new InvalidOperationException("The project's channel count is unavailable or invalid; no notes were added.");
        foreach (NoteSpec note in snapshot)
            if (note.Channel >= channels)
                throw new ArgumentOutOfRangeException(nameof(notes), $"Channel {note.Channel} does not exist (project has {channels} channels); no notes were added.");
        return snapshot;
    }

    private static void ValidateNoteValues(NoteSpec note)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(note.Channel);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(note.Channel, ushort.MaxValue);
        ArgumentOutOfRangeException.ThrowIfNegative(note.Key);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(note.Key, 131);
        ArgumentOutOfRangeException.ThrowIfNegative(note.Velocity);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(note.Velocity, 127);
        ValidateNoteTime(note.StartTick, note.LengthTick);
    }

    private static void ValidateNoteTime(int start, int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(start);
        ArgumentOutOfRangeException.ThrowIfLessThan(length, 1);
        if ((long)start + length > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(length), "The note's end tick exceeds the supported signed 32-bit timeline.");
    }

    private async Task AppendCompleteNotesAsync(int pattern, ulong recorder, NoteSpec[] notes, CancellationToken ct)
    {
        Exception? failure = null;
        bool attempted = false;
        try
        {
            foreach (NoteSpec note in notes)
            {
                ct.ThrowIfCancellationRequested();
                attempted = true;
                // Verified 25.2.5/26.1.3: this historic RecordNoteOn alias initializes and appends a
                // COMPLETE 24-byte record. Use actual duration, fine pitch 120 and release 64; the
                // recording-only NoteOff path applies live loop rules and is unnecessary here.
                await CallAsync("f6d740", new ulong[] { recorder, (uint)note.StartTick,
                    ((uint)note.Channel << 16) | 0x4000u, (uint)note.LengthTick, (uint)note.Key,
                    0x00400078UL, 0x80800040u | ((uint)note.Velocity << 8) }, CancellationToken.None);
            }
            ct.ThrowIfCancellationRequested();
        }
        catch (Exception error) { failure = error; }

        if (attempted)
        {
            try
            {
                await CommitNoteRecorderAsync(recorder, CancellationToken.None);
                await RefreshPatternAsync(pattern, CancellationToken.None);
            }
            catch (Exception cleanupError)
            {
                if (failure is null) throw;
                throw new AggregateException("Note append and finalization failed; inspect the pattern before retrying.", failure, cleanupError);
            }
        }
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    /// <summary>Commit a pattern's note recorder (recorder->vtbl[3] @ vt+0x18; compaction) after edits.</summary>
    private Task CommitNoteRecorderAsync(ulong rec, CancellationToken ct)
        => CallVtblAsync(rec, 0x18, "Note commit", new[] { rec }, ct);

    /// <summary>Project timebase: ticks per quarter note (PPQ). Falls back to 96.</summary>
    public async Task<int> GetPpqAsync(CancellationToken ct = default)
    {
        ulong p = await GPtrAsync("14a79f8", ct);
        if (p == 0) return 96;
        return await AI32Async(p, ct);
    }

    // ============================ Patterns ============================

    /// <summary>Current (selected) pattern index, 1-based.</summary>
    public async Task<int> GetCurrentPatternAsync(CancellationToken ct = default)
    {
        ulong p = await GPtrAsync("14ab580", ct);
        return p == 0 ? 0 : await AI32Async(p, ct);
    }

    /// <summary>Select/switch to a pattern (1-based); full UI switch. FLpat_SetCurrentPattern.</summary>
    public Task SelectPatternAsync(int index, CancellationToken ct = default)
    {
        ValidatePattern(index);
        return CallAsync("cbb300", new ulong[] { (uint)index }, ct);
    }

    /// <summary>True if a pattern has no notes/automation/name. FLpat_IsPatternEmpty.</summary>
    public async Task<bool> IsPatternEmptyAsync(int index, CancellationToken ct = default)
    {
        ValidatePattern(index);
        return await CallAsync("11db510", new ulong[] { (uint)index, 1, 0, 0 }, ct) != 0;
    }

    /// <summary>Select the first empty pattern (a "new" pattern); returns its index.</summary>
    public async Task<int> CreatePatternAsync(CancellationToken ct = default)
    {
        LogOp("CreatePattern");
        for (int i = 1; i <= 999; i++)
            if (await IsPatternEmptyAsync(i, ct)) { await SelectPatternAsync(i, ct); return i; }
        throw new InvalidOperationException("No empty pattern slot available.");
    }

    /// <summary>Delete all notes in a pattern (recorder clear + commit + refresh).</summary>
    public async Task ClearPatternAsync(int index, CancellationToken ct = default)
    {
        LogOp("ClearPattern", $"index={index}");
        ValidatePattern(index);
        ulong rec = await CallAsync("11d4080", new ulong[] { (uint)index, 1 }, ct);
        if (rec == 0) return;
        await CallAsync("11e0930", new ulong[] { rec }, ct);   // FLpat_NoteArray_Clear
        await CommitNoteRecorderAsync(rec, ct);
        await RefreshPatternAsync(index, ct);
    }

    /// <summary>Rename a pattern (1-based). Uses an FL-OWNED heap string (see <see cref="MakeOwnedDelphiStringAsync"/>)
    /// so the setter's UStrAsg-share keeps a durable reference — the name survives scratch reuse AND save/reload
    /// (the previously-deferred rename bug). FLpat_SetPatternNameAndNotify self-refreshes the UI.</summary>
    public async Task SetPatternNameAsync(int index, string name, CancellationToken ct = default)
    {
        ValidatePattern(index);
        LogOp("SetPatternName", $"index={index}");
        using var scratch = await LeaseScratchAsync(ct).ConfigureAwait(false);
        ulong str = await MakeOwnedDelphiStringAsync(name ?? string.Empty, scratch, ct);
        await CallAsync("11d3960", new ulong[] { (uint)index, str }, ct);   // FLpat_SetPatternNameAndNotify
    }

    /// <summary>A pattern's display name.</summary>
    public async Task<string> GetPatternNameAsync(int index, CancellationToken ct = default)
    {
        ulong namePtr = await GPtrAsync((0x1803B68 + (long)index * 0xC0).ToString("x"), ct);
        string s = await ReadDelphiStringAsync(namePtr, ct);
        return string.IsNullOrEmpty(s) ? $"Pattern {index}" : s;
    }

    /// <summary>List patterns that have content: "idx: name [len ticks = bars, N notes] (current)".
    /// Length + note count ride along because they're what the model needs next: clip placement math
    /// (start ticks) needs the length, and the note count separates real parts from named-but-empty
    /// shells — without them every arrange turn burned extra get_notes probes.</summary>
    public async Task<string> ListPatternsAsync(CancellationToken ct = default)
    {
        int cur = await GetCurrentPatternAsync(ct);
        int ppqBar = await GetPpqAsync(ct) * 4;   // bars assume 4/4, same as GetSongStateAsync
        ulong patArr = await GPtrAsync("14aa0c8", ct);
        var sb = new System.Text.StringBuilder();
        for (int i = 1; i <= 256; i++)
        {
            if (await IsPatternEmptyAsync(i, ct)) continue;
            sb.Append(i).Append(": ").Append(await GetPatternNameAsync(i, ct));
            // Length from the same per-pattern field AddPatternClipsAsync resolves (+0x50); note count
            // from the note recorder GetNotesAsync reads (+0x14). Implausible values (garbage on a
            // never-realized pattern) degrade to "unshown", never to a wrong number.
            int len = patArr != 0 ? await AI32Async(patArr + (ulong)i * 0xC0 + 0x50, ct) : 0;
            ulong rec = await GPtrAsync((0x1803B90 + (ulong)i * 0xC0).ToString("x"), ct);
            int notes = rec != 0 ? await AI32Async(rec + 0x14, ct) : 0;
            if (notes is < 0 or > 1_000_000) notes = 0;
            if (len is > 0 and <= 100_000_000)
            {
                sb.Append(" [").Append(len).Append(" ticks");
                if (ppqBar > 0) sb.Append(" = ").Append((len / (double)ppqBar).ToString("0.##")).Append(" bars");
                sb.Append(", ").Append(notes).Append(" notes]");
            }
            else if (notes > 0) sb.Append(" [").Append(notes).Append(" notes]");
            if (i == cur) sb.Append(" (current)");
            sb.Append('\n');
        }
        return sb.Length == 0 ? "(no patterns with content)" : sb.ToString().TrimEnd();
    }

    private static string KeyName(int key)
    {
        string[] n = { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };
        return n[((key % 12) + 12) % 12] + (key / 12);  // FL labels MIDI 60 as C5
    }

    // ---- read notes ----
    /// <summary>Max note LINES per GetNotes call: ~40 chars each keeps a full page (~5.3KB) under
    /// the agent's 6KB tool-result cap, so a busy pattern pages cleanly (offset) instead of being
    /// cut mid-list by the truncation filter.</summary>
    private const int NotesPageSize = 140;

    /// <summary>Reads piano-roll notes of a pattern (pattern: 1-based, or &lt;=0 = current), paged.
    /// channel&lt;0 = all channels; offset skips the first N notes (raw index — the continuation
    /// hint in the output feeds straight back in).</summary>
    public async Task<string> GetNotesAsync(int pattern, int channel, int offset = 0, CancellationToken ct = default)
    {
        // The note-recorder static array 0x1803B90 is indexed by the 1-based pattern number directly
        // (verified: 11d4080(N) <-> static[N]), the SAME index AddNotesAsync writes to — so add/read
        // round-trips on any pattern. (No -1: that read static[pattern-1] and reported "no notes".)
        int patIdx = pattern <= 0 ? await GetCurrentPatternAsync(ct) : pattern;
        ValidatePattern(patIdx);
        ulong rec = await NoteRecorderAsync(patIdx, ct);
        if (rec == 0) return $"(pattern {patIdx} has no notes)";
        int count = CheckedQueryCount(await AI32Async(rec + 0x14, ct), 1_000_000);
        ulong data = await APtrAsync(rec + 8, ct);
        if (count <= 0 || data == 0) return $"(pattern {patIdx} has no notes)";
        if (offset < 0) offset = 0;
        if (offset >= count) return $"(pattern {patIdx}: offset {offset} is past the last note — {count} notes total)";
        int n = Math.Min(count - offset, 300);
        byte[] all = await PeekAbsAsync(data + (ulong)offset * 0x18, n * 0x18, ct);
        var (rows, shown, nextOffset) = FormatNoteRows(all, channel, offset);
        if (nextOffset < 0 && offset + n < count) nextOffset = offset + n;
        if (shown == 0)
            return channel >= 0
                ? $"(no notes on channel {channel} in pattern {patIdx}{(nextOffset >= 0 ? $" here — more notes exist, call again with offset={nextOffset}" : "")})"
                : $"(pattern {patIdx} has no notes)";
        string more = nextOffset >= 0 ? $"\n(more — call again with offset={nextOffset})" : "";
        return $"Pattern {patIdx}: showing {shown} of {count} notes{(channel >= 0 ? $" (channel {channel} only)" : "")}{(offset > 0 ? $" from offset {offset}" : "")}:\n"
            + rows + more;
    }

    private static (string rows, int shown, int nextOffset) FormatNoteRows(byte[] all, int channel, int offset)
    {
        var sb = new StringBuilder();
        int shown = 0;
        int nextOffset = -1;   // raw index to continue from, or -1 = nothing left
        for (int i = 0; i < all.Length / NoteStride; i++)
        {
            var note = DecodeQueryNote(all, i * NoteStride, offset + i);
            int ch = note.Channel;
            if (channel >= 0 && ch != channel) continue;
            if (shown == NotesPageSize) { nextOffset = offset + i; break; }
            int pos = note.StartTick, len = note.LengthTick, key = note.Key, vel = note.Velocity;
            bool muted = note.Muted;
            sb.Append($"ch{ch} {KeyName(key)}({key}) pos={pos} len={len} vel={vel}{(muted ? " muted" : "")}\n");
            shown++;
        }
        return (sb.ToString().TrimEnd(), shown, nextOffset);
    }

    // ---- surgical note edit / delete (in-place on the 24-byte struct array) ----
    // The note store is a contiguous array of 24-byte NoteRec structs: rec = *(0x1803B90 + patIdx*0xC0),
    // count @ rec+0x14, data ptr @ rec+8 (the SAME array GetNotesAsync reads + AddNotesAsync writes).
    // Editing/deleting works directly on these bytes so EVERY field survives (pos@0, chan(u16)@6, len@8,
    // key(u16)@0xC, finePitch@0x10, release@0x12, muted bit @0x13&0x20, pan@0x14, vel@0x15, cut@0x16, res@0x17)
    // — a clear+re-add through the recorder would reset the fields AddNotes doesn't set. We only ever shrink
    // or rewrite in place (never grow past `count`), so no reallocation/capacity concern.
    private const int NoteStride = 0x18;
    private static int NoteCh(byte[] n)  => BitConverter.ToUInt16(n, 6);
    private static int NoteKey(byte[] n) => BitConverter.ToUInt16(n, 0xC);
    private static int NotePos(byte[] n) => BitConverter.ToInt32(n, 0);
    private static int NoteLen(byte[] n) => BitConverter.ToInt32(n, 8);

    /// <summary>Resolves every target to the indices of the notes it addresses in <paramref name="notes"/>:
    /// the (channel, key, startTick) triple, narrowed by lengthTick when the target supplies one. FL allows
    /// stacked duplicates that share the triple (and even the length), so a target that matches several notes
    /// is refused — before anything is written — unless <paramref name="allowMultiple"/> is set, in which case
    /// all of them are addressed. A target that matches nothing yields an empty list (the caller's count
    /// reports it). Matching runs against the ORIGINAL snapshot, so one edit's move can't shadow another's.</summary>
    internal static List<int>[] MatchNoteTargets(IReadOnlyList<byte[]> notes,
        IReadOnlyList<(int Channel, int Key, int StartTick, int? LengthTick)> targets, bool allowMultiple)
    {
        var result = new List<int>[targets.Count];
        for (int t = 0; t < targets.Count; t++)
        {
            var (channel, key, start, length) = targets[t];
            var hits = new List<int>();
            for (int i = 0; i < notes.Count; i++)
            {
                byte[] n = notes[i];
                if (NoteCh(n) != channel || NoteKey(n) != key || NotePos(n) != start) continue;
                if (length is int want && NoteLen(n) != want) continue;
                hits.Add(i);
            }
            if (hits.Count > 1 && !allowMultiple)
            {
                string lengths = string.Join(", ", hits.Select(i => NoteLen(notes[i])));
                string narrowed = length is int l ? $", length {l}" : "";
                throw new InvalidOperationException(
                    $"Note (channel {channel}, key {key}, tick {start}{narrowed}) matches {hits.Count} stacked notes (lengths {lengths}); " +
                    "no notes were changed. Set lengthTick to address one of them, or pass allowMultiple=true to address all of them.");
            }
            result[t] = hits;
        }
        return result;
    }

    /// <summary>Reads a pattern's raw note structs (each a 24-byte copy) plus the recorder + data pointers,
    /// or null when the pattern has no note store. An empty (count 0) store returns an empty list.</summary>
    private async Task<(ulong rec, ulong data, List<byte[]> notes)?> ReadNoteStructsAsync(int patIdx, CancellationToken ct)
    {
        ulong rec = await NoteRecorderAsync(patIdx, ct);
        if (rec == 0) return null;
        int count = CheckedQueryCount(await AI32Async(rec + 0x14, ct), 1_000_000);
        ulong data = await APtrAsync(rec + 8, ct);
        var list = new List<byte[]>(Math.Max(0, count));
        if (count > 0 && data != 0)
        {
            byte[] all = await PeekAbsAsync(data, count * NoteStride, ct);
            for (int i = 0; i < count; i++)
            {
                var b = new byte[NoteStride];
                Array.Copy(all, i * NoteStride, b, 0, NoteStride);
                list.Add(b);
            }
        }
        return (rec, data, list);
    }

    /// <summary>Writes the (possibly shrunk) note list back to the store: sort by position so the array
    /// stays ordered even after a moved note, poke the structs verbatim, set the count, compact, refresh.</summary>
    private async Task WriteNoteStructsAsync(int patIdx, ulong rec, ulong data, List<byte[]> notes, CancellationToken ct)
    {
        notes.Sort((a, b) => NotePos(a).CompareTo(NotePos(b)));
        if (notes.Count > 0 && data != 0)
        {
            byte[] buf = new byte[notes.Count * NoteStride];
            for (int i = 0; i < notes.Count; i++) Array.Copy(notes[i], 0, buf, i * NoteStride, NoteStride);
            await PokeAbsAsync(data, buf, ct);
        }
        await PokeAbsAsync(rec + 0x14, BitConverter.GetBytes(notes.Count), ct);   // count (int32)
        await CommitNoteRecorderAsync(rec, ct);
        await RefreshPatternAsync(patIdx, ct);
    }

    /// <summary>Edit existing notes in place (matched by original channel+key+startTick, optionally lengthTick),
    /// preserving every other note + every unspecified field. An edit matching several stacked duplicates is
    /// refused before any write unless <paramref name="allowMultiple"/>. Returns the number of notes changed.</summary>
    public async Task<int> EditNotesAsync(int pattern, IReadOnlyList<NoteEdit> edits, bool allowMultiple = false, CancellationToken ct = default)
    {
        if (edits is null || edits.Count == 0) return 0;
        int patIdx = pattern <= 0 ? await GetCurrentPatternAsync(ct) : pattern;
        ValidatePattern(patIdx);
        LogOp("EditNotes", $"pattern={patIdx} edits={edits.Count} allowMultiple={allowMultiple}");

        var read = await ReadNoteStructsAsync(patIdx, ct);
        if (read is null) throw new InvalidOperationException($"Pattern {patIdx} has no note store (open a project in FL).");
        var (rec, data, notes) = read.Value;
        if (notes.Count == 0) return 0;

        // Resolve every edit against the ORIGINAL snapshot (so one edit's move can't shadow another's) and
        // refuse ambiguous targets before touching any struct.
        var matches = MatchNoteTargets(notes, edits.Select(e => (e.Channel, e.Key, e.StartTick, e.LengthTick)).ToList(), allowMultiple);
        int changed = 0;
        for (int t = 0; t < edits.Count; t++)
        {
            foreach (int i in matches[t])
            {
                ApplyNoteEdit(notes[i], edits[t]);
                changed++;
            }
        }
        if (changed > 0) await WriteNoteStructsAsync(patIdx, rec, data, notes, ct);
        return changed;
    }

    private static void ApplyNoteEdit(byte[] note, NoteEdit edit)
    {
        ValidateSuppliedNoteEdit(note, edit);
        if (edit.NewKey is int key) BitConverter.GetBytes((ushort)key).CopyTo(note, 0xC);
        if (edit.NewStartTick is int start) BitConverter.GetBytes(start).CopyTo(note, 0);
        if (edit.NewLength is int length) BitConverter.GetBytes(length).CopyTo(note, 8);
        if (edit.NewVelocity is int velocity) note[0x15] = (byte)velocity;
        if (edit.Muted is bool muted) note[0x13] = (byte)(muted ? note[0x13] | 0x20 : note[0x13] & ~0x20);
    }

    private static void ValidateSuppliedNoteEdit(byte[] note, NoteEdit edit)
    {
        if (edit.NewKey is int key)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(key);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(key, 131);
        }
        if (edit.NewVelocity is int velocity)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(velocity);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(velocity, 127);
        }
        if (edit.NewStartTick.HasValue || edit.NewLength.HasValue)
            ValidateNoteTime(edit.NewStartTick ?? NotePos(note), edit.NewLength ?? BitConverter.ToInt32(note, 8));
    }

    /// <summary>Delete specific notes (matched by channel+key+startTick, optionally lengthTick), keeping the rest
    /// of the pattern. A target matching several stacked duplicates is refused before any write unless
    /// <paramref name="allowMultiple"/>. Returns the number deleted.</summary>
    public async Task<int> DeleteNotesAsync(int pattern, IReadOnlyList<NoteRef> targets, bool allowMultiple = false, CancellationToken ct = default)
    {
        if (targets is null || targets.Count == 0) return 0;
        int patIdx = pattern <= 0 ? await GetCurrentPatternAsync(ct) : pattern;
        ValidatePattern(patIdx);
        LogOp("DeleteNotes", $"pattern={patIdx} targets={targets.Count} allowMultiple={allowMultiple}");

        var read = await ReadNoteStructsAsync(patIdx, ct);
        if (read is null) throw new InvalidOperationException($"Pattern {patIdx} has no note store (open a project in FL).");
        var (rec, data, notes) = read.Value;
        if (notes.Count == 0) return 0;

        var matches = MatchNoteTargets(notes, targets.Select(t => (t.Channel, t.Key, t.StartTick, t.LengthTick)).ToList(), allowMultiple);
        var kill = new HashSet<int>(matches.SelectMany(m => m));
        var survivors = notes.Where((_, i) => !kill.Contains(i)).ToList();
        int deleted = notes.Count - survivors.Count;
        if (deleted > 0) await WriteNoteStructsAsync(patIdx, rec, data, survivors, ct);
        return deleted;
    }

    /// <summary>Clone a pattern's notes into a fresh empty pattern; returns the new 1-based index (0 = nothing
    /// to clone). Copies the full 24-byte structs into the new pattern's store via the SAME poke+commit+refresh
    /// path, so every note field is preserved. (Notes only — automation/name/color are a later upgrade.)</summary>
    public async Task<int> ClonePatternAsync(int sourcePattern, CancellationToken ct = default)
    {
        int srcIdx = sourcePattern <= 0 ? await GetCurrentPatternAsync(ct) : sourcePattern;
        ValidatePattern(srcIdx);
        LogOp("ClonePattern", $"source={srcIdx}");

        var read = await ReadNoteStructsAsync(srcIdx, ct);
        if (read is null || read.Value.notes.Count == 0) return 0;   // nothing to clone
        List<byte[]> notes = read.Value.notes;

        // 1) Add every note with its basic fields — this ALLOCATES the destination's note-array (count = N)
        //    through the proven recorder path, so we don't have to hand-seed the store.
        var specs = notes.Select(n => new NoteSpec(
            NoteCh(n), NoteKey(n), NotePos(n), BitConverter.ToInt32(n, 8), Math.Min(n[0x15], (byte)127))).ToList();
        await ValidateNoteBatchAsync(specs, ct); // Reject orphan source notes before selecting a destination.
        int destIdx = await CreatePatternAsync(ct);   // first empty pattern, selected
        await AddNotesAsync(destIdx, specs, ct);

        // 2) Overwrite the freshly-added structs with the FULL originals so pan/fine-pitch/release/cut/res/
        //    mute survive too. The counts match (N notes), so this is an in-place rewrite of the same array.
        var destRead = await ReadNoteStructsAsync(destIdx, ct);
        if (destRead is { } dr && dr.data != 0 && dr.notes.Count == notes.Count)
            await WriteNoteStructsAsync(destIdx, dr.rec, dr.data, new List<byte[]>(notes), ct);
        return destIdx;
    }

    // Pattern indices feed FL's per-pattern static arrays (note recorder DAT_01803B90+N*0xC0, param
    // recorder +0x20, etc.) DIRECTLY. FL's pattern array is only ~1000 slots (patterns 1..999); a larger
    // index reads adjacent .data (e.g. -1) and, because a pattern-clip's paint (FUN_00ccc330) reads
    // patternArray[N]+0x20 with only a "!= 0" guard, an out-of-range N decodes to a garbage pointer and
    // AVs inside FL's playlist paint on the UI thread (fatal, uncatchable). Cap at FL's real max 999.
    private const int MaxPatternIndex = 999;
    private static void ValidatePattern(int idx)
    {
        if (idx < 1 || idx > MaxPatternIndex)
            throw new InvalidOperationException($"Pattern {idx} is out of range (1..{MaxPatternIndex}).");
    }
}
