using FruityLink.FlStudio.Inject;

// Live-verify plugin param control against a running FL Studio (Serum 2 loaded in the channel rack).
// Talks to the in-process bridge over \\.\pipe\FruityLinkBridge using the REAL shipping code path.
//   (no args)                 -> discover channels + list Serum's cutoff/filter/macro/master params
//   set <index> <0..1>        -> read current, set param on the Serum channel, read back
//   list <filter>             -> list Serum params matching <filter>

var fl = new FlInjectBridge();
var ct = CancellationToken.None;

if (!await fl.IsAvailableAsync(ct))
{
    Console.WriteLine("BRIDGE NOT AVAILABLE (is FL running with the bridge loaded?)");
    return 1;
}
Console.WriteLine("bridge: available");

// cliptest: the exact crash-repro path (create pattern -> add note -> place pattern clip -> move it),
// now with the fix. Run on a NEW EMPTY project. Success = FL does NOT crash + clips list + renders.
if (args.Length > 0 && args[0].Equals("cliptest", StringComparison.OrdinalIgnoreCase))
{
    Console.WriteLine("=== CLIP TEST (create pattern + note + place + move; expect NO crash) ===");
    int chans = await fl.GetChannelCountAsync(ct);
    Console.WriteLine($"channels: {chans}");
    int pat;
    try { pat = await fl.CreatePatternAsync(ct); Console.WriteLine($"created pattern {pat}"); }
    catch (Exception ex) { Console.WriteLine("create pattern FAILED: " + ex.Message); return 3; }
    try { await fl.AddNotesAsync(pat, new[] { new FruityLink.Core.Abstractions.NoteSpec(0, 60, 0, 480, 100) }, ct); Console.WriteLine("added note ch0 key60"); }
    catch (Exception ex) { Console.WriteLine("add note FAILED: " + ex.Message); }
    try { await fl.AddPatternClipAsync(pat, 1, 0, 0, ct); Console.WriteLine($"placed pattern {pat} clip on track 1 @0 -- NO CRASH"); }
    catch (Exception ex) { Console.WriteLine("ADD CLIP FAILED: " + ex.Message); return 4; }
    try { Console.WriteLine("clips after add:\n" + await fl.ListClipsAsync(0, -1, ct)); }
    catch (Exception ex) { Console.WriteLine("list FAILED: " + ex.Message); }
    try { await fl.MoveClipAsync(0, 1920, 2, ct); Console.WriteLine("moved clip 0 -> track 2 @1920"); }
    catch (Exception ex) { Console.WriteLine("move FAILED: " + ex.Message); }
    try { Console.WriteLine("clips after move:\n" + await fl.ListClipsAsync(0, -1, ct)); }
    catch (Exception ex) { Console.WriteLine("list FAILED: " + ex.Message); }
    Console.WriteLine("=== CLIP TEST DONE — if FL is still alive + clips listed, the fix holds ===");
    return 0;
}

// playtest: reproduce the "playhead stuck while playing" bug. Create a pattern + notes, ARRANGE two
// pattern clips onto playlist tracks (start 0 and start=1 bar) so the song has length, force SONG mode,
// START playback, then read the REAL engine playhead tick (0x14A92D0 via DiagTransport) TWICE ~500ms
// apart. PASS = the tick ADVANCES between reads (the song plays). Uses SCRATCH tracks 200/201 and
// cleans up the clips it added. Prints full transport/song state BEFORE vs AFTER the arrange so a bad
// field (songBars / time-selection) is visible.
if (args.Length > 0 && args[0].Equals("playtest", StringComparison.OrdinalIgnoreCase))
{
    static List<(int idx, int track, int start, int len, int pattern)> ParseClips(string list)
    {
        var rows = new List<(int, int, int, int, int)>();
        foreach (var ln in list.Split('\n'))
        {
            var mi = System.Text.RegularExpressions.Regex.Match(ln, @"^\[(\d+)\]");
            if (!mi.Success) continue;
            int Field(string key)
            {
                var mm = System.Text.RegularExpressions.Regex.Match(ln, key + @"=?\s*(-?\d+)");
                return mm.Success ? int.Parse(mm.Groups[1].Value) : -1;
            }
            rows.Add((int.Parse(mi.Groups[1].Value), Field("track"), Field("start"), Field("len"), Field("pattern")));
        }
        return rows;
    }
    static int EngTick(string diag)
    {
        var m = System.Text.RegularExpressions.Regex.Match(diag, @"pos=(-?\d+)");
        return m.Success ? int.Parse(m.Groups[1].Value) : int.MinValue;
    }

    static int CurrentArrIndex(string list)
    {
        foreach (var ln in list.Split('\n'))
        {
            var m = System.Text.RegularExpressions.Regex.Match(ln, @"^\*\s*\[(\d+)\]");
            if (m.Success) return int.Parse(m.Groups[1].Value);
        }
        return 0;
    }
    static int MaxRange(string diag)
    {
        var m = System.Text.RegularExpressions.Regex.Match(diag, @"\.\.(-?\d+)\]");
        return m.Success ? int.Parse(m.Groups[1].Value) : int.MinValue;
    }

    // Mode: default = fresh empty arrangement (song starts at 0). "cur" = arrange into the CURRENT,
    // already-populated arrangement (the real user scenario: adding clips on top of an existing song).
    bool freshArr = !(args.Length > 1 && args[1].Equals("cur", StringComparison.OrdinalIgnoreCase));
    Console.WriteLine($"=== PLAYTEST ({(freshArr ? "FRESH empty arrangement" : "CURRENT populated arrangement")}: arrange -> play -> playhead MUST advance) ===");
    int ppq = await fl.GetPpqAsync(ct);
    int barTicks = ppq * 4;
    Console.WriteLine($"ppq={ppq} barTicks={barTicks}");
    // Real user flow: arrange in the PLAYLIST while STOPPED and in SONG mode, then press Play. Ensure that
    // state up front (arranging while the transport is running is a separate crash hazard we don't test here).
    try { await fl.TransportStopAsync(ct); } catch (Exception ex) { Console.WriteLine("stop note: " + ex.Message); }
    try { await fl.SetSongModeAsync(true, ct); } catch (Exception ex) { Console.WriteLine("SetSongMode note: " + ex.Message); }
    Console.WriteLine($"song mode now: {await fl.GetSongModeAsync(ct)}");
    Console.WriteLine("BEFORE            : " + await fl.DiagTransportAsync(ct));

    int baseArr = CurrentArrIndex(await fl.ListArrangementsAsync(ct));
    int newArr = -1;
    // Two clips at bar 1 and bar 2. In "cur" mode place them FAR past the existing song end (bars 9 & 10)
    // on empty tracks so the test's arrange must EXTEND the existing song length; in fresh mode, bars 1 & 2.
    int t0 = 3, t1 = 5;
    int startA = freshArr ? 0 : 8 * barTicks;
    int startB = freshArr ? barTicks : 9 * barTicks;
    int expectedEnd = startB + barTicks - 1;   // song end tick after the arrange (range max = songEnd-1)
    if (freshArr)
    {
        try { newArr = await fl.AddArrangementAsync("PlaytestTmp", ct); Console.WriteLine($"created + switched to empty arrangement {newArr} (base was {baseArr})"); }
        catch (Exception ex) { Console.WriteLine("AddArrangement FAILED: " + ex.Message); return 3; }
        t0 = 1; t1 = 2;
        Console.WriteLine("EMPTY new arr     : " + await fl.DiagTransportAsync(ct));
    }

    int pat;
    try { pat = await fl.CreatePatternAsync(ct); Console.WriteLine($"created pattern {pat}"); }
    catch (Exception ex) { Console.WriteLine("create pattern FAILED: " + ex.Message); return 3; }
    try
    {
        await fl.AddNotesAsync(pat, new[]
        {
            new FruityLink.Core.Abstractions.NoteSpec(0, 60, 0, ppq, 100),
            new FruityLink.Core.Abstractions.NoteSpec(0, 62, barTicks, ppq, 100),
        }, ct);
        Console.WriteLine("added 2 notes");
    }
    catch (Exception ex) { Console.WriteLine("add notes FAILED: " + ex.Message); }

    // Re-assert SONG mode (creating/switching an arrangement can change it) so the arrange's song-length
    // refresh runs in the mode the transport plays in.
    try { await fl.SetSongModeAsync(true, ct); } catch (Exception ex) { Console.WriteLine("SetSongMode note: " + ex.Message); }

    // THE ARRANGE: place two pattern clips so the song must be at least (startB + 1 bar) long.
    var specs = new List<FruityLink.Core.Abstractions.PatternClipSpec>
    {
        new(pat, t0, startA, 0),
        new(pat, t1, startB, 0),
    };
    try { await fl.AddPatternClipsAsync(specs, ct); Console.WriteLine($"ARRANGED pattern {pat}: clip @track{t0} start{startA}, clip @track{t1} start{startB}"); }
    catch (Exception ex) { Console.WriteLine("ARRANGE (AddPatternClips) FAILED: " + ex.Message); return 4; }

    string afterArrange = await fl.DiagTransportAsync(ct);
    Console.WriteLine("AFTER arrange     : " + afterArrange);
    int maxAfter = MaxRange(afterArrange);
    Console.WriteLine($"ASSERT song-length-extended: {(maxAfter >= expectedEnd ? "PASS" : "FAIL")} (playRange max={maxAfter}, need >= {expectedEnd} to cover the arrange @ start{startB})");

    // Start at the top, then play and sample the playhead twice.
    try { await fl.SeekAsync(0, ct); } catch (Exception ex) { Console.WriteLine("seek0 note: " + ex.Message); }
    try { await fl.TransportPlayAsync(ct); Console.WriteLine("PLAY"); } catch (Exception ex) { Console.WriteLine("PLAY FAILED: " + ex.Message); }
    await Task.Delay(800, ct);
    string r1 = await fl.DiagTransportAsync(ct); int e1 = EngTick(r1);
    Console.WriteLine("PLAY read1 (@~800ms) : " + r1);
    await Task.Delay(500, ct);
    string r2 = await fl.DiagTransportAsync(ct); int e2 = EngTick(r2);
    Console.WriteLine("PLAY read2 (@~1300ms): " + r2);
    // Definitive proof the arranged clips are now inside the played song: seek INTO the arranged region
    // (just before the first arranged clip) and confirm the playhead actually plays THERE — past the old
    // song end. Without the fix, SeekAsync clamps to the stale song length so the playhead can never be
    // there. Sample after ~700ms so the playhead has moved but not wrapped.
    int probeSeek = Math.Max(0, startA - barTicks / 2);
    try { await fl.SeekAsync(probeSeek, ct); } catch (Exception ex) { Console.WriteLine("seek-probe note: " + ex.Message); }
    try { await fl.TransportPlayAsync(ct); } catch (Exception ex) { Console.WriteLine("PLAY2 note: " + ex.Message); }
    await Task.Delay(700, ct);
    string r3 = await fl.DiagTransportAsync(ct); int e3 = EngTick(r3);
    Console.WriteLine($"PLAY-in-region (seek {probeSeek}) : " + r3);
    try { await fl.TransportStopAsync(ct); Console.WriteLine("STOP"); } catch (Exception ex) { Console.WriteLine("STOP note: " + ex.Message); }

    // The playhead advances if it moved between reads AND is not stranded near tick 0/1.
    bool moved = e1 != int.MinValue && e2 != int.MinValue && e2 != e1;
    bool notStuck = Math.Max(e1, e2) > 2;
    bool advanced = moved && notStuck;
    bool songExtended = maxAfter >= expectedEnd;
    bool reachedRegion = e3 >= startA - barTicks;   // playhead is in/near the arranged region (past the old end)
    Console.WriteLine($"ASSERT playhead-advances: {(advanced ? "PASS" : "FAIL")} (pos read1={e1} read2={e2})");
    Console.WriteLine($"ASSERT playhead-reaches-arranged-region: {(reachedRegion ? "PASS" : "FAIL")} (pos in region={e3}, arranged clips start at {startA})");
    bool overall = advanced && songExtended && reachedRegion;
    Console.WriteLine($"OVERALL: {(overall ? "PASS" : "FAIL")} (song-extended={songExtended}, playhead-advances={advanced}, reaches-region={reachedRegion})");

    // Cleanup: restore the user's project.
    try
    {
        if (freshArr)
        {
            await fl.DeleteArrangementAsync(newArr, ct);
            await fl.SelectArrangementAsync(baseArr, ct);
            Console.WriteLine($"cleaned up: deleted temp arrangement {newArr}, back to {baseArr}");
        }
        else
        {
            string lst = await fl.ListClipsAsync(0, -1, ct);
            var del = new List<int>();
            foreach (var c in ParseClips(lst)) if (c.track == t0 || c.track == t1) del.Add(c.idx);
            if (del.Count > 0) { await fl.DeleteClipsAsync(del, ct); }
            Console.WriteLine($"cleaned up: deleted {del.Count} test clip(s) on tracks {t0}/{t1}");
        }
    }
    catch (Exception ex) { Console.WriteLine("cleanup note: " + ex.Message); }

    Console.WriteLine("=== PLAYTEST DONE ===");
    return overall ? 0 : 8;
}

