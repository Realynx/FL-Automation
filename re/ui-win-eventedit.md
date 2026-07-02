# ui-win-eventedit — TEventEditForm: Piano Roll + Playlist + Event Editor (one class, 3 modes)

Wave-2 concrete-window RE of **`TEventEditForm`** — the SINGLE Delphi/WP form class behind FL Studio's
**Piano Roll**, **Playlist** and **Event Editor**. Static Ghidra on `FLEngine_x64.dll`, image base `0x400000`
(all addresses absolute/Ghidra; runtime = `ghidra - 0x400000 + flEngineBase`). Builds on Wave-1 re/ui-01..06
(WP core / controls / forms catalog / skin / input / layout) and re/22-window-{host,manager}; data side cross-ref
re/11-automation-clips.md + re/21-clip-pattern-*.md + re/generated/controls-{playlist,patterns,channelrack}.md.
Focus = **REUSE**: how the editor canvas is drawn and how interactions map to data ops, so we can render/drive
the PR/Playlist visually. Ghidra tag for the whole window: **`UI_win_eventedit`**.

## TL;DR / verdict (HIGH confidence)
- **One class, three modes.** `TEventEditForm` (VMT **0xd280f8**, classRef **0xd28110**, descriptor
  `forms.eventeditform:wpform`, FormCreate **0xd40fb0**) is the Piano Roll, the Playlist AND the Event Editor.
  The **mode** is an `int` at **byte +0xB00** (0 = Event Editor, 1 = Piano Roll, 2 = Playlist).
  > **Offset correction:** Wave-1 docs say "mode @+0x160". That is the *decompiler longlong-index* `form[0x160]`
  > seen in the factory (where the var is typed `longlong*`, so `[0x160]` = byte 0x160×8 = **0xB00**).
  > `FLui_Editor_CountByMode@0xd2cea0` reads it as the raw byte `*(int*)(inst+0xb00)`, confirming **byte 0xB00**.
- **Shared factory** = `FLui_Form_CreateEventEditor@0xd2d0d0` (one recipe; mode passed in). It calls
  `FLui_CreateFormFromClassRef(&PTR_FUN_00d28110,&out)`, writes mode @+0xB00, then **branches per mode** to build
  the right canvas + toolbar set and to **destroy the `.dfm`-baked controls the mode doesn't use** (the
  `tmp=form[idx]; form[idx]=0; FUN_0040faa0(tmp)` free idiom).
- **Three canvases, one slot.** Each mode owns a different editing-canvas control, but they all funnel through one
  "active canvas" pointer at **byte +0xd34**:
  | mode | canvas field | canvas class (VMT / classRef) | created by | mouse handlers |
  |---|---|---|---|---|
  | 0 Event Editor | **+0xc24** (also present in mode 1 as the bottom event/velocity lane) | VMT 0xd26fd8 / cr **0xd26ff0** | `FUN_00d84900` | `EventPanelMouse*` |
  | 1 Piano Roll | **+0xc40** (`form[0x188]`) | VMT 0xd27520 / cr **0xd27538** | `FUN_00d85ca0` (+`FUN_00da6910`) | `PianoPanelMouse*` |
  | 2 Playlist | **+0xd04** | VMT 0xd278c8 / cr **0xd278e0** | `FUN_00d95860` | `PianoPanelMouse*`+`AUTracksPanel*` |
- **RTTI is intact.** ~120 `TEventEditForm.*` published methods are already symbol-named (the `.dfm` event
  handlers). So this pass **keeps** those names and only **tags + plate-comments** them; the **rename targets are
  the unnamed `FUN_` helpers** (canvas paint / hit-test / drag / data-op bridges) → `FLui_Editor_<Name>`.
- **Instances are tracked in a global list** `DAT_0157e980` (TList: items@+8, count@+0x10, cap@+0x14); the active
  **PR = `DAT_0157e998`**, active **PL = `DAT_0157e9a0`** (window-manager singleton slots: PR `*0x14A9B20`,
  PL `*0x14AAB88`, re/22). `FLui_Editor_FindByTarget@0xd2ce10` looks up an editor by the target/pattern id stored
  at `canvas+0x3f4`.

---

## 1. Class identity, modes, instance registry

- **Class:** `TEventEditForm`, VMT **0xd280f8**, classRef **0xd28110** (= VMT+0x18), descriptor
  `forms.eventeditform:wpform` (UStr @ byte **+0x6e8**, set in FormCreate). Family: **TChildVectorForm**-derived
  (docked editor; re/ui-03). Object name (`vtbl[0x50]`) set per mode to **"PRForm" / "PLForm" / "EventEditForm"**
  (also stored UStr @ byte **+0xbe4**).
- **Mode field:** `int @ byte +0xB00` — `0`=Event Editor, `1`=Piano Roll, `2`=Playlist. (Decompiler index
  `form[0x160]` on a `longlong*`.)
- **Factory:** `FLui_Form_CreateEventEditor@0xd2d0d0(targetId, mode, flags3, flags4)`.
  - `mode==1` always creates a new PR; otherwise it may reuse an existing editor (`FLui_Editor_FindByTarget`,
    `FLui_Editor_CountByMode`).
  - For the PR/PL re-show path it can re-use `DAT_0157e998` (active PR) and just `FormShow` + vtbl[0x260].
