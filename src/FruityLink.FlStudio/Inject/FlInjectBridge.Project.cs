using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Text.Json;
using FruityLink.Core.Abstractions;

namespace FruityLink.FlStudio.Inject;

// Project + song: transport play/stop, song/transport state, markers, project lifecycle (open/save/new), arrangements, render/export.
// Partial of the FlInjectBridge god-class split; see FlInjectBridge.cs for the class doc.
public sealed partial class FlInjectBridge
{
    // ============================ Transport ============================
    // FLgl_GlobalCommandDispatch(op, value, mode, flag) — scalar args, uses internal song/transport
    // globals (no context → cannot crash like the earlier fake-ctx attempt).

    // args: op, value(must be >0), mode(must have bit 0x2 or it returns -1), flag(0x8 to reach dispatch).
    // Verified live via the play-state flag (*(*0x14A81C0)): PLAY 0->1, STOP ->0.
    public Task TransportPlayAsync(CancellationToken ct = default)
    { LogOp("TransportPlay"); return CallAsync("ef7b20", new ulong[] { 10, 1, 2, 8 }, ct); }
    public Task TransportStopAsync(CancellationToken ct = default)
    { LogOp("TransportStop"); return CallAsync("ef7b20", new ulong[] { 11, 1, 2, 8 }, ct); }
    public Task TransportToggleRecordAsync(CancellationToken ct = default)
    { LogOp("TransportToggleRecord"); return CallAsync("ef7b20", new ulong[] { 12, 1, 2, 8 }, ct); }

    /// <summary>FL's current status/hint bar text, cleaned of FL's "tooltip|status" split + '^' markup.</summary>
    public async Task<string> GetStatusAsync(CancellationToken ct = default)
        => CleanHint(await RawAsync("status", 4096, ct));

