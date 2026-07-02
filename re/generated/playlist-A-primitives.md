# Playlist A — CANONICAL clip primitives (place / move / resize / delete via FL's own funcs)

Static RE of `FLEngine_x64.dll` (Ghidra image base `0x400000`; addresses are Ghidra-absolute,
live = liveFLEngineBase + (addr − 0x400000)). Goal: replace hand-rolled clip pokes with FL's own
primitives so `native_add_pattern_clip` stops AV'ing (see `re/21-clip-pattern-bug.md`
§clip-insert-crash). **Static only — no live FL touched.** Live-verify items flagged at the end.

---

## TL;DR — what the bridge must do

1. **Place a clip** = the exact sub-sequence inside `FLpl_SendPatternToPlaylist @0xCB0780`:
   realize the pattern → `init` a zeroed 0x48 temp via the collection vtable → write 7 fields →
   `insert` via the collection vtable → recount + repaint. The collection is
   **`Cobj = *(FLpl_GetCurrentArrangement() + 0x14)`**.
2. **There is NO dedicated move/resize/delete/paste MODEL primitive.** FL itself moves/resizes by
   writing the clip struct fields directly (`+0x00` start, `+0x08` len, `+0x0c` 500−track) then
   repainting; it deletes by clearing the `+0x13` ACTIVE bit (`0x80`) + recount. The only real
   per-field setters are `FLpl_SetClipMuted` (`+0x13` 0x20) and `FLpl_SetClipSourceRange`
   (`+0x18/+0x1c`, audio/pattern-aware). So the bridge's field-poke move/resize/delete is the
   *correct* pattern — the bug is purely in the clip-ADD path.
3. **The crash fix**: our `AddPatternClipAsync` never **realizes the pattern's note recorder**
   before inserting the clip. The canonical primitive calls
   `FLpat_GetOrCreateNoteRecorder(pattern, …)` FIRST. An unrealized pattern's clip/score list is
   `-1`, and the playlist paint (`FUN_00ccc330 @0xCCC47D`, `CMP [RAX+0x14],0` where
   `RAX = *(patternArray[pat]+0x20)`) dereferences it → AV. Add the realize call (and give the
   insert its own private, atomically-built scratch struct).

---

## 1. `FLpl_SendPatternToPlaylist @0x00CB0780` — the canonical "send pattern to playlist"

### Signature (recovered)
```c
// __fastcall. Ghidra shows undefined(void); real args recovered from the body:
void FLpl_SendPatternToPlaylist(void* songEngine /*RCX = *PTR_DAT_014A8EA0 ctx*/,
                                int  patternNumber /*EDX, 1-based (1..999)*/);
```
- **param_1 (`songEngine`)** — the song/step-seq engine context; only forwarded to the channel-clip
  layout helpers (`FLpl_LayoutChannelClipsInPattern`, `FUN_00cac9c0`). Not needed for a bare
  pattern-clip add.
- **param_2 (`patternNumber`)** — **1-based** pattern number. The SAME index drives the note
  recorder, the pattern name, the length lookup, AND the clip source ID. This is the crux of the
  off-by-one already documented in `re/21`.
- **Current arrangement is NOT passed** — it is fetched internally every time via
  `FLpl_GetCurrentArrangement() @0x11E32C0` = `*(DAT_018329d8 + DAT_0149e8b4*8)`.

This is the full "Send to Playlist" button handler (called by `TStepSeqForm.SendToPlaylistBtnClick
@0xF464FD`). It does a lot more than add one clip: it allocates enough empty playlist tracks, lays
out each in-pattern **channel** clip, sets track names/colors, arranges the channel rack, and wraps
everything in an undo transaction. The bridge only needs the **pattern-clip add** core (§2).

