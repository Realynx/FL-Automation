# Controls — PLAYLIST & ARRANGEMENT

Static RE of FL Studio 2025 `FLEngine_x64.dll` (Ghidra image base `0x400000`; all addresses
below are Ghidra absolute). Source of truth = the `FLpy_playlist_*` / `FLpy_arrangement_*`
scripting natives (see `fl-py-methods.txt`) + the engine model methods they call. The native
PyCFunction wrappers parse Python args then call the **inner engine functions** documented here —
the injected DLL should call those inner functions / touch the structs directly (see `re/06`).

Two dispatch styles are used by the natives:
- **Direct model write** — read/get ops call the model fn directly (e.g. `FLpl_GetCurrentArrangement`).
- **`FL_DispatchOpEvent(ctx,resBuf,handlerFn,a0,a1,a2,…,1)` (`0xE2C350`)** — set/mutate ops marshal
  args into a record then run `handlerFn` on the main thread. Handler reads args at `rec+0x18`(a0),
  `rec+0x20`(a1), `rec+0x28`(a2)… and writes status to `rec+0x60`. The op handlers are the
  `FLpl_op_*` functions below; their core work is the model fns.

NOTE: clip create/move via the *Python* API does not exist (the `playlist` module is tracks +
live-perf only). Clips are mutated by manipulating the **clip array + struct** directly
(below) then calling the playlist refresh (`playlistObj` vtbl `+0x178`).

---

## Operations

### Playlist — TRACK ops  (track[idx] = `FLpl_GetCurrentArrangement()` + idx*`0x114`, idx 1..500)

| Operation | native (Ghidra) | inner fn / field | value units / range | flags | notes |
|---|---|---|---|---|---|
| track count | `FLpy_playlist_trackCount 0xDFD370` | const `500` | int | — | always 500 lanes |
| get track name | `0xDFD390` | `FLpl_GetTrackName 0x11E76B0` (track+0x24) | string | — | default "Track N" |
| set track name | `0xDFD510` | `FLpl_op_SetTrackName 0xDFD480`→`FLpl_SetTrackNameAndColor 0x11E7940` | string | a0=name,a1=idx | |
| get track color | `0xDFD670` | track+0x2c | u32 BGR→0xFFRRGGBB | — | stored BGR (COLORREF); R/B swapped on read |
| set track color | `0xDFD810` | `FLpl_op_SetTrackColor 0xDFD700`→`FLpl_SetTrackNameAndColor` | u32 (R/B swapped→BGR) | a0=idx,a1=color | also sets track+0x70 customColor flag (see `FUN_011E3650`) |
| is muted | `0xDFD930` | track+0x3c == 0 | bool | — | +0x3c is "enabled" (0 = muted) |
| mute / toggle | `0xDFDB00` | `FLpl_op_MuteTrack 0xDFD9B0` (writes +0x3c over group) | a1: -1=toggle,0/1=set | a0=idx,a1=value,a2=group | group=1 applies to whole grouped block |
| is mute-locked | `0xDFDC60` | track+0xd5 | bool | — | |
| mute-lock toggle | `0xDFDD90` | `FLpl_op_MuteTrackLock 0xDFDCE0` (writes +0xd5) | a1:-1=toggle else set | a0=idx,a1=value | |
| is solo | `0xDFDEC0` | `FLpl_GetTrackSolo 0x11E8130` | bool | — | solo = this on, others off+not mutelocked |
| solo / toggle | `0xDFE000` | `FLpl_op_SoloTrack 0xDFDF40`→`FLpl_SetTrackSolo 0x11E9810` | a1:-1=toggle | a0=idx,a1=value,a2=group | uses +0x3c/+0xd4/+0xd6 |
| is selected | `0xDFE210` | `FUN_00DFE160` (scans sel-array) / track+0xe5 | bool | — | |
| select track | `0xDFE330` | `FLpl_op_SelectTrack 0xDFE280`→`FLpl_SetTrackSelection 0x11E9C30` | a1:-1=toggle,0=desel,else sel | a0=idx,a1=value | |
| select all | `0xDFE4D0` | `FLpl_op_SelectAllTracks 0xDFE460`→`SetTrackSelection mode 4` | — | a0=1 | |
| deselect all | `0xDFE5A0` | `FLpl_op_SelectAllTracks`→`SetTrackSelection mode 6` | — | a0=0 | |
| track activity | `0xDFE670` / `0xDFE6E0` | track+0x5c (float) | 0..1 | — | live VU level |
| select tool | `0xDFFE40` | `FUN_00DFFDC0` | int tool id | a0=tool | playlist edit tool |
| scroll to | `0xDFFC60` | `FUN_00DFF9E0` | a0,a1,a2,a3 | bar/tick/step | view scroll |
| song start tick | `0xDFF9C0` | `*(int*)PTR_DAT_014A7960` | ticks | — | |

