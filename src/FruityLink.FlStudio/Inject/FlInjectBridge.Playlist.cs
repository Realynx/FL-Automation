using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Text.Json;
using FruityLink.Core.Abstractions;

namespace FruityLink.FlStudio.Inject;

// Playlist: tracks (name/color/mute/collapse), clips (list/add/move/resize/delete/slice/duplicate), song-length recompute.
// Partial of the FlInjectBridge god-class split; see FlInjectBridge.cs for the class doc.
public sealed partial class FlInjectBridge
{
    // ---- playlist tracks ----
    // Track N struct = root + N*0x114. Field offsets (RE-verified) are shared by the getters, setters and
    // ListPlaylistTracksAsync, so each getter reads exactly the field its setter writes — structurally.
    private const ulong PlaylistTrackStride = 0x114;
    private const int TrackFieldName        = 0x24;  // Delphi UnicodeString ptr ("" / 0 = default)
    private const int TrackFieldColor       = 0x2c;  // BGR color
    private const int TrackFieldEnabled     = 0x3c;  // byte: 0 = muted
    private const int TrackFieldCustomColor = 0x70;  // byte flag: 0 = follows the theme default color
    private const int TrackFieldMode        = 0xd8;  // int: 0 normal / 1 audio / 3 instrument
    private const int TrackFieldCollapsed   = 0xe4;  // byte
    private const int TrackFieldSelected    = 0xe5;  // byte

    /// <summary>Absolute address of a playlist track's field: root + track*0x114 + field.</summary>
    private static ulong TrackField(ulong root, int track, int field) => root + (ulong)track * PlaylistTrackStride + (ulong)field;

    /// <summary>FL stores playlist-track colors as BGR; convert to the RGB every public surface uses.</summary>
    private static uint BgrToRgb(uint c) => (c & 0xFF) << 16 | (c & 0xFF00) | (c >> 16) & 0xFF;

    /// <summary>Inverse of <see cref="BgrToRgb"/> for writing a color back to the track struct.</summary>
    private static uint RgbToBgr(int rgb) => (uint)(((rgb >> 16) & 0xFF) | (((rgb >> 8) & 0xFF) << 8) | ((rgb & 0xFF) << 16));

    private Task<ulong> PlaylistRootAsync(CancellationToken ct) => CallAsync("11e32c0", Array.Empty<ulong>(), ct);

    private async Task RepaintPlaylistAsync(CancellationToken ct)
    {
        // 0x14aab88 is a POINTER-TO-POINTER (PTR_DAT): *(0x14aab88) is an in-module data slot, and the
        // real playlist object is *(that) = a heap pointer. FLpl_RepaintPlaylist(da40c0) dereferences its
        // arg as the object, so passing the single-deref'd in-module slot FAULTS (SEH-caught, silent) and
        // nothing repaints — THIS was the "clips look broken until you click them" bug. Double-deref.
        ulong slot = await GPtrAsync("14aab88", ct);
        if (slot == 0) return;
        ulong pl = await APtrAsync(slot, ct);
        if (pl != 0) await CallAsync("da40c0", new ulong[] { pl }, ct);
    }

    // Recompute the arrangement's SONG LENGTH so the transport can advance the playhead across the clips we
    // just changed. ROOT CAUSE of the "press play but the playhead is stuck / newly-arranged clips don't
    // play" bug: FL confines the playhead to a cached play-range whose end is the SONG LENGTH. FL derives
    // that length from the playlist only on specific events — an arrangement switch, or a pattern whose own
    // playlist-block LENGTH changed (FLpat_RebuildPatternAndRefresh @0x11D4140 → FUN_00d37450). A raw clip
    // insert/move/resize that references an already-sized pattern triggers NONE of those, so the song keeps
    // its OLD length: clips past the old end fall outside the play-range and the transport "plays" but loops
    // inside the stale range (stranded near tick 0/1 when the prior song was short/empty).
    //
    // TWO-STEP FIX (the single-step version RECURRED 2026-07-03 — see below):
    //  (1) Re-select the CURRENT arrangement via FLpl_SetCurrentArrangement(@0x11FC880). Selecting the same
    //      index runs FL's full sanctioned refresh (rebinds the playlist sub-object, re-derives the play-range
    //      + toolbar slider, redraws) with no data loss. DECOMPILE (re-verified 2026-07-03): there is NO
    //      "if(new==current) return" short-circuit — only the tail usage-timer FUN_010cefd0 + a UI-poke are
    //      gated on the "index actually changed" flag; the song-length recompute FUN_00d37450(songObj,1) runs
    //      UNCONDITIONALLY. So the same-index re-select is NOT a no-op (live: play-range [0..3839]→[0..34559]).
    //  (2) BUT FUN_00d37450's playlist scan that sets the length (songObj+0xB04 = max clip end) is GATED on
    //      songObj+0xB00>=2 and dereferences the possibly-null playlist sub-object songObj+0xD04. In the live
    //      GUI those hold (b00=2, d04 valid) so step 1 alone works — which is exactly why the harness passed
    //      while a real session still got stuck: in ANY state where that gate/deref doesn't hold, step 1
    //      silently leaves the song length SHORT. So AFTER step 1 we ALSO recompute the length DIRECTLY from
    //      the clip collection (mode- and d04-INDEPENDENT) and GROW songObj+0xB04 to cover the furthest clip.
    //      Grow-only: a delete/resize SHRINK is handled by step 1's scan, and any marker-based length is kept.
    //      (Dead ends still avoided: we DON'T poke the transient slider range globals and DON'T drive
    //      FUN_010ce8a0 off the bus — both were live-confirmed to be overwritten/wedge FL. b04 written to the
    //      true clip max is stable across transport ticks — live-verified it holds [0..34559] through playback.)
    // Both steps are SEH-guarded on the bridge; a fault must never fail the edit.
    private async Task RecomputeSongLengthAsync(CancellationToken ct)
    {
        int idx = BitConverter.ToInt32(await PeekAsync("149e8b4", 4, ct), 0);   // current arrangement index
        LogOp("RecomputeSongLength", $"arrangement={idx}");
        if (idx < 0) return;
        try { await CallAsync("11fc880", new ulong[] { (uint)idx }, ct); }      // (1) FLpl_SetCurrentArrangement(current)
        catch (InvalidOperationException) { /* refresh is best-effort; never break the edit */ }
        try { await SetSongLengthToContentAsync(ct); }                          // (2) authoritative, mode-independent
        catch (InvalidOperationException) { /* best-effort */ }
    }

