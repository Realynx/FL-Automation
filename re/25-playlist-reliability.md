# 25 — Playlist / arrangement reliability (RE + fixes, 2026-07-02)

Why: the AI struggled to control the playlist (create clips from patterns, move clips), clips "looked
broken until you click them," and `native_add_pattern_clip` CRASHED FL. 3-agent review
(`re/generated/playlist-{A-primitives,B-refresh,C-audit}.md`) + fixes. Ghidra addrs, base 0x400000.

## Root causes (all confirmed)
1. **CRASH** — our hand-rolled `InsertClipRawAsync` never REALIZED the target pattern. FL's own
   `FLpl_SendPatternToPlaylist @0xCB0780` calls `FLpat_GetOrCreateNoteRecorder @0x11D4080` first; without
   it a clip points at an unrealized pattern whose clip/score list `patternArray[N]+0x20` stays **-1**, and
   FL's playlist paint (`FUN_00ccc330 @0xCCC47D`) dereferences it → AV. Also: the clip struct was built in
   the SHARED scratch buffer across a NON-ATOMIC poke→insert sequence (parallel sub-agents could clobber it).
2. **"Broken until clicked"** — a clip's on-screen length is a DERIVED CACHE at `clip+0x08`, recomputed only
   by `FUN_00f71fb0 @0xF71FB0` (via `FUN_00d49840(songArr, patternNo, 3) @0xD49840`). `FLpl_RepaintPlaylist
   @0xDA40C0` only invalidates windows/headers/scrollbar — it never runs the per-clip resolver, so a freshly
   poked clip paints stale until an interaction triggers `f71fb0`.
3. **Prompt wrong** — system prompt referenced the tool with "pattern + channel" (it's a playlist TRACK),
   and gave no arranging workflow. `native_add_notes` was already correct (goes through
   `FLpat_RebuildPatternAndRefresh @0x11D4140`, whose tail calls `d49840`, so note edits DO propagate).

## Fixes applied (FlInjectBridge.cs + NativeControlPlugin.cs + SystemPrompts.cs) — compiles
- `AddPatternClipAsync`: **realize the pattern** (`CallAsync("11d4080", {pat,1})`) before insert → kills the
  crash. Then `RefreshPatternClipsAsync(pattern)` = `d49840(songArr,pat,3)` + `RepaintPlaylist` → renders
  without a click.
- `InsertClipRawAsync`: wrapped in a new **`_scratchGate`** lease (distinct from `_pipeGate`, no deadlock) so
  the build→insert span is atomic vs other scratch-building ops.
- `MoveClipAsync`: now `RefreshOneClipAsync(clip)` = `f71fb0(clip)` → poke `+0x08` → repaint (renders move
  without a click; works for pattern + audio clips).
- `native_add_pattern_clip` **re-enabled** (crash-safe) with corrected `[Description]` (playlist track 1-500,
  not a channel). MCP surface auto-fixed (shares the bridge method).
- System prompt: added the ARRANGE workflow (pattern+notes → place clip on a playlist track → list-clips for
  index before move/resize) + corrected the track-vs-channel guidance.

## Engine reference (playlist)
- Clip place primitive: `FLpl_SendPatternToPlaylist @0xCB0780` (full "send to playlist"; we extract its core).
- Pattern realize: `FLpat_GetOrCreateNoteRecorder @0x11D4080(pat1based, create=1)`.
- Per-clip length resolve: `FUN_00f71fb0 @0xF71FB0`; pattern-wide resolve+dirty: `FUN_00d49840 @0xD49840(songArr, patternNo, 3)`.
- Recount active: `FLpl_RecountActiveClips @0xF6E180`; repaint: `FLpl_RepaintPlaylist @0xDA40C0(songArr=*(0x14aab88))`.
- Clip collection Cobj = `*(FLpl_GetCurrentArrangement @0x11E32C0 + 0x14)`; vtbl +8 insert / +0x10 init / +0x18 commit; data@+8 stride@+0x10 count@+0x14 activeCount@+0x48.
- Clip struct: +0x00 start, +0x04 sourceID, +0x08 len(CACHE), +0x0c (500-track) i16, +0x13 flags(0x80 active/0x20 mute), +0x18/+0x1c source trim(0xffffffff=full), +0x20 uid. sourceID: pattern `0x50005000+(pat1based<<16)`, channel `chanIdx0based<<16`.
- Note edit refresh (already correct): `FLpat_RebuildPatternAndRefresh @0x11D4140`.

## PLAYBACK-STUCK after arrange — song length not recomputed (fixed 2026-07-03, live-verified)
Symptom: after the AI arranges pattern clips into the playlist, pressing Play shows "playing" but the
playhead loops in the OLD song range and the just-arranged clips never play (stranded near tick 0/1 when the
prior song was short/empty). ROOT CAUSE: FL confines the playhead to a play-range whose end is the SONG
LENGTH (`*(*(0x14AAB88))+0xB04`, ticks); the transport recomputes that range from a CACHED length every tick,
and FL only refreshes the cache from the playlist on an arrangement switch or a pattern-length change. A raw
`InsertClipRaw`/move/delete referencing an already-sized pattern triggers neither, so the song keeps its old
length. (Dead ends confirmed live: poking the range global / songObj+0xB04 is overwritten next tick;
`FUN_00D37450`'s playlist scan is mode-gated on songObj+0xB00>=2 and reads a null songObj+0xD04 headless;
driving `FUN_010CE8A0` off the bus WEDGES FL.) See re/12 §Transport for the corrected offsets (the task-#29
`*(int*)0x14A...` reads were all one deref short — these are .bss pointer slots).
FIX (`FlInjectBridge`): after Add/Move/Delete clip ops, call `RecomputeSongLengthAsync` =
`FLpl_SetCurrentArrangement(*(int*)0x149E8B4)` (@0x11FC880) — re-selecting the SAME arrangement index runs
FL's full correct refresh (rebinds the playlist sub-object, recomputes song length from the real clips,
re-derives the transport range + slider) with no data loss, and shrinks correctly after a delete.
Also fixed the mode reads (`GetSongState`/`SetSongMode`/`GetSongMode` now double-deref 0x14A8670).
Verify: `tools/SerumProbe playtest [cur]` — arrange 2 clips (fresh empty arr, or bars 9-10 of the populated
template), song mode, play, read playhead twice + seek into the arranged region. PASS = play-range extends to
cover the clips (`[0..3839]`→`[0..42239]`) AND the playhead reaches the arranged region (verified live).

