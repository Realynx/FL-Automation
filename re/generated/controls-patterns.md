# Controls — PATTERNS & PIANO-ROLL (note authoring)

Domain map for the injected-DLL bridge: how the LLM agent writes melodies (notes),
manages markers/time-signatures, and controls patterns. Recovered **statically** from
`FLEngine_x64.dll` (Delphi/x64, image base 0x400000) in the shared Ghidra project.
All addresses are absolute VAs.

The piano-roll/score natives live in Python module **`flpianoroll`** (shown as module
"?" in `fl-py-methods.txt` — registered without a standard PyModuleDef). The note/marker
objects are `flpianoroll.Note` / `flpianoroll.Marker`. Patterns natives are module
`patterns`.

> As with all `FLpy_*`/`FLpat_score_*` wrappers, these are **PyCFunction shims**
> `(PyObject* self, PyObject* args)`. The bridge is native: use this as the **map**
> (which list/struct, what offsets, units) and manipulate the engine structs / call the
> inner routines directly. The `self` for the score natives is the **TPianoRollScript**
> object (the score wrapper for the currently-open piano roll).

---

## 1. PIANO-ROLL / SCORE operations

| Operation | function (VA) | Python sig / args | units / range | notes |
|---|---|---|---|---|
| add note (**write melody**) | `FLpat_score_addNote` **0xB7E210** | `addNote(note: Note)` (fmt "O") | — | appends Note obj to score; see NOTE struct |
| get note | `FLpat_score_getNote` 0xB7E0A0 | `getNote(i:int)`→Note (fmt "i") | i: 0..count-1 | returns Python Note obj |
| delete note | `FLpat_score_deleteNote` 0xB7E130 | `deleteNote(i:int)` (fmt "i") | i: 0..count-1 | "Invalid note index" if OOB |
| note count | (none direct) | use `len(self+0x24 list)` | — | `*(i64*)(*(self+0x24)+0x10)` = count |
| clear notes | `FLpat_score_clearNotes` 0xB7E470 | `clearNotes(all:int=?)` (fmt "i") | all=0 → selection only | deletes notes with active-flag(+0x44) |
| markers (enum) | `FLpat_score_getMarker` 0xB7E280 | `getMarker(i:int)`→Marker (fmt "i") | — | from markers list self+0x18 |
| add marker | `FLpat_score_addMarker` 0xB7E3C0 | `addMarker(m: Marker)` (fmt "O") | — | appends Marker obj |
| delete marker | `FLpat_score_deleteMarker` 0xB7E300 | `deleteMarker(i:int)` | — | |
| clear markers | `FLpat_score_clearMarkers` 0xB7E520 | `clearMarkers(all:int)` (fmt "i") | all=0 → in selected-notes time range | |
| clear all | `FLpat_score_clear` 0xB7E680 | `clear()` | — | = clearMarkers + clearNotes |
| get timeline selection | `FLpat_score_getTimelineSelection` 0xB7E790 | `getTimelineSelection()`→(start,end) | ticks | tuple of self+0x2c, self+0x30 |
| set timeline selection | `FLpat_score_setTimelineSelection` 0xB7E810 | `setTimelineSelection(start,end)` (fmt "ii") | ticks | writes self+0x2c / self+0x30 |
| default note props | `FLpat_score_getDefaultNoteProperties` 0xB7E860 | `getDefaultNoteProperties()`→Note | — | reads editor default NoteRec @ activeData+0x30 |
| next free group | `FLpat_score_getNextFreeGroupIndex` 0xB7EA20 | `getNextFreeGroupIndex()`→int | 1..0xFFFE | scans note+0x1c group ids |
| load score file | `FLpat_score_loadScoreFromFile` 0xB7EA80 | `loadScoreFromFile(path:str)` | — | imports into score data (self+0x80) |

Helper/inner routines (renamed):
- `FLpat_score_markDirty` 0xB7D920 — sets `*(activeData+0x80)+0x50 = 1` after edits.
- `FLpat_GetActivePianoRollData` 0xB7D8F0 — resolves the active piano-roll data object.
- `FLpat_NoteRec_to_PyNote` 0xB7DD10 — internal NoteRec → Python Note (defines both layouts).
- `FLpat_MarkerRec_to_PyMarker` 0xB7DBA0 — internal marker → Python Marker.
- TList primitives: `FUN_00508980`=Add(returns idx), `FUN_00508bc0`=Get(idx), `FUN_00508a10`=Delete(idx).