### Full init/refresh order (top to bottom, trimmed)
```c
// --- open undoable action ---
FUN_00cb0360(&trkHelper);                       // find first run of empty PL tracks -> startTrack (local_b4)
  if (startTrack < 1) { error "There aren't enough empty playlist tracks available."; return; }
FUN_0054c5e0(&s, L"send Loop Starter pattern to the playlist");
FUN_00f36c20(*PTR_DAT_014a9360, s, &DAT_00cb0d20, 1);   // BEGIN undo transaction
if (*PTR_DAT_014aab88) FLui_Editor_DeselectAll(*PTR_DAT_014aab88, 0);

// --- start tick (local_b8): time-selection start if active, else song-start clamp>=0 ---
startTick = (songArr && *(int*)(songArr+0xd4c) >= 0) ? *(int*)(songArr+0xd4c)
                                                     : max(0, *(int*)(PTR_DAT_014a9068+8));

// --- lay out CHANNEL clips for every channel that belongs to this pattern ---
for (ch = 0; ch < channelCount; ch++) {
    chObj = FLcr_ChannelListGetItem(*PTR_DAT_014a98d8, ch);
    if (FUN_00f12750(chObj, pat) && chObj[0x7ac] /*channel enabled in pattern*/) {
        arr = FLpl_GetCurrentArrangement();
        FLpl_SetTrackNameAndColor(arr, curTrack, chObj+0x50 /*name*/,
                                  *(u32*)(PTR_DAT_014aa0c8 + pat*0xc0 + 8) /*pattern color*/);
        // (instrument-track wiring omitted)
        FLpl_LayoutChannelClipsInPattern(songEngine, ch, pat, startTick);  // @0xCB0000 (audio/chan clips)
        curTrack++;
    }
    FUN_00cac9c0(songEngine, ch, 1);
}

// --- PATTERN realization + the ONE pattern clip (only if the pattern actually has notes) ---
rec = FLpat_GetOrCreateNoteRecorder(pat, 0);                 // @0x11D4080  << REALIZES the pattern
if (rec[0x14] > 0 /*recorder has events*/ || globalForceFlag) {
    FLpat_GetPatternName(pat, &nameStr, 1);                  // @ (pattern name for the track label)
    arr = FLpl_GetCurrentArrangement();
    FLpl_SetTrackNameAndColor(arr, startTrack, nameStr,
                              *(u32*)(PTR_DAT_014aa0c8 + pat*0xc0 + 8));  // pattern color

    arr  = FLpl_GetCurrentArrangement();
    Cobj = *(void**)(arr + 0x14);                            // << CLIP COLLECTION (deref of arr+0x14)
    (*(Cobj->vtbl[0x10/8]))(Cobj, &tmp);                     // vtbl+0x10 = INIT element (fills defaults+vtable/links)
    tmp.at(0x04) = pat*0x10000 + 0x50005000;                 // sourceID = 0x50005000 + (pat1based<<16)
    tmp.at(0x00) = startTick;                                //           timeline start (ticks)
    tmp.at(0x08) = *(u32*)(PTR_DAT_014aa0c8 + pat*0xc0 + 0x50); // length = pattern length (ticks)
    tmp.at(0x0c) = (i16)FLpl_EncodeClipTrackField(startTrack);  // = 500 - trackNo   @0xB47000
    tmp.at(0x13) |= 0x80;                                    // ACTIVE flag
    tmp.at(0x18) = tmp.at(0x1c) = 0xffffffff;                // full source trim (loop whole pattern)
    arr  = FLpl_GetCurrentArrangement();
    Cobj = *(void**)(arr + 0x14);
    (*(Cobj->vtbl[8/8]))(Cobj, &tmp);                        // vtbl+0x08 = INSERT clip
}

// --- finalize (these are step-seq specific, NOT the generic clip finalize) ---
FUN_00cb05c0(&trkHelper);   // Loop-Starter pattern-name/undo bookkeeping
FUN_00cb06b0();             // "extract audio clips from Loop Starter" undo point
if (*PTR_DAT_014a8bf8) FLui_ChanRack_ArrangeChannels(*PTR_DAT_014a8bf8, 1, 1);
// (undo transaction closes on scope exit)
```

**Key point for the bridge:** the primitive does **not** explicitly call `RecountActiveClips` or
`RepaintPlaylist` after the insert — it relies on the collection's `insert` (vtbl+8) + the undo
transaction + `ArrangeChannels` chain. For a standalone bridge insert (no transaction) the correct
finalize is `FLpl_RecountActiveClips(Cobj)` + `FLpl_RepaintPlaylist(*PTR_DAT_014aab88)` (see §3),
which is what the delete/edit paths use. The undo-transaction wrap (`FUN_00f36c20`) is optional —
skipping it just means the change isn't undoable.

