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

## Deferred (medium/low from audit — not blockers)
- **Stable clip handles** (audit #6): clip index = raw array slot; after a delete+recount, indices shift so
  move/resize can hit the wrong clip. Fix: address clips by the `+0x20` uid, not slot. (Improves "move
  entries around" reliability.)
- **Persistent resize** (audit #8, agent B): `ResizeClipAsync` pokes `+0x08` (a cache) → reverts on the next
  pattern edit / d49840. Correct: set source range `+0x18/+0x1c` via `FLpl_SetClipSourceRange @0xF71A70`.
- Full `_scratchGate` coverage of ALL multi-poke ops (track name/color, arrangement/marker/save) — currently
  only `InsertClipRaw` leased.
- DeleteClip keys on `+0x13` active flag; FL validity also keys on source id `+0x04` — verify playback stops.
- Clamps: ResizeClip len>=1, MoveClip startTick>=0; SelectTrack/SelectArrangement repaint.
- ListClips = one round-trip per clip (dense-project latency) — batch-read.

## Verify (harness `tools/SerumProbe cliptest`, on a NEW EMPTY project — never the user's work)
create pattern → add note (ch0) → `native_add_pattern_clip(pat,1,0,0)` → list → move → list. PASS = FL does
NOT crash + clips list + (visually) render without a click. Then build-and-stage + reinstall for the in-FL LLM.
