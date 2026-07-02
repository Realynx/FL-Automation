# Playlist B — correct post-mutation REFRESH sequence (arrangement view)

Static RE of `FLEngine_x64.dll` (Ghidra image base `0x400000`; all addresses Ghidra-absolute;
live = liveFLEngineBase + (addr − 0x400000)). Purpose: fix "after the AI changes clips/patterns the
playlist fails to refresh — pattern clips look broken until you click them." Companion to
`re/21-clip-pattern-bug.md` (the off-by-one) and `re/generated/controls-playlist.md`.

---

## TL;DR — what the bridge must run after ANY clip/pattern mutation

The current bridge does `RepaintPlaylist(*0x14aab88)` [0xDA40C0] + `RecountActiveClips(Cobj)`
[0xF6E180]. **Those only invalidate windows, reposition track-header widgets and fix the scrollbar.
Neither one recomputes a clip's geometry.** A pattern clip's on-screen length is *derived* from its
source-range field (`clip+0x18/+0x1c`) by `FUN_00f71fb0`, and cached in `clip+0x08`. RepaintPlaylist
never calls `FUN_00f71fb0`, so the cache stays at whatever raw value we poked — hence "broken until
clicked" (a click / drag / any pattern edit is what triggers the resolver).

**Correct order after a clip mutation (add / move / resize / delete / mute):**

```
1. (mutate clip fields — as today)
2. FLpl_RecountActiveClips(Cobj)              @0xF6E180   // recompute active count (+0x48) FIRST
3. FUN_00d49840(songArr, patternNo, 3)        @0xD49840   // <-- THE MISSING CALL:
                                                          //     recompute clip+0x08 = FUN_00f71fb0(clip)
                                                          //     for every clip of patternNo, then mark the
                                                          //     playlist dirty + invalidate (FUN_00d90c00)
4. FLpl_RepaintPlaylist(songArr)              @0xDA40C0   // scroll + scrollbar range + track-header layout + window invalidate
```

- `Cobj` = `*(FLpl_GetCurrentArrangement()@0x11E32C0 + 0x14)` (clip collection).
- `songArr` = `*(0x14aab88)` (PTR_DAT_014aab88).
- `patternNo` = 1-based pattern the affected clip references = `(clip.sourceID − 0x50000000) >> 16`
  (read `clip+0x04`; only for pattern clips, `sourceID >= 0x50000000`).

If you want ONE call that also refreshes the pattern-picker side panel, use the engine wrapper
**`FUN_011d31e0(patternNo)` @0x11D31E0** = `FUN_00d49840(songArr,pat,3)` + `FUN_00da0f10(*(songArr+0xd0c))`.

**Generic (source-type-agnostic) alternative** — resolve the specific clip you touched instead of a
whole pattern: `len = FUN_00f71fb0(clipAddr)@0xF71FB0; poke clipAddr+0x08 = len;` then step 2 + 4.
`FUN_00f71fb0` handles pattern / audio / automation clips uniformly, so this works for any clip and
needs no pattern-number decode.

**Pattern NOTE mutation (native_add_notes) is ALREADY correct** — see §4. No change needed there;
`FLpat_RebuildPatternAndRefresh 0x11D4140` already propagates note changes into the playlist clips.

---

## 1. Why RepaintPlaylist @0xDA40C0 alone is insufficient (decompiled)

```c
void FLpl_RepaintPlaylist(songArr) {                         // param_1 = *(0x14aab88)
  plObjB = *(songArr + 0xd04);                               // playlist object B (model+view)
  if (*(songArr + 0x818))                                    // transport present?
     plObjB->vtbl[0x200](plObjB, ...playhead...);            // scroll to song position
  grid = *(songArr + 0x830);                                 // clip-area GRID control
  if (grid) grid->vtbl[0x178](grid);                         // schedule WM_PAINT (invalidate only)
  FUN_00d90c00(plObjB, 1);                                   // set plObjB+0x53c = 1 (paint-dirty) + vtbl[0x178]
  FUN_00d96130(plObjB);                                      // recompute vertical scrollbar range
  FUN_011f03c0(arr, 0xffffffff);                             // per-track HEADER widget layout, tracks 1..500
}
```

Everything it does is **window invalidation + chrome**:
- `grid->vtbl[0x178]` and `FUN_00d90c00` only **mark dirty / post a repaint** (`FUN_00d90e60` just sets
  `plObjB+0x53c = 1`). They do not touch clip fields.
- `FUN_011f03c0(arr,-1)` loops tracks and calls `FUN_00f27bd0(*(arr+0xe8+idx*0x114))` — that function
  re-lays-out the per-track **header** widgets (mute/solo/name via `FLui_WP_SetShowing`, mixer-track
  routing). Nothing about clip interiors.