---

## 2. Minimal crash-free clip-ADD (bridge recipe — pattern OR audio/channel clip)

```c
// 1. REALIZE the target (this is the step our bridge is MISSING -> the AV):
FLpat_GetOrCreateNoteRecorder(pattern /*1-based*/, 1);   // @0x11D4080; flag=1 also fires the notify
                                                         //   (for an AUDIO clip, realize the CHANNEL instead — see §7)
// 2. Resolve the collection + its vtable funcs:
void*  arr  = FLpl_GetCurrentArrangement();              // @0x11E32C0
void** Cobj = *(void***)((char*)arr + 0x14);            // DEREF arr+0x14  (NOT songArr playlist B+0x57c — see live-verify #1)
void** vt   = *Cobj;
initFn   = vt[2];   // *(vt+0x10)  init element
insertFn = vt[1];   // *(vt+0x08)  insert element
// 3. Build the clip in a PRIVATE 0x48 scratch, atomically (no other bridge op may touch it mid-build):
char tmp[0x48] = {0};
initFn(Cobj, tmp);                                       // fills defaults incl. the clip's internal vtable/links
*(int*)   (tmp+0x00) = startTick;                        // timeline start (PPQ ticks)
*(u32*)   (tmp+0x04) = sourceID;                         // pattern: 0x50005000+(pat<<16); audio/chan: chanIdx<<16
*(int*)   (tmp+0x08) = lengthTicks;                      // pattern: pattern length @ patArr+pat*0xc0+0x50
*(i16*)   (tmp+0x0c) = (short)(500 - trackNo);           // FLpl_EncodeClipTrackField
          (tmp+0x13) |= 0x80;                            // ACTIVE
*(u32*)   (tmp+0x18) = 0xffffffff;                       // source trim start (full)
*(u32*)   (tmp+0x1c) = 0xffffffff;                       // source trim end   (full)
insertFn(Cobj, tmp);                                     // append into the collection (bumps count @+0x14)
// 4. Finalize:
FLpl_RecountActiveClips(Cobj);                           // @0xF6E180  refresh activeCount @+0x48
FLpl_RepaintPlaylist(*PTR_DAT_014aab88);                 // @0xDA40C0  redraw
```

> Our current `InsertClipRawAsync` (`FlInjectBridge.cs:1229`) already does steps 2–4 correctly
> (right collection `*(arr+0x14)`, init via vtbl+0x10, insert via vtbl+8, RecountActiveClips). It is
> **missing step 1** (pattern realization) and it builds `tmp` in the **shared** scratch region with
> a non-atomic poke→init→poke→insert round-trip. Those two gaps are the crash (`re/21`
> §clip-insert-crash). Fix = call `FLpat_GetOrCreateNoteRecorder(pattern,1)` in `AddPatternClipAsync`
> before `InsertClipRawAsync`, and give the insert a private scratch slice + hold the bridge lock
> across the whole build.

---

## 3. Clip collection `Cobj` — layout + vtable (shared container class)

`Cobj = *(FLpl_GetCurrentArrangement() + 0x14)`. Same class is used for the per-pattern note
recorder (`FLpat_GetOrCreateNoteRecorder` returns one) and every `FLpl_GetClipByIndex` /
`RecountActiveClips` operand.

| off | field | notes |
|---|---|---|
| +0x00 | vtable ptr | `*Cobj` |
| +0x08 | data base | element array; `FLpl_GetClipByIndex(C,i)=*(C+8)+i*stride` |
| +0x10 | stride | clip struct size (element size) |
| +0x14 | count | total elements (bumped by `insert`) |
| +0x48 | activeCount | # clips with `+0x13 & 0x80`; recomputed by `FLpl_RecountActiveClips` |

Vtable slots used:
| slot | offset | role |
|---|---|---|
| [1] | +0x08 | **insert(Cobj, &element)** — append a clip |
| [2] | +0x10 | **init(Cobj, &element)** — zero/init an element (defaults + internal vtable/links) BEFORE field writes |
| [3] | +0x18 | **commit/finalize(Cobj)** — used by `FLpl_LayoutChannelClipsInPattern` after batch edits |