// arrangetest: the COMPLEX-arrange repro (mirrors a real multi-turn AI session that leaves the playhead
// stuck). Builds a fresh temp arrangement, then: 3 patterns w/ notes -> MANY pattern clips across several
// tracks at INCREASING starts (last clip far past bar 1) -> a RESIZE that extends a clip well past the
// current song end -> a DUPLICATE that lands the copy past the end. It reads the transport play-range
// (DiagTransport, max = song-end tick) AFTER EACH op so we can see EXACTLY which op fails to extend the
// song. Then SetSongMode(true) + Seek(0) + Play + read playhead twice ~600ms apart, and finally Seek INTO
// the far region + Play and confirm the playhead reaches there. PASS = the play-range grows to cover every
// op AND the playhead advances AND reaches the far clips. Non-destructive: uses a temp arrangement it
// deletes on the way out. grep-friendly ("ASSERT <name>: PASS/FAIL").
if (args.Length > 0 && args[0].Equals("arrangetest", StringComparison.OrdinalIgnoreCase))
{
    static int EngTick(string diag)
    {
        var m = System.Text.RegularExpressions.Regex.Match(diag, @"pos=(-?\d+)");
        return m.Success ? int.Parse(m.Groups[1].Value) : int.MinValue;
    }
    static int MaxRange(string diag)
    {
        var m = System.Text.RegularExpressions.Regex.Match(diag, @"\.\.(-?\d+)\]");
        return m.Success ? int.Parse(m.Groups[1].Value) : int.MinValue;
    }
    static int CurrentArrIndex(string list)
    {
        foreach (var ln in list.Split('\n'))
        {
            var m = System.Text.RegularExpressions.Regex.Match(ln, @"^\*\s*\[(\d+)\]");
            if (m.Success) return int.Parse(m.Groups[1].Value);
        }
        return 0;
    }
    // Parse a ListClips line "[i] track T start=S len=L pattern N" -> the (idx,start,len) of the clip on a
    // given track whose start == wantStart (or the first on that track when wantStart < 0). (-1,..) if none.
    static (int idx, int start, int len) ClipOnTrack(string list, int track, int wantStart)
    {
        foreach (var ln in list.Split('\n'))
        {
            var m = System.Text.RegularExpressions.Regex.Match(ln, @"^\[(\d+)\] track (\d+) start=(-?\d+) len=(-?\d+)");
            if (!m.Success) continue;
            if (int.Parse(m.Groups[2].Value) != track) continue;
            int s = int.Parse(m.Groups[3].Value);
            if (wantStart >= 0 && s != wantStart) continue;
            return (int.Parse(m.Groups[1].Value), s, int.Parse(m.Groups[4].Value));
        }
        return (-1, -1, -1);
    }

    Console.WriteLine("=== ARRANGETEST (complex multi-op arrange: song length + playhead must stay correct) ===");
    int ppq = await fl.GetPpqAsync(ct);
    int barTicks = ppq * 4;
    Console.WriteLine($"ppq={ppq} barTicks={barTicks}");

    try { await fl.TransportStopAsync(ct); } catch (Exception ex) { Console.WriteLine("stop note: " + ex.Message); }
    try { await fl.SetSongModeAsync(true, ct); } catch (Exception ex) { Console.WriteLine("SetSongMode note: " + ex.Message); }

    int baseArr = CurrentArrIndex(await fl.ListArrangementsAsync(ct));
    int newArr = -1;
    try { newArr = await fl.AddArrangementAsync("ArrTestTmp", ct); Console.WriteLine($"created + switched to temp arrangement {newArr} (base was {baseArr})"); }
    catch (Exception ex) { Console.WriteLine("AddArrangement FAILED: " + ex.Message); return 3; }
    try { await fl.SetSongModeAsync(true, ct); } catch { }
    Console.WriteLine("EMPTY temp arr   : " + await fl.DiagTransportAsync(ct));

    // 3 patterns, each with two notes INSIDE the first bar (keeps pattern length ~1 bar so clip ends are
    // predictable). Reuse them across the placed clips.
    var pats = new List<int>();
    for (int k = 0; k < 3; k++)
    {
        int p;
        try { p = await fl.CreatePatternAsync(ct); }
        catch (Exception ex) { Console.WriteLine($"create pattern {k} FAILED: " + ex.Message); return 3; }
        try
        {
            await fl.AddNotesAsync(p, new[]
            {
                new FruityLink.Core.Abstractions.NoteSpec(0, 60 + k, 0, ppq, 100),
                new FruityLink.Core.Abstractions.NoteSpec(0, 64 + k, ppq * 2, ppq, 100),
            }, ct);
        }
        catch (Exception ex) { Console.WriteLine($"add notes to pat {p} note: " + ex.Message); }
        pats.Add(p);
        Console.WriteLine($"pattern {p} created (+2 notes)");
    }
    try { await fl.SetSongModeAsync(true, ct); } catch { }

    // ARRANGE: 6 pattern clips on tracks 1..6 at bars 0,2,4,6,8,10 (last clip start = bar 10, far past bar 1).
    var specs = new List<FruityLink.Core.Abstractions.PatternClipSpec>();
    for (int k = 0; k < 6; k++)
        specs.Add(new(pats[k % pats.Count], k + 1, k * 2 * barTicks, 0));
    try { await fl.AddPatternClipsAsync(specs, ct); Console.WriteLine("ARRANGED 6 clips on tracks 1..6 at bars 0,2,4,6,8,10"); }
    catch (Exception ex) { Console.WriteLine("ARRANGE FAILED: " + ex.Message); return 4; }

    string listAdd = await fl.ListClipsAsync(0, -1, ct);
    Console.WriteLine(listAdd);
    var far = ClipOnTrack(listAdd, 6, 10 * barTicks);   // furthest original clip (track 6 @ bar 10)
    int clipLen = far.len > 0 ? far.len : barTicks;
    int addFurthestEnd = 10 * barTicks + clipLen;
    string dAdd = await fl.DiagTransportAsync(ct); int maxAdd = MaxRange(dAdd);
    Console.WriteLine("AFTER add        : " + dAdd + $"   (furthest clip end={addFurthestEnd})");
    bool addOk = maxAdd >= addFurthestEnd - barTicks;
    Console.WriteLine($"ASSERT add-extends-song: {(addOk ? "PASS" : "FAIL")} (playRange max={maxAdd}, need >= {addFurthestEnd - barTicks})");

    // RESIZE the furthest clip (track 6 @ bar 10) to 4 bars -> it now ends at bar 14 (well past the bar-11
    // song end). This is the prime suspect: the shipped ResizeClips did NOT recompute the song length.
    int resizeLen = 4 * barTicks;
    int resizeEnd = 10 * barTicks + resizeLen;   // bar 14
    if (far.idx >= 0)
    {
        try { await fl.ResizeClipsAsync(new[] { new FruityLink.Core.Abstractions.ClipResize(far.idx, resizeLen) }, ct); Console.WriteLine($"RESIZED track-6 clip [{far.idx}] -> len {resizeLen} (ends at bar 14)"); }
        catch (Exception ex) { Console.WriteLine("RESIZE FAILED: " + ex.Message); }
    }
    else Console.WriteLine("RESIZE SKIPPED: furthest clip not found");
    string listResize = await fl.ListClipsAsync(0, -1, ct);
    var farR = ClipOnTrack(listResize, 6, 10 * barTicks);
    Console.WriteLine("clips after resize:\n" + listResize);
    bool resizePersist = farR.len == resizeLen;
    string dResize = await fl.DiagTransportAsync(ct); int maxResize = MaxRange(dResize);
    Console.WriteLine("AFTER resize     : " + dResize + $"   (resized clip end={resizeEnd})");
    Console.WriteLine($"ASSERT resize-persists-length: {(resizePersist ? "PASS" : "FAIL")} (track-6 len={farR.len}, want {resizeLen})");
    bool resizeOk = maxResize >= resizeEnd - barTicks;
    Console.WriteLine($"ASSERT resize-extends-song: {(resizeOk ? "PASS" : "FAIL")} (playRange max={maxResize}, need >= {resizeEnd - barTicks})");

    // DUPLICATE the resized clip -> the copy lands at bar 14 (start+len) and ends at bar 18 (even further).
    var farForDup = ClipOnTrack(listResize, 6, 10 * barTicks);
    int dupEnd = resizeEnd + resizeLen;   // bar 18
    if (farForDup.idx >= 0)
    {
        try { await fl.DuplicateClipAsync(farForDup.idx, ct); Console.WriteLine($"DUPLICATED track-6 clip [{farForDup.idx}] -> copy at bar 14, ends at bar 18"); }
        catch (Exception ex) { Console.WriteLine("DUPLICATE FAILED: " + ex.Message); }
    }
    else Console.WriteLine("DUPLICATE SKIPPED: resized clip not found");
    string listDup = await fl.ListClipsAsync(0, -1, ct);
    Console.WriteLine("clips after duplicate:\n" + listDup);
    var dup = ClipOnTrack(listDup, 6, resizeEnd);   // the copy at bar 14
    string dDup = await fl.DiagTransportAsync(ct); int maxDup = MaxRange(dDup);
    Console.WriteLine("AFTER duplicate  : " + dDup + $"   (duplicate clip end={dupEnd})");
    bool dupPresent = dup.idx >= 0;
    Console.WriteLine($"ASSERT duplicate-present: {(dupPresent ? "PASS" : "FAIL")} (copy on track 6 @ start {resizeEnd})");
    bool dupOk = maxDup >= dupEnd - barTicks;
    Console.WriteLine($"ASSERT duplicate-extends-song: {(dupOk ? "PASS" : "FAIL")} (playRange max={maxDup}, need >= {dupEnd - barTicks})");

    // The furthest content is now at bar 18 (dup) or bar 14 (resize) — use the biggest reach we could verify.
    int furthestEnd = dupPresent ? dupEnd : resizeEnd;
    int furthestStart = dupPresent ? resizeEnd : 10 * barTicks;

    // PLAY from the top and confirm the playhead advances.
    try { await fl.SeekAsync(0, ct); } catch (Exception ex) { Console.WriteLine("seek0 note: " + ex.Message); }
    try { await fl.TransportPlayAsync(ct); Console.WriteLine("PLAY"); } catch (Exception ex) { Console.WriteLine("PLAY FAILED: " + ex.Message); }
    await Task.Delay(700, ct);
    string r1 = await fl.DiagTransportAsync(ct); int e1 = EngTick(r1);
    Console.WriteLine("PLAY read1       : " + r1);
    await Task.Delay(600, ct);
    string r2 = await fl.DiagTransportAsync(ct); int e2 = EngTick(r2);
    Console.WriteLine("PLAY read2       : " + r2);
    bool moved = e1 != int.MinValue && e2 != int.MinValue && e2 != e1;
    bool notStuck = Math.Max(e1, e2) > 2;
    bool advanced = moved && notStuck;
    Console.WriteLine($"ASSERT playhead-advances: {(advanced ? "PASS" : "FAIL")} (pos read1={e1} read2={e2})");

    // SEEK INTO the far region (just before the furthest clip) and confirm the playhead can actually be
    // THERE — past the OLD (bar-11) song end. Without the song-length recompute, SeekAsync clamps to the
    // stale length and the playhead can never reach the far clips.
    int probe = Math.Max(0, furthestStart - barTicks / 2);
    try { await fl.SeekAsync(probe, ct); } catch (Exception ex) { Console.WriteLine("seek-probe note: " + ex.Message); }
    try { await fl.TransportPlayAsync(ct); } catch (Exception ex) { Console.WriteLine("PLAY2 note: " + ex.Message); }
    await Task.Delay(700, ct);
    string r3 = await fl.DiagTransportAsync(ct); int e3 = EngTick(r3);
    Console.WriteLine($"PLAY-in-region (seek {probe}) : " + r3);
    try { await fl.TransportStopAsync(ct); Console.WriteLine("STOP"); } catch (Exception ex) { Console.WriteLine("STOP note: " + ex.Message); }
    bool reached = e3 >= furthestStart - barTicks;   // playhead is in/near the far region (past the old end)
    Console.WriteLine($"ASSERT playhead-reaches-far-region: {(reached ? "PASS" : "FAIL")} (pos={e3}, far clips start at {furthestStart})");

    bool overall = addOk && resizePersist && resizeOk && dupPresent && dupOk && advanced && reached;
    Console.WriteLine($"OVERALL: {(overall ? "PASS" : "FAIL")} (add={addOk} resizePersist={resizePersist} resize={resizeOk} dupPresent={dupPresent} dup={dupOk} advances={advanced} reaches={reached})");

    // Cleanup: delete the temp arrangement, back to the user's.
    try
    {
        await fl.DeleteArrangementAsync(newArr, ct);
        await fl.SelectArrangementAsync(baseArr, ct);
        Console.WriteLine($"cleaned up: deleted temp arrangement {newArr}, back to {baseArr}");
    }
    catch (Exception ex) { Console.WriteLine("cleanup note: " + ex.Message); }
    Console.WriteLine($"FL alive: {await fl.IsAvailableAsync(ct)}");
    Console.WriteLine("=== ARRANGETEST DONE ===");
    return overall ? 0 : 8;
}