- `FUN_00d96130` fixes the scrollbar extent (so a clip past song-end does extend the timeline — task
  hypothesis (d) is HANDLED here; not the bug).

Nowhere does RepaintPlaylist call `FUN_00f71fb0` (per-clip length resolve) or `FUN_00d49840`
(pattern→clip propagation). So a freshly poked clip is painted from unresolved fields.

---

## 2. Why a clip "looks broken until you click it" — the per-clip realize (task hyp. (a) = CONFIRMED)

A pattern clip's length is **NOT authoritative in `clip+0x08`**. It is derived from the source-range
`clip+0x18/+0x1c` and *cached* into `clip+0x08`.

`FUN_00f71fb0(clip)` @0xF71FB0 — the per-clip measure:
```c
long clip_length(clip) {
  FLpl_GetClipSourceRange(clip, &start, &end);   // 0xF71D60
  return max(1, round(end) - round(start));
}
```

`FLpl_GetClipSourceRange` @0xF71D60, PATTERN branch (`sourceID >= 0x50000000`), reads `+0x18/+0x1c`
as **int**:
```c
start = (double)*(int*)(clip+0x18);
end   = (double)*(int*)(clip+0x1c);
if (start < 0) {                                  // 0xffffffff == -1  → "full source"
   start = 0;
   end   = *(int*)(PTR_DAT_014aa0c8 + pat*0xc0 + 0x58);   // pattern CONTENT length (+0x58!)
}
```

And `FLpl_SetClipSourceRange` @0xF71A70 confirms `0xffffffff/0xffffffff` is the **canonical "whole
pattern" encoding** (it normalizes `start<1 && end==pattern[+0x58]` back to `0xffffffff/0xffffffff`).

Consequences:
- The authoritative pattern-clip length is the source range; **`clip+0x08` is a derived cache**.
- The cache is only (re)written by `FUN_00f71fb0`, which is invoked by the **pattern-changed
  propagation** (`FUN_00d496e0` / `FUN_00d49840`, see §3) or when the playlist **edit tool touches
  the clip** (click/drag hit-test). RepaintPlaylist does neither → the clip keeps the raw `+0x08` we
  poked until the first interaction. That is the "broken until clicked."
- Extra defect in our writer: bridge auto-length reads pattern **`+0x50`** (`AddPatternClipAsync`
  `patArr + pattern*0xC0 + 0x50`), but the engine's clip length comes from pattern **`+0x58`**. When
  `+0x50 != +0x58` the clip shows the wrong length even before any resolve. Fix: don't guess — let
  `FUN_00d49840`/`FUN_00f71fb0` set `+0x08`; set `+0x18/+0x1c = 0xffffffff` (already done) and drop the
  `+0x50` guess (or read `+0x58` if a value is needed pre-resolve).

Verdicts on the task's other hypotheses:
- (b) cached render/segment list not rebuilt — **PARTIAL**. The window *will* repaint (dirty flag +
  invalidate are set), but it repaints from the unresolved `clip+0x08`. It's the clip *geometry
  cache*, not a separate segment list, that's stale.