### `addNote` path (THE melody-writing path) — crystal clear
```
score.addNote(note)            # note is an flpianoroll.Note object
  parse 1 PyObject  -> noteObj            (PyArg @0x14a9530, fmt "O" @0xb7e27c)
  vcall noteObj (normalize)               (*0x14aa980)(noteObj)
  idx = TList.Add(self+0x10, noteObj)     # self+0x10 = notes list
        TList.Add(self+0x24, idx)         # self+0x24 = display-order index list
  FLpat_score_markDirty(self)             # *(self+0x80)+0x50 = 1
  return Py_None
```
The Note's fields (positions/lengths in **PPQ ticks**) are read when the score is
committed back to the pattern. To author a melody the bridge builds Note objects (or
writes internal NoteRecs directly into the pattern's note channel) with the layout below.

---

## 2. Data structures

### NOTE object — `flpianoroll.Note` (the LLM-facing note)
Field names recovered from the type's name table @0xB7DEC?; offsets/defaults from the
constructor (@~0xb7dec0) and `FLpat_NoteRec_to_PyNote` @0xB7DD10. Cross-confirmed:
`+0x14`=time (clearMarkers), `+0x1c`=group (getNextFreeGroupIndex), `+0x3b`=selected.

| Off | Field | Type | Range / units | Default | Internal NoteRec src |
|---|---|---|---|---|---|
| +0x10 | **number** | int32 | note/key 0..131 | 0 | rec@0x0C u16 |
| +0x14 | **time** | int32 | start position, **PPQ ticks** | 0 | rec@0x00 u32 |
| +0x18 | **length** | int32 | length, **PPQ ticks** | 0 | rec@0x08 u32 |
| +0x1c | **group** | uint32 | note group id (0=none) | — | rec@0x0E u16 |
| +0x20 | **pan** | float | 0.0..1.0, center 0.5 | 0.5 | rec@0x14 u8 /128 |
| +0x24 | **velocity** | float | 0.0..1.0 | 0.78125 | rec@0x15 u8 /128 |
| +0x28 | **color** | uint32 | 0..15 (MIDI ch / color group) | 0 | rec@0x13 & 0x0F |
| +0x2c | **fcut** | float | 0.0..1.0 (filter cutoff mod) | ~0.5 | rec@0x16 u8 /255 |
| +0x30 | **fres** | float | 0.0..1.0 (filter reso mod) | ~0.5 | rec@0x17 u8 /255 |
| +0x34 | **pitchofs** | int32 | fine pitch, -120..+? (byte-120) | 0 | rec@0x10 u8 −120 |
| +0x38 | **slide** | bool | | false | (rec@0x04 & 0x0F)==8 |
| +0x39 | **porta** | bool | | false | rec@0x13 & 0x10 |
| +0x3a | **muted** | bool | | false | rec@0x13 & 0x20 |
| +0x3b | **selected** | bool | | false | rec@0x13 & 0x80 |
| +0x3c | **release** | float | 0.0..1.0 | 0.5 | rec@0x12 u8 /128 |
| +0x40 | **repeats** | int32 | repeat count | 0 | rec@0x11 u8 |
| +0x44 | (active flag) | u8 | internal "real note" =1 | 1 | — |
| +0x55 | (reserved) | u8 | =120 | 120 | — |
| +0x59 | (reserved) | int | from global | — | — |

### INTERNAL packed NoteRec — 24 bytes (0x18) (the on-disk/score note)
This is what's actually stored in the pattern's note channel (matches the FLP note event).

| Off | Field | Type | Notes |
|---|---|---|---|
| 0x00 | position | u32 | ticks |
| 0x04 | flags1 | u16 | low nibble==8 ⇒ slide |
| 0x06 | rack/channel | u16 | source channel/rack index |
| 0x08 | length | u32 | ticks |
| 0x0C | key | u16 | note number 0..131 |
| 0x0E | group | u16 | note group id |
| 0x10 | fine_pitch | u8 | 120 = center (pitchofs = value−120) |
| 0x11 | repeats | u8 | |
| 0x12 | release | u8 | /128 → float |
| 0x13 | midich/flags2 | u8 | 0x0F=color, 0x10=porta, 0x20=muted, 0x80=selected |
| 0x14 | pan | u8 | center 64 (/128) |
| 0x15 | velocity | u8 | default 100 (/128) |
| 0x16 | mod_x (fcut) | u8 | /255 |
| 0x17 | mod_y (fres) | u8 | /255 |

### TPianoRollScript (score `self`) object layout
| Off | Meaning |
|---|---|
| +0x10 | notes TList (Python Note objects) |
| +0x18 | markers TList (Python Marker objects) |
| +0x20 | u8 "has timeline selection" flag |
| +0x24 | display-order index TList (idx → +0x10 slot); count at `*(+0x24)+0x10` |
| +0x2c | timeline selection start (ticks) |
| +0x30 | timeline selection end (ticks) |
| +0x80 | score-data container (dirty flag at +0x50; load target) |

(VMT: `TPianoRollScript` @ 0xB7D820, instSize 0x60. Related: `TScoreNodeHelper`
@0xF98E30, `TNoteRecChannel` @0xF611D0, `TNoteSelector` @0xF824C0.)

### MARKER object — `flpianoroll.Marker` (type @0xB7DCF0)
Fields (name table @0xB7DCC?): `time, mode, tsnum, tsden, scale_root, name, scale_helper`.
- `time` at object +0x10 (ticks). `mode` = marker type/action.
- **Time signature** markers carry `tsnum`/`tsden` (numerator/denominator).
- `scale_root`/`scale_helper` = key/scale markers. (Chord/strum markers: `addChordMarker`
  0xC69530 / `getChordMarker` 0xC695B0, fields velocity_ref/velocity_depth/repeats.)

### PATTERN array + root
- **Pattern array base** = `*(void**)0x14AA0C8` (static 0x01803B68, BSS), **stride 0xC0**,
  index **1..999** (`patternMax`=999). Slot indexed directly by pattern number.
- **Current pattern number** (int) = `*(int*)*(0x14AB580)` (→ BSS 0x0149D798).
- Pattern struct known offsets: **+0x08** = color (ARGB, BGR-packed; mask 0xFF00FF swaps R/B);
  **+0x50** = length in *pulses* (→ ticks via `FLpat_PatternLengthTicks` 0x11D33C0).
- **Patterns engine context** = `(*0x14A7DC0)()` (`FLpat_GetPatternsContext` 0xE0E630) —
  the dispatch target for FL_DispatchOpEvent pattern ops (null-at-rest, like other py-ctx
  getters; use the engine singleton reachable from host-context 0x14A7E40).
- Pattern groups: dynamic array @ `*0x14A9010`, length at `[-8]`.

### Timebase (PPQ)
- **PPQ (ticks per quarter)** for note positions/lengths = `*(int*)*(0x14A79F8)`
  (`general.getRecPPQ` 0xE102C0). Cluster of timebase globals (all → ~0x14A0C1C..0x14A0C4C):
  ticks/beat `*(int*)*(0x14A9368)`, pulses/beat `*(int*)*(0x14A9E40)`.

---

## 3. PATTERNS operations

Most mutating ops route through **`FL_DispatchOpEvent`** (0xE2C350):
`FL_DispatchOpEvent(ctx, resultBuf, handlerFn, p0, a1, a2, a3, ...,1)` where
`ctx = *FLpat_GetPatternsContext()` and `resultBuf` is built by `FUN_00415610(buf,&DAT_00e28818)`.

| Operation | function (VA) | args | units / range | mechanism |
|---|---|---|---|---|
| current pattern # | `FLpy_patterns_patternNumber` 0xE0C550 | () | 1..999 | read `*(int*)*0x14AB580` |
| pattern count | `FLpy_patterns_patternCount` 0xE0C570 | () | | counts non-empty 1..999 (`FUN_011db510`) |
| max pattern id | `FLpy_patterns_patternMax` 0xE0C5C0 | () | =999 | constant |
| get name | `FLpy_patterns_getPatternName` 0xE0C5E0 | (idx) | idx 0..999 | `FUN_011d3840(idx,&buf)` |
| set name | `FLpy_patterns_setPatternName` 0xE0C760 | (idx,name) | | OpEvent handler `FUN_00E0C6D0` |
| get color | `FLpy_patterns_getPatternColor` 0xE0C8C0 | (idx)→RGB | | reads patArr[idx].+0x08 |
| set color | `FLpy_patterns_setPatternColor` 0xE0CA10 | (idx,color) | | OpEvent handler `FUN_00E0C960` |
| get length | `FLpy_patterns_getPatternLength` 0xE0CB30 | (idx)→ticks | ticks | `FLpat_PatternLengthTicks` |
| select (set sel state) | `FLpy_patterns_selectPattern` 0xE0D540 | (idx[,value,mode]) | | OpEvent handler `FUN_00E0D3F0`; uses idx-1 |
| jump (set current) | `FLpy_patterns_jumpToPattern` 0xE0CD20 | (idx) | | OpEvent handler `FUN_00E0CCD0` |
| clone | `FLpy_patterns_clonePattern` 0xE0DD90 | (idx=-1) | | OpEvent handler `FUN_00E0DD00` |
| move | `FLpy_patterns_movePattern` 0xE0DF00 | | | |
| is selected | `FLpy_patterns_isPatternSelected` 0xE0D300 | (idx)→bool | | |
| is default | `FLpy_patterns_isPatternDefault` 0xE0D370 | (idx)→bool | | |
| select all | `FLpy_patterns_selectAll` 0xE0D740 | () | | |
| deselect all | `FLpy_patterns_deselectAll` 0xE0D870 | () | | |
| find next empty | `FLpy_patterns_findFirstNextEmptyPat` 0xE0CEB0 | | | (create-by-use) |
| channel loop style | `FLpy_patterns_getChannelLoopStyle` 0xE0D010 | | | |
| set channel loop | `FLpy_patterns_setChannelLoop` 0xE0D110 | | | |
| burn loop | `FLpy_patterns_burnLoop` 0xE0D9E0 | | | |
| loopstarter root | `FLpy_patterns_setLoopStarterRootNote` 0xE0DBD0 | | | |
| group count | `FLpy_patterns_getPatternGroupCount` 0xE0E040 | () | | dyn-array len `*0x14A9010[-8]` |
| active group | `FLpy_patterns_getActivePatternGroup` 0xE0E020 | () | | |
| group name | `FLpy_patterns_getPatternGroupName` 0xE0E070 | (g) | | |
| patterns in group | `FLpy_patterns_getPatternsInGroup` 0xE0E150 | (g) | | |
| ensure note record | `FLpy_patterns_ensureValidNoteRecord` 0xE0CC40 | () | | |
| block-set status | `FLpy_patterns_getBlockSetStatus` 0xE0CBB0 | | | |

> There is **no explicit "create pattern" native** — pattern slots 1..999 always exist;
> a pattern is materialized by jumping to / writing into an empty slot
> (`findFirstNextEmptyPat` finds the next free one).

Global commands: pattern selection/length/timesig can alternatively route through
`FL_DispatchCommand` 0xF53FE0 (global `0x4000xxxx` cmds) — see `re/08-command-bus.md`.

---

## 4. Ghidra symbols renamed (this session)

Score natives (`FLpy___*` → `FLpat_score_*`):
`addNote 0xB7E210, getNote 0xB7E0A0, deleteNote 0xB7E130, clearNotes 0xB7E470,
getMarker 0xB7E280, addMarker 0xB7E3C0, deleteMarker 0xB7E300, clear 0xB7E680,
clearMarkers 0xB7E520, getTimelineSelection 0xB7E790, setTimelineSelection 0xB7E810,
getDefaultNoteProperties 0xB7E860, getNextFreeGroupIndex 0xB7EA20,
loadScoreFromFile 0xB7EA80`.

Helpers:
`FLpat_NoteRec_to_PyNote 0xB7DD10, FLpat_MarkerRec_to_PyMarker 0xB7DBA0,
FLpat_score_markDirty 0xB7D920, FLpat_GetActivePianoRollData 0xB7D8F0,
FLpat_PatternLengthTicks 0x11D33C0, FLpat_GetPatternsContext 0xE0E630`.

Plate comments added: 0xB7E210 (addNote + Note struct), 0xB7DD10 (both note layouts),
0xB7E0A0 (getNote), 0x11D33C0 (pattern length + array root), 0xB7DBA0 (marker fields).

(`FLpy_patterns_*` natives were already named by the prior pass; left as-is.)