Call these **through the runtime vtable** (as above) — do not hardcode addresses; they are virtual.
`EnsureInModule` the resolved `initFn`/`insertFn` (a non-code ptr → AV), which the bridge already does.

---

## 4. Clip struct layout (CONFIRMED — matches `re/21` §6 and `controls-playlist.md`)

`clip = FLpl_GetClipByIndex(Cobj, i) = *(Cobj+8) + i*stride`.

| off | type | field | primitive / notes |
|---|---|---|---|
| +0x00 | int | **timeline start** (PPQ ticks) | poke directly (move) |
| +0x04 | u32 | **source ID** | `<0x50000000` = channel (`id>>16` = 0-based chan); `>=0x50000000` = pattern (`(id−0x50000000)>>16` = **1-based** pat). Encode: pattern `0x50005000+(pat<<16)`; channel `chan<<16`. Decode/validate `FLpl_DecodeClipSourceID @0xF72D40` |
| +0x06 | u16 | source index (= hi16 of +0x04) | pattern number / channel index |
| +0x08 | int | **length** (PPQ ticks, <1→1) | poke directly (resize). End = `FLpl_GetClipEndTick @0xF6C740` = start+len |
| +0x0c | i16 | **track field** = `500 − trackNo` | `FLpl_EncodeClipTrackField @0xB47000` / `FLpl_GetClipTrack @0xB47010` |
| +0x13 | u8 | **flags** | `0x80` = ACTIVE (clear to delete); `0x20` = muted (`FLpl_SetClipMuted @0xF71A60`) |
| +0x14 | u32 | packed edge/selection flags | |
| +0x18 | int/float | **source trim start** | `FLpl_SetClipSourceRange @0xF71A70`; `0xFFFFFFFF` = full |
| +0x1c | int/float | **source trim end** | audio=int samples, pattern=float beats; max 0x1000000 |
| +0x20 | int | clip unique id | |
| +0x24 | u32 | selection flags | |
| +0x2c | float | slide/fade | |
| +0x40 | double | time-stretch / length multiplier | |

Validity: `FLpl_IsClipDeleted @0xF719A0(clip)` runs the decoder on `+0x04`.

---

## 5. MOVE / RESIZE / DELETE / PASTE — no dedicated model primitive

Search of every `FLpl_*Clip*` / `*Clip*` engine symbol turned up **only** getters + two setters
(`FLpl_SetClipMuted`, `FLpl_SetClipSourceRange`) + the insert path. There is **no**
`FLpl_MoveClip` / `SetClipStart` / `SetClipLength` / `SetClipTrack` / `DeleteClip` /
`PasteClip` model function. FL's own UI does these by writing the struct fields and repainting:

| op | how FL does it (and the bridge should) | refresh |
|---|---|---|
| **MOVE** | write `+0x00` start (and `+0x0c`=500−track to change lane) | `FLpl_RepaintPlaylist` |
| **RESIZE** | write `+0x08` length (timeline). For AUDIO also `FLpl_SetClipSourceRange` to change the sample window | `FLpl_RepaintPlaylist` |
| **DELETE** | clear `+0x13 & 0x80` (ACTIVE) → slot stays in array but inactive/reusable (no compaction) | `FLpl_RecountActiveClips(Cobj)` + `FLpl_RepaintPlaylist` |
| **MUTE** | `FLpl_SetClipMuted(clip, bool) @0xF71A60` (`+0x13` bit 0x20) | `FLpl_RepaintPlaylist` |
| **PASTE/dup** | `insert` a new element (§2) copying `+0x04/+0x0c/+0x18/+0x1c`, new `+0x00/+0x08` | recount + repaint |