// arrangetest2: reproduce the RECURRED "plays but playhead won't move" bug on the CURRENT already-selected
// arrangement — the EXACT user case from the op-log (4 pattern clips placed 8 bars in at tick 30720, with
// NO SetSongMode anywhere in the session, then the user switches to song mode to hear it and playback is
// stuck). This differs from `arrangetest` in the two ways that matter: (1) it does NOT create a fresh
// arrangement (that real index change masks the bug); (2) it arranges while in PATTERN mode and places
// EVERY clip at tick 30720 with nothing at tick 0. Reads the song-object internals (b00/b04/gmode +
// play-range min/max) after each step so we can see exactly why the transport can't move. `arrangetest2
// song` variant arranges in song mode (to isolate the tick-30720/no-clip-at-0 factor from the mode
// factor). Non-destructive: adds clips on scratch tracks 20..23 and deletes exactly those on the way out.
if (args.Length > 0 && args[0].Equals("arrangetest2", StringComparison.OrdinalIgnoreCase))
{
    static int EngTick(string diag) { var m = System.Text.RegularExpressions.Regex.Match(diag, @"pos=(-?\d+)"); return m.Success ? int.Parse(m.Groups[1].Value) : int.MinValue; }
    static int MaxRange(string diag) { var m = System.Text.RegularExpressions.Regex.Match(diag, @"\.\.(-?\d+)\]"); return m.Success ? int.Parse(m.Groups[1].Value) : int.MinValue; }
    static int MinRange(string diag) { var m = System.Text.RegularExpressions.Regex.Match(diag, @"\[(-?\d+)\.\."); return m.Success ? int.Parse(m.Groups[1].Value) : int.MinValue; }

    bool preSong = args.Length > 1 && args[1].Equals("song", StringComparison.OrdinalIgnoreCase);
    Console.WriteLine($"=== ARRANGETEST2 (current arrangement, arrange @tick 30720 in {(preSong ? "SONG" : "PATTERN")} mode; playhead must reach it) ===");
    int ppq = await fl.GetPpqAsync(ct);
    int barTicks = ppq * 4;
    int startTick = 30720;   // the user's exact clip start (8 bars @ ppq 960)
    Console.WriteLine($"ppq={ppq} barTicks={barTicks} startTick={startTick}");

    // Mirror the user: STOP, then set the arrange-time mode. Default = PATTERN (the op-log had no SetSongMode).
    try { await fl.TransportStopAsync(ct); } catch (Exception ex) { Console.WriteLine("stop note: " + ex.Message); }
    try { await fl.SetSongModeAsync(preSong, ct); } catch (Exception ex) { Console.WriteLine("SetSongMode note: " + ex.Message); }
    Console.WriteLine($"song mode at arrange time: {await fl.GetSongModeAsync(ct)} (want {(preSong ? "song/true" : "pattern/false")})");
    Console.WriteLine("BEFORE arrange   : " + await fl.DiagTransportAsync(ct));

    // Pattern with 2 notes in the first bar (clip length ~1 bar) — mirrors pat6.
    int pat;
    try { pat = await fl.CreatePatternAsync(ct); Console.WriteLine($"created pattern {pat}"); }
    catch (Exception ex) { Console.WriteLine("create pattern FAILED: " + ex.Message); return 3; }
    try { await fl.AddNotesAsync(pat, new[] {
        new FruityLink.Core.Abstractions.NoteSpec(0, 60, 0, ppq, 100),
        new FruityLink.Core.Abstractions.NoteSpec(0, 64, ppq, ppq, 100),
    }, ct); } catch (Exception ex) { Console.WriteLine("add notes note: " + ex.Message); }

    // ARRANGE: 4 clips of `pat`, ALL at start=30720, on scratch tracks 20..23 (exact user shape: same far
    // start, multiple tracks, current arrangement, nothing at tick 0). AddPatternClipsAsync runs the code's
    // internal RecomputeSongLength — the thing under test.
    var specs = new List<FruityLink.Core.Abstractions.PatternClipSpec>();
    for (int t = 0; t < 4; t++) specs.Add(new(pat, 20 + t, startTick, 0));
    try { await fl.AddPatternClipsAsync(specs, ct); Console.WriteLine($"ARRANGED 4 clips of pat {pat} @tick {startTick} on tracks 20..23 (code's RecomputeSongLength ran)"); }
    catch (Exception ex) { Console.WriteLine("ARRANGE FAILED: " + ex.Message); return 4; }

    string listAdd = await fl.ListClipsAsync(0, -1, ct);
    int clipLen = barTicks;
    foreach (var ln in listAdd.Split('\n'))
    {
        var m = System.Text.RegularExpressions.Regex.Match(ln, @"^\[(\d+)\] track (\d+) start=(-?\d+) len=(-?\d+)");
        if (m.Success && int.Parse(m.Groups[2].Value) == 20 && int.Parse(m.Groups[3].Value) == startTick) { clipLen = int.Parse(m.Groups[4].Value); break; }
    }
    int furthestEnd = startTick + clipLen;
    Console.WriteLine($"furthest clip end = {furthestEnd} (start {startTick} + len {clipLen})");
    Console.WriteLine("AFTER arrange    : " + await fl.DiagTransportAsync(ct));

    // Switch to SONG mode (what the user does to hear the arrangement) and re-read the range.
    try { await fl.SetSongModeAsync(true, ct); } catch (Exception ex) { Console.WriteLine("SetSongMode(true) note: " + ex.Message); }
    string dSong = await fl.DiagTransportAsync(ct);
    int maxSong = MaxRange(dSong), minSong = MinRange(dSong);
    Console.WriteLine("AFTER song-mode  : " + dSong);
    bool rangeCovers = maxSong >= furthestEnd - barTicks;
    Console.WriteLine($"ASSERT range-covers-clips: {(rangeCovers ? "PASS" : "FAIL")} (playRange max={maxSong}, need >= {furthestEnd - barTicks}; min={minSong})");

    // Check the public content-length report without changing private object fields. The legacy +0xB04
    // diagnostic poke overwrites part of a viewport double on FL 2026; use native arrangement refresh only.
    static long Field(string s, string key) { var m = System.Text.RegularExpressions.Regex.Match(s, key + @"=(-?\d+)"); return m.Success ? long.Parse(m.Groups[1].Value) : long.MinValue; }
    string scope = await fl.DiagSongScopeAsync(ct: ct);
    long maxClipEnd = Field(scope, "maxClipEnd");
    string publicState = await fl.GetSongStateAsync(ct);
    long expectedBars = maxClipEnd > 0 ? (maxClipEnd + barTicks - 1) / barTicks : 0;
    bool lengthMatchesClips = maxClipEnd >= furthestEnd && Field(publicState, "songLength") == expectedBars;
    Console.WriteLine("scope (read-only): " + scope);
    Console.WriteLine($"ASSERT reported-length-covers-clips: {(lengthMatchesClips ? "PASS" : "FAIL")} (maxClipEnd={maxClipEnd}, expectedBars={expectedBars}; {publicState})");

    // PLAY from the top; playhead must advance.
    try { await fl.SeekAsync(0, ct); } catch (Exception ex) { Console.WriteLine("seek0 note: " + ex.Message); }
    try { await fl.TransportPlayAsync(ct); Console.WriteLine("PLAY from 0"); } catch (Exception ex) { Console.WriteLine("PLAY FAILED: " + ex.Message); }
    await Task.Delay(700, ct);
    string r1 = await fl.DiagTransportAsync(ct); int e1 = EngTick(r1);
    await Task.Delay(600, ct);
    string r2 = await fl.DiagTransportAsync(ct); int e2 = EngTick(r2);
    Console.WriteLine("PLAY read1       : " + r1);
    Console.WriteLine("PLAY read2       : " + r2);
    bool advanced = e1 != int.MinValue && e2 != int.MinValue && e2 != e1 && Math.Max(e1, e2) > 2;
    Console.WriteLine($"ASSERT playhead-advances: {(advanced ? "PASS" : "FAIL")} (pos read1={e1} read2={e2})");

    // SEEK into the far region and confirm the playhead can be THERE (past the old/stale song end).
    int probe = Math.Max(0, startTick - barTicks / 2);
    try { await fl.SeekAsync(probe, ct); } catch (Exception ex) { Console.WriteLine("seek-probe note: " + ex.Message); }
    try { await fl.TransportPlayAsync(ct); } catch (Exception ex) { Console.WriteLine("PLAY2 note: " + ex.Message); }
    await Task.Delay(700, ct);
    string r3 = await fl.DiagTransportAsync(ct); int e3 = EngTick(r3);
    Console.WriteLine($"PLAY-in-region (seek {probe}): " + r3);
    try { await fl.TransportStopAsync(ct); } catch (Exception ex) { Console.WriteLine("stop note: " + ex.Message); }
    bool reached = e3 >= startTick - barTicks;
    Console.WriteLine($"ASSERT playhead-reaches-far-region: {(reached ? "PASS" : "FAIL")} (pos={e3}, clips at {startTick})");

    // Cleanup: delete exactly the scratch clips we added (tracks 20..23 @ startTick), found by signature so a
    // reordered collection can never make us delete a real clip.
    string listEnd = await fl.ListClipsAsync(0, -1, ct);
    var toDelete = new List<int>();
    foreach (var ln in listEnd.Split('\n'))
    {
        var m = System.Text.RegularExpressions.Regex.Match(ln, @"^\[(\d+)\] track (\d+) start=(-?\d+)");
        if (m.Success)
        {
            int tr = int.Parse(m.Groups[2].Value), st = int.Parse(m.Groups[3].Value);
            if (tr >= 20 && tr <= 23 && st == startTick) toDelete.Add(int.Parse(m.Groups[1].Value));
        }
    }
    if (toDelete.Count > 0) { try { await fl.DeleteClipsAsync(toDelete, ct); Console.WriteLine($"cleaned up: deleted {toDelete.Count} scratch clips"); } catch (Exception ex) { Console.WriteLine("cleanup note: " + ex.Message); } }
    try { await fl.SetSongModeAsync(false, ct); } catch { }

    bool overall = rangeCovers && advanced && reached && lengthMatchesClips;
    Console.WriteLine($"OVERALL: {(overall ? "PASS" : "FAIL")} (rangeCovers={rangeCovers} advances={advanced} reaches={reached} lengthMatchesClips={lengthMatchesClips})");
    Console.WriteLine($"FL alive: {await fl.IsAvailableAsync(ct)}");
    Console.WriteLine("=== ARRANGETEST2 DONE ===");
    return overall ? 0 : 8;
}

