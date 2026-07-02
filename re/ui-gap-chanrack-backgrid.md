# UI Gap G1 — Channel Rack back-grid step-sequencer control (RESOLVED)

Static RE of `FLEngine_x64.dll` (image base 0x400000; runtime = ghidra − 0x400000 + flEngineBase).
All addresses below are Ghidra absolute. Functions renamed `FLui_ChanRack_BackGrid_*` and tagged `UI_win_chanrack`.

## TL;DR — the class

**The "BackWP" in the gap brief is a red herring for the *cells*.** `form+0x7a8` (BackWP) and
`form+0x7a0` (content scroll area) are only pan/scroll containers — their handlers (already RE'd:
`BackWPMouseDown/Move/Gesture/AdjustScroller`) do right-drag pan + h/v scroll, nothing else.

The **step cells are drawn + toggled by a dedicated per-channel control class:**

| | |
|---|---|
| **Class name** | **`TSSGrid`** (verified — RTTI string `07 "TSSGrid"` at the VMT tail) |
| **VMT** | **`0x011d1570`** (bookmarked, category `UI_win_chanrack`) |
| **InstanceSize** | `0x22fc` |
| **Instances** | **one per channel** — stored at `channelRow+0x7bc`; positioned by `FLui_ChanRack_ArrangeChannels@0xF50340` over the grid columns (x=`GetColumnX(5)`, width=grid-content-width `*0x14AB940`, height=row height) |
| **Channel binding** | `grid+0x3b4` = `TChannel*`; `grid+0x18` (= `grid[3]`) = channel index |
| **Constructor** | `FLui_ChanRack_BackGrid_Create@0x011d5890` (zeros a 1000-entry per-channel block array at `+0x3bc`; sets InstanceSize-class via `TObject.Create` path `FUN_00410320`) |
| RTTI published step params | "Pan, Velocity, Release, Mod X, Mod Y, Fine pitch, Repeat, Shift, Note, (merged)" |

`TSSGrid` overrides only **2** VMT slots beyond the WP base: the (virtual) constructor at `+0x78`
and **Paint at `+0x1a0`**. Mouse handling is via FL's WP **dynamic-method table (DMT)**, not the
VMT — the DMT (right after the VMT, 4 entries) maps:

```
msg 0xffd4 -> FLui_ChanRack_BackGrid_MouseDown      @0x011da800
msg 0xffd3 -> FLui_ChanRack_BackGrid_ApplyStepDrag  @0x011da4b0
msg 0xffd2 -> FUN_011dab30   (mouse-up, not RE'd in depth)
msg 0xffd5 -> FUN_011da790   (mouse-move hint, not RE'd in depth)
```
WP base helpers used: SetBounds = VMT `+0x188`, Invalidate/Realign = VMT `+0x178`.

## Key addresses

| Addr | Name | Role |
|---|---|---|
| 0x011d1570 | (TSSGrid VMT) | class vtable, bookmark |
| 0x011d5890 | FLui_ChanRack_BackGrid_Create | ctor |
| 0x011d9230 | **FLui_ChanRack_BackGrid_Paint** | step-cell paint (VMT +0x1a0) |
| 0x011da800 | **FLui_ChanRack_BackGrid_MouseDown** | left-click entry (DMT 0xffd4) |
| 0x011da4b0 | **FLui_ChanRack_BackGrid_ApplyStepDrag** | set steps over drag range (DMT 0xffd3) |
| 0x011d6640 | FLcr_GetStepNote | read step on/off (fast cache + note scan) |
| 0x011d6920 | FLcr_SetStepNote | **write/toggle a step (data-op)** |
| 0x00e02c80 | FLcr_ApplyGridBit | engine-level grid-bit apply (calls SetStepNote) |
| 0x011d8e80 | FLui_ChanRack_BackGrid_Refresh | rebuild list + repaint |
| 0x011d6e30 | FLui_ChanRack_BackGrid_AllocCacheBitmap | alloc/free `grid+0x3a0` cached render bitmap |
| 0x011d9090 | FLui_ChanRack_BackGrid_InvalidateStepCell | invalidate one cell's cached pixels |
| 0x011d90e0 | FLui_ChanRack_BackGrid_CommitAndRefresh | post-edit commit → `FLpat_RebuildPatternAndRefresh` |
| 0x011d2ee0 | FLui_ChanRack_BackGrid_GetStepNoteInPattern | step state for a given pattern (paint helper) |
| 0x011d6730 | FLui_ChanRack_BackGrid_IsStepPlaying | is step currently a live note (overlay) |