`FLui_EditorCanvas_PL_CommitClipEdits @0xD64650(canvas, plObj, force)` is a **UI-side** commit that
reconciles overlapping/edited clips after a drag (dedup by `+0x06` source idx over the selection
list at `plObj+0x3e0`, then `RecountGrids`/`ArrangeChannels`). It is NOT a callable "move clip"
primitive — it operates on the UI drag/selection state, not on (clip,newStart) args. **Conclusion:
the bridge's existing field-poke move/resize/delete (`FlInjectBridge.cs:1191-1224`) is the right
approach; only the ADD path needs the realize-pattern fix.**

Refresh signatures:
```c
void FLpl_RecountActiveClips(void* Cobj);                 // @0xF6E180  Cobj+0x48 = #(clips with +0x13&0x80)
void FLpl_RepaintPlaylist(void* songArr /* *PTR_DAT_014aab88 */); // @0xDA40C0
void FLpl_SetClipMuted(void* clip, char muted);           // @0xF71A60  +0x13 bit 0x20
void FLpl_SetClipSourceRange(void* clip, double s, double e); // @0xF71A70  +0x18/+0x1c, clamps [0,0x1000000], pattern/channel/audio-aware
int  FLpl_EncodeClipTrackField(int trackNo);              // @0xB47000  return 500 - trackNo
int  FLpl_GetClipEndTick(void* clip);                     // @0xF6C740  start + len
void* FLpl_GetClipByIndex(void* Cobj, int idx);           // @0x11E0DD0
```

---

## 6. AUDIO / channel clip vs pattern clip — shares the ADD primitive

The clip-ADD path (§2) is **identical** for both; only the **sourceID (+0x04)** differs:

| clip kind | sourceID (+0x04) | realize step | decode |
|---|---|---|---|
| **pattern** | `0x50005000 + (pattern1based << 16)` (`>= 0x50000000`) | `FLpat_GetOrCreateNoteRecorder(pat,1) @0x11D4080` | `(id−0x50000000)>>16` = 1-based pat |
| **audio / channel / automation** | `channelIdx0based << 16` (`< 0x50000000`) | ensure the CHANNEL exists (channel rack), then it plays that channel's sample/automation | `id>>16` = 0-based channel, validated `< channelCount` (`FLcr_ChannelListGetItem`) |

So to place an audio clip the bridge inserts the same struct with `sourceID = chanIdx<<16` and
sets a real source trim (`+0x18/+0x1c` = sample in/out, not `0xFFFFFFFF`) instead of realizing a
pattern recorder. `FLpl_SetClipSourceRange` handles the audio-vs-pattern unit conversion at +0x18/+0x1c.

Note: `FLpl_LayoutChannelClipsInPattern @0xCB0000(songEngine, chanIdx, pat, startTick)` — the
step-seq "send to playlist" channel-clip expander — is a **separate, specialized** path: it stores
channel clips inside the **pattern's note-recorder** collection (`FLpat_GetOrCreateNoteRecorder(pat)`)
via `FUN_00caff20`, tiles them across the pattern length, then commits via that collection's
`vtbl+0x18`. The bridge does NOT need it for a direct audio-clip-on-timeline; it's only for
replicating the full "Send to Playlist" step-seq behavior.

---

## 7. What our bridge is missing (the crash fix, concretely)

`FlInjectBridge.AddPatternClipAsync` (`FlInjectBridge.cs:1166`) → `InsertClipRawAsync` (`:1229`):

1. **Missing pattern realization.** Add before the insert:
   `await CallAsync("11d4080", new ulong[]{ (uint)pattern, 1 }, ct);` // FLpat_GetOrCreateNoteRecorder(pat,1)
   This creates the recorder + score list so the paint code doesn't deref `-1`. (For audio clips,
   realize the channel instead.)
2. **Shared, non-atomic scratch.** `ScratchAsync()` hands out a shared region and the build is
   poke→init→poke×7→insert across many bridge round-trips. Under concurrency another op's
   `ScratchAsync` use stomps the half-built clip → garbage → paint AV. Fix: dedicate a private
   scratch slice for clip-insert (or serialize under the existing bridge lock for the whole
   init→writes→insert span).
3. Everything else already matches canonical: collection `*(arr+0x14)` ✓, vtbl+0x10 init ✓,
   vtbl+0x8 insert ✓, field offsets ✓, `RecountActiveClips` ✓, 1-based sourceID ✓ (post the `re/21`
   fix), repaint ✓.