- **Instance registry / accessors (central glue, renamed this pass):**
  | addr | name | role |
  |---|---|---|
  | 0xd2ce10 | `FLui_Editor_FindByTarget` | scan `DAT_0157e980`; return the editor whose EventPanel(+0xc24) or PianoPanel(+0xc40) field `+0x3f4` == target id |
  | 0xd2cea0 | `FLui_Editor_CountByMode` | count editors in `DAT_0157e980` with mode `@+0xB00` == arg |
  | 0xd2cef0 | (`FLui_Editor_LoadReferenceScales`) | lazily build `DAT_0157ebb0` reference-scale list from `.FSC` "Reference scales\" (PR scale-snap); see PR lane |
- **Globals:** `DAT_0157e980` editor list · `DAT_0157e998` active PR · `DAT_0157e9a0` active PL ·
  `DAT_0157ebb0` reference scales · per-mode marker array `PTR_DAT_014a9068[mode]`.
- **View-menu / window-manager wiring** (re/22): the editors are docked children; View toggles drive
  `wm.vtbl[0x70]` ids **2 = PR**, **0 = PL**; visibility flag at the shared TCustomWPForm `+0x6a9`.

## 2. Construction recipe (FLui_Form_CreateEventEditor@0xd2d0d0)

Mode-agnostic skeleton (then a per-mode branch — see the lane sections):
```c
FLui_CreateFormFromClassRef(&PTR_FUN_00d28110, &form);   // create TEventEditForm (form-mgr + HWND)
*(int*)(form + 0xB00) = mode;                             // 0/1/2
form->flags(+0xda) |= 4;                                  // mark as a mode editor
// --- per mode: create canvas, wire handlers+scroller, destroy unused .dfm controls ---
// mode 0/1: EventPanel @+0xc24 = FUN_00d84900(&cr_0xd26ff0,1,form); wire EventPanelMouse*; scroller@+0x40c
// mode 1 : PianoPanel @+0xc40 = FUN_00d85ca0(&cr_0xd27538,1,form); FUN_00da6910(); wire PianoPanelMouse*
// mode 2 : Playlist  @+0xd04 = FUN_00d95860(&cr_0xd278e0,1,form); wire PianoPanelMouse*+touch; scroller
(*form->vtbl[0x50])(form, L"PRForm"/L"PLForm");           // object name
*(void**)(form + 0xd34) = activeCanvas;                   // the active editing canvas
// category strings form[0x18a]/[0x18b]; options-button glyph FUN_007184e0(form[0x10a], id); hints
// register: PL sets DAT_0157e9a0; all append to DAT_0157e980 (TList)
// layout: FUN_007e43b0 / FUN_007fe210 / SetBounds(vtbl[0x188]); splitter aspect from "SplitterAspect"
TEventEditForm.FormResize(form);                          // 0xd3e340
// show: FUN_007ea5d0 (FormShow) for modes 0/1; PL shown via dock path
```
`FormCreate@0xd40fb0` (the VCL OnCreate) runs first: sets descriptor, applies the main theme
(`vtbl[0x138]` with `*(mainForm+0xb18)`), seeds default colours (time-sel colour `SetTimeSelColor@0xd40bb0`),
and calls the shared sub-builders `FUN_00d584e0`, `FUN_00d82610`, `FUN_00d4cf00`, `FUN_00d3cc90` (see the
Toolbars/Layout lane).

## 3. Form-level field map (byte offsets; decompiler `form[idx]` on a `longlong*` ⇒ byte = idx×8)

| byte off | idx | field | conf |
|---:|---|---|---|
| +0x6e8 | [0xdd] | WP skin descriptor `forms.eventeditform:wpform` (inherited TCustomWPForm) | HIGH |
| **+0xB00** | [0x160] | **mode** int (0=EventEdit / 1=PianoRoll / 2=Playlist) | HIGH |
| +0xbe4 | — | object-name UStr ("PRForm"/"PLForm"/"EventEditForm") | HIGH |
| +0xc24 | — | **EventPanel** canvas (modes 0 & 1; bottom event/velocity lane) | HIGH |
| +0xc40 | [0x188] | **PianoPanel** canvas (mode 1, the note grid) | HIGH |
| +0xd04 | — | **Playlist/AUTracks** canvas (mode 2) | HIGH |
| +0xd34 | — | **active editing canvas** ptr (= one of +0xc24/+0xc40/+0xd04) | HIGH |
| +0x780 | [0xf0] | **TimePanel** (timeline ruler) | HIGH |
| +0x820 | [0x104] | horizontal **Time scroller** (TQuickScroller) | HIGH |
| +0x790 | [0xf2] | **Splitter** (aspect f32 @+0x3a8, spare @+0x3ac) | HIGH |
| +0x848 | [0x109] | play button (EE/PL "Play / pause song") | MED |
| +0x850 | [0x10a] | options menu button (PR "Piano roll options" / PL "Playlist options") | HIGH |
| +0x768 | [0xed] | a panel; font @+0x340 used to resolve note colours (PL) | MED |
| +0x788 | [0xf1] | toolbar button | MED |
| +0x798 | [0xf3] | toolbar button (both PR+PL) | MED |
| +0x7a0 | [0xf4] | button (PL "Current clip source") | MED |
| +0x7b8 | [0xf7] | **Paint/draw tool** button (PL "Paint") | MED |
| +0x7e8 | [0xfd] | **Snap** button (PR "Snap to grid") | MED |
| +0x818 | [0x103] | **note-size / zoom wheel** (PR "Change note size") | MED |
| +0x9b8 | [0x137] | PL-only sub-panel (SetShowing on mode==2) | MED |
| +0x9d0 | [0x13a] | PR-only sub-panel (SetShowing(1) in PR) | MED |
| +0x9e8 | [0x13d] | sub-panel hidden in EE/PL | LOW |
| +0xa2c | — | per-mode helper obj (PR `FUN_00f6d640(&0xf611e8)`, PL `FUN_00f6f770(&0xf61588)`) | MED |
| +0xb28 | [0x165] | toolbar **Snap value** (`FUN_0057dc30(...,"Snap",..)`) | MED |
| +0xb31/+0xb32/+0xb33 | — | MagicLasso / LeftResizing / ShiftMouseWheelNudging bools | MED |
| +0xc1e | — | KeepLabelsOnScreen bool | MED |
| +0xc50/+0x166 | [0x166] | **SwapPanels** bool (swap grid/picker; config "SwapPanels") | MED |
| (cat) | [0x18a]/[0x18b] | category strings ("piano roll"/"playlist"/"event editor" ; "note"/"clip"/"event") | HIGH |