    /// <summary>The song object: <c>*(*(0x14aab88))</c> (a .data→.bss DOUBLE deref; single-deref reads were
    /// "one deref short" — see re/12 §Transport). Its +0xB04 field is the cached song length in ticks.</summary>
    private async Task<ulong> SongObjAsync(CancellationToken ct)
    {
        ulong slot = await GPtrAsync("14aab88", ct);
        return slot != 0 ? await APtrAsync(slot, ct) : 0;
    }

    /// <summary>Furthest playlist-clip END tick (max of start+len) over the live clip collection — the value
    /// FL's own scan computes for the song length, but derived here WITHOUT FL's mode-gate / null-deref risk.
    /// Every slot 0..count(+0x14) is a real clip (see <see cref="ListClipsAsync"/>); holes (src 0 / len&lt;=0)
    /// are skipped.</summary>
    private async Task<long> MaxClipEndAsync(CancellationToken ct)
    {
        var (data, stride, count) = await ClipCollectionAsync(ct);
        if (data == 0 || stride <= 0 || count <= 0) return 0;
        long max = 0;
        for (int i = 0; i < count; i++)
        {
            byte[] cb = await PeekAbsAsync(data + (ulong)i * (ulong)stride, 0x14, ct);
            int start = BitConverter.ToInt32(cb, 0);
            int len = BitConverter.ToInt32(cb, 8);
            // "Active/placed" is the +0x13 & 0x80 flag AddPatternClips sets and DeleteClips clears — NOT
            // src==0: src is (channelIndex<<16) for audio clips, so channel 0 is a legit source id of 0
            // and the old src==0 skip UNDER-counted it (would strand a channel-0 audio clip past a shorter
            // clip). Skip inactive holes + malformed spans; count everything else.
            if ((cb[0x13] & 0x80) == 0 || start < 0 || len <= 0) continue;
            long end = (long)start + len;
            if (end > max) max = end;
        }
        return max;
    }

    /// <summary>Furthest timeline-marker tick (marker manager @songObj+0xd5c; entries stride 0x34, tick @+0)
    /// — folded into the song length so an INTENTIONAL end-marker placed past the last clip still sizes the
    /// song (this is why the length recompute must be max(clipEnd, markerEnd), matching FL's FUN_00d37450).</summary>
    private async Task<long> MaxMarkerTickAsync(ulong songObj, CancellationToken ct)
    {
        try
        {
            ulong mgr = await APtrAsync(songObj + 0xd5c, ct);
            if (mgr == 0) return 0;
            ulong a = await APtrAsync(mgr, ct);
            if (a == 0) return 0;
            int count = await AI32Async(a - 8, ct);
            long max = 0;
            for (int i = 0; i < count && i < 512; i++)
            {
                int tick = await AI32Async(a + (ulong)i * 0x34, ct);
                if (tick > max) max = tick;
            }
            return max;
        }
        catch (InvalidOperationException) { return 0; }
    }

    /// <summary>Step (2) of the song-length recompute: set the cached song length (songObj+0xB04) to the
    /// TRUE content length = max(furthest clip end, furthest marker tick) — FL's own FUN_00d37450 definition,
    /// so FL agrees and won't fight it. AUTHORITATIVE (grow AND shrink) + mode/d04-INDEPENDENT: fixes BOTH the
    /// stale-SHORT stuck-playhead bug AND the stale-LONG "extend → song too long → plays but never advances"
    /// regression that grow-only could never undo when FL's gated scan (step 1) no-ops. Clamped to a ceiling
    /// so one mis-placed/garbage clip can't push b04 near int.MaxValue (which collapses FL's per-tick play-range
    /// recompute → stranded playhead).</summary>
    private async Task SetSongLengthToContentAsync(CancellationToken ct)
    {
        ulong songObj = await SongObjAsync(ct);
        if (songObj <= 0x10000) return;
        long want = Math.Max(await MaxClipEndAsync(ct), await MaxMarkerTickAsync(songObj, ct));
        if (want <= 0) return;                       // no content to size to — leave FL's value alone
        const long TickCeiling = 0x40000000;         // ~279k bars @ ppq 960; keeps b04 far from int.MaxValue
        if (want > TickCeiling) want = TickCeiling;
        if (await AI32Async(songObj + 0xb04, ct) != (int)want)
            await PokeAbsAsync(songObj + 0xb04, BitConverter.GetBytes((int)want), ct);
    }

    /// <summary>DIAGNOSTIC/TEST hook (harness only): reports the song-object scope field (b00), the cached
    /// song length (b04) and the true furthest clip end (maxClipEnd). Optionally FORCES b04 to a value first
    /// (to SIMULATE a stale/failed FL scan) and/or runs the direct grow-recompute — so a test can prove step
    /// (2) grows the length back regardless of FL's gated path. Never called by product code.</summary>
    public async Task<string> DiagSongScopeAsync(int forceB04 = int.MinValue, bool recompute = false, CancellationToken ct = default)
    {
        ulong songObj = await SongObjAsync(ct);
        long maxEnd = await MaxClipEndAsync(ct);
        if (songObj > 0x10000 && forceB04 != int.MinValue)
            await PokeAbsAsync(songObj + 0xb04, BitConverter.GetBytes(forceB04), ct);
        if (recompute) await SetSongLengthToContentAsync(ct);
        int b00 = songObj > 0x10000 ? await AI32Async(songObj + 0xb00, ct) : int.MinValue;
        int b04 = songObj > 0x10000 ? await AI32Async(songObj + 0xb04, ct) : int.MinValue;
        return $"songObj=0x{songObj:x} b00={b00} b04={b04} maxClipEnd={maxEnd}";
    }