if (args.Length > 0 && args[0].Equals("clips", StringComparison.OrdinalIgnoreCase))
{
    Console.WriteLine("=== ListClipsAsync (public tool path) ===");
    try { Console.WriteLine(await fl.ListClipsAsync(0, -1, ct)); }
    catch (Exception ex) { Console.WriteLine("ListClips THREW: " + ex.Message); }
    Console.WriteLine("=== raw clip-collection dump ===");
    try { Console.WriteLine(await fl.DiagClipsAsync(ct)); }
    catch (Exception ex) { Console.WriteLine("DiagClips THREW: " + ex.Message); }
    Console.WriteLine("=== channels (what project loaded) ===");
    try { Console.WriteLine(await fl.ListChannelsAsync(ct)); }
    catch (Exception ex) { Console.WriteLine("ListChannels THREW: " + ex.Message); }
    return 0;
}

// deltest: verify move + REAL delete on a SCRATCH clip (track 250, using existing pattern 1) so the
// template's own clips are never touched. PASS = clip adds, moves (renders), deletes (count restored), FL alive.
if (args.Length > 0 && args[0].Equals("deltest", StringComparison.OrdinalIgnoreCase))
{
    static int FindIdxOnTrack(string list, int track)
    {
        foreach (var ln in list.Split('\n'))
        {
            var m = System.Text.RegularExpressions.Regex.Match(ln, @"^\[(\d+)\] track (\d+)");
            if (m.Success && int.Parse(m.Groups[2].Value) == track) return int.Parse(m.Groups[1].Value);
        }
        return -1;
    }
    int st = 250;
    int c0 = await fl.ClipCountAsync(ct);
    Console.WriteLine($"initial clip count = {c0}");
    try { await fl.AddPatternClipAsync(1, st, 0, 0, ct); Console.WriteLine($"added pattern 1 clip on scratch track {st}"); }
    catch (Exception ex) { Console.WriteLine("ADD FAILED: " + ex.Message); return 5; }
    string l1 = await fl.ListClipsAsync(0, -1, ct); Console.WriteLine(l1);
    int sIdx = FindIdxOnTrack(l1, st);
    if (sIdx < 0) { Console.WriteLine("scratch clip not found in list!"); return 6; }
    Console.WriteLine($"scratch clip index = {sIdx}");
    try { await fl.MoveClipAsync(sIdx, 3840, st + 1, ct); Console.WriteLine($"moved clip {sIdx} -> track {st + 1} @3840"); }
    catch (Exception ex) { Console.WriteLine("MOVE FAILED: " + ex.Message); }
    string l2 = await fl.ListClipsAsync(0, -1, ct); Console.WriteLine(l2);
    int sIdx2 = FindIdxOnTrack(l2, st + 1);
    if (sIdx2 < 0) { Console.WriteLine("moved clip not found on new track — move didn't apply!"); return 7; }
    try { await fl.DeleteClipAsync(sIdx2, ct); Console.WriteLine($"deleted clip {sIdx2}"); }
    catch (Exception ex) { Console.WriteLine("DELETE FAILED: " + ex.Message); }
    int c2 = await fl.ClipCountAsync(ct);
    Console.WriteLine($"clip count after delete = {c2} (expect {c0})");
    Console.WriteLine(await fl.ListClipsAsync(0, -1, ct));
    Console.WriteLine($"FL alive: {await fl.IsAvailableAsync(ct)}");
    Console.WriteLine("=== DELTEST DONE ===");
    return 0;
}