**Per-canvas fields (on the EventPanel/PianoPanel/Playlist control object):** `+0x3f4` target/pattern id ·
`+0x40c` its TQuickScroller · `+0x3f0` mode/var · `+0x418` range (e.g. key range 0x83 in PR) · `+0x608` data
source ptr · `+0x144/+0x154/+0x164` mouse-down/move/up TMethods · `+0x224/+0x234` leave/enter.

---

## 3a. The shared time-grid canvas (one base class for all 3 editors)  — HIGH

The single most important reuse fact: **all three editing canvases descend from ONE base time-grid control**,
and the Playlist grid is literally a subclass of the Piano-Roll note grid:

```
TFLEditorCanvas (base)        VMT 0xd26fd8 cr 0xd26ff0   ctor FLui_EditorCanvas_BaseCtor@0xd83160
   = the EventPanel / value-lane class (used directly for form+0xc24)
   └ TFLNoteGrid (PianoPanel)        VMT 0xd27520 cr 0xd27538  ctor FLui_EditorCanvas_NoteGridCtor@0xd85ca0
        └ TFLPlaylistGrid (AUTracksPanel) VMT 0xd278c8 cr 0xd278e0 ctor FLui_Editor_CreatePlaylistGrid@0xd95860
```
(Class names `TFL*` synthesized — FL kept no RTTI string for the canvas classes.) Ctor chain proven:
`FLui_Editor_CreatePlaylistGrid` calls `FLui_EditorCanvas_NoteGridCtor`, which calls the base
`FLui_EditorCanvas_BaseCtor`, which calls the WP base `FUN_007ffbd0` (re/ui-02). So PR/PL/EE share the same
scroll/zoom/select/hit-test machinery; only the **item type** (notes vs clips vs automation points) differs.

**Base-canvas ctor (`0xd83160`) builds, per instance:** its own `TQuickScroller @ canvas+0x40c` (skin
`forms.eventeditform.controls.scrollbar:tquickscroller`, page 300), parent-form back-ptr `canvas[0x73]` (byte
+0x398), the resize handler `canvas+0x194 = FLui_EditorCanvas_OnResize@0xd844c0`, then sub-init `FUN_00d83510`.
The note-grid ctor adds the **item collections** `canvas[0xc2..0xc6]` (TLists: visible/selection/scratch),
the **group/colour filter** `canvas[0x86]` (0xffffffff = all), display sub-objects (`+0x64c/+0x654/+0x65c`),
and the data-source accessor callback `FUN_00d953e0` (reads `canvas+0x57c`). The playlist ctor additionally
wires the main-form **automation-point** menu items (`FLac_UIChangePointCurve`, `FLac_UISetPointValue`,
`TFruityLoopsMainForm_EEAUMenuOpenWindow`) — "AU" = audio/automation.

**Canvas ↔ data binding = `FLui_Editor_BindDataSource@0xd3dc90`** (called from the factory + on pattern/arr
switch). It stores the model object at **`canvas+0x57c`** and the per-mode block at `form+0xd5c`:
| mode | `canvas+0x57c` ← | `form+0xc2c` | `form+0xd5c` |
|---|---|---|---|
| 1 PR | `FLpat_GetOrCreateNoteRecorder(chan)` (note list) | ParamRecorder | pattern block `0x14aa0c8 + pat*0xc0 + 0x30` |
| 2 PL | `FLpl_GetCurrentArrangement()+0x14` (clip list) | — | arrangement `+0x21c48` |
| 0 EE | — | `FLpat_GetOrCreateParamRecorder()` (events) | pattern block (as PR) |

**Coordinate model (HIGH):** horizontal `time = round((canvas+0xafc scrollX + mouseX) × form+0xaec)` where
`form+0xaec` = ticks/seconds-per-pixel (h-zoom). Vertical: PR `y→pitch` (rows `canvas+0x418`), PL `y→track`,
EE `y→value` (range `form+0xc34..+0xc38`, lane height `form+0xa6c`). **Snap** = `FLui_Editor_SnapTime@0xd583c0`
→ `FLui_Editor_SnapTimeToGrid@0xd580f0` (timebase-aware via `FUN_00f6fd50/fc60/fc90` ppq/step; snap mode
`form+0xb2c`, modes 0xd/0xe = snap-to-event, 1/0x100 = line snap; step calc `FLui_Editor_CalcSnapStep@0xd57c30`).