    // A pattern clip's on-screen length is a DERIVED CACHE (clip+0x08) that FLpl_RepaintPlaylist does NOT
    // recompute — so after a raw insert the clip paints stale ("broken until you click it"). The proven
    // fix is FL's own pattern rebuild (RefreshPatternAsync → 11d4140), the SAME path native_add_notes uses:
    // it re-resolves the pattern's playlist clips (internally via d49840) + marks dirty. Then repaint the
    // window. (Calling d49840 directly needs the song object, not the playlist-window ptr — this avoids that.)
    private async Task RefreshPatternClipsAsync(int patternNo, CancellationToken ct)
    {
        if (patternNo > 0) await RefreshPatternAsync(patternNo, ct);
        await RepaintPlaylistAsync(ct);
    }

    /// <summary>Lists playlist tracks 1..50 (name, color, mute, collapse, selection, mode), SKIPPING
    /// pristine default tracks: an empty project would otherwise emit 50 identical "Track N" lines —
    /// pure token waste. "Pristine" = unnamed, unmuted, expanded, unselected, normal mode, AND no
    /// custom color. Color is tested via the track's custom-color FLAG (+0x70, RE-verified: 0 =
    /// follows the theme default; FL's own SetTrackNameAndColor — the routine behind
    /// native_set_track_color — sets it), not a guessed default color constant, so a track whose
    /// only customization is its color is always shown (fail-open). A trailing default run is
    /// summarized as "(tracks N-50 default)".</summary>
    public async Task<string> ListPlaylistTracksAsync(CancellationToken ct = default)
    {
        ulong root = await PlaylistRootAsync(ct);
        if (root == 0) return "(playlist not available)";
        string[] modes = { "normal", "audio", "?", "instrument" };
        var sb = new StringBuilder();
        int lastCustom = 0;
        for (int i = 1; i <= 50; i++)
        {
            ulong t = TrackField(root, i, 0);
            byte[] tr = await PeekAbsAsync(t, (int)PlaylistTrackStride, ct);
            string name = await ReadDelphiStringAsync(BitConverter.ToUInt64(tr, TrackFieldName), ct);
            uint c = BitConverter.ToUInt32(tr, TrackFieldColor);
            uint rgb = BgrToRgb(c);
            byte enabled = tr[TrackFieldEnabled], customColor = tr[TrackFieldCustomColor], collapsed = tr[TrackFieldCollapsed], selected = tr[TrackFieldSelected];
            int mode = BitConverter.ToInt32(tr, TrackFieldMode);
            if (string.IsNullOrEmpty(name) && enabled != 0 && customColor == 0 && collapsed == 0 && selected == 0 && mode == 0)
                continue;  // pristine default — unlisted (the header says so)
            lastCustom = i;
            string disp = string.IsNullOrEmpty(name) ? $"Track {i}" : name;
            sb.Append($"{i}: {disp} [#{rgb:X6}{(enabled == 0 ? " MUTED" : "")}{(collapsed != 0 ? " collapsed" : "")}{(selected != 0 ? " selected" : "")}{(mode >= 0 && mode < modes.Length ? " " + modes[mode] : "")}]\n");
        }
        if (sb.Length == 0) return "Playlist tracks 1-50: all default (unnamed, unmuted).";
        string tail = lastCustom < 50 ? $"\n(tracks {lastCustom + 1}-50 default)" : "";
        return "Playlist tracks 1-50 (unlisted = default):\n" + sb.ToString().TrimEnd() + tail;
    }

    public async Task SetTrackNameAsync(int track, string name, CancellationToken ct = default)
    {
        ulong root = await PlaylistRootAsync(ct);
        if (root == 0) throw new InvalidOperationException("Playlist not available.");
        uint color = BitConverter.ToUInt32(await PeekAbsAsync(TrackField(root, track, TrackFieldColor), 4, ct), 0);
        // NOTE: still the transient scratch-const string (as shipped). This rename is on FL's UStrAsg-SHARE
        // path so it's technically fragile (see MakeOwnedDelphiStringAsync), but it WORKS today — leaving it
        // until the shared makeUStr helper is live-verified, so a regression can't slip into a working tool.
        ulong strPtr = await WriteDelphiStringAsync(name, ct);
        await CallAsync("11e7940", new ulong[] { root, (uint)track, strPtr, color }, ct);  // SetTrackNameAndColor (self-refreshes)
    }

    /// <summary>Toggle exclusive solo on a playlist track (solo again = un-solo). FLpl_SetTrackSolo
    /// (0x11E9810): args (trackIndex, value −1 = toggle, group 0); it carries FL's exclusive-solo semantics.</summary>
    public async Task SetTrackSoloAsync(int track, CancellationToken ct = default)
    {
        LogOp("SetTrackSolo", $"track={track}");
        await CallAsync("11e9810", new ulong[] { (uint)track, unchecked((ulong)(-1L)), 0 }, ct);  // FLpl_SetTrackSolo(idx, toggle, group)
    }

    /// <summary>Read a playlist track's name from the same +0x24 field the setter writes ("" when default).</summary>
    public async Task<string> GetTrackNameAsync(int track, CancellationToken ct = default)
    {
        ulong root = await PlaylistRootAsync(ct);
        if (root == 0) return string.Empty;
        return await ReadDelphiStringAsync(await APtrAsync(TrackField(root, track, TrackFieldName), ct), ct);
    }

    /// <summary>Read a playlist track's RGB color from the same +0x2c field the setter writes (BGR→RGB,
    /// matching <see cref="ListPlaylistTracksAsync"/>).</summary>
    public async Task<int> GetTrackColorAsync(int track, CancellationToken ct = default)
    {
        ulong root = await PlaylistRootAsync(ct);
        if (root == 0) return 0;
        uint c = BitConverter.ToUInt32(await PeekAbsAsync(TrackField(root, track, TrackFieldColor), 4, ct), 0);
        return (int)BgrToRgb(c);
    }