## BULLETPROOFED — every arrangement-length mutator now recomputes (2026-07-03 pm, live-verified)
The first pass only wired `RecomputeSongLengthAsync` into Add/Move/Delete, so the stuck-playhead bug RECURRED
after a big real arrange whose reach came from a RESIZE or DUPLICATE (those skipped the recompute). Reproduced
in the harness (`SerumProbe arrangetest`, ppq=960): arrange 6 clips to bar 11, then resize the furthest clip
to bar 14 and duplicate it to bar 18. BEFORE fix the play-range FROZE at the post-add value `[0..42239]` while
the resized/duplicated tail (42240→69120) was UNREACHABLE — seek to 51840 clamped to 42239 (the exact "plays
but the playhead is stuck" symptom). FIX: `RecomputeSongLengthAsync` now runs after **Resize / Duplicate /
Slice** and after **arrangement create / clone / delete** too — so after ANY arrangement mutation the song
length == real max clip end and playback always advances.
- **Persistent resize (was Deferred):** re-selecting the arrangement re-resolves each pattern-clip's `+0x08`
  length CACHE from its SOURCE RANGE, so a `+0x08`-only resize would be reverted by the recompute (and the song
  would shrink back). `ResizeClipsAsync` now sets the source range `[0..len]` via `FLpl_SetClipSourceRange`
  (@0xF71A70, doubles → `+0x18/+0x1c`) BEFORE poking `+0x08` and BEFORE the recompute. Live-verified: after the
  recompute the resized clip KEEPS len=15360 (source range drives the resolver) AND the play-range grows to
  `[0..53759]`. Confirms the RE hypothesis below — source range is the persistent store, `+0x08` is derived.
- The recompute always targets `*(int*)0x149E8B4` (current arrangement index), which our clip ops write into
  (Add/Move/Delete/Resize/Dup all act on `FLpl_GetCurrentArrangement`), so it can't target the wrong arrangement.
- Diagnosability: `FlInjectBridge.LogOp` appends one line per MUTATING op to
  `%APPDATA%\FLAutomate\logs\fl-ops-<yyyyMMdd>.log` (`<HH:mm:ss.fff> Method(args)`; raw peek/poke/call NOT
  logged) — a future "playhead stuck" report shows the exact op sequence + every RecomputeSongLength that ran.
- Verify: `SerumProbe arrangetest` (add→resize→duplicate→play→seek-into-far-region, all ASSERT PASS) + no
  regression in `bulktest`/`cliptest`/`playtest`.

## RECURRED AGAIN — the SetCurrentArrangement recompute is mode-gated (fixed 2026-07-03, two-step, live-verified)
A real session still got stuck after `AddPatternClips(pat6@t7/9/11/13 : 30720)` + `RecomputeSongLength(arrangement=0)`
(op-log confirms both ran; no `SetSongMode` all session). Investigation:
- **Prime hypothesis (SetCurrentArrangement no-ops on the current index) is WRONG.** Decompiled
  `FLpl_SetCurrentArrangement @0x11FC880`: the `param_1==DAT_0149e8b4` "index unchanged" flag gates ONLY the
  leave-old-arrangement cleanup + the tail usage-timer `FUN_010cefd0` + one UI poke `FUN_00b6a1d0(7,0)`. The
  song-length recompute `FUN_00d37450(songObj,1)` runs **UNCONDITIONALLY**. Live-confirmed on the CURRENT,
  already-selected arrangement (no fresh temp arrangement to mask it): same-index re-select DID grow the song
  length 3840→34560 and the play-range `[0..3839]→[0..34559]` for clips at tick 30720, in BOTH pattern and
  song mode. So the harness `arrangetest2` (exact user shape) PASSES even without the fix — which is precisely
  why the bug hid.
- **Actual weakness:** `FUN_00d37450`'s length scan (`songObj+0xB04 = max clip end`) is GATED on
  `songObj+0xB00>=2` AND dereferences the possibly-null playlist sub-object `songObj+0xD04`. In the live GUI
  those hold (`b00=2`, `d04` valid — verified), so step-1 alone works; but in ANY project state where the gate
  or deref doesn't hold, the re-select silently leaves the song length SHORT → the recurring stuck playhead.
  Relying on that one gated/null-sensitive FL internal is the fragility.
- **FIX (two-step `RecomputeSongLengthAsync`):** (1) keep `FLpl_SetCurrentArrangement(*0x149E8B4)` for FL's
  full UI refresh (slider/markers/rebind); (2) THEN recompute the length DIRECTLY from the clip collection
  (`MaxClipEndAsync` = max start+len over slots 0..count) and GROW `songObj+0xB04` (double-deref of 0x14aab88)
  to cover it — **mode- and d04-independent**. Grow-only: delete/resize SHRINK stays owned by step-1's scan,
  and any marker-based length is preserved. b04 written to the TRUE clip max is stable across transport ticks
  (live: held `[0..34559]` through playback — the old "overwritten next tick" only bites a value FL's own
  scan disagrees with). DON'T poke the transient slider globals / drive `FUN_010ce8a0` off the bus (still wedge).
- **DiagTransport** now reads the correct DOUBLE-deref song object (`*(*(0x14aab88))`) + exposes `b00`, real
  `b04`, `d04`, the time-selection `sel=[d4c..d50]` and `gmode`. `DiagSongScopeAsync` is a harness hook that
  can force `b04` stale then run the direct recompute in isolation.
- **Verify:** `SerumProbe arrangetest2` (current arrangement @tick 30720, pattern+`song` variants): all ASSERT
  PASS incl. `direct-recompute-grows-b04` (force b04→3840, direct recompute restores 34560) + playhead advances
  and reaches the tick-30720 region. No regression in `arrangetest`/`playtest`/`bulktest`/`cliptest`. FL alive.

## Deferred (medium/low from audit — not blockers)
- **Stable clip handles** (audit #6): clip index = raw array slot; after a delete+recount, indices shift so
  move/resize can hit the wrong clip. Fix: address clips by the `+0x20` uid, not slot. (Improves "move
  entries around" reliability.)
- ~~**Persistent resize** (audit #8, agent B)~~ RESOLVED 2026-07-03 (see BULLETPROOFED above): `ResizeClipsAsync`
  now sets the source range `+0x18/+0x1c` via `FLpl_SetClipSourceRange @0xF71A70` before the `+0x08` poke +
  recompute, so the resize survives d49840 AND grows the song. Live-verified.
- Full `_scratchGate` coverage of ALL multi-poke ops (track name/color, arrangement/marker/save) — currently
  only `InsertClipRaw` leased.
- DeleteClip keys on `+0x13` active flag; FL validity also keys on source id `+0x04` — verify playback stops.
- Clamps: ResizeClip len>=1, MoveClip startTick>=0; SelectTrack/SelectArrangement repaint.
- ListClips = one round-trip per clip (dense-project latency) — batch-read.

## Verify (harness `tools/SerumProbe cliptest`, on a NEW EMPTY project — never the user's work)
create pattern → add note (ch0) → `native_add_pattern_clip(pat,1,0,0)` → list → move → list. PASS = FL does
NOT crash + clips list + (visually) render without a click. Then build-and-stage + reinstall for the in-FL LLM.