// savetest: reproduce the UI-freeze path — save on an UNTITLED project. PASS = SaveProject throws a
// CLEAN error (no modal) and FL's MAIN THREAD stays alive (a callOnMain still works right after).
if (args.Length > 0 && args[0].Equals("savetest", StringComparison.OrdinalIgnoreCase))
{
    Console.WriteLine("project info:\n" + await fl.GetProjectInfoAsync(ct));
    string scratch = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "fl-savetest.flp");
    Console.WriteLine("SaveProjectAsync on the current project (expect a CLEAN throw if untitled, NOT a freeze)...");
    try { await fl.SaveProjectAsync(scratch, ct); Console.WriteLine("  -> returned OK (project is titled)"); }
    catch (Exception ex) { Console.WriteLine("  -> threw (expected on untitled): " + ex.Message); }
    Console.WriteLine("IsAvailable: " + await fl.IsAvailableAsync(ct));
    int ppq = -1;
    try { ppq = await fl.GetPpqAsync(ct); } catch (Exception ex) { Console.WriteLine("  ppq read threw: " + ex.Message); }
    Console.WriteLine($"PPQ read = {ppq}  (returns => FL responsive)");
    Console.WriteLine("=== SAVETEST DONE — confirm FL not wedged via: flprobe bridge \"call 11e32c0\" (ok:1) ===");
    return 0;
}