    public async Task SetTrackColorAsync(int track, int rgb, CancellationToken ct = default)
    {
        ulong root = await PlaylistRootAsync(ct);
        if (root == 0) throw new InvalidOperationException("Playlist not available.");
        ulong namePtr = await APtrAsync(TrackField(root, track, TrackFieldName), ct);  // preserve existing name
        uint bgr = RgbToBgr(rgb);
        await CallAsync("11e7940", new ulong[] { root, (uint)track, namePtr, bgr }, ct);
        // Mark the color as CUSTOM (+0x70; RE: 0 = follows the theme default). FL's own color op
        // (0xDFD700) sets this flag, but we call SetTrackNameAndColor (0x11E7940) directly and it
        // isn't verified to set it — and ListPlaylistTracksAsync now uses the flag to decide
        // whether a track is customized, so our setter must guarantee a set color is visible.
        await PokeAbsAsync(TrackField(root, track, TrackFieldCustomColor), new byte[] { 1 }, ct);
    }

    public async Task SetTrackMuteAsync(int track, bool muted, CancellationToken ct = default)
    {
        ulong root = await PlaylistRootAsync(ct);
        if (root == 0) throw new InvalidOperationException("Playlist not available.");
        await PokeAbsAsync(TrackField(root, track, TrackFieldEnabled), new byte[] { (byte)(muted ? 0 : 1) }, ct);
        await RepaintPlaylistAsync(ct);
    }

    /// <summary>Read a playlist track's mute state from the same +0x3c "enabled" byte the setter writes.</summary>
    public async Task<bool> GetTrackMuteAsync(int track, CancellationToken ct = default)
    {
        ulong root = await PlaylistRootAsync(ct);
        if (root == 0) return false;
        return (await PeekAbsAsync(TrackField(root, track, TrackFieldEnabled), 1, ct))[0] == 0;   // enabled==0 ⇒ muted
    }

    public async Task SetTrackCollapsedAsync(int track, bool collapsed, CancellationToken ct = default)
    {
        ulong root = await PlaylistRootAsync(ct);
        if (root == 0) throw new InvalidOperationException("Playlist not available.");
        await PokeAbsAsync(TrackField(root, track, TrackFieldCollapsed), new byte[] { (byte)(collapsed ? 1 : 0) }, ct);
        await RepaintPlaylistAsync(ct);
    }

    /// <summary>Read a playlist track's collapsed state from the same +0xe4 byte the setter writes.</summary>
    public async Task<bool> GetTrackCollapsedAsync(int track, CancellationToken ct = default)
    {
        ulong root = await PlaylistRootAsync(ct);
        if (root == 0) return false;
        return (await PeekAbsAsync(TrackField(root, track, TrackFieldCollapsed), 1, ct))[0] != 0;
    }

    public async Task SelectTrackAsync(int track, CancellationToken ct = default)
    {
        ulong root = await PlaylistRootAsync(ct);
        if (root == 0) throw new InvalidOperationException("Playlist not available.");
        await CallAsync("11e9c30", new ulong[] { root, (uint)track, 1 }, ct);  // FLpl_SetTrackSelection mode 1 = exclusive
    }

    // ---- playlist clips ----
    // Clip collection = *(FLpl_GetCurrentArrangement@0x11E32C0 + 0x14) — a DEREFERENCE of arr+0x14 (the engine's own
    // FLpl_SendPatternToPlaylist resolves it this way). Cobj: vtbl@0, data@8, stride@0x10, count@0x14.
    private async Task<ulong> ClipCollObjAsync(CancellationToken ct)
    {
        ulong arr = await PlaylistRootAsync(ct);          // FLpl_GetCurrentArrangement
        if (arr == 0) return 0;
        return await APtrAsync(arr + 0x14, ct);           // Cobj
    }

    private async Task<(ulong data, int stride, int count)> ClipCollectionAsync(CancellationToken ct)
    {
        ulong C = await ClipCollObjAsync(ct);
        if (C == 0) return (0, 0, 0);
        return (await APtrAsync(C + 8, ct), await AI32Async(C + 0x10, ct), await AI32Async(C + 0x14, ct));
    }

    /// <summary>Number of playlist clips in the current arrangement (collection count(+0x14)).</summary>
    public async Task<int> ClipCountAsync(CancellationToken ct = default)
    {
        var (_, _, count) = await ClipCollectionAsync(ct);
        return count;
    }

    // TEMP DIAGNOSTIC (clip-list debugging): raw dump of the playlist clip collection so we can see why
    // pre-existing/template clips aren't listed (arr/Cobj resolution, count vs activeCount, per-clip fields).
    public async Task<string> DiagClipsAsync(CancellationToken ct = default)
    {
        var sb = new StringBuilder();
        ulong arr = await PlaylistRootAsync(ct);
        ulong C = arr == 0 ? 0 : await APtrAsync(arr + 0x14, ct);
        sb.AppendLine($"arr(11e32c0)=0x{arr:x}  Cobj(*(arr+0x14))=0x{C:x}");
        if (C == 0) return sb.ToString();
        ulong data = await APtrAsync(C + 8, ct);
        int stride = await AI32Async(C + 0x10, ct);
        int count = await AI32Async(C + 0x14, ct);
        int active = await AI32Async(C + 0x48, ct);
        sb.AppendLine($"data=0x{data:x} stride={stride} count(+0x14)={count} activeCount(+0x48)={active}");
        if (data == 0 || stride <= 0) return sb.ToString();
        int n = Math.Min(count, 16);
        for (int i = 0; i < n; i++)
        {
            byte[] cb = await PeekAbsAsync(data + (ulong)i * (ulong)stride, 0x28, ct);
            int start = BitConverter.ToInt32(cb, 0);
            uint src = BitConverter.ToUInt32(cb, 4);
            int len = BitConverter.ToInt32(cb, 8);
            short trk = BitConverter.ToInt16(cb, 0xc);
            byte flags = cb[0x13];
            string kind = src >= 0x50000000 ? $"pattern {(int)((src - 0x50000000) >> 16)}" : $"channel {(int)(src >> 16)}";
            sb.AppendLine($"[{i}] start={start} src=0x{src:x8}({kind}) len={len} trkField={trk}(track={500 - trk}) flags=0x{flags:x2} active={((flags & 0x80) != 0)}");
        }
        return sb.ToString();
    }