**Active-canvas + select/redraw helpers (renamed this pass):** `FLui_Editor_GetActiveCanvas@0xd45dd0`
(`(+0xd04)?:(+0xc40)`), `FLui_Editor_DeselectAll@0xd58740`, `FLui_Editor_SelectByGroup@0xd59500`,
`FLui_Editor_PushSelectionUndo@0xd58650`, `FLui_Editor_InvalidateCanvases@0xd585f0`,
`FLui_Editor_RecountGrids@0xd45d90`.

### 3b. Unified time-grid mouse handling (PR + PL share it)
The factory wires the **same** `PianoPanelMouseDown@0xd5a9b0` (+Move `0xd6b8d0`, +Up `0xd64bb0`,
+Leave `0xd6c160`) onto BOTH the PR note grid AND the PL clip grid. It is one ~68 KB dispatcher that branches on
`form+0xb00` (mode) + `form+0xa00` (tool) + mouse button, hit-tests via the item list / recorder, then calls the
data op (see §4/§5/§10). Pixel→time/vert per §3a; right-click → `FLmenu_ShowPopup`; add-channel →
`FLcr_OpenAddChannelPicker`. **The EventPanel/value-lane has its own** `EventPanelMouseDown@0xd49ec0` (§6).

### 3c. Canvas rendering + hit-testing — the draw path (the prime reuse surface)  — HIGH/MED
This is the shared machinery that **paints items and resolves the mouse to an item+zone** — reusable to render
notes/clips ourselves or to drive a custom overlay. All on the canvas object (PR/PL/EE share it).

- **Paint pipeline:** `FLui_EditorCanvas_Paint@0xd95410` (canvas **vtbl+0x1b8**) →
  `FLui_EditorCanvas_AcquireGfx@0xd847e0` (skin canvas `+0x304` → `+0xa0` → gfx ctx `canvas+0x3a0`) →
  `FLui_Editor_DrawGridBackground@0xd450d0` (bars/beats/rows) → `FLui_EditorCanvas_DrawItems@0xd86700` →
  `FLui_EditorCanvas_DrawSelectionOverlay@0xd93550` + `FLui_Editor_DrawCanvasOverlay@0xd45d50`.
- **Item rendering:** `FLui_EditorCanvas_DrawItems@0xd86700` iterates the **draw cache `canvas+0x574`**
  (rebuilt by `FLui_EditorCanvas_RebuildItemCache@0xd90e70` before paint/zoom); per item:
  rect = `FLui_EditorCanvas_ItemToRect@0xd86390` (uses h-zoom `form+0xaec`, scrollX `canvas+0xafc`, row/track
  height), **fill colour = `item+0xc`**, **velocity/value→alpha = `item+0x14`** (float, scaled ×224 into the
  high byte), drawn as a rounded-rect through the gfx vtbl. → To render our own notes/clips: build the same
  `{rect,colour,value}` cache, or call these against a gfx ctx.
- **Hit / hover:** `FLui_EditorCanvas_ResolveHoverTarget@0xd8cf40` scans the **hit-rect cache `canvas[0xc2]`
  (byte +0x610)** (entry: `+0`=id, `+8`=record ptr, `+0xc/+0x14`=x0/x1, `+0x10/+0x18`=y0/y1), then sets the
  **hit item id `canvas+0x588`** (`canvas[0xb1]`; -1/0x7fffffff = none) and the **hit-zone `canvas+0x5b4`**
  (`7`=move-body, `0xc9`/`0x200`=resize edge, `0xcf`=left-resize, `0x2000`=empty/area, `0x15`/`0x2712`=PL
  variants), and the cursor. Low-level point→item = engine `FUN_011cf650`, wrapped by
  `FLui_EditorCanvas_HitTest@0xd95390` (range-checked vs `canvas+0x3f4/+0x3f8`).
- **Gesture model:** `PianoPanelMouseDown` reads the hit-zone and starts a gesture, storing the **gesture id at
  `canvas[0x80]`** (`0xb`=move, `0xc`/`0xd`=resize, `0xe`=select, `0x32`=lasso), wrapping the edit in an undo
  step (`FUN_00f37ec0`); marquee armed via `FLui_Editor_ArmMarquee@0xd6d410` (helper `form+0x828`). Touch/pinch
  zoom + wheel routed through `FLui_EditorCanvas_GestureMsg@0xd83ba0` (vtbl+0xe0) which re-dispatches Mouse*.
- **Item caches on the canvas:** `+0x574` draw cache · `+0x610` (`canvas[0xc2]`) hit-rect cache ·
  `+0x618/+0x620/+0x630` selection/aux lists · `+0x57c` data model (§3a) · `+0x64c` note/clip display model
  (`FUN_00da7350(&PTR_FUN_00d27308)`, VMT 0xd272f0) · `+0x588` hit id · `+0x5b4` hit zone · `[0x80]` gesture.
- **Key audition (PR):** `FLui_Editor_HighlightKey@0xd58c50` paints the pressed key on the keyboard gutter
  (driven by `MIDIKbNoteOn/Off`).

---

<!-- LANE: PIANO ROLL -->
## 4. Piano Roll mode (mode 1)  — HIGH/MED

- **Canvas = PianoPanel @ form+0xc40** (`form[0x188]`), class `TFLNoteGrid` (VMT 0xd27520), ctor
  `FLui_EditorCanvas_NoteGridCtor@0xd85ca0` + post-init `FUN_00da6910`. `canvas+0x3f0 = 1`; key range
  `canvas+0x418 = 0x83` (131 keys).