Move/resize/delete/mute (`:1191-1224`) need no change — they mirror FL's own field-poke approach.

---

## 8. Live-verify flags

1. **`*(arr+0x14)` vs `*(songArr+0xD04)+0x57C`.** `FLpl_SendPatternToPlaylist` inserts into
   `*(FLpl_GetCurrentArrangement()+0x14)`. `controls-playlist.md` also documents a clip array at
   `B+0x57C` where `B=*(songArr+0xD04)`. These are likely the same live object (current arrangement =
   song playlist), but confirm by reading both pointers live and asserting equality; the bridge must
   use `*(arr+0x14)` (which it does). If they ever differ, inserting into `B+0x57C` AVs.
2. **`FLpat_GetOrCreateNoteRecorder` create flag semantics.** Confirm `flag=1` on an
   already-realized pattern is a no-op (it early-returns the existing recorder; flag only gates the
   `FUN_010e2ef0` notify on FIRST creation) and does not disturb existing notes.
3. **RecountActiveClips necessity after insert.** Confirm the collection's `insert` (vtbl+8) does
   NOT itself update `+0x48` activeCount, so the explicit `FLpl_RecountActiveClips` is required for
   the new clip to play/render (static reading says yes; verify a placed clip actually sounds).
4. **`FLpat_GetPatternName` address** — not re-decompiled here; reuse the address the bridge already
   uses (`GetPatternNameAsync`). Only used for the track label in the canonical path, not required
   for a bare clip add.

---

## 9. Address table (Ghidra, base 0x400000)

| symbol | addr | role |
|---|---|---|
| `FLpl_SendPatternToPlaylist` | `0xCB0780` | canonical send-pattern-to-playlist (full handler) |
| `FLpl_LayoutChannelClipsInPattern` | `0xCB0000` | step-seq channel-clip expander (into pattern recorder) |
| `FLpl_GetCurrentArrangement` | `0x11E32C0` | `*(DAT_018329d8 + DAT_0149e8b4*8)`; Cobj = `*(ret+0x14)` |
| `FLpat_GetOrCreateNoteRecorder` | `0x11D4080` | **REALIZE pattern** (recorders @ `DAT_01803B90+idx*0xC0`, 1-based) |
| `FLpl_GetClipByIndex` | `0x11E0DD0` | `*(C+8)+idx*stride` |
| `FLpl_RecountActiveClips` | `0xF6E180` | recompute Cobj+0x48 active count |
| `FLpl_RepaintPlaylist` | `0xDA40C0` | redraw (arg = `*PTR_DAT_014aab88` songArr) |
| `FLpl_EncodeClipTrackField` | `0xB47000` | `500 - trackNo` |
| `FLpl_GetClipTrack` | `0xB47010` | `500 - (+0x0c)` |
| `FLpl_GetClipEndTick` | `0xF6C740` | start+len |
| `FLpl_SetClipMuted` | `0xF71A60` | +0x13 bit 0x20 |
| `FLpl_GetClipMuted` | `0xF71A50` | read +0x13 bit 0x20 |
| `FLpl_SetClipSourceRange` | `0xF71A70` | +0x18/+0x1c trim (audio/pattern-aware) |
| `FLpl_GetClipSourceRange` | `0xF71D60` | read trim |
| `FLpl_DecodeClipSourceID` | `0xF72D40` | `<0x50000000`=chan `id>>16`; `>=`=pat `(id−0x50000000)>>16` |
| `FLpl_IsClipDeleted` | `0xF719A0` | validity via decoder on +0x04 |
| `FLui_EditorCanvas_PL_CommitClipEdits` | `0xD64650` | UI drag commit/reconcile (NOT a callable move) |
| pattern array | `PTR_DAT_014AA0C8` | stride 0xC0, 1-based; length `+0x50`, color `+0x08` |
| note recorders | `DAT_01803B90` | `+idx*0xC0`, 1-based |
| song/main root | `PTR_DAT_014AAB88` | deref → songArr (RepaintPlaylist arg) |
| channel list root | `PTR_DAT_014A98D8` | `FLcr_ChannelListGetItem`; count `+0x10` |