// bulktest: verify the BULK playlist-clip ops against a running FL, PROVING they're correct — especially
// that bulk DELETE is descending-index shift-safe (a naive ascending delete removes the WRONG clips once
// indices shift). NON-DESTRUCTIVE: uses SCRATCH playlist tracks 240-246 + EXISTING patterns 1-4 (creates
// no new patterns) and cleans up after itself. PASS/FAIL lines are grep-friendly ("ASSERT <name>: PASS").
if (args.Length > 0 && args[0].Equals("bulktest", StringComparison.OrdinalIgnoreCase))
{
    // Parse ListClips output ("[i] track T start=S len=L pattern N") into structured rows. Tolerant of
    // both "key=val" and "key val" so it survives minor formatting drift.
    static List<(int idx, int track, int start, int len, int pattern)> ParseClips(string list)
    {
        var rows = new List<(int, int, int, int, int)>();
        foreach (var ln in list.Split('\n'))
        {
            var mi = System.Text.RegularExpressions.Regex.Match(ln, @"^\[(\d+)\]");
            if (!mi.Success) continue;
            int Field(string key)
            {
                var mm = System.Text.RegularExpressions.Regex.Match(ln, key + @"=?\s*(-?\d+)");
                return mm.Success ? int.Parse(mm.Groups[1].Value) : -1;
            }
            rows.Add((int.Parse(mi.Groups[1].Value), Field("track"), Field("start"), Field("len"), Field("pattern")));
        }
        return rows;
    }
    static int IdxOnTrack(List<(int idx, int track, int start, int len, int pattern)> clips, int track)
    {
        foreach (var c in clips) if (c.track == track) return c.idx;
        return -1;
    }
    static bool IsScratch(int track) => track >= 240 && track <= 246;
    // Fingerprint of the NON-scratch (pre-existing / template) clips so we can prove none were touched.
    static string TemplateFingerprint(List<(int idx, int track, int start, int len, int pattern)> clips)
    {
        var sigs = new List<string>();
        foreach (var c in clips) if (!IsScratch(c.track)) sigs.Add($"{c.track}|{c.start}|{c.len}|{c.pattern}");
        sigs.Sort();
        return string.Join(";", sigs);
    }

    Console.WriteLine("=== BULKTEST (bulk add/move/resize/mute/delete; delete MUST be index-shift-safe) ===");

    // 1) initial count + snapshot of the pre-existing template clips.
    int initial = -1;
    try { initial = await fl.ClipCountAsync(ct); } catch (Exception ex) { Console.WriteLine("COUNT FAILED: " + ex.Message); }
    Console.WriteLine($"initial count = {initial}");
    string listInit = "";
    try { listInit = await fl.ListClipsAsync(0, -1, ct); } catch (Exception ex) { Console.WriteLine("LIST FAILED: " + ex.Message); }
    string tmplBefore = TemplateFingerprint(ParseClips(listInit));

    // 2) BULK ADD — four PatternClipSpec (patterns 1-4 on scratch tracks 240-243 @start 0, len 0) in ONE call.
    var toAdd = new List<FruityLink.Core.Abstractions.PatternClipSpec>
    {
        new(1, 240, 0, 0),
        new(2, 241, 0, 0),
        new(3, 242, 0, 0),
        new(4, 243, 0, 0),
    };
    try { await fl.AddPatternClipsAsync(toAdd, ct); Console.WriteLine("bulk-added 4 clips (pat 1-4 -> tracks 240-243) in ONE AddPatternClipsAsync call"); }
    catch (Exception ex) { Console.WriteLine("BULK ADD FAILED: " + ex.Message); }
    int afterAdd = -1;
    try { afterAdd = await fl.ClipCountAsync(ct); } catch (Exception ex) { Console.WriteLine("COUNT FAILED: " + ex.Message); }
    Console.WriteLine($"count after add = {afterAdd} (expect {initial + 4})");
    Console.WriteLine($"ASSERT add-count: {(afterAdd == initial + 4 ? "PASS" : "FAIL")}");
    string listAfterAdd = "";
    try { listAfterAdd = await fl.ListClipsAsync(0, -1, ct); Console.WriteLine(listAfterAdd); } catch (Exception ex) { Console.WriteLine("LIST FAILED: " + ex.Message); }

    // 3) BULK MOVE — find the track-240 and track-241 scratch clips, move BOTH in ONE call
    //    (track-240 clip -> track 245 @1920, track-241 clip -> track 246 @3840).
    var clipsA = ParseClips(listAfterAdd);
    int mv240 = IdxOnTrack(clipsA, 240);
    int mv241 = IdxOnTrack(clipsA, 241);
    Console.WriteLine($"scratch indices to move: track240=[{mv240}] track241=[{mv241}]");
    if (mv240 >= 0 && mv241 >= 0)
    {
        var moves = new List<FruityLink.Core.Abstractions.ClipMove>
        {
            new(mv240, 1920, 245),
            new(mv241, 3840, 246),
        };
        try { await fl.MoveClipsAsync(moves, ct); Console.WriteLine($"bulk-moved [{mv240}]->track245@1920 and [{mv241}]->track246@3840 in ONE MoveClipsAsync call"); }
        catch (Exception ex) { Console.WriteLine("BULK MOVE FAILED: " + ex.Message); }
    }
    else Console.WriteLine("BULK MOVE SKIPPED: scratch clips on tracks 240/241 not found");
    string listAfterMove = "";
    try { listAfterMove = await fl.ListClipsAsync(0, -1, ct); Console.WriteLine(listAfterMove); } catch (Exception ex) { Console.WriteLine("LIST FAILED: " + ex.Message); }
    var clipsM = ParseClips(listAfterMove);
    bool landed245 = IdxOnTrack(clipsM, 245) >= 0;
    bool landed246 = IdxOnTrack(clipsM, 246) >= 0;
    bool left240 = IdxOnTrack(clipsM, 240) < 0;
    bool left241 = IdxOnTrack(clipsM, 241) < 0;
    Console.WriteLine($"ASSERT move-landed: {((landed245 && landed246 && left240 && left241) ? "PASS" : "FAIL")} (on245={landed245} on246={landed246} vacated240={left240} vacated241={left241})");

    // 4) BULK RESIZE — resize the track-242 scratch clip to length 7680 in ONE call; verify via ListClips.
    int rz242 = IdxOnTrack(clipsM, 242);
    const int resizeLen = 7680;
    if (rz242 >= 0)
    {
        var resizes = new List<FruityLink.Core.Abstractions.ClipResize> { new(rz242, resizeLen) };
        try { await fl.ResizeClipsAsync(resizes, ct); Console.WriteLine($"bulk-resized [{rz242}] (track 242) -> len {resizeLen} in ONE ResizeClipsAsync call"); }
        catch (Exception ex) { Console.WriteLine("BULK RESIZE FAILED: " + ex.Message); }
    }
    else Console.WriteLine("BULK RESIZE SKIPPED: scratch clip on track 242 not found");
    string listAfterResize = "";
    try { listAfterResize = await fl.ListClipsAsync(0, -1, ct); Console.WriteLine(listAfterResize); } catch (Exception ex) { Console.WriteLine("LIST FAILED: " + ex.Message); }
    var clipsR = ParseClips(listAfterResize);
    int len242 = -1;
    foreach (var c in clipsR) if (c.track == 242) len242 = c.len;
    Console.WriteLine($"track-242 len after resize = {len242} (expect {resizeLen})");
    Console.WriteLine($"ASSERT resize-len: {(len242 == resizeLen ? "PASS" : "FAIL")}");

    // 4b) BULK MUTE smoke — exercise SetClipsMutedAsync on all scratch clips, then unmute. Mute state is
    //     not surfaced by ListClips, so this is a no-throw smoke check (no PASS/FAIL assert) and leaves
    //     the clips unmuted before delete.
    var muteIdx = new List<int>();
    foreach (var t in new[] { 242, 243, 245, 246 }) { int mi = IdxOnTrack(clipsR, t); if (mi >= 0) muteIdx.Add(mi); }
    try { await fl.SetClipsMutedAsync(muteIdx, true, ct); Console.WriteLine($"bulk-muted scratch clips [{string.Join(",", muteIdx)}] in ONE call"); }
    catch (Exception ex) { Console.WriteLine("BULK MUTE FAILED: " + ex.Message); }
    try { await fl.SetClipsMutedAsync(muteIdx, false, ct); Console.WriteLine("bulk-unmuted scratch clips in ONE call"); }
    catch (Exception ex) { Console.WriteLine("BULK UNMUTE FAILED: " + ex.Message); }

    // 5) BULK DELETE — the accuracy test. The 4 scratch clips now live on tracks 242,243,245,246 and are
    //    NON-CONTIGUOUS in the collection. Delete all four in ONE DeleteClipsAsync call, passing indices in
    //    ASCENDING order (the adversarial case): a naive ascending delete would shift later indices down and
    //    remove the WRONG clips. A correct impl deletes descending. PASS = count restored AND every template
    //    clip still present AND nothing left on scratch tracks.
    var delIdx = new List<int>();
    foreach (var t in new[] { 242, 243, 245, 246 }) { int di = IdxOnTrack(clipsR, t); if (di >= 0) delIdx.Add(di); }
    delIdx.Sort();
    Console.WriteLine($"deleting NON-CONTIGUOUS scratch indices [{string.Join(",", delIdx)}] (ascending) in ONE DeleteClipsAsync call");
    try { await fl.DeleteClipsAsync(delIdx, ct); Console.WriteLine("bulk-deleted"); }
    catch (Exception ex) { Console.WriteLine("BULK DELETE FAILED: " + ex.Message); }
    int afterDel = -1;
    try { afterDel = await fl.ClipCountAsync(ct); } catch (Exception ex) { Console.WriteLine("COUNT FAILED: " + ex.Message); }
    Console.WriteLine($"count after delete = {afterDel} (expect {initial})");
    string listFinal = "";
    try { listFinal = await fl.ListClipsAsync(0, -1, ct); Console.WriteLine(listFinal); } catch (Exception ex) { Console.WriteLine("LIST FAILED: " + ex.Message); }
    var clipsF = ParseClips(listFinal);
    bool anyScratchLeft = false;
    foreach (var c in clipsF) if (IsScratch(c.track)) anyScratchLeft = true;
    string tmplAfter = TemplateFingerprint(clipsF);
    bool tmplIntact = tmplBefore == tmplAfter;
    Console.WriteLine($"ASSERT delete-count: {(afterDel == initial ? "PASS" : "FAIL")}");
    Console.WriteLine($"ASSERT delete-no-scratch-left: {(!anyScratchLeft ? "PASS" : "FAIL")}");
    Console.WriteLine($"ASSERT delete-template-intact: {(tmplIntact ? "PASS" : "FAIL")}  (proves descending-index shift-safety)");
    if (!tmplIntact)
    {
        Console.WriteLine("  template BEFORE: " + tmplBefore);
        Console.WriteLine("  template AFTER : " + tmplAfter);
    }

    Console.WriteLine($"FL alive: {await fl.IsAvailableAsync(ct)}");
    Console.WriteLine("=== BULKTEST DONE ===");
    return 0;
}