- **Data:** `PianoPanel+0x57c = FLpat_GetOrCreateNoteRecorder(currentChannel)`; per-note automation
  `form+0xc2c = FLpat_GetOrCreateParamRecorder`.
- **Input:** the unified `PianoPanelMouseDown` (§3b). Notes added/selected/moved/resized/sliced/muted by the
  active tool (`form+0xa00`) via the **`FUN_00f6*` NoteRecorder op family** (add/move/del/resize/select;
  `FUN_00f6de50` = clear-selection). `y→MIDI key`, `x→time` (§3a).
- **On-screen MIDI keyboard** (left gutter): `MIDIKbMouseDown@0xd829f0`/Move/Up, `MIDIKbNoteOn@0xd82e20` /
  `MIDIKbNoteOff@0xd82eb0` — audition the clicked key through the channel.
- **Velocity / event lane** = the EventPanel @ form+0xc24 below the grid (§6) — same value-lane class.
- **Note size / v-zoom:** note-size wheel `form[0x103]` ("Change note size"); vertical key-height zoom via
  `VZoomSelectChange@0xd82350` (widget mechanics §7). PR keyboard panel `form[0x13a]` shown (SetShowing 1).
- **Note colours / MIDI channels:** 7 colour names built in the factory (`PTR_DAT_014ab888`); selector
  `NoteColSelectChange@0xda14d0` / `NoteColSelectPaintCell@0xda1550`.
- **Ghost notes:** `GhostNotesBtnClick@0xd53b60`, `GhostMenuPopup@0xd52e10`, `GhostMenuItemClick@0xd548c0`.
- **Scale-snap / highlight:** `ScaleSnapBtnBeforePopup@0xd41b60`, `ScaleSnapBtnMouseUp@0xd41bf0`; reference
  scales loaded by `FLui_Editor_LoadReferenceScales@0xd2cef0` (`DAT_0157ebb0`).
- **PR tool dialogs** (Quantize/Strum/Arp/Chord/…): separate modal forms — `TPRBaseToolForm` family, re/ui-03 §4.3.
- **Reuse:** add a note = NoteRecorder add on `PianoPanel+0x57c` then `FLui_Editor_InvalidateCanvases(form)`;
  iterate that recorder to read notes; pitch↔y / time↔x via §3a.

<!-- LANE: PLAYLIST -->
## 5. Playlist mode (mode 2)  — HIGH/MED

- **Main canvas = AUTracksPanel @ form+0xd04**, class `TFLPlaylistGrid` (VMT 0xd278c8) which **derives from the
  PianoPanel note grid** (ctor `FLui_Editor_CreatePlaylistGrid@0xd95860`). `canvas+0x3f0 = 3`. Render = the
  ~11 KB `AUTracksPanelPaint@0xd7cec0` (track lanes + clips + automation).
- **Data:** `AUTracksPanel+0x57c = FLpl_GetCurrentArrangement()+0x14` (clip list); `form+0xd5c =
  arrangement+0x21c48`.
