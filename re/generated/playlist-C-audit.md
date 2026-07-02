# Playlist / Arrangement SDK — C# audit (code review, 2026-07-02)

Scope: `FlInjectBridge.cs` (playlist/clip/track/arrangement), `NativeControlPlugin.cs` (native_*
tools), `SystemPrompts.cs`, `PlaylistTools.cs` (MCP), `INativeFlControl.cs`. Cross-checked against
`re/21-clip-pattern-bug.md`, `re/21-clip-pattern-evidence.md`, `re/generated/controls-playlist.md`.
Two sibling agents are RE'ing the canonical clip primitive (`FLpl_SendPatternToPlaylist @0xCB0780`)
+ the refresh/realize sequence; items tagged **[verify-RE]** should be confirmed against their output.

Status note: the re/21 **off-by-one is already FIXED** in the current tree (writer passes 1-based
straight through, bridge encodes `(uint)pattern<<16` / reads `pattern*0xC0`, ListClips decode has no
`+1`). The remaining defects are the crash, realization, atomicity, tool-gating and prompt gaps.

---

## P0 — Critical (AI cannot arrange at all, or crashes FL)

### 1. The AI has NO tool to place a pattern clip — arrangement is impossible from the agent
`NativeControlPlugin.cs:601-615` — `AddPatternClipAsync` is deliberately **not** a `[KernelFunction]`
(HOTFIX 2026-07-02: pulled because it crashed FL, see #2). This is the ONLY "create clip from
pattern" primitive. With it gone, the agent can create patterns, add notes, and move/resize/delete/
mute/slice/duplicate *existing* clips — but it can never CREATE one. Every "arrange a song / lay out
the pattern into the playlist" request is unsatisfiable through the agent today. This is the single
biggest reason the AI "struggles to control the playlist."
**Fix:** re-expose as `native_add_pattern_clip` after #2 is fixed; keep the `Guard("track",1,500)`.

### 2. Pattern-clip insert crashes FL — missing pattern realization + non-atomic shared scratch
`FlInjectBridge.cs:1229-1250` (`InsertClipRawAsync`) + `1166-1181` (`AddPatternClipAsync`).
Two independent defects, both documented in re/21 §clip-insert-crash:
- **No pattern realization.** The canonical `FLpl_SendPatternToPlaylist @0xCB0780` calls
  `FLpat_GetOrCreateNoteRecorder(pattern)` FIRST, which allocates the pattern's clip/score list.
  Our path never realizes the pattern, so if the clip's target pattern has no recorder yet, FL's
  next playlist paint (`FUN_00CCC330 @0xCCC47D`) reads `*(patternArray[N]+0x20) == -1` and derefs
  `[-1+0x14]` → AV. Placing a clip *before* notes are added, or for an empty pattern, crashes FL.
- **Non-atomic build in the shared scratch buffer.** `tmp = await ScratchAsync()` returns the
  bridge's single shared scratch region; the clip is built across ~10 separate `RawAsync`
  round-trips (poke zeros → init → poke start/src/len/track/flags/trim → insert). `_pipeGate` is
  released between each message, so a concurrent bridge op (parallel sub-agent) can grab the same
  scratch and clobber the half-built clip → malformed clip → paint AV.
**Fix (preferred):** route `AddPatternClipAsync` through `FLpl_SendPatternToPlaylist @0xCB0780`
(realizes the pattern + builds the clip natively/atomically). **Fallback:** call
`FLpat_GetOrCreateNoteRecorder(pattern,1)` before insert AND make the build atomic (see #4) AND use
a private scratch offset. Then re-run the re/21 §5 read-back self-check.

### 3. MCP surface still exposes the crashing tool (gating inconsistent with the agent)
`PlaylistTools.cs:66-73` — `native_add_pattern_clip` is a live `[McpServerTool]` that calls
`fl.AddPatternClipAsync` directly, with **no track guard and no realize**. So while the agent tool
was pulled to stop the crash, any MCP client can still trigger the exact #2 AV. Gate both surfaces
the same way (disable until #2 is fixed, or fix #2 and re-enable both).

---

## High

### 4. Non-atomic shared-scratch hazard across EVERY multi-poke op (parallelism corruption)
`FlInjectBridge.cs:283` (`ScratchAsync`) returns a fixed shared address. `_pipeGate` (line 48)
serializes individual messages, NOT multi-message sequences. The system prompt actively encourages
FAN OUT / `run_parallel_tasks`, and the same `FlInjectBridge` instance/gate is shared, so two
concurrent ops that each build in scratch corrupt each other. Affected (all write scratch offset 0
or fixed offsets): `InsertClipRawAsync` (1237), `WriteDelphiStringAsync` (599-611) → used by
`SetTrackNameAsync` (1064), `SetTrackColorAsync` (1072), `AddChannelAsync`/`AddSampleChannelAsync`/
`ReplaceChannelSampleAsync` (via `LoadIntoChannelAsync`), `AddMixerEffectAsync`/`Clone`/`Remove`,
`AddMarkerAsync` (1523), `AddArrangementAsync`/`CloneArrangementAsync`/`RenameArrangementAsync`
(1646/1656/1662), and the save-path outSlots (1542/1583/1598/1603/1630). Symptom: parallel edits
place the wrong plugin/name/clip, or crash.
**Fix:** add a scratch lease that holds `_pipeGate` for the full build+call sequence (a
`RawBatchAsync`/`using` scope), or serialize all scratch-using ops behind a second semaphore; longer
term have the bridge hand out a per-connection scratch region.

### 5. System prompt: wrong tool name, wrong param, and no arrangement workflow
`SystemPrompts.cs:64-124`.
- **Dangling reference:** lines 97-98 tell the model to use `native_add_pattern_clip` — a tool that
  is NOT in the agent surface (pulled, #1). Model is told to call a nonexistent tool.
- **Wrong signature:** same line says it "take[s] pattern + channel directly." The clip tool takes
  pattern + **track** (playlist lane), not channel. This actively teaches the track/channel mixup.
- **No playlist conventions.** The Authoring notes cover notes/PPQ/MIDI but say nothing about:
  playlist tracks are **1-based (1-500)** while channels are **0-based**; a clip's `startTick` is in
  PPQ (bar = 4×PPQ); you must create the pattern AND add its notes (realize it) before placing a
  clip; move/resize/delete/mute/slice/duplicate need the **slot index from `native_list_clips`**
  first. Without this, even with the tool present the model picks wrong tracks/units.
**Fix:** drop the stale reference, fix "channel"→"track", add a short "Arranging" note:
create_pattern → add_notes → add_pattern_clip(pattern, track 1-based, startTick PPQ) → list_clips to
get slot indices for edits.

---

## Medium

### 6. Clip handle is the raw array index — unstable → edits hit the wrong clip
`ListClipsAsync:1137-1151` reports `[i]` = raw slot in the flat clip array; `ClipAddrAsync:1183-1189`
and every mutation (`MoveClip/Resize/Delete/Mute/Slice/Duplicate`) index by that `i`. After a delete
(clears flag, `RecountActiveClips`), an insert, or any FL-internal reorder/compaction, those indices
go stale and the next move/resize/delete lands on a different clip. The clip struct has a **stable
unique id at +0x20** (`re/generated/controls-playlist.md`, "clip unique id lookup key"). **Fix:**
expose that id as the handle and resolve id→addr, or at minimum document that indices are only valid
until the next mutating clip op. **[verify-RE]** whether FL compacts the array on delete.

### 7. DeleteClip may not stop playback (validity is keyed on source id, not the active flag)
`DeleteClipAsync:1206-1215` clears `clip+0x13 & ~0x80` + calls `RecountActiveClips`. But
`re/generated/controls-playlist.md` + re/21 say `FLpl_IsClipDeleted @0xF719A0` decides validity from
the **source id (+0x04)**, not the +0x13 flag. Our delete only hides the clip from *our* list; FL
may still treat it as valid/audible. **Fix / [verify-RE]:** confirm the cleared-flag+recount path
actually removes it from playback; if not, invalidate +0x04 or call FL's real clip-delete op.

### 8. ResizeClip has no lower-bound clamp on length
`NativeControlPlugin.cs:632-640`, `PlaylistTools.cs:83-88`, bridge `ResizeClipAsync:1199-1204` poke
`lengthTick` raw. Zero/negative stores a ≤0 length (engine only coerces <1→1 in end-tick math, not
in the stored field) → zero-width/invisible clip. **Fix:** clamp `lengthTick >= 1` at the tool.

### 9. Clip-collection resolution differs from the RE-documented song path  **[verify-RE]**
`ClipCollObjAsync:1108-1113` resolves the clip array as `FLpl_GetCurrentArrangement(0x11E32C0) +
0x14`. The RE doc's canonical chain is `songArr(+0x14ABA80/+0x14AAB88) → +0xD04 (playlist obj B) →
B+0x57C (clip array)`, and `RepaintPlaylistAsync:1018-1022` passes `*(0x14AAB88)` to `0xDA40C0`. If
the arrangement object and the song object diverge (e.g., after `SelectArrangement`), clip
reads/writes and the repaint could target different collections. Confirm both resolve the same live
object for all arrangements.

### 10. SliceClip on a PATTERN clip doesn't offset the second half's source
`SliceClipAsync:1377-1408` splits the source window only for audio (`if (audio)`, 1397). For a
pattern clip the second half is inserted with source trim -1/-1 (full), so it restarts the pattern
from beat 0 instead of continuing at the cut. Pattern-clip source trim (+0x18/+0x1c) is float-beats
per the RE doc. **Fix:** set the 2nd half's `+0x18` to the cut offset in beats for pattern clips too.

### 11. MoveClip "keep track" sentinel differs between surfaces + no track upper bound
Agent `MoveClipAsync` maps `track <= 0 → -1` (NativeControlPlugin.cs:626), but MCP `MoveClip:75-81`
passes `track` straight through. Bridge `MoveClipAsync:1191-1195` pokes when `track >= 0`, so MCP
`track=0` → `(short)(500-0)=500` → clip jumps to track 500 instead of "keep." Bridge also never
range-checks track (no upper bound). **Fix:** treat `<=0` as keep and Guard 1..500 in the bridge (or
both surfaces).

### 12. AddPatternClip does not validate `track`
Bridge `AddPatternClipAsync:1166-1181` validates the pattern but not the track; MCP `AddPatternClip`
(PlaylistTools.cs:66) doesn't Guard it either. A bad track → `(short)(500-track)` garbage lane.
(Agent tool did Guard 1..500, but it's disabled.) **Fix:** Guard track 1..500 in the bridge.

### 13. Pattern range for clips allows non-existent patterns → same crash class as #2
`AddPatternClipAsync:1168` uses `ValidatePattern` (1..9999). A high-but-empty pattern index passes,
gets encoded into the clip, and FL's paint derefs the unrealized `patternArray[N]+0x20` → AV.
**Fix:** realize the pattern (#2) or reject patterns that don't exist / have no content.

---

## Low

### 14. ListClips does one bridge round-trip PER clip
`ListClipsAsync:1137-1153` peeks 0x24 bytes per clip for every clip up to `offset+page`, each a
separate main-thread pipe round-trip. Dense arrangements → many round-trips, risking the 4 s bridge
timeout. **Fix:** bulk-peek the whole `data..data+count*stride` region once, then parse in C#.

### 15. MoveClip startTick not clamped
`MoveClipAsync:1194` pokes `startTick` raw; negative places the clip before bar 1. Clamp `>=0`.

### 16. Selection / flag changes don't repaint
`SelectTrackAsync:1098-1103`, `SelectArrangementAsync:1676-1677`, and the `+0x70` custom-color poke
in `SetTrackColorAsync:1079` have no `RepaintPlaylistAsync`, so the change may not be visible until
the user interacts ("looks broken until clicked"). Cosmetic. (Clip mutations DO all repaint.)

### 17. DeleteArrangement doesn't validate idx < count
`DeleteArrangementAsync:1664-1674` (tool guards only `>=0`) calls `FLpl_DeleteArrangement(idx,…)`
without an upper bound; an out-of-range idx risks OOB. Guard against `ListArrangements` count.
```