`FLpl_SetTrackSelection(root,idx,mode)` modes: 0=select, 1=exclusive, 2=toggle, 3=deselect,
4=all, 5=smart-toggle-all, 6=none, 7=prev, 8=next, 9=extend-up, 10=extend-down.

Track "live/performance-mode" + display-zone natives (`getLive*`, `incLive*`, `triggerLiveClip
0xDFF030`, `getDisplayZone 0xDFE750`, `lockDisplayZone`, `liveDisplayZone`, `getPerformanceModeState`)
also exist; they read/write live-trigger state on the engine context (`FUN_00E00550`→ctx+0x60) and
are secondary to the static clip/track model.

### Arrangement ops  (arrangements: array `DAT_018329D8` of `TPLArrangement*`, current idx `DAT_0149E8B4`)

| Operation | native / fn (Ghidra) | inner fn / field | value units | notes |
|---|---|---|---|---|
| arrangement count | `FLpl_GetArrangementCount 0x11FB1A0` | dynarray len `*(DAT_018329D8-8)` | int | |
| get arrangement obj | `FLpl_GetArrangementByIndex 0x11FB190` | `*(DAT_018329D8+idx*8)` | ptr | TPLArrangement* |
| get arrangement name | `FLpl_GetArrangementName 0x11FB160` | `*(arrObj+8)` | string | |
| current arrangement idx | — | `DAT_0149E8B4` / `*(int*)PTR_DAT_014AC0C8` | int | |
| **switch arrangement** | `FLpl_SetCurrentArrangement 0x11FC880` | sets `DAT_0149E8B4` | idx | blocked while recording; validates idx<count |
| rename arrangement | `FLpl_RenameArrangement 0x11FB0D0` | `FUN_011E53C0(arrObj,str)` | idx,name | |
| add arrangement | `FLpl_AddArrangement 0x11FABC0` | grows array, allocs `TPLArrangement` | (switchTo,copyModes)->newIdx | |
| clone content | `FLpl_CopyArrangementInto 0x11FB420` | — | (src,dst) | "Clone…" menu |
| delete arrangement | `FLpl_DeleteArrangement 0x11FB1C0` | shrinks array | (idx,adjustCur,addUndo) | |
| jump to marker | `FLpy_arrangement_jumpToMarker 0xE1ED00` | `FUN_00E1EF90`→`FUN_010797A0` | a0=markerNum,a1=delta | |
| get marker name | `0xE1EE80` | markers list `songObj+0xd5c` via `FUN_00F70C30` | idx→string | |
| add auto time marker | `0xE1F040` | `FUN_00E1EF90` | tick,name | |
| current time | `0xE1F380` | `FUN_00D420D0(songArr,mode)` | ticks/ms/etc | mode arg |
| current time hint | `0xE1F400` | `FUN_00D499A0` | (h,m,...) | uses PPB at `songArr+0xb08` |
| selection start | `0xE1F540` | `songArr+0xd4c` | ticks | |
| selection end | `0xE1F560` | `songArr+0xd50` | ticks | |
| selection active | `0xE1F580` | `FLpl_IsTimeSelectionActive 0xD41FB0` | bool | both >=0 |
| **set selection** | `0xE1F7A0` | `FLpl_op_SelectionSet 0xE1F6E0`→`FLpl_SetTimeSelection 0xD41E60` | (start,end ticks) | |
| clear selection | `0xE1F610` | `FLpl_op_SelectionClear 0xE1F5B0`→`SetTimeSelection(-1,-1)` | — | |
| live selection | `0xE1F220` | `FUN_00E1F1C0` | start,end | perf mode |

### Timebase

