# UI Gap G5 — Mixer per-strip control offset table + strip drag/reorder/route gesture path

Target: `FLEngine_x64.dll` (image base `0x400000`; all addresses absolute/Ghidra, runtime = `ghidra - 0x400000 + flEngineBase`).
Static RE only. Closes gap **G5** from `re/ui-gaps.md`; builds on `re/ui-win-mixer.md` (§3 data model, §4 strips, §9 select/reorder).

Anchors: strip builder **`FLui_Mixer_BuildLayoutStrips @0x01183c80`** (~107 KB; readable dump cached at
`scratchpad/FUN_01183c80.c`). Track array **`g_MixerTrackArrayPtr`, 502 tracks, stride `0x1474`**; `track[i] =
g_MixerTrackArrayPtr + i*0x1474`. Form = `TFXForm` (VMT `0x117b6b0`).

---

## 0. TL;DR
- **Every per-strip widget offset in `track+0x13d0..+0x1440` is now pinned** (§1). In the builder they are addressed
  through `local_1d0 = (int*)(track + 0x13d0)`, so a slot written as `local_1d0 + N` (int-pointer arithmetic) lives at
  **`track + 0x13d0 + N*4`**. The region is a 16-byte layout rect (`+0x13d0`) followed by twelve 8-byte control
  pointers at `+0x13e8 .. +0x1440`.
- **There is NO free mouse-drag "drag-to-route" and NO drag-reorder-by-dragging-the-strip in this build.** The four
  real strip *drag* gestures are: (1) value-tweak drag on a fader/wheel, (2) **swipe-across-strips enable/disable**
  (the only multi-strip drag), (3) drag-pan scroll of the tracks panel, (4) splitter resize. **Reorder** is
  menu/keyboard-driven (Move-Left/Right → a permutation swap). **Routing** is the per-strip **send button** toggle
  (+ Ctrl/Shift "route-to-only") and the route context menus — a click/toggle, not a drag. (§2–§4)
- Renamed 6 helpers `FLui_Mixer_*` and tagged them `UI_win_mixer` (§5). Program saved.

---

## 1. Per-strip control offset table (`track+0x13d0..+0x1440`) — HIGH

Base helper in `FLui_Mixer_BuildLayoutStrips`: `local_1d8 = track base`; `local_1d0 = (int*)(local_1d8 + 0x13d0)`.
Because `local_1d0` is an `int*`, **`local_1d0 + N`  ⇒  byte offset `track + 0x13d0 + N*4`**. Each control is an 8-byte
WP-control pointer (so consecutive controls are `N` = even, +2 apart). Verified line-by-line against the cached dump.