    /// <summary>Max clip LINES per ListClips call: ~50 chars each keeps a full page (~5KB) under
    /// the agent's 6KB tool-result cap, so a dense arrangement pages cleanly (offset) instead of
    /// being cut mid-list by the truncation filter.</summary>
    private const int ClipsPageSize = 100;

    /// <summary>Lists active playlist clips (slot index, track, start, length, source), paged.
    /// offset skips the first N MATCHING clips; track&gt;0 filters to one playlist track.</summary>
    public async Task<string> ListClipsAsync(int offset = 0, int track = -1, CancellationToken ct = default)
    {
        var (data, stride, count) = await ClipCollectionAsync(ct);
        if (data == 0 || count <= 0 || stride <= 0) return "(no clips)";
        if (offset < 0) offset = 0;
        var sb = new StringBuilder();
        int matched = 0, shown = 0;
        bool hasMore = false;
        // Source NAMES ride along (memoized per call — one lookup per distinct pattern/channel, not per
        // clip) so the model doesn't have to cross-reference list_patterns/list_channels by number.
        // Quoted and AFTER the numeric fields; ClipList.Parse reads only the pre-quote head, so undo
        // identity parsing is unaffected. Default names ("Pattern 3") add nothing and are omitted.
        var srcNames = new Dictionary<uint, string>();
        for (int i = 0; i < count; i++)
        {
            byte[] cb = await PeekAbsAsync(data + (ulong)i * (ulong)stride, 0x24, ct);
            byte flags = cb[0x13];
            // Do NOT filter on flag 0x80: RecountActiveClips shows it's a selection-style flag (0 on
            // FL-authored/template clips), NOT "this clip exists". count(+0x14) is the live clip count, so
            // every slot 0..count is a real clip. Filtering on 0x80 hid all template clips (the reported bug).
            int trackNo = 500 - BitConverter.ToInt16(cb, 0xc);
            if (track > 0 && trackNo != track) continue;
            matched++;
            if (matched <= offset) continue;
            if (shown == ClipsPageSize) { hasMore = true; break; }   // one matching clip past the page proves there is more
            int start = BitConverter.ToInt32(cb, 0);
            uint src = BitConverter.ToUInt32(cb, 4);
            int len = BitConverter.ToInt32(cb, 8);
            if (!srcNames.TryGetValue(src, out string? srcName))
            {
                try
                {
                    if (src >= 0x50000000)
                    {
                        int p = (int)((src - 0x50000000) >> 16);
                        srcName = await GetPatternNameAsync(p, ct);
                        if (srcName == $"Pattern {p}") srcName = "";
                    }
                    else
                    {
                        int c = (int)(src >> 16);
                        srcName = await GetChannelNameAsync(c, ct);
                        if (srcName == $"Channel {c}") srcName = "";
                    }
                }
                catch (InvalidOperationException) { srcName = ""; }   // garbage/foreign source id — omit, never fail the listing
                srcNames[src] = srcName;
            }
            string srcDesc = (src >= 0x50000000 ? $"pattern {(int)((src - 0x50000000) >> 16)}" : $"channel {(int)(src >> 16)}")
                + (srcName.Length > 0 ? $" '{srcName}'" : "");
            sb.Append($"[{i}] track {trackNo} start={start} len={len} {srcDesc}{((flags & 0x20) != 0 ? " muted" : "")}\n");
            shown++;
        }
        if (shown == 0)
            return track > 0 ? $"(no active clips on track {track}{(offset > 0 ? $" at offset {offset}" : "")})"
                : offset > 0 ? $"(no clips at offset {offset} — {matched} active clips total)"
                : "(no active clips)";
        string more = hasMore ? $"\n(more — call again with offset={offset + shown})" : "";
        return $"{shown} clips{(track > 0 ? $" on track {track}" : "")}{(offset > 0 ? $" from offset {offset}" : "")}:\n"
            + sb.ToString().TrimEnd() + more;
    }

    /// <summary>Adds a pattern clip to the playlist timeline. <paramref name="pattern"/> is 1-based (the same
    /// index used by notes/patterns/name/length); 0 (the reserved slot) and out-of-range throw.
    /// lengthTick&lt;=0 = the pattern's own length.</summary>
    public Task AddPatternClipAsync(int pattern, int track, int startTick, int lengthTick, CancellationToken ct = default)
        => AddPatternClipsAsync(new[] { new PatternClipSpec(pattern, track, startTick, lengthTick) }, ct);