### Cached metric globals (set at FormCreate@0xF45870 / layout)
| Global | Meaning |
|---|---|
| `DAT_01803b20` | **step cell width in px** (render-space; THIS is what paint+click use, not `*0x14AB898`) |
| `DAT_0149d654` | **first visible step** (h-scroll, in steps) |
| `DAT_0149d64c` | **visible step count** |
| `DAT_0149d658` | **grid left X** (px, start of step area) |
| `*0x14A9368` | beat group = steps per beat (accent period) |
| `*0x14A9C90` | ticks per step (PPQ/step) — used by the note-event data layer |
| `DAT_0149d798` | current pattern index used by the grid |

## Paint recipe — `FLui_ChanRack_BackGrid_Paint@0x011d9230`

Two modes. Primary (cells) is the `grid+0x3a0 == 0` branch (no off-screen cache bitmap):

For each `step` in `[DAT_0149d654 .. DAT_0149d654 + DAT_0149d64c - 1]`:

1. **cell X (px):** `x = DAT_0149d658 + (step - DAT_0149d654) * DAT_01803b20`
   cell width = `DAT_01803b20`; the row Y/height come from the control's own bounds (one row tall).
2. **on/off state:** `on = FLcr_GetStepNote(grid, step)` (0/1).
   - extra OR with `FLui_ChanRack_BackGrid_GetStepNoteInPattern(grid, DAT_0149d798, chan, step)`
     and `FLui_ChanRack_BackGrid_IsStepPlaying(grid, step)` so live/foreign notes also light.
3. **base sprite index:** `base = (on != 0) ? 2 : 0`.
4. **playing-step highlight:** if `grid+0x398 == step` (current play position), `base += 2`.
5. **beat accent:** `accent = (step / *0x14A9368) & 1` (alternates every beat group).
   final image cell index = `base + accent` (so 0=off/even,1=off/odd,2=on/even,3=on/odd,+2 if playing).
6. **blit:** skin image set `*0x14A91B8` (a sprite sheet of step dots), drawn into the control's
   canvas (`*(grid+0x304)`+0xA0) via `FUN_006b89c0` (measure cell) + `FUN_006a4b30 / FUN_006a9de0 /
   FUN_006a9ce0` (image blit). Cell rect built with `FUN_004f0ae0(rect, x, 0, x+cellW)`.
7. After the loop it paints the two **loop/pattern-length bounds bars** (at step `DAT_0149d654` and
   `DAT_0149d654 + DAT_0149d64c*cellW`) using the control colour `grid+0xC4`.

(The `grid+0x3a0 != 0` branch blits a pre-rendered off-screen bitmap instead — same cells, cached.)

## Toggle recipe — MouseDown `@0x011da800` → ApplyStepDrag `@0x011da4b0`