- **Input:** the unified `PianoPanelMouseDown` (§3b) does clip place/select/move/resize/slice via the `FLpl_*`
  ops (`y→track`, `x→time`). The dedicated `AUTracksPanel{MouseDown@0xda25a0,Move@0xda4140,Up@0xda4c80,
  Wheel@0xda4cf0,Paint@0xd7cec0,Touch@0xd7fc20}` methods are the `.dfm`-published handlers of the playlist
  track-strip (wired via the form's published-method table @0x1930f04). *(Exact split unified-vs-strip = MED.)*
- **Clip ops (data bridges, §10):** `FLpl_GetClipByIndex`, `FLpl_GetClipTrack`, `FLpl_EncodeClipTrackField`,
  `FLpl_GetClipSourceRange`/`FLpl_SetClipSourceRange`, `FLpl_RecountActiveClips`, `FLpl_SetTimeSelection`.
- **Clip-source picker** (left): `PickerPanelMouseDown@0xd4b6f0`, Move/Up/Leave/Wheel,
  **`PickerPanelAfterDrop@0xd4c160`** (drop a picked pattern/sample onto the grid), `CheckDragOver@0xd4c6c0`,
  `PickerPanelVZoomSelectChange@0xd4c870`.
- **Clip controls:** `PLClipSelectChange@0xda12b0`/MouseUp/`PaintCell@0xda13c0`; `ClipStepBtnClick@0xda17a0`,
  `ClipZeroCrossingBtnClick@0xda17f0`.
- **Arrangements:** `OnArrangementClick@0xd3bf40`, `ArrangementSelectBeforePopup@0xd3c6a0`/MouseWheel,
  `CreateVariantsMenuClick@0xd38330`.
- **Per-track audio I/O:** `PLTrackInputSelectMenuPopup@0xda3650`, `PLTrackMonitorMenuPopup@0xda3d40` /
  OffMenuClick@0xda4040 / ExtInputOnlyMenuClick@0xda3c70 (record arm / monitor per playlist track).
- **Clip fades:** `PLFadeHandleMenuPopup@0xda1f10` / Click / OpenWindow / CloseMenu.
- **Add instrument/track:** `AddInstrBtnBeforePopup@0xd6eab0`, MouseDown@0xd6eb00, MouseUp@0xd6eb40.
- **Scrollers:** AudioScroller (v-track) `AudioScrollerChange@0xd49230`; pattern-picker `PatternScrollerChange@
  0xd49280`. PL-only sub-panel `form[0x137]`; audio helper `form+0xa2c=FUN_00f6f770`.
- **Reuse:** place a clip = `FLpl_*` add / `FLpl_SetClipSourceRange` on the arrangement clip list
  (`canvas+0x57c`), set track via `FLpl_EncodeClipTrackField`, then `FLpl_RecountActiveClips` +
  `FLui_Editor_InvalidateCanvases`. Clip struct + a known clip/pattern bug: re/21-clip-pattern-{evidence,bug}.md.

<!-- LANE: SHARED CANVAS + TIMELINE + TOOLBARS + LAYOUT -->
## 6. Event Editor mode (mode 0) + the value-lane (also the PR bottom strip)  — HIGH/MED

- **Canvas = EventPanel @ form+0xc24**, the base time-grid class (VMT 0xd26fd8), created for BOTH EE and PR
  (in PR it is the bottom **velocity/automation value lane**; in EE it is the whole window).
- **Input `EventPanelMouseDown@0xd49ec0` (HIGH):** right-click → context menu `FUN_00d49c40`; else
  `x→time = round((canvas+0xafc + x) × form+0xaec)`, `y→value` clamped to `form+0xc34..+0xc38` over lane height
  `form+0xa6c` (`FUN_006277b0`); pushes undo ("event edit"/"%s edit"), stores anchor `form+0xd84=(time,value)`,
  then `EventPanelMouseMove` applies; PR tool 7 / shift = line/ramp draw (`canvas+0x400=0xe`); falls through to
  `TimePanelMouseDown` for time-selection (`FLpl_SetTimeSelection`).
- **Data:** `form+0xc2c = FLpat_GetOrCreateParamRecorder` (automation points; point/curve model =
  re/11-automation-clips.md `FLac_*`). Play button `form[0x109]` (`EEPlayBtnClick@0xd49e30`); event scroller
  `EventScrollerChange@0xd492d0`, knob `EventScrollerSetKnobWidth@0xd41870`.
- **Reuse:** the cleanest automation surface — edit through ParamRecorder + `FLac_UISetPointValue` /
  `FLac_UIChangePointCurve`, then `FLui_Editor_InvalidateCanvases`.

## 7. Timeline + scrollers + zoom  — HIGH/MED

- **Timeline ruler = TimePanel @ form+0x780** (`form[0xf0]`): `TimePanelPaint@0xd47b20` (bars/beats),
  `TimePanelMouseDown@0xd54ae0` (playhead / time-selection), Move/Up/Wheel/Touch/Gesture
  (`0xd55420/0xd56ac0/0xd56e20/0xd6c750/0xd54a00`); time-sel colour `SetTimeSelColor@0xd40bb0`. Ruler event
  markers: `TLEventPanelPaint@0xd6e050`, DblClick/MouseDown/MouseUp.
- **Scroller wiring pattern (shared):** every canvas owns a `TQuickScroller @ canvas+0x40c` (re/ui-02 §7.4),
  wired by poking TMethods on it — **Change `code+0x668 / Self+0x670`**, **Knob `code+0x688 / Self+0x690`**:
  | scroller | Change | Knob |
  |---|---|---|
  | horizontal **Time** (`form[0x104]`, +0x820) | `TimeScrollerChange@0xd49130` | `TimeScrollerPaintKnob@0xd48e30` |
  | PR vertical (PianoPanel+0x40c) | `PianoScrollerChange@0xd491a0` | `PianoScrollerSetKnobWidth@0xd41880` |
  | PL vertical (AUTracksPanel+0x40c) | `AudioScrollerChange@0xd49230` | (base) |
  | EE (EventPanel+0x40c) | `EventScrollerChange@0xd492d0` | `EventScrollerSetKnobWidth@0xd41870` |
  | PL pattern picker | `PatternScrollerChange@0xd49280` | — |
  Page-size `FLui_Ctl_Scroller_SetPageSize`; position `FLwp_SetControlValue(canvas+0x40c, pos)`.
- **Zoom:** horizontal = `form+0xaec` (sec/px) driven by the Time-scroller knob + `ZoomBtnMouseUp@0xd80750`,
  `EventZoomSelectChange@0xd82070`. Vertical = the **VZoomSelect** widget: `VZoomSelectMouseDown@0xd4d9c0`,
  MouseUp, **`VZoomSelectPaint@0xd4d9e0`**, Release@0xd4ddd0 (per-mode: PR key-height `@0xd82350`, PL
  track-height, EE value-height); picker has `PickerPanelVZoomSelect*`.
- **Reuse:** scroll/zoom programmatically = set scroller value and/or `form+0xaec`, then invalidate.

## 8. Toolbars + tool / snap selectors  — MED

Toolbar = a row of WP buttons/selectors (`TQuickEditToolBar*` composites, re/ui-02 §7.4 note; family @0xb4fdb0)
laid out by **`FLui_Editor_LayoutToolbar@0xd3d150`**, which also sets **per-mode visibility** of each child
(e.g. `+0x840` PR-only, `+0x808` PL-only, `+0x7f0` EE/PR, `+0x838/+0x8e8/+0x9c8` PR-only) and places them L→R
via `FUN_00d3d0e0`. Active **tool @ form+0xa00**; `FLui_Editor_UpdateToolCursor@0xd4cf00` maps it to a cursor id
(`form+0xa5c`) on the time/event/grid panels.

| control | handler(s) | role |
|---|---|---|
| Tool selector | `SelectToolBtnClick@0xd4d290`, `SelectBtnMouseUp@0xd41dd0` | set `form+0xa00` (draw/paint/select/slice/…) |
| Snap | `SnapBtnBeforePopup@0xda1860`, `SnapBtnMouseUp@0xda18c0` | choose/cycle `form+0xb2c` |
| Stamp / Slide / Stretch | `StampBtnBeforePopup@0xd6d3b0`, `SlideBtnClick@0xd6e060`, `StretchModeMenuPopup@0xd4d4c0` | chord stamp / slide / time-stretch mode |
| Tools menu | `ToolsBtnBeforePopup@0xda1930`, `ToolsBtnMouseDown@0xda1990` | PR/PL tools menu |
| Options | `MenuBtn2BeforePopup@0xd7c320` (`form[0x10a]`) | per-mode options menu |
| Loop | `LoopBtnClick@0xd705f0`, `LoopBtnBeforePopup@0xd70590` | loop/playback |
| Color | `ColorBtnBeforePopup@0xda1c40`, MouseDown@0xda1c60, MouseWheel@0xda1db0 | current item colour |
| Channel select (EE) | `ChanSelectBeforePopup@0xda2100`, MouseDown/Wheel | EE target channel |
| Controller select (EE) | `CtrlSelectBeforePopup@0xda1e10` (+Mouse*) | EE target controller/param |

Shared FormCreate sub-builders: `FUN_00d584e0` (`FLui_Editor_SetModeBgColor`, bg @form+0xb08), `FUN_00d82610`,
`FUN_00d3cc90`, `FLui_Editor_UpdateToolCursor@0xd4cf00`, `FUN_00d805a0`, `FUN_00d36c50`.

## 9. Splitter / layout / form-level handlers  — MED

- **Splitter** between the two panels = `form[0xf2]` (byte +0x790): `SplitterChange@0xd401a0`,
  `SplitterChanging@0xd40220`, `SplitterPaint@0xd40350`; persisted aspect `+0x3a8` ("SplitterAspect"), spare
  `+0x3ac`; **SwapPanels** `form[0x166]` (`FUN_00d3e6d0`, `FUN_007d0d90`).
- **Layout/resize:** `FormResize@0xd3e340` relayouts canvases + toolbar + scrollers; base canvas
  `FLui_EditorCanvas_OnResize@0xd844c0`; helpers `FUN_007e43b0`/`FUN_007fe210`/`FUN_00d3e740`/`FUN_00d3e5a0`.
- **Form-level:** `FormPaint@0xd81d70`, `FormMouseWheel@0xd81620`, `FormActivate@0xd56e80`,
  `FormKeyDown@0xd503b0`, `FormKeyUp@0xd513d0`, `FormClose@0xd414c0`, `FormDestroy@0xd40c90`,
  `FormDeactivate@0xd40c40`, `MouseBtnTimerTimer@0xd6d4e0`.

## 9a. Reuse cookbook (drive the editor from our bridge)
- **Get the live window:** PR `*0x14A9B20` (`DAT_0157e998`), PL `*0x14AAB88` (`DAT_0157e9a0`); or create via
  `FLui_Form_CreateEventEditor(targetId, mode, …)`.
- **Active canvas + model:** `c = FLui_Editor_GetActiveCanvas(form)`; `model = *(c+0x57c)`.
- **Add/edit content:** call the §10 data op on `model` (NoteRecorder add / `FLpl_SetClipSourceRange` /
  `FLac_UISetPointValue`), then `FLui_Editor_InvalidateCanvases(form)` (+ `FLpl_RecountActiveClips` for PL).
- **Selection:** `FLui_Editor_DeselectAll(form)`, `FLui_Editor_SelectByGroup(c, group)`, undo
  `FLui_Editor_PushSelectionUndo(form)`.
- **Coords/overlays:** `time = round((c+0xafc + x) × form+0xaec)` and inverse; snap `FLui_Editor_SnapTime`.
- **Scroll/zoom:** `FLwp_SetControlValue(c+0x40c, pos)` / set `form+0xaec`, then invalidate.
- **Tool/snap:** set `form+0xa00` then `FLui_Editor_UpdateToolCursor(form)`; set `form+0xb2c` for snap.
- **Data-op bridges (cross-area, NOT renamed):** clips `FLpl_GetClipByIndex/GetClipTrack/EncodeClipTrackField/
  Get|SetClipSourceRange/RecountActiveClips/SetTimeSelection/GetCurrentArrangement`; notes `FLpat_GetOrCreate
  NoteRecorder` + `FUN_00f6*` family (`FUN_00f6de50` clear-sel; `FUN_00f6fd50/fc60/fc90` timebase/ppq/step;
  `FUN_00f702a0/f70300` snap-to-event); automation `FLpat_GetOrCreateParamRecorder` + `FLac_UIPointEditDispatch/
  UIChangePointCurve/UISetPointValue`; menus `FLmenu_ShowPopup`; add channel `FLcr_OpenAddChannelPicker`.

---

## 10. Ghidra annotations made this pass
- **Tag `UI_win_eventedit`** created (with description) and attached to **163** functions: all ~120
  `TEventEditForm.*` published methods + the factory + every renamed helper.
- **Central / registry renames:** `FLui_Editor_FindByTarget@0xd2ce10`, `FLui_Editor_CountByMode@0xd2cea0`,
  `FLui_Editor_LoadReferenceScales@0xd2cef0`.
- **Renamed 18 unnamed helpers** (canvas/snap/select/layout/ctors): `FLui_Editor_BindDataSource@0xd3dc90`,
  `FLui_Editor_SetModeBgColor@0xd584e0`, `FLui_Editor_SnapTime@0xd583c0`, `FLui_Editor_SnapTimeToGrid@0xd580f0`,
  `FLui_Editor_CalcSnapStep@0xd57c30`, `FLui_Editor_InvalidateCanvases@0xd585f0`,
  `FLui_Editor_DeselectAll@0xd58740`, `FLui_Editor_PushSelectionUndo@0xd58650`,
  `FLui_Editor_SelectByGroup@0xd59500`, `FLui_Editor_GetActiveCanvas@0xd45dd0`,
  `FLui_Editor_RecountGrids@0xd45d90`, `FLui_EditorCanvas_BaseCtor@0xd83160`,
  `FLui_Editor_CreateEventPanel@0xd84900`, `FLui_EditorCanvas_NoteGridCtor@0xd85ca0`,
  `FLui_Editor_CreatePlaylistGrid@0xd95860`, `FLui_Editor_LayoutToolbar@0xd3d150`,
  `FLui_Editor_UpdateToolCursor@0xd4cf00`, `FLui_EditorCanvas_OnResize@0xd844c0`. Plus
  `FLui_Editor_ApplyVZoom@0xd82150`, `FLui_Editor_FillTimeRange@0xd474d0`,
  `FLui_Editor_PR_RefreshScaleHighlight@0xda6910`, `FLui_Editor_SetActiveTool@0xd4d2b0`.
- **Renamed 13 canvas render/hit-test helpers** (§3c, the draw path):
  `FLui_EditorCanvas_Paint@0xd95410`, `FLui_EditorCanvas_DrawItems@0xd86700`,
  `FLui_EditorCanvas_ItemToRect@0xd86390`, `FLui_EditorCanvas_AcquireGfx@0xd847e0`,
  `FLui_EditorCanvas_DrawSelectionOverlay@0xd93550`, `FLui_EditorCanvas_ResolveHoverTarget@0xd8cf40`,
  `FLui_EditorCanvas_HitTest@0xd95390`, `FLui_EditorCanvas_RebuildItemCache@0xd90e70`,
  `FLui_EditorCanvas_GestureMsg@0xd83ba0`, `FLui_Editor_HighlightKey@0xd58c50`,
  `FLui_Editor_DrawGridBackground@0xd450d0`, `FLui_Editor_DrawCanvasOverlay@0xd45d50`,
  `FLui_Editor_ArmMarquee@0xd6d410`. (38 renames total: 3 registry + 22 helpers + 13 render/hit.)
- **Plate-commented ~50** functions: the factory + FormCreate (mode byte-offset correction) + the renamed
  helpers + the key canvas/timeline/toolbar handlers (PianoPanel/EventPanel/AUTracksPanel/TimePanel mouse+paint,
  scrollers, MIDIKb, snap/tool, picker, arrangement, FormResize). Program saved.
- **Cross-area finds (noted, NOT renamed):** the `FUN_00f6*` NoteRecorder op family; `FUN_00d6c1f0` (PL
  touch/aux); `FUN_00d49c40` (EE context menu); FormCreate sub-builders `FUN_00d82610`/`FUN_00d3cc90`/
  `FUN_00d805a0`/`FUN_00d36c50`/`FUN_00d3d0e0`; the data bridges in §9a.

## 11. Confidence + open items
- **HIGH:** one class / three modes; **mode int @ byte 0xB00** (corrects ui-03's "+0x160" longlong-index); the
  factory recipe + per-mode canvas map (EventPanel +0xc24 / PianoPanel +0xc40 / Playlist +0xd04, active @+0xd34);
  the shared base→note-grid→playlist-grid canvas class chain; the canvas↔data binding at `canvas+0x57c` via
  `FLui_Editor_BindDataSource`; the unified `PianoPanelMouseDown` for PR+PL; the pixel↔time model
  (`form+0xaec` + `canvas+0xafc`); snap (`form+0xb2c`) + tool (`form+0xa00`); the scroller wiring pattern;
  instance registry (`DAT_0157e980` + active PR/PL globals); the data-op bridge set; **the canvas draw + hit-test
  path** (§3c: Paint → DrawItems → ItemToRect, draw cache `canvas+0x574`, hit-rect cache `canvas+0x610`,
  hit id `canvas+0x588`, hit-zone `canvas+0x5b4`, gesture `canvas[0x80]`).
- **MED:** exact boundary between the unified grid handler and the `.dfm` `AUTracksPanel*` per-strip handlers
  (both exist — unified wired in the factory, AUTracksPanel* via the published-method table @0x1930f04; likely a
  separate track-header strip, not byte-confirmed); identity of each `+0x7xx/+0x8xx/+0x9xx` toolbar child slot
  (laid out by `FLui_Editor_LayoutToolbar`, individually un-pinned); canvas item-list roles `canvas[0xc2..0xc6]`;
  VZoom per-mode field offsets.
- **Gaps / not deeply read:** `PianoPanelMouseMove/Up` (~68 KB family mapped by call-graph, not line-by-line);
  `AUTracksPanelPaint` (~11 KB render, characterized not traced); the `FUN_00f6*` NoteRecorder op semantics
  (data-side — belongs to a patterns wave). `re/27`/`re/28` referenced by the brief **do not exist** in this
  repo; used re/11 + re/21 + re/generated/controls-{playlist,patterns} instead. No live FL (a few drag/resize
  data paths inferred from the static dispatch, not runtime-confirmed).