    /// <summary>Place many pattern clips, then refresh each touched pattern's clip caches + repaint ONCE
    /// (not per clip — the win over looping the singular). Each insert reuses the singular's realize-both-
    /// recorders + atomic scratch-guarded InsertClipRaw core; clips are addressed by (pattern,track,start)
    /// so the add-order index shifts don't matter.</summary>
    public async Task AddPatternClipsAsync(IReadOnlyList<PatternClipSpec> clips, CancellationToken ct = default)
    {
        if (clips is null || clips.Count == 0) return;
        LogOp("AddPatternClips", string.Join(", ", clips.Select(c => $"pat{c.Pattern}@t{c.Track}:{c.StartTick}" + (c.LengthTick > 0 ? $"len{c.LengthTick}" : ""))));

        // Distinct patterns touched — each needs its clip-length caches rebuilt once at the end.
        var patterns = new HashSet<int>();
        foreach (var c in clips)
        {
            ValidatePattern(c.Pattern); // 1-based; rejects 0 + out-of-range (a 0-based index here mis-targets patterns)

            // REALIZE the target pattern's recorders before inserting a clip that references it. FL's playlist
            // paint (FUN_00ccc330) dereferences patternArray[N]+0x20 (the PARAM recorder) and +0x28 (the note
            // recorder) with only a "!= 0" guard, so both must be non-garbage. ValidatePattern above caps N at
            // 999 (FL's real max) so these indexed creates stay in-bounds; the param-recorder create is the one
            // the earlier "realize" missed (note-recorder alone left +0x20 uninitialized).
            await CallAsync("11d4080", new ulong[] { (uint)c.Pattern, 1 }, ct);  // FLpat_GetOrCreateNoteRecorder(pat, create=1) -> +0x28
            await CallAsync("11d4000", new ulong[] { (uint)c.Pattern, 1 }, ct);  // FLpat_GetOrCreateParamRecorder(pat, create=1) -> +0x20

            int len = c.LengthTick;
            if (len <= 0)
            {
                ulong patArr = await GPtrAsync("14aa0c8", ct);
                if (patArr != 0) len = await AI32Async(patArr + (ulong)c.Pattern * 0xC0 + 0x50, ct);
                if (len <= 0) len = await GetPpqAsync(ct) * 4;
            }
            // FL encodes a pattern-clip sourceID from the 1-based pattern number (FLpl_SendPatternToPlaylist
            // @0xCB0780: 0x50005000 + (N<<16)) — the SAME index that drives the note recorder / name / length.
            // Feeding a 0-based index here stored N-1, so pattern 1 -> slot 0 ("pattern 0"), pattern 2 -> pattern 1, etc.
            // InsertClipRaw is itself atomic under _scratchGate (+ its own RecountActiveClips).
            await InsertClipRawAsync(c.StartTick, 0x50005000u + ((uint)c.Pattern << 16), len, c.Track, -1, -1, ct);
            patterns.Add(c.Pattern);
        }
        // ONE refresh pass at the end: resolve each new clip's derived length cache + dirty so it renders
        // correctly WITHOUT a click (RepaintPlaylist alone leaves it stale — the "broken until clicked" bug),
        // then a SINGLE window repaint. d49840 (via RefreshPatternAsync) sets the authoritative clip+0x08.
        foreach (int p in patterns) await RefreshPatternAsync(p, ct);
        await RepaintPlaylistAsync(ct);
        // Recompute the SONG LENGTH to cover the newly-arranged clips so the transport can play across them
        // (fixes the "press play but the playhead is stuck / clips past the old song end don't play" bug —
        // the song otherwise keeps its OLD length and the playhead loops inside the stale range). See below.
        await RecomputeSongLengthAsync(ct);
    }

    private async Task<ulong> ClipAddrAsync(int index, CancellationToken ct)
    {
        var (data, stride, count) = await ClipCollectionAsync(ct);
        if (data == 0 || stride <= 0) throw new InvalidOperationException("Playlist clip collection not available.");
        if (index < 0 || index >= count) throw new InvalidOperationException($"Clip index {index} out of range (0..{count - 1}).");
        return data + (ulong)index * (ulong)stride;
    }

    public Task MoveClipAsync(int clipIndex, int startTick, int track, CancellationToken ct = default)
        => MoveClipsAsync(new[] { new ClipMove(clipIndex, startTick, track) }, ct);

    /// <summary>Move many clips (poke each clip's +0x00 start / +0x0c track), then ONE repaint. Moves don't
    /// reorder the collection, so all indices stay valid — the collection is resolved once for the batch.</summary>
    public async Task MoveClipsAsync(IReadOnlyList<ClipMove> moves, CancellationToken ct = default)
    {
        if (moves is null || moves.Count == 0) return;
        LogOp("MoveClips", string.Join(", ", moves.Select(m => $"[{m.Index}]->t{m.Track}@{m.StartTick}")));
        var (data, stride, count) = await ClipCollectionAsync(ct);
        if (data == 0 || stride <= 0) throw new InvalidOperationException("Playlist clip collection not available.");
        foreach (var m in moves)
        {
            if (m.Index < 0 || m.Index >= count)
                throw new InvalidOperationException($"Clip index {m.Index} out of range (0..{count - 1}).");
            ulong clip = data + (ulong)m.Index * (ulong)stride;
            await PokeAbsAsync(clip + 0, BitConverter.GetBytes(m.StartTick), ct);
            if (m.Track >= 0) await PokeAbsAsync(clip + 0xc, BitConverter.GetBytes((short)(500 - m.Track)), ct);
        }
        await RepaintPlaylistAsync(ct);   // move only changes position (+0x00/+0x0c), not the length cache
        await RecomputeSongLengthAsync(ct);  // moving a clip past the old song end must extend the play-range
    }

    public Task ResizeClipAsync(int clipIndex, int lengthTick, CancellationToken ct = default)
        => ResizeClipsAsync(new[] { new ClipResize(clipIndex, lengthTick) }, ct);

    /// <summary>Resize many clips (poke each clip's +0x08 length), then ONE repaint.</summary>
    public async Task ResizeClipsAsync(IReadOnlyList<ClipResize> resizes, CancellationToken ct = default)
    {
        if (resizes is null || resizes.Count == 0) return;
        LogOp("ResizeClips", string.Join(", ", resizes.Select(r => $"[{r.Index}]len={r.LengthTick}")));
        var (data, stride, count) = await ClipCollectionAsync(ct);
        if (data == 0 || stride <= 0) throw new InvalidOperationException("Playlist clip collection not available.");
        ulong setSrcRange = GhidraToRuntime(0xF71A70);   // FLpl_SetClipSourceRange(clip, double start, double end)
        foreach (var r in resizes)
        {
            if (r.Index < 0 || r.Index >= count)
                throw new InvalidOperationException($"Clip index {r.Index} out of range (0..{count - 1}).");
            int len = Math.Max(1, r.LengthTick);
            ulong clip = data + (ulong)r.Index * (ulong)stride;
            // Persist the resize THROUGH the song-length recompute below. Poking +0x08 alone sets a DERIVED
            // CACHE that RecomputeSongLength's arrangement re-select (FL's d49840) re-resolves from the clip's
            // SOURCE RANGE (+0x18/+0x1c) — so a +0x08-only resize is reverted (and the song shrinks back). Set
            // the source range [0..len] via FL's own setter FIRST so the resolver derives the SAME extended
            // length: the resize survives the refresh AND the song grows to cover it (RE: re/25 §Deferred).
            if (setSrcRange != 0 && IsInModule(setSrcRange))
                await CallFAbsAsync(setSrcRange, new ulong[] { clip, Bits(0), Bits(len) }, ct);
            await PokeAbsAsync(clip + 8, BitConverter.GetBytes(len), ct);
        }
        await RepaintPlaylistAsync(ct);
        await RecomputeSongLengthAsync(ct);   // grow/shrink the transport play-range to cover the resized clips
    }