| Quantity | location | notes |
|---|---|---|
| **PPQ (ticks/quarter-beat)** | `*(int*)PTR_DAT_014A79F8` (`general.getRecPPQ 0xE102C0`) | master timebase; clip pos/len & time-selection are in these ticks |
| PPB (ticks/bar) | `songArr+0xB08` (`general.getRecPPB 0xE102A0`) | = PPQ * beats-per-bar |
| set PPQ | `general.setRecPPQ 0xE10350` | reduces=lossy (warns) |
| tempo (BPM) | `*(double*)PTR_DAT_014AA358` | ms = ticks*60000/(BPM*PPQ) |
| song position | `transport.getSongPos 0xE0EBA0` / `setSongPos 0xE0EE00` | songObj `+0x7e8 → +0x3c0` current tick |

---

## Data structures

### Arrangements root
- `DAT_018329D8` — Delphi dynamic array of `TPLArrangement*` (one per arrangement). Length =
  `*(s64*)(DAT_018329D8-8)`.
- `DAT_0149E8B4` — current arrangement index (also mirrored at `*(int*)PTR_DAT_014AC0C8`).
- `FLpl_GetCurrentArrangement 0x11E32C0` = `*(DAT_018329D8 + DAT_0149E8B4*8)`.

### `TPLArrangement` (VMT `0x11E3100`, instance size `0x21CA0`) — the TRACK model
The object IS the track array: `track[idx] = arrObj + idx*0x114`, idx **1..500** (idx 0 / offset
0..0x114 is the header). Object header:
- `+0x00` vtable
- `+0x08` arrangement **name** (Delphi UnicodeString ptr)

Track struct (stride `0x114` = 276 bytes):
| off | type | field |
|---|---|---|
| +0x24 | str ptr | track name |
| +0x2c | u32 | color (internal BGR COLORREF) |
| +0x30 | u32 | track icon/setting (copied on clone) |
| +0x3c | u8 | **enabled** (0 = muted) |
| +0x3e | u8 | grouped-with-prev flag |
| +0x5c | float | activity / VU level |
| +0x70 | u8 | custom-color set flag (0 ⇒ follows theme color `PTR_DAT_014A8E50`) |
| +0xd4 | u8 | solo temp tag |
| +0xd5 | u8 | **mute lock** |
| +0xd6 | u8 | saved mute (pre-solo) |
| +0xd8 | u32 | **track mode**: 1 = instrument track, 3 = audio track |
| +0xdc | u32 | linked channel (instrument track) |
| +0xe0 | u32 | linked mixer track (audio track) |
| +0xe5 | u8 | **selected** |

### Playlist / clip model
Chain from the song object (`FLpl_GetSongArrangementObj 0xE1EC70` = `*PTR_DAT_014ABA80` else
`*PTR_DAT_014AAB88`):
- `songArr + 0xC40` → timeline object
- `songArr + 0xD04` → **playlist object** `B`
- `songArr + 0xD4C / +0xD50` → time-selection start / end (ticks)
- `songArr + 0xD5C` → markers list
- `songArr + 0xB08` → PPB

Playlist object `B` (= `*(songArr+0xD04)`):
- `B + 0x57C` → **clip array** `C` (also reachable via the playlist-UI getter vtbl `+0x74`)
- `B + 0x654` → clip-group helper
- vtbl `+0x178` → refresh/repaint

Clip array `C` (= `*(B+0x57C)`), a flat element array:
- `C + 0x08` → data pointer (base)
- `C + 0x10` → element stride (clip struct size)
- `C + 0x14` → **clip count**
- `FLpl_GetClipByIndex 0x11E0DD0(C,idx)` = `*(C+8) + idx*stride`.

**Clip struct** (element of `C`):
| off | type | field |
|---|---|---|
| +0x00 | int | **timeline start position** (PPQ ticks) |
| +0x04 | u32 | **source ID** ("IDToChanPat"): if `<0x50000000` → pattern/channel clip, `srcIdx = id>>16`; else audio/automation clip, `idx = (id+0xB0000000)>>16` |
| +0x06 | u16 | source index (= hi16 of +0x04; e.g. pattern number) |
| +0x08 | int | **length** (PPQ ticks; <1 treated as 1). End tick = `FLpl_GetClipEndTick 0xF6C740` = start+len |
| +0x0c | i16 | **track**: trackNo = `500 - this` (`FLpl_GetClipTrack 0xB47010`) |
| +0x13 | u8 | flags; **bit 0x20 = muted** (`FLpl_GetClipMuted 0xF71A50` / `SetClipMuted 0xF71A60`) |
| +0x14 | u32 | packed edge/selection flags (`FUN_00F729B0/F0`) |
| +0x18 | int/float | **source trim start** (loop-in into source). int for audio, float-beats for pattern |
| +0x1c | int/float | **source trim end** (loop-out). `FLpl_GetClipSourceRange 0xF71D60` / `SetClipSourceRange 0xF71A70` (max 0x1000000) |
| +0x20 | int | clip unique id (lookup key, `FUN_00C34E20`) |
| +0x24 | u32 | selection flags (`FUN_00F72910` bit0, `FUN_00F72920` bit1) |
| +0x2c | float | slide/fade value |
| +0x34 | float | (per-clip param; compared for clip-merge) |
| +0x40 | double | time-stretch / length multiplier |