// vctest: prove SaveCopyAsync (the VC backup path) is MODAL-FREE + side-effect-free on an UNTITLED
// project (raw FLproj_WriteFlpFile). PASS = .flp written, FL main thread NOT wedged, title unchanged.
if (args.Length > 0 && args[0].Equals("vctest", StringComparison.OrdinalIgnoreCase))
{
    Console.WriteLine("project info BEFORE:\n" + await fl.GetProjectInfoAsync(ct));
    string p = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "fl-vctest.flp");
    if (System.IO.File.Exists(p)) System.IO.File.Delete(p);
    Console.WriteLine($"SaveCopyAsync -> {p} (untitled-safe raw writer; expect NO modal/freeze)...");
    try { await fl.SaveCopyAsync(p, ct); Console.WriteLine("  SaveCopy returned"); }
    catch (Exception ex) { Console.WriteLine("  SaveCopy threw: " + ex.Message); }
    Console.WriteLine("IsAvailable: " + await fl.IsAvailableAsync(ct));
    bool wrote = System.IO.File.Exists(p);
    Console.WriteLine($".flp written: {wrote} size={(wrote ? new System.IO.FileInfo(p).Length : 0)}");
    Console.WriteLine("project info AFTER (title/path must be UNCHANGED = still untitled):\n" + await fl.GetProjectInfoAsync(ct));
    Console.WriteLine("=== VCTEST DONE — confirm main thread NOT wedged via: flprobe bridge \"call 11e32c0\" (ok:1) ===");
    return 0;
}

// getset: verify the UNDO building blocks live — for each covered value-set op, read OLD -> set NEW ->
// read back (must equal NEW) -> restore OLD (must equal OLD). A FAIL means the getter/setter scales differ,
// so undo would restore a WRONG value. Net-zero (everything restored).
if (args.Length > 0 && args[0].Equals("getset", StringComparison.OrdinalIgnoreCase))
{
    Console.WriteLine("=== GET/SET SYMMETRY (undo building blocks) ===");
    static string PF(bool b) => b ? "PASS" : "FAIL";
    { double o = await fl.GetTempoAsync(ct); double n = o + 7; await fl.SetTempoAsync(n, ct); double g = await fl.GetTempoAsync(ct); await fl.SetTempoAsync(o, ct); double r = await fl.GetTempoAsync(ct);
      Console.WriteLine($"ASSERT tempo:       {PF(System.Math.Abs(g - n) < 0.5 && System.Math.Abs(r - o) < 0.5)} (old={o} set={n} got={g} restored={r})"); }
    { int o = await fl.GetMasterPitchAsync(ct); int n = o + 40; await fl.SetMasterPitchAsync(n, ct); int g = await fl.GetMasterPitchAsync(ct); await fl.SetMasterPitchAsync(o, ct); int r = await fl.GetMasterPitchAsync(ct);
      Console.WriteLine($"ASSERT masterpitch: {PF(g == n && r == o)} (old={o} set={n} got={g} restored={r})"); }
    { long o = await fl.GetChannelVolumeAsync(0, ct); int n = (int)o > 4000 ? (int)o - 1500 : (int)o + 1500; await fl.SetChannelVolumeAsync(0, n, ct); long g = await fl.GetChannelVolumeAsync(0, ct); await fl.SetChannelVolumeAsync(0, (int)o, ct); long r = await fl.GetChannelVolumeAsync(0, ct);
      Console.WriteLine($"ASSERT chanvol:     {PF(g == n && r == o)} (old={o} set={n} got={g} restored={r})"); }
    { int o = await fl.GetChannelPanAsync(0, ct); int n = o > 0 ? o - 500 : o + 500; await fl.SetChannelPanAsync(0, n, ct); int g = await fl.GetChannelPanAsync(0, ct); await fl.SetChannelPanAsync(0, o, ct); int r = await fl.GetChannelPanAsync(0, ct);
      Console.WriteLine($"ASSERT chanpan:     {PF(g == n && r == o)} (old={o} set={n} got={g} restored={r})"); }
    { string o = await fl.GetTrackNameAsync(1, ct); await fl.SetTrackNameAsync(1, "UndoVerify", ct); string g = await fl.GetTrackNameAsync(1, ct); await fl.SetTrackNameAsync(1, o, ct); string r = await fl.GetTrackNameAsync(1, ct);
      bool ok = g == "UndoVerify" && r == o;
      Console.WriteLine($"ASSERT trackname:   {PF(ok)} (old='{o}' got='{g}' restored='{r}')"); }
    Console.WriteLine("=== GETSET DONE — all PASS => undo restores those values correctly ===");
    return 0;
}