    // FL stores the raw hint as "tooltip|status" with '^' markup tokens (re/ui-gap-popup-hint-render). Take
    // the status side (after '|') and strip '^X' markup for a clean, human-readable status string.
    private static string CleanHint(string raw)
    {
        if (string.IsNullOrEmpty(raw) || raw.StartsWith("err:", StringComparison.Ordinal)) return string.Empty;
        int bar = raw.IndexOf('|');
        string s = bar >= 0 ? raw.Substring(bar + 1) : raw;
        var sb = new System.Text.StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '^') { i++; continue; }   // skip '^' + its following markup char
            sb.Append(s[i]);
        }
        return sb.ToString().Trim();
    }

    /// <summary>Reads playhead (bar/beat/tick), song-vs-pattern mode, play state, loop region and playlist content length.</summary>
    public async Task<string> GetSongStateAsync(CancellationToken ct = default)
    {
        // Mode + play state are behind the .data→.bss indirection table — read *(*(slot)), not *(slot).
        int mode = await DerefI32Async("14a8670", ct);     // 0 pattern / 1 song
        int playing = await DerefI32Async("14a81c0", ct);  // 1 while transport is playing
        int ppq = await GetPpqAsync(ct);
        long ppqBar = (long)ppq * 4;                       // assumes 4/4
        // The toolbar slider supplies the playhead only. Its min/max describe the full seek domain,
        // not the selected loop; FL draws its selection from separate native transport globals.
        var (rangeMin, rangeMax) = await ReadTransportRangeAsync(ct);
        int tick = 0;
        try
        {
            ulong slot = await GPtrAsync("14aa4c8", ct);
            ulong toolbar = slot != 0 ? await APtrAsync(slot, ct) : 0;
            ulong sp = toolbar != 0 ? await APtrAsync(toolbar + 0x7e8, ct) : 0;
            if (sp > 0x10000)
            {
                tick = await AI32Async(sp + 0x3c0, ct);
            }
        }
        catch (InvalidOperationException) { /* toolbar not ready — report tick 0 rather than throw */ }
        long bar = ppqBar > 0 ? tick / ppqBar + 1 : 0;
        long beat = (ppq > 0 && ppqBar > 0) ? (tick % ppqBar) / ppq + 1 : 0;
        // Content length comes from live clip spans, excluding marker-only export tails.
        // Cached song-object offsets are not a verified reporting contract across FL builds.
        long songTicks = await MaxClipEndAsync(ct);
        long songBars = songTicks > 0 && ppqBar > 0 ? 1 + (songTicks - 1) / ppqBar : 0;
        // Tempo + master state ride along: they're one bus GET each and the model otherwise has NO
        // read for master volume/pitch/shuffle at all (their setters are tools, their getters weren't).
        double bpm = await GetTempoAsync(ct);
        int masterVol = await GetMasterVolumeAsync(ct);
        int shuffle = await GetShuffleAsync(ct);
        int masterPitch = await GetMasterPitchAsync(ct);
        return $"mode={(mode == 1 ? "song" : "pattern")} playing={(playing == 1 ? "yes" : "no")} pos=bar {bar} beat {beat} (tick {tick}) ppq={ppq} songLength={songBars} bars playRange=[{rangeMin}..{rangeMax}]"
            + $" tempo={bpm:0.##} masterVol={masterVol}"
            + (shuffle != 0 ? $" shuffle={shuffle}" : "")
            + (masterPitch != 0 ? $" masterPitch={masterPitch}c" : "");
    }

    /// <summary>Read-only toolbar playhead, native transport range/mode and playlist content span.
    /// Unverified cached song-object and marker fields are deliberately omitted.</summary>
    public async Task<string> DiagTransportAsync(CancellationToken ct = default)
    {
        async Task<int> SafeI32(ulong addr) { try { return await AI32Async(addr, ct); } catch { return int.MinValue; } }
        async Task<ulong> SafePtr(ulong addr) { try { return await APtrAsync(addr, ct); } catch { return 0; } }

        // The song-position slider uses full-song seek bounds even while a narrower loop is active.
        // Read its playhead, and get the actual range independently through resolved native globals.
        ulong slot    = await GPtrAsync("14aa4c8", ct);
        ulong toolbar = slot != 0 ? await SafePtr(slot) : 0;
        ulong sp      = toolbar != 0 ? await SafePtr(toolbar + 0x7e8) : 0;
        int pos = int.MinValue;
        var (rmin, rmax) = await ReadTransportRangeAsync(ct);
        if (sp > 0x10000)
        {
            pos  = await SafeI32(sp + 0x3c0);
        }
        long contentEnd = await MaxClipEndAsync(ct);
        int gmode = await DerefI32Async("14a8670", ct);
        return $"sp=0x{sp:x} pos={pos} playRange=[{rmin}..{rmax}] (inclusive; -1=unavailable) contentEndTicks={contentEnd} cachedSongFields=unavailable gmode={gmode}";
    }

    public async Task SetSongModeAsync(bool song, CancellationToken ct = default)
    {
        LogOp("SetSongMode", $"song={song}");
        // 0x14a8670 is a POINTER slot (indirection table): the real mode int is *(*(0x14a8670)). Reading it
        // as a direct int returned the pointer (never == 1), so the setter used to toggle UNCONDITIONALLY —
        // flipping the mode the wrong way on every call. Double-deref so the toggle is idempotent.
        if ((await DerefI32Async("14a8670", ct) == 1) != song)
            await CallAsync("ef7b20", new ulong[] { 15, 1, 2, 0xf }, ct);  // toggle song/pattern
    }

    /// <summary>Read song mode (true) vs pattern mode (false) via the same *(*(0x14a8670)) the setter uses.</summary>
    public async Task<bool> GetSongModeAsync(CancellationToken ct = default)
        => await DerefI32Async("14a8670", ct) == 1;

    /// <summary>Move the song playhead to an absolute tick (live-verified). The function takes the tick
    /// as a DOUBLE in XMM0 (so it uses the XMM call path); mode 0 = normal seek.</summary>
    public async Task SeekAsync(int tick, CancellationToken ct = default)
    {
        LogOp("Seek", $"tick={tick}");
        await CallFSymbolAsync("FLtr_SeekToSongTick", new ulong[] { Bits(Math.Max(0, tick)), 0 }, ct);
    }

    // ---- project lifecycle (verify globals live; affects the user's project) ----
    public async Task OpenProjectAsync(string path, CancellationToken ct = default)
    {
        ulong ctrl = await GPtrAsync("14a8750", ct);
        if (ctrl == 0) throw new InvalidOperationException("Main controller not found.");
        using var scratch = await LeaseScratchAsync(ct).ConfigureAwait(false);
        ulong strPtr = await WriteDelphiStringAsync(path, scratch, ct);
        await CallAsync("10d50c0", new ulong[] { ctrl, strPtr, 1 }, ct);  // FLproj_OpenProject
    }

    public async Task SaveProjectAsync(string path, CancellationToken ct = default)
    {
        await ValidateProjectNotesBeforeSaveAsync(ct);
        ulong song = await GPtrAsync("1581200", ct);
        if (song == 0) throw new InvalidOperationException("Project object not found.");
        // HARD BACKSTOP (root cause of the post-turn UI freeze): FLproj_SaveProjectToFlp @0x10d6190
        // special-cases an UNTITLED project — it compares the current project path (DAT_01581298) against
        // the literal "untitled.flp" and, in that branch, ignores the passed target path; its internal
        // temp→final MoveFileW then fails and it pops a BLOCKING modal error dialog (FLui_ShowMessageBox,
        // "blocking until dismissed") ON FL'S MAIN THREAD → the whole UI wedges (dead playhead/buttons)
        // while the bridge pipe thread keeps answering ping. A managed timeout does NOT dismiss the modal,
        // so the ONLY safe fix is to never invoke the native save on an untitled project. Callers that want
        // to persist an untitled project must give it a real path first (SaveProjectAs).
        if (await IsUntitledProjectAsync(ct).ConfigureAwait(false))
            throw new InvalidOperationException(
                "Cannot save an untitled project via SaveProjectToFlp — it pops a blocking modal on FL's main thread and wedges the UI. Save the project (or use save_project_as) to give it a real path first.");
        using var scratch = await LeaseScratchAsync(ct).ConfigureAwait(false);
        ulong wpath = string.IsNullOrWhiteSpace(path) ? await GPtrAsync("1581298", ct) : await WriteDelphiStringAsync(path, scratch, ct);
        if (wpath == 0) throw new InvalidOperationException("No save path (project not yet saved — pass an explicit path).");
        ulong outSlot = await ZeroedScratchSlotAsync(ScratchOutputSlotOffset, scratch, ct);
        await CallAsync("10d6190", new ulong[] { song, wpath, 1, outSlot }, ct);  // FLproj_SaveProjectToFlp
    }

    /// <summary>True for a never-saved project path: empty, or the filename is FL's "untitled.flp"
    /// (templates load as "…\untitled.flp" too). Mirrors the exact string check inside
    /// FLproj_SaveProjectToFlp — this check is load-bearing for the UI-freeze guard in
    /// <see cref="SaveProjectAsync"/>, so it must exist in exactly ONE place.</summary>
    private static bool IsUntitledPath(string path)
        => string.IsNullOrEmpty(path) || Path.GetFileName(path).Equals("untitled.flp", StringComparison.OrdinalIgnoreCase);

    /// <summary>True when FL's current project has never been saved (path global 0x1581298 is empty or its
    /// filename is "untitled.flp"). Mirrors the exact string check inside FLproj_SaveProjectToFlp; used to
    /// refuse the modal-prone native save on untitled projects (see <see cref="SaveProjectAsync"/>).</summary>
    internal async Task<bool> IsUntitledProjectAsync(CancellationToken ct = default)
    {
        return IsUntitledPath(await ReadProjectPathAsync(ct));
    }

    public async Task NewProjectAsync(CancellationToken ct = default)
    {
        ulong pm0 = await GPtrAsync("14abca8", ct);
        if (pm0 == 0) throw new InvalidOperationException("Project manager not found.");
        ulong projMgr = await APtrAsync(pm0 + 0x10, ct);
        if (projMgr == 0) throw new InvalidOperationException("Project manager not found.");
        await PokeAbsAsync(projMgr + 0x11, new byte[] { 1 }, ct);
        await CallVtblAsync(projMgr, 0x30, "New project", new ulong[] { projMgr, 1, 1, 0 }, ct);  // projMgr.New
    }

    /// <summary>Reads the current project's title, path, and saved/untitled state.</summary>
    public async Task<string> GetProjectInfoAsync(CancellationToken ct = default)
    {
        var project = await QueryProjectAsync(ct);
        string path = project.Path, title = project.Title;
        bool untitled = project.Untitled;
        return $"Title: {(string.IsNullOrEmpty(title) ? "(untitled)" : title)}\nPath: {(string.IsNullOrEmpty(path) ? "(none)" : path)}\nSaved: {(untitled ? "no — untitled" : "yes")}";
    }

    private async Task SetProjectPathAsync(ulong song, string path, CancellationToken ct)
    {
        using var scratch = await LeaseScratchAsync(ct).ConfigureAwait(false);
        ulong s = await WriteDelphiStringAsync(path, scratch, ct);
        await CallAsync("10d2c90", new ulong[] { song, s, s, 1 }, ct);  // FLproj_SetProjectPath(song, path, name, addToRecent)
    }

    /// <summary>Save As: writes to a new path AND makes it the current project (updates title + recent files).</summary>
    public async Task SaveProjectAsAsync(string path, CancellationToken ct = default)
    {
        await ValidateProjectNotesBeforeSaveAsync(ct);
        ulong song = await GPtrAsync("1581200", ct);
        if (song == 0) throw new InvalidOperationException("Project object not found.");
        // Assign the real path FIRST so an UNTITLED project stops matching FLproj_SaveProjectToFlp's
        // "untitled.flp" special-case (which otherwise ignores the target, fails its temp→final move, and
        // pops a blocking modal on FL's main thread). With the path set, the headless (titled) branch runs.
        await SetProjectPathAsync(song, path, ct);
        using var scratch = await LeaseScratchAsync(ct).ConfigureAwait(false);
        ulong s = await WriteDelphiStringAsync(path, scratch, ct);
        ulong outSlot = await ZeroedScratchSlotAsync(ScratchOutputSlotOffset, scratch, ct);
        await CallAsync("10d6190", new ulong[] { song, s, 1, outSlot }, ct);  // FLproj_SaveProjectToFlp
    }

    /// <summary>
    /// Save a full <c>.flp</c> of the CURRENT live project to <paramref name="path"/> WITHOUT changing
    /// FL's current project path/title. Works for BOTH titled and UNTITLED projects — including templates,
    /// which load as "…\untitled.flp". This is the version-control backup hot path.
    ///
    /// Uses FL's low-level writer <c>FLproj_WriteFlpFile(pathChars, flag)</c> @0x10d5a60 with <c>flag=0</c>
    /// (direct write) — the exact "save copy" recipe FL itself uses (FUN_010d7930: when the target already
    /// ends in ".flp" it calls <c>WriteFlpFile(path, 0)</c> STANDALONE, with no serializer prep before and
    /// no SetProjectPath after — proving the writer is self-contained: it serializes the live song and
    /// writes it straight to the given path).
    ///
    /// This avoids the wrapper's untitled-path modal and leaves the current project identity unchanged.
    /// Orphan note references are validated before entering FL's serializer, which can otherwise reject
    /// an invalid project through its own modal error path:
    ///  • It is NOT the SaveProjectToFlp wrapper (@0x10d6190). That wrapper has the "untitled.flp"
    ///    special-case AND a temp→final <c>MoveFileW</c> (FUN_010d5f50) whose failure pops a BLOCKING
    ///    "moving the temporary file" <c>FLui_ShowMessageBox</c> on FL's MAIN thread (the freeze). flag=0 is
    ///    a DIRECT write — no temp file, no move — so that modal code path cannot execute.
    ///  • Its non-wrapper callers (FUN_010d7930, FUN_010e1190) report a false return to their caller.
    ///    This does not exempt the underlying serializer from project-data validation.
    ///  • It does NOT call <c>FLproj_SetProjectPath</c> (@0x10d2c90), so DAT_01581298 (path) and
    ///    DAT_015812a0 (title) are untouched: the user's project stays "untitled" in FL and the
    ///    recent-files MRU is not polluted. The writer never inspects the project path, so titled and
    ///    untitled projects take an identical code path.
    /// The target directory must already exist. Throws if validation or the native write fails.
    /// </summary>
    public async Task SaveCopyAsync(string path, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("A target .flp path is required.", nameof(path));
        await ValidateProjectNotesBeforeSaveAsync(ct);
        using var scratch = await LeaseScratchAsync(ct).ConfigureAwait(false);
        ulong strPtr = await WriteDelphiStringAsync(path, scratch, ct);
        ulong ret = await CallAsync("10d5a60", new ulong[] { strPtr, 0 }, ct);  // FLproj_WriteFlpFile(path, 0 = direct write)
        if ((ret & 0xFF) == 0)
            throw new InvalidOperationException($"FL Studio could not write the project copy to '{path}'.");
    }

    /// <summary>Save an auto-incremented new version (project_2.flp, _3.flp, …) and make it current.</summary>
    public async Task SaveNewVersionAsync(CancellationToken ct = default)
    {
        await ValidateProjectNotesBeforeSaveAsync(ct);
        ulong song = await GPtrAsync("1581200", ct);
        ulong cur = await GPtrAsync("1581298", ct);
        if (song == 0 || cur == 0) throw new InvalidOperationException("Project not yet saved — use save_project_as first.");
        using var scratch = await LeaseScratchAsync(ct).ConfigureAwait(false);
        ulong outStr = await ZeroedScratchSlotAsync(0x200, scratch, ct);
        await CallAsync("7f7800", new ulong[] { outStr, cur }, ct);  // FLproj_AutoIncrementFileName(&out, srcPath)
        ulong np = await APtrAsync(outStr, ct);
        if (np == 0) throw new InvalidOperationException("Could not compute the next version filename.");
        ulong outSlot = await ZeroedScratchSlotAsync(ScratchOutputSlotOffset, scratch, ct);
        await CallAsync("10d6190", new ulong[] { song, np, 1, outSlot }, ct);
        await CallAsync("10d2c90", new ulong[] { song, np, np, 1 }, ct);  // SetProjectPath
    }

    /// <summary>Lists the recent-projects (MRU) list.</summary>
    public async Task<string> ListRecentProjectsAsync(CancellationToken ct = default)
    {
        var sb = new StringBuilder();
        int shown = 0;
        for (int i = 0; i < 49 && shown < 20; i++)
        {
            ulong ptr = await GPtrAsync((0x1581320 + (ulong)i * 8).ToString("x"), ct);
            if (ptr == 0) continue;
            string s = await ReadDelphiStringAsync(ptr, ct);
            if (!string.IsNullOrEmpty(s)) { sb.Append(Path.GetFileName(s)).Append("  —  ").Append(s).Append('\n'); shown++; }
        }
        return shown == 0 ? "(no recent projects)" : sb.ToString().TrimEnd();
    }

    // ---- arrangements ----
    public async Task<string> ListArrangementsAsync(CancellationToken ct = default)
    {
        int count = (int)await CallAsync("11fb1a0", Array.Empty<ulong>(), ct);          // FLpl_GetArrangementCount
        int current = BitConverter.ToInt32(await PeekAsync("149e8b4", 4, ct), 0);
        if (count <= 0) return "(no arrangements)";
        var sb = new StringBuilder();
        for (int i = 0; i < count && i < 64; i++)
        {
            string name = await GetArrangementNameAsync(i, ct);
            sb.Append(i == current ? "* [" : "  [").Append(i).Append("] ").Append(string.IsNullOrEmpty(name) ? "(unnamed)" : name).Append('\n');
        }
        return sb.ToString().TrimEnd();
    }

    public async Task<int> AddArrangementAsync(string? name, CancellationToken ct = default)
    {
        LogOp("AddArrangement", $"name={name}");
        int newIdx = (int)await CallAsync("11fabc0", new ulong[] { 1, 0 }, ct);         // FLpl_AddArrangement(switchTo=1, copyModes=0)
        if (!string.IsNullOrWhiteSpace(name))
            await SetArrangementNameAsync(newIdx, name, ct);
        await RecomputeSongLengthAsync(ct);   // switched to the new (empty) arrangement — refresh the transport play-range
        return newIdx;
    }

    public async Task<int> CloneArrangementAsync(int srcIdx, string? name, CancellationToken ct = default)
    {
        LogOp("CloneArrangement", $"src={srcIdx} name={name}");
        if (srcIdx < 0) srcIdx = BitConverter.ToInt32(await PeekAsync("149e8b4", 4, ct), 0);  // default = current
        int newIdx = (int)await CallAsync("11fabc0", new ulong[] { 0, 0 }, ct);         // add (don't switch yet)
        await CallAsync("11fb420", new ulong[] { (uint)srcIdx, (uint)newIdx }, ct);     // FLpl_CopyArrangementInto(src, dst) — deep copy incl. clips
        if (!string.IsNullOrWhiteSpace(name))
            await SetArrangementNameAsync(newIdx, name, ct);
        await CallAsync("11fc880", new ulong[] { (uint)newIdx }, ct);                   // FLpl_SetCurrentArrangement (recomputes length for the clone)
        await RecomputeSongLengthAsync(ct);   // belt-and-suspenders: ensure the clone's play-range covers its copied clips
        return newIdx;
    }

    public async Task RenameArrangementAsync(int idx, string name, CancellationToken ct = default)
    {
        LogOp("RenameArrangement", $"idx={idx} name={name}");
        await SetArrangementNameAsync(idx, name, ct);
    }

    private async Task SetArrangementNameAsync(int idx, string name, CancellationToken ct)
    {
        using var scratch = await LeaseScratchAsync(ct).ConfigureAwait(false);
        await CallAsync("11fb0d0", new ulong[] { (uint)idx, await WriteDelphiStringAsync(name, scratch, ct) }, ct);
    }

    /// <summary>Read an arrangement's name via FLpl_GetArrangementName ("" when unnamed), symmetric with
    /// <see cref="RenameArrangementAsync"/> (same call <see cref="ListArrangementsAsync"/> uses).</summary>
    public async Task<string> GetArrangementNameAsync(int idx, CancellationToken ct = default)
    {
        using var scratch = await LeaseScratchAsync(ct).ConfigureAwait(false);
        ulong outSlot = await ZeroedScratchSlotAsync(0x300, scratch, ct);
        await CallAsync("11fb160", new ulong[] { outSlot, (uint)idx }, ct);   // FLpl_GetArrangementName(&out, idx)
        return await ReadDelphiStringAsync(await APtrAsync(outSlot, ct), ct);
    }

    public async Task DeleteArrangementAsync(int idx, CancellationToken ct = default)
    {
        LogOp("DeleteArrangement", $"idx={idx}");
        // FLpl_DeleteArrangement triggers an autosave (FLpl_AutoSaveHook) that writes a full .flp on FL's
        // main thread and stalls the bridge call. Suppress it by setting the load-in-progress flag
        // (*(0x14A8748)) = 1 around the delete (verified: delete then runs in ~40ms, no hang).
        ulong fptr = await GPtrAsync("14a8748", ct);
        byte saved = fptr != 0 ? (await PeekAbsAsync(fptr, 1, ct))[0] : (byte)0;
        if (fptr != 0) await PokeAbsAsync(fptr, new byte[] { 1 }, ct);
        try { await CallAsync("11fb1c0", new ulong[] { (uint)idx, 1, 1 }, ct); }       // FLpl_DeleteArrangement(idx, adjustCurrent, addUndo)
        finally { if (fptr != 0) await PokeAbsAsync(fptr, new byte[] { saved }, ct); }
        await RecomputeSongLengthAsync(ct);   // the current arrangement changed — refresh the play-range for the survivor
    }

    public async Task SelectArrangementAsync(int idx, CancellationToken ct = default)
    {
        LogOp("SelectArrangement", $"idx={idx}");
        await CallAsync("11fc880", new ulong[] { (uint)idx }, ct);
    }

    // ---- render / export ----
    /// <summary>Opens FL's audio Export dialog (the user picks format/path + clicks Render). A fully
    /// headless render needs a captured config24 (see re/12). pm=*(*(0x14ABCA8)+0x10); pm-&gt;vtbl[0x48](pm, fmt).
    ///
    /// <para>NON-BLOCKING by contract. <c>FLproj_FileExportFormat</c> opens a MODAL dialog on FL's
    /// main thread and enters a nested message loop that does not return until the user dismisses it —
    /// so the marshaled main-thread call would otherwise block the bridge (and the agent) for as long
    /// as the dialog is open, while transport/playback is stopped. We therefore issue it with a short
    /// bounded timeout and treat the (expected) timeout as "the dialog is now open for the user"; the
    /// bridge is freed immediately. This op is user-initiated only (it is intentionally NOT exposed as
    /// an agent tool — an unprompted modal mid-task freezes playback; see NativeControlPlugin).</para></summary>
    public async Task OpenExportDialogAsync(int formatIndex = 0, CancellationToken ct = default)
    {
        ulong c = await GPtrAsync("14abca8", ct);
        if (c == 0) throw new InvalidOperationException("Project controller not found.");
        ulong pm = await APtrAsync(c + 0x10, ct);
        if (pm == 0) throw new InvalidOperationException("Project manager not found.");
        // Resolve + guard OUTSIDE the try: a timeout while resolving (a wedged FL) must still surface;
        // only the dispatcher call itself may time out "healthily". (This is why the site stays inline
        // instead of using CallVtblAsync.)
        ulong vt = await APtrAsync(pm, ct);
        ulong fn = await APtrAsync(vt + 0x48, ct);
        EnsureInModule("Export dispatcher", fn);
        // Fire-and-return: the dispatcher blocks for the lifetime of the modal, so a short timeout is
        // the expected, healthy outcome — do NOT let it wedge the bridge/agent.
        try { await CallAbsAsync(fn, new ulong[] { pm, (uint)formatIndex }, ct, timeoutMs: 1500); }
        catch (TimeoutException) { /* expected: the export dialog is now open; the user drives it */ }
    }
}