| track offset | `local_1d0+N` | widget | skin descriptor (`forms.mixer.…`) | id / handlers · notes |
|---:|:--:|---|---|---|
| `+0x13d0`..`+0x13df` | `[0..3]` | **strip layout rect** (4×i32 `x1,y1,x2,y2`) | — | build-time scratch bounds; reused for meter placement |
| `+0x13e0` | `+4` | (reserved/aux qword) | — | **not touched** by the strip builder |
| `+0x13e8` | `+6` | **volume fader** (slider) | `track.controls.slider.volume:slider` | id `paramBase+0x70001fc0`; OnChange `0x11a4970`; range 16000, default `0x3200`; tip `\|^b^aVolume`; ctor `FUN_007c1eb0` |
| `+0x13f0` | `+8` | **pan wheel** | `track.controls.wheel.pan:wheel` | id `paramBase+0x70001fc1`; OnChange `0x11a4970`; range `0x1900`; tip `\|^b^aPanning`; centre-value cb `FUN_011a5130` |
| `+0x13f8` | `+0xa` | **stereo-sep wheel** | `track.controls.wheel.stereosep:wheel` | id `paramBase+0x70001fc2`; OnChange `0x11a4970`; tip `\|^b^aStereo separation`; centre-value cb `FUN_011a5160` |
| `+0x1400` | `+0xc` | **send-level wheel** | `track.wheel.sendwheel:wheel` | **built elsewhere** (`FLui_Mixer_RebuildSelTrackSendsCables @0x118fdc0`, re/ui-win-mixer §6); untouched by this builder |
| `+0x1408` | `+0xe` | **send button** | `track.controls.button.sendbutton:quickbutton` | OnChange `FLui_Mixer_Strip_OnSendBtnToggleRoute @0x11ad510`; caption `SendBtn%d`; popup on `form+0x868` |
| `+0x1410` | `+0x10` | **rec/arm button** | `track.controls.button.recbutton:quickbutton` | OnChange `0x11ad340`; click `0x11ad460` |
| `+0x1418` | `+0x12` | **plugins button** | `track.controls.button.pluginsbutton:quickbutton` | OnClick `0x11ace80`; ctrl-key MouseActivate `TToolbarForm.CtrlKeyBtnMouseActivate`; opens FX chain |
| `+0x1420` | `+0x14` | **mute button** | `controls.button.mutebtn:quickbtn` | OnClick `0x11ad050`; mouse `0x11ad130`; tip `\|^dMute / solo`; ctor `FLui_WP_CreateControl(&LAB_00715520)` |
| `+0x1428` | `+0x16` | **delay/PDC button** | `track.controls.button.delaybutton:quickbutton` | OnClick `0x0119ff40`; wheel `TFXForm.DelayLabelMouseWheel`; only built when strip width allows |
| `+0x1430` | `+0x18` | **flip-Y (reverse polarity)** | `track.controls.button.flipy:quickbutton` | OnClick `TFXForm.RevSBtnClick @0x11af010`; **id = `trackIdx`**; tip `\|Reverse polarity` |
| `+0x1438` | `+0x1a` | **rev-stereo (swap L/R)** | `track.controls.button.revstereo:quickbutton` | OnClick `TFXForm.RevSBtnClick @0x11af010`; **id = `trackIdx+0x10000`**; tip `\|Swap left & right channels` |
| `+0x1440` | `+0x1c` | **strip container panel** | `track.*` (bg/header/selectedcolor/separator) | x `panel+0x90`, w `panel+0x98`, h `panel+0x9c`; parent `panel+0x78`; **meter/content rect `panel+0x398` (4×i32)**; h-scroll-ofs `panel+0x3b8` |