    public Task DeleteClipAsync(int clipIndex, CancellationToken ct = default)
        => DeleteClipsAsync(new[] { clipIndex }, ct);

    /// <summary>Delete many clips in one pass. FL keys clip presence on collection membership + count(+0x14),
    /// NOT the 0x80 flag (see ListClipsAsync — 0x80 is a selection flag, 0 on template clips). Real delete =
    /// Delphi-TList Delete: shift the tail down one slot, then decrement count. The collection is a TList, so
    /// deleting index i shifts everything after it down; to keep every caller-supplied index valid we DEDUPE
    /// and remove HIGH→LOW (an earlier removal never disturbs a lower, not-yet-processed index). One recount +
    /// one repaint at the end (not per clip). Serialized against clip-build ops so a concurrent insert can't
    /// race the shift.</summary>
    public async Task DeleteClipsAsync(IReadOnlyList<int> clipIndices, CancellationToken ct = default)
    {
        if (clipIndices is null || clipIndices.Count == 0) return;
        LogOp("DeleteClips", string.Join(",", clipIndices));
        // Dedupe + sort DESCENDING: the indices all come from ONE ListClips snapshot, so validate them against
        // the original count; removing top-down means the surviving lower indices never shift under us.
        var indices = clipIndices.Distinct().OrderByDescending(i => i).ToList();
        await _scratchGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ulong C = await ClipCollObjAsync(ct);
            if (C == 0) throw new InvalidOperationException("Playlist clip collection not available.");
            ulong data = await APtrAsync(C + 8, ct);
            int stride = await AI32Async(C + 0x10, ct);
            int count = await AI32Async(C + 0x14, ct);
            if (data == 0 || stride <= 0) throw new InvalidOperationException("Playlist clip collection not available.");
            foreach (int idx in indices)   // validate ALL up front against the snapshot count
                if (idx < 0 || idx >= count)
                    throw new InvalidOperationException($"Clip index {idx} out of range (0..{count - 1}).");
            int curCount = count;
            foreach (int idx in indices)   // already high→low; each idx < curCount by construction
            {
                int tail = curCount - idx - 1;
                if (tail > 0)
                {
                    byte[] rest = await PeekAbsAsync(data + (ulong)(idx + 1) * (ulong)stride, tail * stride, ct);
                    await PokeAbsAsync(data + (ulong)idx * (ulong)stride, rest, ct);
                }
                curCount--;
            }
            await PokeAbsAsync(C + 0x14, BitConverter.GetBytes(curCount), ct);   // final count (one write)
            await CallAsync("f6e180", new ulong[] { C }, ct);                    // RecountActiveClips (+0x48) ONCE
        }
        finally { _scratchGate.Release(); }
        await RepaintPlaylistAsync(ct);   // ONCE
        await RecomputeSongLengthAsync(ct);  // deleting the furthest clip must shrink the play-range to match
    }

    /// <summary>Mute/unmute a playlist clip (sets clip+0x13 bit 0x20 via FLpl_SetClipMuted).</summary>
    public Task SetClipMutedAsync(int clipIndex, bool muted, CancellationToken ct = default)
        => SetClipsMutedAsync(new[] { clipIndex }, muted, ct);

    /// <summary>Read a playlist clip's mute state — the SAME clip+0x13 bit 0x20 FLpl_SetClipMuted writes,
    /// so this is the exact read-before-write for granular clip-mute undo. Throws when the slot is out of
    /// range (via <see cref="ClipAddrAsync"/>), so a bad slot never returns a bogus "unmuted".</summary>
    public async Task<bool> GetClipMutedAsync(int clipIndex, CancellationToken ct = default)
    {
        ulong clip = await ClipAddrAsync(clipIndex, ct);
        byte flags = (await PeekAbsAsync(clip + 0x13, 1, ct))[0];
        return (flags & 0x20) != 0;
    }

    /// <summary>Mute/unmute many clips (FLpl_SetClipMuted per clip), then ONE repaint. Mute doesn't reorder
    /// the collection, so indices stay valid — resolved once for the batch.</summary>
    public async Task SetClipsMutedAsync(IReadOnlyList<int> clipIndices, bool muted, CancellationToken ct = default)
    {
        if (clipIndices is null || clipIndices.Count == 0) return;
        LogOp("SetClipsMuted", $"muted={muted} idx={string.Join(",", clipIndices)}");
        var (data, stride, count) = await ClipCollectionAsync(ct);
        if (data == 0 || stride <= 0) throw new InvalidOperationException("Playlist clip collection not available.");
        foreach (int idx in clipIndices)
        {
            if (idx < 0 || idx >= count)
                throw new InvalidOperationException($"Clip index {idx} out of range (0..{count - 1}).");
            ulong clip = data + (ulong)idx * (ulong)stride;
            await CallAsync("f71a60", new ulong[] { clip, (ulong)(muted ? 1 : 0) }, ct);  // FLpl_SetClipMuted
        }
        await RepaintPlaylistAsync(ct);
    }

    // Shared clip-insert core (same path the engine's FLpl_SendPatternToPlaylist uses): init defaults into a
    // 0x48 temp, set fields, insert into the collection. Guards the resolved vtbl fn ptrs against the FLEngine
    // module range (calling a non-code ptr AVs and crashes FL).
    private async Task InsertClipRawAsync(int startTick, uint sourceID, int len, int track, int srcStart, int srcEnd, CancellationToken ct)
    {
        // Lease the scratch buffer for the WHOLE build→insert span so no other scratch-building op can
        // clobber the half-built clip struct between our pokes and the insert (the corruption class that
        // helped crash FL). Held across several RawAsync calls; _scratchGate != _pipeGate, so no deadlock.
        await _scratchGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ulong C = await ClipCollObjAsync(ct);
            if (C == 0) throw new InvalidOperationException("Playlist clip collection not available.");
            ulong vt = await APtrAsync(C, ct);
            ulong initFn = await APtrAsync(vt + 0x10, ct);
            ulong insertFn = await APtrAsync(vt + 8, ct);
            EnsureInModule("Clip-insert", initFn, insertFn);
            ulong tmp = await ScratchAsync(ct);
            await PokeAbsAsync(tmp, new byte[0x48], ct);
            await CallAbsAsync(initFn, new ulong[] { C, tmp }, ct);   // init defaults + internal links FIRST
            await PokeAbsAsync(tmp + 0x00, BitConverter.GetBytes(startTick), ct);
            await PokeAbsAsync(tmp + 0x04, BitConverter.GetBytes(sourceID), ct);
            await PokeAbsAsync(tmp + 0x08, BitConverter.GetBytes(len), ct);
            await PokeAbsAsync(tmp + 0x0c, BitConverter.GetBytes((short)(500 - track)), ct);
            byte fl = (await PeekAbsAsync(tmp + 0x13, 1, ct))[0];
            await PokeAbsAsync(tmp + 0x13, new byte[] { (byte)(fl | 0x80) }, ct);
            await PokeAbsAsync(tmp + 0x18, BitConverter.GetBytes(srcStart), ct);
            await PokeAbsAsync(tmp + 0x1c, BitConverter.GetBytes(srcEnd), ct);
            await CallAbsAsync(insertFn, new ulong[] { C, tmp }, ct);
            await CallAsync("f6e180", new ulong[] { C }, ct);  // RecountActiveClips
        }
        finally { _scratchGate.Release(); }
    }

    private async Task<ulong> FindClipAddrAsync(int startTick, short trackField, uint sourceID, ulong exclude, CancellationToken ct)
    {
        var (data, stride, count) = await ClipCollectionAsync(ct);
        for (int i = 0; i < count; i++)
        {
            ulong addr = data + (ulong)i * (ulong)stride;
            if (addr == exclude) continue;
            byte[] cb = await PeekAbsAsync(addr, 0x20, ct);
            if ((cb[0x13] & 0x80) == 0) continue;
            if (BitConverter.ToInt32(cb, 0) == startTick && BitConverter.ToInt16(cb, 0xc) == trackField && BitConverter.ToUInt32(cb, 4) == sourceID)
                return addr;
        }
        return 0;
    }

    /// <summary>Slice/chop a clip at an absolute tick into two clips. Audio source ranges are split so playback stays continuous.</summary>
    public async Task SliceClipAsync(int clipIndex, int tick, CancellationToken ct = default)
    {
        LogOp("SliceClip", $"index={clipIndex} tick={tick}");
        ulong clip = await ClipAddrAsync(clipIndex, ct);
        if (clip == 0) throw new InvalidOperationException("Clip not found.");
        byte[] cb = await PeekAbsAsync(clip, 0x20, ct);
        if ((cb[0x13] & 0x80) == 0) throw new InvalidOperationException($"Clip {clipIndex} is not an active clip.");
        int start = BitConverter.ToInt32(cb, 0);
        uint src = BitConverter.ToUInt32(cb, 4);
        int len = BitConverter.ToInt32(cb, 8);
        short trackField = BitConverter.ToInt16(cb, 0xc);
        int origEnd = start + len;
        if (tick <= start || tick >= origEnd)
            throw new InvalidOperationException($"Slice tick {tick} must be strictly inside the clip ({start}..{origEnd}).");
        int off = tick - start;
        bool audio = src < 0x50000000;
        // 1) shorten the original to the slice point
        await PokeAbsAsync((await ClipAddrAsync(clipIndex, ct)) + 8, BitConverter.GetBytes(off), ct);
        // 2) insert the second half (same source/track)
        await InsertClipRawAsync(tick, src, origEnd - tick, 500 - trackField, -1, -1, ct);
        // 3) for AUDIO, split the source window so the second half continues the sample
        if (audio)
        {
            ulong sr = GhidraToRuntime(0xF71A70);  // FLpl_SetClipSourceRange(clip, double start, double end)
            if (sr != 0)
            {
                ulong c1 = await ClipAddrAsync(clipIndex, ct);
                await CallFAbsAsync(sr, new ulong[] { c1, Bits(0), Bits(off) }, ct);
                ulong c2 = await FindClipAddrAsync(tick, trackField, src, c1, ct);
                if (c2 != 0) await CallFAbsAsync(sr, new ulong[] { c2, Bits(off), Bits(origEnd - start) }, ct);
            }
        }
        await RepaintPlaylistAsync(ct);
        await RecomputeSongLengthAsync(ct);   // the two halves span the original, so length is unchanged — but keep the guarantee uniform
    }

    /// <summary>Duplicate a clip, placing the copy immediately after it on the same track.</summary>
    public async Task DuplicateClipAsync(int clipIndex, CancellationToken ct = default)
    {
        LogOp("DuplicateClip", $"index={clipIndex}");
        ulong clip = await ClipAddrAsync(clipIndex, ct);
        if (clip == 0) throw new InvalidOperationException("Clip not found.");
        byte[] cb = await PeekAbsAsync(clip, 0x20, ct);
        if ((cb[0x13] & 0x80) == 0) throw new InvalidOperationException($"Clip {clipIndex} is not an active clip.");
        int start = BitConverter.ToInt32(cb, 0);
        uint src = BitConverter.ToUInt32(cb, 4);
        int len = BitConverter.ToInt32(cb, 8);
        short trackField = BitConverter.ToInt16(cb, 0xc);
        int srcStart = BitConverter.ToInt32(cb, 0x18);
        int srcEnd = BitConverter.ToInt32(cb, 0x1c);
        await InsertClipRawAsync(start + len, src, len, 500 - trackField, srcStart, srcEnd, ct);
        await RepaintPlaylistAsync(ct);
        await RecomputeSongLengthAsync(ct);   // the copy lands at start+len — may be the new furthest clip; grow the song to cover it
    }
}