- (c) RecountActiveClips before repaint (ORDER) — **CONFIRMED, and already satisfied** on the add /
  delete paths (`InsertClipRawAsync` calls `f6e180` right after insert; `DeleteClipAsync` calls it
  after clearing 0x80). `+0x48` (activeCount) must be current before any consumer/paint reads it. Keep
  recount *before* repaint. (move / resize don't change the active set, so recount is optional there.)
- (d) song-length / timeline extend — **HANDLED** by `FUN_00d96130` inside RepaintPlaylist (and the
  pattern-length recompute in `0x11D4140`). Not the cause.

---

## 3. The pattern→clip propagation the clip path is missing

`FUN_00d49840(songArr, pat, mode)` @0xD49840 — "a pattern's clips changed, fix + repaint them":
```c
for each clip in currentArrangement.clips:
   if (clip.sourceID == 0x50005000 + (pat<<16))          // pattern clip of `pat`
        clip+0x08 = FUN_00f71fb0(clip);   bVar3 = true;  // RE-RESOLVE the length cache
   else if (clip.sourceID == 0x60000000)                 // automation clip → pattern
        if decode(clip)==pat { clip+0x08 = pattern[+0x58]; bVar2 = true; }
if (!globalSuppress) {
   if (bVar2 && (mode&1)) FUN_00d49670(songArr, pat);        // automation refresh
   if (bVar3 && (mode&2)) FUN_00d90c00(*(songArr+0xd04), 1); // dirty + invalidate playlist
}
```
`mode=3` (=1|2) does both. This is exactly the resolve+invalidate the clip path needs.

`FUN_00d496e0(songArr, pat, oldLen, newLen)` @0xD496E0 — same clip loop; resolves `clip+0x08` via
`FUN_00f71fb0`, and where the resolved length equals `oldLen`, overrides to `newLen`. Used by the
pattern-length-changed path (auto-follow). (Only caller: `0x11D4140`.)

`FUN_011d31e0(pat)` @0x11D31E0 — canonical wrapper: `FUN_00d49840(songArr,pat,3)` +
`FUN_00da0f10(*(songArr+0xd0c))` (pattern-picker/side-panel refresh). Called by project load
(`FLproj_LoadFileByPath`), step-seq loop ops, etc.

---

## 4. Pattern NOTE change (native_add_notes) — already complete

Bridge `RefreshPatternAsync` calls `0x11D4140` + `0xF53D30` + `0xD421C0` + rack. That is sufficient:

`FLpat_RebuildPatternAndRefresh(pat, doUI)` @0x11D4140 recomputes the pattern length, then at its
tail runs BOTH `FUN_00d496e0(songArr, pat, oldLen, newLen)` AND `FUN_00d49840(songArr, pat, 3)` —
i.e. it re-resolves every playlist clip of that pattern and invalidates the playlist — and then
`FLpat_NotifyPatternChanged(pat)` @0xF53D30 (piano-roll broadcast via `*(0x14ab950)`->vtbl+0xd8).
`FL_RefreshEditorViews` @0xD421C0 refreshes all open editor canvases; `0x107EAD0` the rack.

So note edits propagate into the playlist clip previews automatically. The gap is ONLY the raw
clip-mutation path, which bypasses `0x11D4140` and thus never reaches `0xD49840`.

---

## 5. Concrete bridge changes (`FlInjectBridge.cs`)

Add a helper and call it before the repaint on every clip mutation:

```csharp
// Resolve a pattern's playlist clips (length cache from source range) + mark playlist dirty/invalidate.
// FUN_00d49840(songArr, pattern, 3): recompute clip+0x08 = FUN_00f71fb0(clip) for all clips of `pattern`,
// then FUN_00d90c00(playlistObj,1). This is what RepaintPlaylist does NOT do — the "broken until clicked" fix.
private async Task UpdatePatternClipsAsync(int pattern, CancellationToken ct) {
    ulong songArr = await GPtrAsync("14aab88", ct);
    if (songArr != 0) await CallAsync("d49840", new ulong[] { songArr, (uint)pattern, 3 }, ct);
}
```

- `AddPatternClipAsync` (~L1166): after `InsertClipRawAsync`, add `await UpdatePatternClipsAsync(pattern, ct);`
  before `RepaintPlaylistAsync`. Also stop guessing the length from `+0x50` — insert with
  `+0x18/+0x1c = 0xffffffff` (already the case) and let `d49840` set `+0x08` (`FUN_00f71fb0` → pattern `+0x58`).
- `MoveClipAsync` / `ResizeClipAsync` / `DeleteClipAsync` / `SetClipMutedAsync`: read the clip's
  `+0x04` sourceID, and if `>= 0x50000000` decode `pat=(src-0x50000000)>>16` and call
  `UpdatePatternClipsAsync(pat, ct)` before `RepaintPlaylistAsync`. (Generic option: instead call
  `len = FUN_00f71fb0(clipAddr)` and poke `clip+0x08=len` — works for audio/automation clips too.)

### Latent bug flagged (not a refresh issue): RESIZING a pattern clip
`ResizeClipAsync` pokes `clip+0x08` directly. For a pattern clip `+0x08` is a **derived cache** — the
next pattern edit (or our new `d49840`) will overwrite it via `FUN_00f71fb0`, reverting the resize.
To resize a pattern clip *persistently* you must set the **source range** `+0x18/+0x1c` (int ticks for
patterns) via `FLpl_SetClipSourceRange @0xF71A70` (or poke the two ints), NOT `+0x08`.

---

## 6. Runtime globals / handles (task Q4)

| what | how |
|---|---|
| current arrangement (TPLArrangement) | `FLpl_GetCurrentArrangement()` @0x11E32C0 |
| clip collection `Cobj` | `*(arr + 0x14)` (vtbl@0, data@+8, stride@+0x10, count@+0x14, activeCount@+0x48) |
| song/main object `songArr` | `*(0x14aab88)` (PTR_DAT_014aab88) |
| playlist object B (dirty flag +0x53c) | `*(songArr + 0xd04)` — arg to `FUN_00d90c00`/`FUN_00d96130` |
| clip-area GRID control | `*(songArr + 0x830)` — RepaintPlaylist invalidates via vtbl+0x178; **may be 0** if the event editor isn't showing the playlist (then that invalidate is skipped — live-verify) |
| pattern-picker side panel | `*(songArr + 0xd0c)` — refreshed by `FUN_00da0f10` (via `FUN_011d31e0`) |
| pattern array A | `*(0x14aa0c8)` (PTR_DAT_014aa0c8), stride 0xC0; `+0x50` length, **`+0x58` content length (clip source length)**, `+0x08` color |
| current pattern (1-based) | `*(0x14ab580)` (PTR_DAT_014ab580) |

---

## 7. Function reference (Ghidra, base 0x400000)

| addr | name / role |
|---|---|
| 0xDA40C0 | `FLpl_RepaintPlaylist(songArr)` — scroll + scrollbar + track-header layout + window invalidate. Insufficient alone. |
| 0xF6E180 | `FLpl_RecountActiveClips(Cobj)` — recompute `+0x48` active count. Run before repaint. |
| **0xD49840** | **`FUN_00d49840(songArr, pat, mode)` — recompute pattern-`pat` clips' `+0x08` via `FUN_00f71fb0`, then (mode&2) dirty+invalidate. THE missing call.** |
| 0xD496E0 | `FUN_00d496e0(songArr, pat, oldLen, newLen)` — resolve clip lengths + auto-follow (only from 0x11D4140). |
| 0x11D31E0 | `FLpat_UpdatePatternClipsInPlaylist(pat)` — wrapper: `d49840(songArr,pat,3)` + side-panel refresh `da0f10`. |
| 0xF71FB0 | `FLpl_GetClipDisplayLength(clip)` — `max(1, round(end)-round(start))` from source range. Per-clip realize. |
| 0xF71D60 | `FLpl_GetClipSourceRange(clip,&s,&e)` — pattern branch reads `+0x18/+0x1c` as int; `<0` ⇒ full source = pattern `+0x58`. |
| 0xF71A70 | `FLpl_SetClipSourceRange(clip,s,e)` — writes `+0x18/+0x1c`; normalizes full-source to `0xffffffff`. Use to resize pattern clips. |
| 0xD90C00 | `FUN_00d90c00(plObjB,1)` — set paint-dirty `plObjB+0x53c=1` (`FUN_00d90e60`) + vtbl+0x178. |
| 0xD96130 | scrollbar-range recompute (inside RepaintPlaylist; handles timeline extend). |
| 0x11F03C0 | `FUN_011f03c0(arr,-1)` — per-track header widget layout (inside RepaintPlaylist). |
| 0x11D4140 | `FLpat_RebuildPatternAndRefresh(pat,ui)` — note-change path; tail calls d496e0 + d49840 + NotifyPatternChanged. |
| 0xF53D30 | `FLpat_NotifyPatternChanged(pat)` — piano-roll broadcast. |
| 0xD421C0 | `FL_RefreshEditorViews()` — refresh all open editor canvases. |
| 0xCB0780 | `FLpl_SendPatternToPlaylist` — canonical clip-add primitive (its tail = undo-scope commit, not the paint). |

---

## 8. Live-verify checklist (FL running, scratch project)

1. **Repro**: `native_add_pattern_clip(K, track=1, start=0, len=0)` on a project where pattern K's
   `+0x50 != +0x58` (has a differing content length). With the OLD refresh, the clip renders with
   wrong length/empty preview until you click it. Read `clip+0x08` before vs after a click:
   pre-click = our poked value; post-click = `FUN_00f71fb0` value.
2. **Fix**: add `d49840(songArr, K, 3)` before RepaintPlaylist → clip should render correct WITHOUT a
   click, and `clip+0x08` should already equal `FUN_00f71fb0(clip)` = pattern `K` `+0x58`.
3. Confirm pattern `+0x50` vs `+0x58` actually differ in practice (drives the "wrong length" half).
4. Confirm `*(songArr+0x830)` is non-null while the AI runs headless; if it can be 0, RepaintPlaylist
   skips the clip-grid invalidate — may need to invalidate the grid another way.
5. Confirm the resize revert: `ResizeClipAsync` on a pattern clip, then trigger any pattern edit →
   length reverts (proves `+0x08` is a cache; fix by setting `+0x18/+0x1c` instead).
6. Sanity: the note path (`native_add_notes`) already redraws playlist clips with no click — verify it
   still does (it goes through 0x11D4140 → d49840).