Parallel arrays (same `*0x1474` stride, keyed by track index — NOT inside the `+0x13d0` region):
- **`DAT_01582f9c + track*0x1474`** = the **strip header tab** control (number/name/colour), built by
  `FLui_Mixer_BuildStripHeaderTab @0x11ca730`. Colour read from `(&DAT_01581b60)[track*0x51d] | 0xff000000`
  (`0x51d*4 = 0x1474`, i.e. the track's BGR inside the same struct). Caption: track# / `Master` (idx 0) / `Current` (idx 0x1f5).
- Track struct (re/ui-win-mixer §3, refined here): FX-slot plugin objects `track+0x1324 + slot*8` (10), FX-slot UI
  mirror `track+0x1374 + slot*8`; **send-active matrix records `track+0x2e4 + dest*8` (8-byte record; the "active"
  byte is `+0x2e8` = record+4)** — confirmed by the permutation copy in `FLui_Mixer_ApplyTrackPermutation`;
  header back-refs `track+0x1460 / +0x1468` (each `+8` = self index); armed `+0x145c`.

Lazy-create guard/register helpers (return "exists?" / register a freshly-created child): `FUN_011839a0`,
`FUN_011838c0`, `FUN_01183ad0`, `FUN_01183980`. First build sets skin descriptor (`ctl+0x328`, or slider/wheel
`ctl[0x65]`), event-id (`ctl[3] = ctl+0x18`), the TMethod handler pairs, then `SetBounds` (`vtbl[0x188]`).

---

## 2. Strip drag-state fields (on the `TFXForm` instance)

| field | role |
|---|---|
| `form+0xd7c` | **captured/active strip control** — set on strip mouse-down (`OnClickSelectTrack`), cleared on mouse-up (`OnMouseUp`). This is the "which fader/wheel am I dragging" pointer. |
| `form+0xd84` | **multi-strip drag/gesture-in-progress flag** — first swipe touch sets it =1; the relative/linked-tweak + the enable-swipe use it. |
| `form+0xd78` | **tracks-panel pan-scroll anchor** — the gesture-0x104 drag origin (`= scrolledPanel+0x54c + pt.x`). |
| `form+0xe20 + track*8` | **per-track dB readout cache (double)** — recomputed for every strip touched during a gesture. |

---

## 3. Strip gesture paths

### 3.1 Value-tweak drag (fader / pan / stereo-sep / send wheel) — the everyday drag
Wired on each slider/wheel in the builder:
- **MouseDown** `ctl+0x144` (backptr `ctl+0x14c`) = `FLui_Mixer_Strip_OnClickSelectTrack @0x11a5070`: decodes the
  control id (`FUN_011c2390(id-0x70000000)`) → track; if `*PTR_DAT_014ab1f8` selects it via
  `FLui_Mixer_SelectTrackUI(form, track, 0x15)`; records `form+0xd7c = ctl` (only if the track is in-use `+0x19`).
- **Drag/Change** wheel `[0x73]/[0x74]` (`+0x398/+0x3a0`) = `FLui_Mixer_Strip_OnVolPanChange @0x11a4970` →
  `FL_DispatchCommand(id, value, 0x3dd)` + dB-cache refresh + relative tweak of other selected tracks.
- **MouseUp** `ctl+0x164` (backptr `ctl+0x16c`) = **`FLui_Mixer_Strip_OnMouseUp @0x11a50f0`**: `form+0xd7c = 0` +
  `ReleaseCapture()`.
- Secondary TMethods: `ctl+0x2bc/+0x2c4` = `FUN_011a5110` (`FL_DispatchCommand(id, ctl+0x3c0, 4)` — dispatch the
  control's default/current value; MED); pan/sep `ctl[0x94]/[0x95]` (`+0x4a0/+0x4a8`) = `FUN_011a5130`/`FUN_011a5160`
  (centre/detent value provider).

### 3.2 Tracks-panel `OnGesture` = `TFXForm.TracksPanelMGesture @0x11af400`
`param_3` = a WP gesture record (`*param_3` = msg id; `param_3+2` = x; `param_3+0xe` = travel; `param_3+6` = flags).
Sets `*param_4 = 1` (handled) in all branches.

- **`0x104` (drag-pan / scroll):**
  - flag bit `1` (begin) → set anchor `form+0xd78 = scrolledPanel+0x54c + pt.x`.
  - else if bit `4` (cancel/inertia) → `FUN_006ce8c0(scrolledPanel+0x534)`.
  - else → `delta = form+0xd78 - pt.x`; (optionally clamped to `[FUN_006d4d40, FUN_006d4d80]` of the scroller) →
    `FUN_0077da00(scrolledPanel, 0, delta, 3)` (set scroll position). This is the finger/drag-to-scroll of the strips.
- **`0x106` (press/tap-drag):**
  - if **travel `< 150*dpi`** (a tap): scan every track, take its strip panel `track+0x1440`, match
    `panel+0x78 == the gestured panel` AND `panel+0x90 <= pt.x <= panel+0x90+panel+0x98-1` → hit track → call
    **`FLui_Mixer_Strip_GestureToggleEnable(form, hitTrack, (track+0x19 == 0))`** (§3.3).
  - else (**travel `≥ threshold`** = a real drag) → `TFXForm.SplitterBoxRMouseDown(form, form+0x980, 1, 0, -1, -1)` —
    i.e. a long horizontal drag on the panel grabs the **strips↔shared-panel splitter** (grip drawn by
    `SplitterBoxRPaint @0x11a41b0` as 3 dots; drag handled by `SplitterBoxR{MouseDown 0x11a3e90, MouseMove 0x11a3fb0,
    MouseUp 0x11a40e0}`).

### 3.3 Swipe-across-strips enable/disable = `FLui_Mixer_Strip_GestureToggleEnable @0x011925c0`
`(form, track, targetEnable)` — the only genuine multi-strip drag action.
- If `targetEnable != 0` and `track != selected` and `form+0xd84 == 0`: set `form+0xd84 = 1` (arm the drag). On
  subsequent touches (flag already 1) it recomputes the dB cache `form+0xe20+track*8` (from the fader value at
  `track+0x13e8`, level `track+0x2b4`) for all touched tracks.
- If `track+0x19 != targetEnable` (state actually changes): recompute this track's dB cache, then commit —
  `FUN_011c59b0(track, targetEnable)` (set in-use), **`FUN_011c61d0(track+0x13d0)`** (reset/rebuild the strip's
  control-pointer region so widgets are (re)created on next layout), `FUN_0118e880(track, 0x1a, 1)` (command bus),
  `FUN_011c5a20(track, 3)`. Net effect: swiping the pointer across strips **enables empty slots / disables used
  tracks** (toggle-on-first-touch).

---

## 4. Reorder + routing (NOT drag gestures in this build)

### 4.1 Track reorder — `FLui_Mixer_MoveSelectedTrack @0x011a78d0`
Entry: `TFXForm.MoveLeftMenuClick @0x11aff80` (and its move-right mirror) → `MoveSelectedTrack(delta, saveUndo, notify)`
(delta = menu-item event-id `±1`). It:
1. Allocates an identity permutation of **0x1f6 ints** (`FUN_00409410(0x7d8)`).
2. Runs `|delta|` adjacent swaps via **`FLui_Mixer_ReorderSwapStep @0x011a7810`** — swaps two entries in the perm
   array (`state+0x40`), skips non-usable tracks (`FUN_011c5a80`), and keeps the selected slot pointer `state+0x3c`
   current (dir `state+0x4c`, step `state+0x90`, wrap mod `count-2`).
3. Commits via **`FLui_Mixer_ApplyTrackPermutation @0x011a7470(perm, notify)`** — under CS `PTR_DAT_014ab2d8`:
   snapshots all 0x1f6 track structs (clone), copies each struct to its new slot, builds the inverse map, and
   **remaps every cross-reference**: the **send-active matrix** (`track+0x2e4 + i*8`, all 0x1f6 records permuted),
   the **FX-slot plugin objects** (`track+0x1324 + slot*8` × 10, re-notified via `FUN_011c21b0`), the **header-tab
   back-refs** (`track+0x1460/+0x1468` → their `+8` self-index), the **self index** (`track+0x28`), the
   **arrangement PL-track mixer targets** (`+0xb8`), and the **channel-rack channel targets** (`chan+0x288`).
   Then reroutes/refreshes cables + arrangement.

So mixer track order is fully mutable, but the trigger is Move-Left/Right (menu / Alt+arrows), **not** a drag of the
strip. (`ReorderSwapStep`/`ApplyTrackPermutation` are the reusable primitives if we ever want a drag-reorder UI.)

### 4.2 Routing — send button `FLui_Mixer_Strip_OnSendBtnToggleRoute @0x011ad510`
OnChange of the per-strip send button (`track+0x1408`). Records the button as the routing popup target
(`*(form+0x868)+0xc4 = btn`), reads Ctrl (`0x11`) / Shift (`0x10`):
- **no modifier:** begin undo, then loop every in-use track and call
  **`FLmx_SetRouteActiveCore @0x011a67f0(form, srcTrack, dstId, onoff, 0)`** where `dstId = btn+0x18` (the button's
  event-id / track) and `onoff = btn+0x492` (the toggle state) — enables/disables the route.
- **Ctrl / Shift / both:** `TFXForm.RouteToOnlyMenuClick` with the appropriate menu target
  (`form+0x870` / `form+0x8d8` / `form+0x8e0`) — "route to only".

`FLmx_SetRouteActiveCore` callers (all routing entry points, cross-lane): the data-side op
`FLmx_SetRouteTo_op @0xe08e20`, internal `FUN_00df6810 / FUN_00f33d60 / FUN_0117edd0 / FUN_0117fe40 / FUN_011a86c0`,
this send button, and the route menus (`RouteSelToThis1 0x11af8f0`, `RouteThisToSel1 0x11afd20`, `RouteToOnly
0x11afe40`, `FXTrackUnrouteRoutedTracks 0x11aef20`). **No mouse-drag caller exists** → there is no drag-to-route
gesture; per-destination send *level* is the send wheel (`dest+0x1400`, OnChange `TFruityLoopsMainForm.FrontVolWheelChange`,
re/ui-win-mixer §6).

---

## 5. Ghidra annotations made (this gap — in-lane only)

Renamed `FUN_*` → `FLui_Mixer_*` and tagged **`UI_win_mixer`**:
- `0x011925c0` → **`FLui_Mixer_Strip_GestureToggleEnable`** (swipe-across-strips enable/disable toggle)
- `0x011a78d0` → **`FLui_Mixer_MoveSelectedTrack`** (reorder driver)
- `0x011a7810` → **`FLui_Mixer_ReorderSwapStep`** (single adjacent perm swap)
- `0x011a7470` → **`FLui_Mixer_ApplyTrackPermutation`** (apply reorder + remap all cross-refs)
- `0x011a50f0` → **`FLui_Mixer_Strip_OnMouseUp`** (clear `form+0xd7c` + ReleaseCapture)
- `0x011ad510` → **`FLui_Mixer_Strip_OnSendBtnToggleRoute`** (send button → route toggle)

Left as-is (already named / cross-lane, per re/ui-win-mixer §11 & re/12 data lane): `TFXForm.TracksPanelMGesture
0x11af400`, `MoveLeftMenuClick 0x11aff80`, `SplitterBoxR* 0x11a3e90..`, `FLui_Mixer_Strip_OnClickSelectTrack
0x11a5070`, `FLui_Mixer_Strip_OnVolPanChange 0x11a4970`, `FLui_Mixer_BuildStripHeaderTab 0x11ca730`,
`FLmx_SetRouteActiveCore 0x11a67f0` (data core). Program saved.

---

## 6. Reuse notes (#80 mixer clone / drive FL)
- **Strip widget layout:** with §1 you can now address any strip control directly:
  `*(void**)(g_MixerTrackArrayPtr + track*0x1474 + <offset>)` (fader `+0x13e8`, pan `+0x13f0`, stereosep `+0x13f8`,
  send-wheel `+0x1400`, send-btn `+0x1408`, arm `+0x1410`, plugins `+0x1418`, mute `+0x1420`, delay `+0x1428`,
  flipy `+0x1430`, revstereo `+0x1438`, panel `+0x1440`; header tab `DAT_01582f9c + track*0x1474`). To build our own,
  mirror `FLui_Mixer_BuildLayoutStrips` (create → set `ctl+0x328`/`ctl[0x65]` descriptor → `ctl[3]=id` →
  handler TMethods → `SetBounds`).
- **Reorder programmatically:** call `FLui_Mixer_MoveSelectedTrack(delta, 1, 1)` after selecting a track, or drive
  the primitives (`ReorderSwapStep` to build a perm, `ApplyTrackPermutation` to commit) for an arbitrary reorder —
  it already fixes up sends/FX/PL/channel targets, so no manual matrix surgery needed.
- **Route from the bridge:** toggle a route with `FLmx_SetRouteActiveCore` / the data op `FLmx_SetRouteTo_op`, or poke
  the send-active byte `g_MixerTrackArrayPtr[src*0x1474 + dst*8 + 0x2e8]` then relayout (re/ui-win-mixer §10).

## 7. Confidence
- **HIGH:** the full `+0x13d0..+0x1440` offset table (line-by-line from the builder); the gesture dispatcher
  (`TracksPanelMGesture` 0x104/0x106 branches); the swipe-enable action; the reorder chain
  (`MoveSelectedTrack`→`ReorderSwapStep`→`ApplyTrackPermutation`) and everything it remaps; the send-button route
  toggle + that no mouse-drag route/reorder path exists.
- **MED:** the exact semantic of the secondary wheel TMethods `FUN_011a5110/5130/5160` (default/centre-value
  providers — named-by-behaviour, not renamed); the WP gesture msg ids `0x104`/`0x106` (labelled pan / tap-drag from
  their handling, not from a symbol).
- **Open (non-blocking):** whether a touch/pen build wires additional gesture ids; the `0x13e0` aux qword's purpose
  (untouched by the builder).

**G5 status: RESOLVED.** Both sub-goals delivered — the complete per-strip offset table and the full strip
drag/gesture + reorder + route path, with the (negative but useful) finding that FL exposes no free drag-to-route
or drag-reorder gesture; reorder/route are menu/keyboard/button-driven over reusable primitives.
