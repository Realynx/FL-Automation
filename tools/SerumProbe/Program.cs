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