// osctest / oscsweep / oscset: WAVE-SHAPE sound-design verification on FL's native 3xOSC generator.
// Proves the standard plugin-param path (ListPluginParams/SetPluginParam) works for a NATIVE generator
// (not just VST3/Serum). Run against a scratch/empty project — each osctest adds ONE 3x Osc channel.
//   osctest                                 -> add a "3x Osc" channel, dump ALL params (name = display)
//   oscsweep <chan> <idx>                    -> sweep param <idx> 0..1 in 0.1 steps (NB: 3xOSC's osc-shape
//                                               DISPLAY string is cosmetic/stuck; the SET still applies)
//   oscset  <chan> <idxA> <valA> [idxB valB] -> set one/two params (norm 0..1), read them back
// Shape params: OSC1=1, OSC2=8, OSC3=15; value=shapeIndex/6 (sine 0, tri .167, sq .333, saw .5, rsaw .667,
// noise .833, custom 1.0). Verified: oscset 17 1 0.5 8 0.333 15 0.833 -> OSC1 saw, OSC2 square, OSC3 noise.
if (args.Length > 0 && args[0].Equals("osctest", StringComparison.OrdinalIgnoreCase))
{
    Console.WriteLine("=== OSCTEST: add native 3x Osc channel + dump params (does the VST param path work for it?) ===");
    int ch;
    try { ch = await fl.AddChannelAsync("3x Osc", ct); Console.WriteLine($"added channel {ch} = '3x Osc'"); }
    catch (Exception ex) { Console.WriteLine("ADD FAILED: " + ex.Message); return 3; }
    try { Console.WriteLine("plugin: " + await fl.GetChannelPluginAsync(ch, ct)); } catch (Exception ex) { Console.WriteLine("getplugin note: " + ex.Message); }
    try { Console.WriteLine("--- ALL params (index: name = display) ---\n" + await fl.ListPluginParamsAsync(ch, -1, null, ct)); }
    catch (Exception ex) { Console.WriteLine("LIST FAILED: " + ex.Message); return 4; }
    Console.WriteLine($"FL alive: {await fl.IsAvailableAsync(ct)}");
    Console.WriteLine("=== OSCTEST DONE (use the printed channel index for oscsweep/oscset) ===");
    return 0;
}
if (args.Length >= 3 && args[0].Equals("oscsweep", StringComparison.OrdinalIgnoreCase)
    && int.TryParse(args[1], out int swChan) && int.TryParse(args[2], out int swIdx))
{
    Console.WriteLine($"=== OSCSWEEP chan {swChan} param {swIdx}: sweep 0..1 -> reveal value->shape enum map ===");
    for (int s = 0; s <= 10; s++)
    {
        double v = s / 10.0;
        try { await fl.SetPluginParamAsync(swChan, -1, swIdx, v, ct); }
        catch (Exception ex) { Console.WriteLine($"  set {v:0.0} FAILED: " + ex.Message); continue; }
        await Task.Delay(70, ct);
        string cur = await fl.ListPluginParamsAsync(swChan, -1, null, ct);
        string hit = cur.Split('\n').FirstOrDefault(l => l.TrimStart().StartsWith(swIdx + ":")) ?? "(not found)";
        Console.WriteLine($"  set {v:0.00} -> {hit.Trim()}");
    }
    Console.WriteLine("=== OSCSWEEP DONE ===");
    return 0;
}
if (args.Length >= 4 && args[0].Equals("oscset", StringComparison.OrdinalIgnoreCase)
    && int.TryParse(args[1], out int osChan))
{
    var sets = new List<(int idx, double val)>();
    for (int a = 2; a + 1 < args.Length; a += 2)
        if (int.TryParse(args[a], out int pi) && double.TryParse(args[a + 1], System.Globalization.CultureInfo.InvariantCulture, out double pv))
            sets.Add((pi, pv));
    Console.WriteLine($"=== OSCSET chan {osChan}: set {sets.Count} param(s) + read back ===");
    string before = await fl.ListPluginParamsAsync(osChan, -1, null, ct);
    foreach (var (si, _) in sets)
        Console.WriteLine("before -> " + (before.Split('\n').FirstOrDefault(l => l.TrimStart().StartsWith(si + ":")) ?? "(?)").Trim());
    foreach (var (si, sv) in sets)
    {
        try { await fl.SetPluginParamAsync(osChan, -1, si, sv, ct); Console.WriteLine($"set param {si} = {sv:0.###}"); }
        catch (Exception ex) { Console.WriteLine($"set param {si} FAILED: " + ex.Message); }
    }
    await Task.Delay(150, ct);
    string after = await fl.ListPluginParamsAsync(osChan, -1, null, ct);
    foreach (var (si, _) in sets)
        Console.WriteLine("after  -> " + (after.Split('\n').FirstOrDefault(l => l.TrimStart().StartsWith(si + ":")) ?? "(?)").Trim());
    Console.WriteLine($"FL alive: {await fl.IsAvailableAsync(ct)}");
    Console.WriteLine("=== OSCSET DONE ===");
    return 0;
}

// Locate Serum's channel.
int count = await fl.GetChannelCountAsync(ct);
int serum = -1;
Console.WriteLine($"--- {count} channels ---");
for (int i = 0; i < count; i++)
{
    string nm = "", plug = "";
    try { nm = await fl.GetChannelNameAsync(i, ct); } catch { }
    try { plug = await fl.GetChannelPluginAsync(i, ct); } catch { }
    Console.WriteLine($"ch {i}: name='{nm}' plugin='{plug}'");
    if (serum < 0 && (nm + " " + plug).Contains("serum", StringComparison.OrdinalIgnoreCase))
        serum = i;
}
if (serum < 0) { Console.WriteLine("No Serum channel found."); return 2; }
Console.WriteLine($">> Serum channel = {serum}");

string mode = args.Length > 0 ? args[0].ToLowerInvariant() : "discover";


if (mode == "set" && args.Length >= 3 && int.TryParse(args[1], out int idx) &&
    double.TryParse(args[2], System.Globalization.CultureInfo.InvariantCulture, out double val))
{
    string before = await fl.ListPluginParamsAsync(serum, -1, null, ct);
    foreach (var line in before.Split('\n'))
        if (line.StartsWith(idx + ":")) Console.WriteLine("before -> " + line.Trim());
    Console.WriteLine($"setting param {idx} = {val:0.###} ...");
    await fl.SetPluginParamAsync(serum, -1, idx, val, ct);
    await Task.Delay(150, ct);
    string after = await fl.ListPluginParamsAsync(serum, -1, null, ct);
    foreach (var line in after.Split('\n'))
        if (line.StartsWith(idx + ":")) Console.WriteLine("after  -> " + line.Trim());
    return 0;
}

if (mode == "list" && args.Length >= 2)
{
    Console.WriteLine($"--- params matching '{args[1]}' ---");
    Console.WriteLine(await fl.ListPluginParamsAsync(serum, -1, args[1], ct));
    return 0;
}

// discovery
foreach (var f in new[] { "cutoff", "filter", "macro", "master", "level", "vol", "osc" })
{
    Console.WriteLine($"--- params matching '{f}' ---");
    Console.WriteLine(await fl.ListPluginParamsAsync(serum, -1, f, ct));
}
return 0;