`FLui_ChanRack_BackGrid_MouseDown(grid, button, shift, x, y)`:
1. Right button (or pan modifiers) → `FLui_ChanRack_GridPanBegin` (pan), else continue.
2. `FLcr_SelectOneChannelByIndex(grid+0x18)` if option set (click selects channel).
3. Begin undo txn: `FUN_00f37ec0(*0x14A9360, "step seq edit", &DAT_011dab24, 1)`.
4. **hit-test step:** `anchorStep = x / DAT_01803b20 + DAT_0149d654` (clamp ≥0), stored at `grid+0x230c`.
5. **decide paint value (toggle):**
   `grid+0x2308 = FLcr_GetStepNote(grid, anchorStep) ^ 1` (i.e. opposite of current → that's the toggle).
   (Shift-variants force a fixed 0/1 instead, for box-drag fill.)
6. Dispatch DMT 0xffd3 (`FUN_0040ffe0(grid, 0xffd3)`) → `ApplyStepDrag` for the initial cell + drag.

`FLui_ChanRack_BackGrid_ApplyStepDrag(grid, _, x)`:
1. `step = x / DAT_01803b20 + DAT_0149d654`.
2. target value = `grid+0x2308` (`grid[0x461]`). If ≥ 0, for every step from `grid+0x230c`
   (anchor) to `step` (inclusive, fills the drag range) and inside `[DAT_0149d654 ..
   +DAT_0149d64c-1]`:
   - if `FLcr_GetStepNote(grid, s) != target`:
     - `FLcr_SetStepNote(grid, s, target, /*commit*/0, /*vel*/100)`   ← **the data op**
     - `FLui_ChanRack_BackGrid_InvalidateStepCell(grid, s)`
3. `FLui_ChanRack_BackGrid_CommitAndRefresh(grid)` → `FLpat_RebuildPatternAndRefresh(DAT_0149d798,0)`.
4. update anchor `grid+0x230c = step`.

### Data op — `FLcr_SetStepNote@0x011d6920(grid, step, onOff, commit, vel0..0x7F)`
Steps are **stored as note events** in the pattern note recorder, NOT a simple bit array:
- `pos = step * *0x14A9C90` (ticks/step).
- recorder = `FLpat_GetOrCreateNoteRecorder(DAT_0149d798, 1)`.
- note event id = `TChannel(grid+0x3b4)->recEventId(+0x9c) + 0x4000`.
- onOff==1 → insert a note (vel clamped 0..0x7F, len 0x3c); onOff==0 → set the event's flag bit
  `event+0x13 |= 0x20` (mute) or remove it.
- `commit!=0` triggers `FLui_ChanRack_BackGrid_CommitAndRefresh`.

### Read op — `FLcr_GetStepNote@0x011d6640(grid, step) -> 0/1`
- **fast path:** cached step bitmap `*(grid+0x22fc)` (byte array, 1 byte per step) → `bitmap[step]`.
- **slow path** (step beyond cache): binary-search note events at `step * ticksPerStep` and test
  whether a note for this channel (`grid+0x3b4`) overlaps and isn't flag-0x20.

(Note: a second, legacy per-channel step representation exists — `FUN_00f1e690` reads a block at
`ctrl[0x7bc]→+0x3bc[chan]` with step bytes at `block+0x4800` (bit0=on) + parallel arrays at
`+0x000/+0x800/+0x1800/+0x2000/+0x2800/+0x3800/+0x4000` for the step params. That path is used by
the steps→notes baking, not the live UI grid. The authoritative UI path is GetStepNote/SetStepNote
+ the `grid+0x22fc` cache.)

## REUSE recipe — render + toggle a step grid in our own native UI

Per channel row (one `TSSGrid`-equivalent strip), with cached metrics:
`cellW`, `firstStep`, `visibleSteps`, `gridLeftX`, `beatGroup`(steps/beat), `playStep`(or -1):

Render each visible column `i = 0..visibleSteps-1`, `step = firstStep + i`:
```
x      = gridLeftX + i * cellW
on     = bridge.GetStepNote(channel, step)          // -> FLcr_GetStepNote
accent = (step / beatGroup) & 1                      // darker/brighter every beat group
sprite = (on ? 2 : 0) + accent + ((step == playStep) ? 2 : 0)
draw cell sprite at (x, rowY, cellW, rowH)
```
Hit-test a click at pixel `px` within the strip:
```
step   = px / cellW + firstStep
target = !GetStepNote(channel, step)                 // toggle = opposite of current
bridge.SetStepNote(channel, step, target, vel=100)   // -> FLcr_SetStepNote
// for drag: repeat for each step between anchor and current, only writing when state != target
commit -> rebuild pattern                              // -> FLpat_RebuildPatternAndRefresh
```
For a native bridge command, the cleanest engine entry points to call are
**`FLcr_GetStepNote`** and **`FLcr_SetStepNote`** (or `FLcr_ApplyGridBit@0xe02c80` which wraps
SetStepNote with the channel/grid resolution + undo). They take a `TSSGrid*` (`*(channelRow+0x7bc)`);
the grid already carries `+0x3b4`=TChannel and the pattern via `DAT_0149d798`, so you only supply the
step index. If you don't have a live `TSSGrid*`, replicate SetStepNote's note-event write directly
(note id = channel recId+0x4000, pos = step*ticksPerStep).

## Confidence & open items
- **Class identity (TSSGrid), Paint, MouseDown, ApplyStepDrag, Get/SetStepNote: HIGH** — class name
  read directly from RTTI; toggle math and data op read directly from decompiled code; DMT entries
  cross-checked against the dispatch calls in MouseDown.
- **G1 status: FULLY RESOLVED** for class + cell paint + left-click toggle + data model + reuse.
- Open / not deep-dived (not needed for G1): DMT 0xffd2 mouse-up `FUN_011dab30` and 0xffd5
  `FUN_011da790`; the off-screen-cache paint branch internals; exact skin sprite-sheet layout in
  `*0x14A91B8`; the legacy `block+0x4800` step-param arrays (only relevant if writing step
  velocity/pan/etc., names known from RTTI: Pan/Velocity/Release/Mod X/Mod Y/Fine pitch/Repeat/Shift/Note).