Validity: `FLpl_IsClipDeleted 0xF719A0(clip)` checks the source id.
To place/move/resize/mute a clip from the bridge: index via `FLpl_GetClipByIndex`, write +0x00
(start), +0x08 (length), +0x0c (`500-track`), +0x04/+0x06 (source), +0x13 bit0x20 (mute), then
call the playlist refresh (`*(songArr+0xD04)` vtbl `+0x178`).

---

## Ghidra symbols renamed (prefix `FLpl_`)

Track model: `FLpl_GetCurrentArrangement 0x11E32C0`, `FLpl_GetTrackName 0x11E76B0`,
`FLpl_SetTrackName_inner 0x11E75F0`, `FLpl_SetTrackColor_inner 0x11E7610`,
`FLpl_SetTrackNameAndColor 0x11E7940`, `FLpl_GetTrackSolo 0x11E8130`, `FLpl_SetTrackSolo 0x11E9810`,
`FLpl_GetTrackGroupRange 0x11E9AA0`, `FLpl_SetTrackSelection 0x11E9C30`,
`FLpl_GetSelectedTracks 0x11E9FF0`.

Clip model: `FLpl_GetClipByIndex 0x11E0DD0`, `FLpl_GetClipEndTick 0xF6C740`,
`FLpl_GetClipMuted 0xF71A50`, `FLpl_SetClipMuted 0xF71A60`, `FLpl_GetClipSourceRange 0xF71D60`,
`FLpl_SetClipSourceRange 0xF71A70`, `FLpl_GetClipTrack 0xB47010`, `FLpl_IsClipDeleted 0xF719A0`.

Arrangement mgmt: `FLpl_GetArrangementCount 0x11FB1A0`, `FLpl_GetArrangementName 0x11FB160`,
`FLpl_GetArrangementByIndex 0x11FB190`, `FLpl_SetCurrentArrangement 0x11FC880`,
`FLpl_RenameArrangement 0x11FB0D0`, `FLpl_AddArrangement 0x11FABC0`,
`FLpl_CopyArrangementInto 0x11FB420`, `FLpl_DeleteArrangement 0x11FB1C0`.

Song/time selection: `FLpl_GetSongArrangementObj 0xE1EC70`, `FLpl_SetTimeSelection 0xD41E60`,
`FLpl_IsTimeSelectionActive 0xD41FB0`.

Native op handlers: `FLpl_op_MuteTrack 0xDFD9B0`, `FLpl_op_SoloTrack 0xDFDF40`,
`FLpl_op_SelectTrack 0xDFE280`, `FLpl_op_SelectAllTracks 0xDFE460`, `FLpl_op_MuteTrackLock 0xDFDCE0`,
`FLpl_op_SetTrackName 0xDFD480`, `FLpl_op_SetTrackColor 0xDFD700`, `FLpl_op_SelectionSet 0xE1F6E0`,
`FLpl_op_SelectionClear 0xE1F5B0`.

## Key globals
| symbol (Ghidra) | meaning |
|---|---|
| `DAT_018329D8` | arrangement-pointer dynarray |
| `DAT_0149E8B4` | current arrangement index |
| `PTR_DAT_014AC0C8` | current arrangement index (mirror) |
| `PTR_DAT_014AAB88` | song/main object root (deref → songArr) |
| `PTR_DAT_014ABA80` | current arrangement override (deref → songArr, else fall back to 014AAB88) |
| `PTR_DAT_014A79F8` | → PPQ (int) |
| `PTR_DAT_014AA358` | tempo BPM (double) |
| `PTR_DAT_014A7960` | song-start tick |
| `PTR_DAT_014A8E50` | default track theme color |
