# ui-02 — Concrete WP control TYPES catalog (overnight UI RE, Wave 1)

Per-control-type catalog of FL Studio's reusable UI widgets — the **"WP" (WidgetPainter)** control
framework layered over Delphi VCL in `FLEngine_x64.dll` (image base `0x400000`; all addresses below are
Ghidra addresses at that base — runtime = `ghidra - 0x400000 + flEngineBase`). Focus = **REUSE**: the exact
create → configure → wire-handler sequence so we can drop each control into our own native FL UI.

Builds on re/13-wp-custom-ui.md (the original WP recipe), re/06-control-api-map.md, re/22-window-host.md
(chrome control tokens @0x761d00). Cross-area finds (forms / skin / input / layout / menus) are **noted, not
renamed** — see §10.

## TL;DR
- Every visible widget is a `TQuick*` (or `TWP*` / `TSliderKnob`) class. They all descend from a small WP base
  layer (`TWPControl` → `TQuickGraphicControl` / `TQuickCustomControl`). **One generic constructor**
  (`FLwp_CreateControl(&VMT,1,0)@0x717A90`) builds any of them; the **VMT picks the class** and a **skin
  descriptor string** (`controls.x;forms.y:typename`) picks the look + behavior variant.
- **Value** = `int @ctrl+0xc4` (set `FLwp_SetControlValue@0x5D0D10`, read = peek). Knobs/wheels store value at
  `ctrl+0x450` instead, with a min/max range via `vtbl[0x1e0]`.
- **Events** = Delphi `TMethod` pairs `{code@off, data(Self)@off+8}` poked directly on the instance — so we
  install our own handlers by writing the slot (see §5 for the full offset map).
- **The class is data, not code**: control behavior lives in **VMT virtual slots** + shared base functions, not
  in many per-class named functions. So "how to use control X" = the create recipe (§4) + X's VMT/flags/value
  field/event offsets, documented per type in §7.

---

## 1. The WP control class family (from re/generated/fl-classes.txt)

Columns: `VMT (ghidra) | instSize | parent VMT`. The **class reference** passed to the constructor is
`VMT + 0x18` (e.g. button create uses `&LAB_00715520` = `0x715508 + 0x18`).

### Interactive controls
| class | VMT | size | parent VMT | role | group |
|---|---|---|---|---|---|
| `TQuickBtn` | 0x715508 | 0x4e8 | 0x714658 (TCustomQuickBtn) | push / toggle / icon button | §7.1 |
| `TQuickCheckBox` | 0x715b10 | 0x4e8 | 0x714658 (TCustomQuickBtn) | checkbox / 2-state toggle | §7.1 |
| `TQuickFocusBtn` | 0x716a20 | 0x520 | 0x7fcb90 (WP win-ctl base) | keyboard-focusable button | §7.1 |
| `TSliderKnob` | 0x6babe0 | 0x3a8 | 0x7fbca8 (TQuickGraphicControl†) | rotary knob / fader | §7.2 |
| `TKnobInput` | 0xceb0c0 | 0x80 | 0xce85f0 | numeric value-entry knob | §7.2 |
| `TVectorWheel` (the "wheel") | 0x7a0c88 | 0x538 | 0x7a0520 | value wheel / rotary (vol/pan) | §7.2 |
| `TVectorDigiWheel` (digiwheel) | 0x7a19c8 | 0x4e8 | 0x79f348 | digit/spinner wheel (mixer-track select) | §7.2 |
| `TQuickGauge` | 0x7a5bd0 | 0x3d8 | 0x7fbca8 | level / progress gauge | §7.2 |
| `TQuickBitTable` | 0x77a328 | 0x3c8 | 0x7fbca8 | step / bit grid (step seq) | §7.2 |
| `TQuickEdit` | 0x7466a0 | 0x730 | 0x745790 (TQuickCustomMemo†) | single-line text edit | §7.3 |
| `TQuickMemo` | 0x746298 | 0x730 | 0x745790 | multi-line text editor | §7.3 |
| `TQuickCustomMemo` | 0x745840 | 0x730 | 0x779078 | edit/memo base | §7.3 |
| `TQuickCombo` | 0x779ad0 | 0x580 | 0x714658 (TCustomQuickBtn) | combobox / dropdown | §7.3 |
| `TQuickStringGrid` | 0x74cfe8 | 0x6f0 | 0x779078 | editable string grid / table | §7.3 |
| `TQuickStringGridHeader` | 0x74c7f8 | 0x508 | 0x7fcb90 | grid column header | §7.3 |
| `TQuickTree` | 0x973c80 | 0xf00 | 0x92edb8 | tree view (browser tree) | §7.4 |
| `TQuickCustomTabSelector` | 0x7405a8 | 0x590 | 0x7a4e58 | tab-strip base | §7.4 |
| `TQuickTabSelector` | 0x740de8 | 0x5a0 | 0x7404f8 | tab strip / pager | §7.4 |
| `TQuickSheetSelector` | 0x7412d8 | 0x5b0 | 0x7404f8 | sheet/page selector | §7.4 |
| `TQuickSheet` | 0x741748 | 0x558 | 0x7a4e58 | a tab page / sheet | §7.4 |
| `TQuickScroller` | 0x6ccd60 | 0x708 | 0x7a4e58 | scrollbar | §7.4 |
| `TQuickScrollerBtn` | 0x6cdb30 | 0x4e8 | 0x715458 (QuickBtn base) | scrollbar arrow button | §7.4 |
| `TQuickScrollerHandle` | 0x6cdeb8 | 0x4f0 | 0x715458 | scrollbar thumb | §7.4 |
| `TQuickScrollBoxScroller` | 0x778000 | 0x708 | 0x6d4650 | scrollbox scroller | §7.4 |
| `TQuickMIDIKb_Visual` | 0xb47788 | 0x3d0 | 0x7fbca8 | on-screen MIDI keyboard (visual) | §7.2 |
| `TQuickMIDIKb` | 0xb47f78 | 0x400 | 0xb476d8 | on-screen MIDI keyboard | §7.2 |
| `TQuickMIDIKbEx`/`TQuickVectorMIDIKb`/`TQuickVectorMIDIKbRange` | 0xb48368 / 0xb48800 / 0xb48d40 | — | — | MIDI-kb variants | §7.2 |

### Static / container / chrome controls
| class | VMT | size | parent VMT | role | group |
|---|---|---|---|---|---|
| `TQuickLabel` | 0x7a3d40 | 0x3c8 | 0x7fbca8 | static caption / text label | §7.3 |
| `TQuickPaintBox` | 0x7a3258 | 0x3b8 | 0x7fbca8 | custom-draw surface (OnPaint) | §7.1 |
| `TQuickContainer` | 0x7a4a88 | 0x508 | 0x7fcb90 | panel / group container | §7.4 |
| `TQuickBorderedControl` | 0x7a4f08 | 0x538 | 0x7fcf70 | bordered panel | §7.4 |
| `TQuickSplitter` | 0x7a53f8 | 0x3f0 | 0x7fbca8 | splitter bar | §7.4 |
| `TQuickToolBar` | 0x90dff0 | 0x5c8 | 0x7dd878 | toolbar | §7.4 |
| `TQuickControlBar` | 0x90ea38 | 0x608 | 0x807d98 | control bar | §7.4 |
| `TQuickEditToolBar` family | 0xb4fdb0… | — | — | piano-roll edit toolbar (specialized) | note §10 |

### Base layer (WP framework — owned by ui-01-wp-core; listed for the hierarchy, NOT renamed here)
| class | VMT | className str | note |
|---|---|---|---|
| `TWPControl` | 0x7dd928 | @0x7ddc1e | root WP control (`twpcontrol` token @0x762248) |
| `TQuickCustomControl` | 0x7fcc40 | @0x7fcf0f | windowed WP custom control |
| `TQuickGraphicControl` | 0x7fbd58 | @0x7fbfda | non-windowed (graphic) WP control — parent of most light controls (knob/label/paintbox/gauge) |
| `TQuickWinControl` | 0x7fc258 | — | windowed WP control |
| `TCustomQuickBtn` | 0x714658 | @0x714a43 | button base (right/middle-click action links: `TCustomQuickBtnRightClickActionLink` etc.) |
| `TCustomSelector` | (CommonQuickControls) | @0x79d0df | tab/selector base (`TCustomSelectorValue` @0x7a178e) |

† Parent-VMT addresses that are not themselves rows in fl-classes.txt (`0x7fbca8`, `0x7fcb90`, `0x7fcf70`,
`0x7fc1a8`, `0x7a4e58`, `0x715458`, `0x745790`, `0x779078`) are intermediate WP/VCL bases the RTTI pass did not
emit as standalone classes; they are shown by address. (`0x745790` = TQuickCustomMemo class-ref region;
`0x715458` = a QuickBtn base region used by the scroller buttons.)

### Non-visual `TQuick*` (NOT UI controls — disambiguation)
`TQuickList`/`TQuickGrowList` (0x64eff0/0x64f120) = `TList`-style data containers; `TQuickPaintList` (0x7fc830)
= paint helper; `TQuickFont` (0x6528c8) = font wrapper; `TQuickTimer*` (0x6b97xx) = timers;
`TQuickRegIniFile` (0x6175c0) = settings store; `TQuick*Module` (0x104xxxx) = VCL data-module wrappers for the
grid/combo/tree. The actual list/table *widget* is `TQuickStringGrid` / `TQuickTree`.

---

## 2. Class hierarchy (the reuse-relevant chain)

```
VCL TGraphicControl / TWinControl / TCustomControl
        │
   TWPControl (0x7dd928)                         ← root of all WP controls; the generic ctor target
        ├── TQuickGraphicControl (0x7fbd58)      ← lightweight, non-windowed (no HWND); fast to draw
        │       ├── TSliderKnob, *wheel*, TQuickGauge, TQuickBitTable, TQuickLabel,
        │       │   TQuickPaintBox, TQuickSplitter, TQuickMIDIKb_Visual …  (parent 0x7fbca8)
        ├── TQuickCustomControl (0x7fcc40) / win-ctl bases (0x7fcb90, 0x7fc258)
        │       ├── TQuickFocusBtn, TQuickContainer, TQuickStringGridHeader, TWPFrame  (parent 0x7fcb90)
        │       └── TQuickBorderedControl (parent 0x7fcf70)
        ├── TCustomQuickBtn (0x714658)
        │       ├── TQuickBtn (0x715508), TQuickCheckBox (0x715b10), TQuickCombo (0x779ad0)
        │       └── (scroller buttons via 0x715458): TQuickScrollerBtn, TQuickScrollerHandle
        ├── TQuickCustomMemo (0x745840) → TQuickEdit (0x7466a0), TQuickMemo (0x746298)
        ├── TCustomSelector → TQuickCustomTabSelector → TQuickTabSelector / TQuickSheetSelector
        └── TQuickScroller, TQuickSheet, TQuickContainer-derived panels  (parent 0x7a4e58)
```
Key takeaway: pick `TQuickGraphicControl` descendants for cheap painted controls (knob/label/paintbox), and the
windowed bases when the control needs an HWND / focus (focus button, edit, grid).

---

## 3. The create entry points (one family, several thin wrappers)

All controls are constructed by a `TObject`-create thunk that takes the **class reference** (`&VMT+0x18`):
`obj = ClassCreate(classRef, allocFlag=1)` → routes to the class's `Create`. The named wrappers:

| wrapper | addr | builds | notes |
|---|---|---|---|
| `FLwp_CreateControl(classRef,1,0)` | 0x717A90 | generic `TWPControl` (any class via its VMT) | the cornerstone; tagged `UI_controls` |
| `FLwp_CreateButtonControl()` | 0xF0DDB0 | `TQuickBtn` (VMT `&LAB_00715520`) | + base-init + apply channel-rack skin; tagged `UI_controls` |
| `FLui_Ctl_Wheel_CreateWithSkin(buf,descrW)` | 0xF0DAF0 | `TVectorWheel` (classRef 0x7a0ca0) | §7.2 |
| `FLui_Ctl_DigiWheel_CreateMixerTrack()` | 0xF0DE00 | `TVectorDigiWheel` (classRef 0x7a19e0) | §7.2 (`FUN_00f0e160` = a `TQuickLabel` glyph, not a wheel) |
| inner create thunks | `FUN_00410320` (ClassCreate) / `FUN_00410360` (AfterConstruction); per-class `FUN_007adde0`, `FUN_007c07c0`, `FUN_007ce7d0` | — | base framework — NOT renamed |

`FLwp_BuildChannelRackControls@0xF0E330` is the **canonical worked example** (creates a select button, mute
button, vol/pan wheels, loop button, FX-route digiwheel — full create+wire for 7 controls). Tagged `UI_controls`.

---

## 4. The generic create → configure → wire recipe (REUSE CORNERSTONE)

Validated from `FLwp_CreateControl@0x717A90`, `FLwp_CreateButtonControl@0xF0DDB0`,
`FLwp_BuildChannelRackControls@0xF0E330` (decompiled). All on the **UI thread**.

```c
// 1. CREATE — VMT selects the class
ctrl = FLwp_CreateControl(&VMT /*classRef = VMT+0x18*/, 1, 0);   // @0x717A90  (TWPControl.Create)
FUN_005d08c0(ctrl, 0);                                            // base init (WP control)

// 2. APPLY SKIN/THEME  (vtbl[0x138]); the channel-rack theme object is *(*0x14A8BF8 + 0x7a0)
(*(ctrl->vtbl[0x138]))(ctrl, *(void**)(*(void**)0x14A8BF8 + 0x7a0));

// 3. SKIN DESCRIPTOR  (UStr @ctrl+0x328)  — "controls.<grp>;forms.<path>:<typename>"  (see §6)
Delphi_UStrAsg(ctrl + 0x328, L"controls.button;forms.x.button.y:quickbutton");

// 4. BOUNDS  (vtbl[0x188](x,y,w,h))  — or poke x@+0x90, y@+0x94, w@+0x474(u16), h@+0x476(u16)
(*(ctrl->vtbl[0x188]))(ctrl, x, y, w, h);
//   width/height also via base setters FUN_005cf940(ctrl,w) / FUN_005cf9a0(ctrl,h)

// 5. HINT  (UStr @ctrl+0xe4)  — tooltip; format "|^icon^...^text" (see channel-rack examples)
FUN_0054c5e0(&t, L"|^^My tooltip"); Delphi_UStrAsg(ctrl + 0xe4, t);

// 6. WIRE EVENT HANDLERS  (TMethod {code@off, Self@off+8}; see §5)
*(void**)(ctrl + 0x14c) = myCtx;  *(code**)(ctrl + 0x144) = myOnClickThunk;   // onClick

// 7. PARENT  — register into a parent container's child collection (this makes it live/hit-tested)
(*(parentChild->vtbl[0x10]))( (ctrl+0x11c), *(void**)(parentForm + 0x11c) );
//   (ctrl+0x11c) is the control's own child node; parentForm+0x11c is the parent's child collection.
//   Channel rack parents children under form+0x7d4 (a TQuickContainer).

// 8. SHOW + FINALIZE
(*(ctrl->vtbl[0x200]))(ctrl, 1);     // show
FUN_0076aef0(ctrl, 1);               // finalize/realize (tagged UI_controls)
```

**Key shared VMT slots** (offsets into the control's vtable, observed across all WP controls):
| slot | meaning |
|---|---|
| `vtbl[0x130]` | (visibility/enable refresh) |
| `vtbl[0x138]` | **apply skin/theme** (arg = theme object) |
| `vtbl[0x188]` | **SetBounds(x,y,w,h)** |
| `vtbl[0x1d8]` | set **default** value (knob/wheel) |
| `vtbl[0x1e0]` | set **range**: `(ctrl, idx, val)` idx 0=min 1=max |
| `vtbl[0x1e8]` | set value-mode / increment behavior |
| `vtbl[0x200]` | **Show(visible)** |
| `vtbl[0x238]` | redraw/invalidate (used by SetControlValue) |
| `vtbl[0x268]` | set value-format / display callback |
| child `vtbl[0x10]` | **add child to a container** (on the `+0x11c` child node) |

---

## 5. Value + event model (applies to every control)

### Value
- **Generic:** `int @ctrl+0xc4`. Set `FLwp_SetControlValue(ctrl,val)@0x5D0D10` (writes +0xc4, clears dirty
  `+0xac=0`, redraws via `vtbl[0x238]`, fires notify `0xb00d`). Read = peek `+0xc4`.
- **Knob/wheel subtype:** `int @ctrl+0x450` (= `ctrl[0x8a]` as `longlong*`). Range via `vtbl[0x1e0](idx,val)`,
  default via `vtbl[0x1d8](val)`. Sub-value step setters `FUN_007ac1f0`/`FUN_007ac210`. (Details §7.2.)
- **Press / armed state:** byte `@ctrl+0x4cd`.
- **Behavior flag word:** `u32 @ctrl+0x48a` (button toggle/latch bits — e.g. `0x4001` = toggle, `0x20000`
  transient during create, `0x5009` for the select button). `@ctrl+0x42c`, `@ctrl+0x45a` = sub-mode bytes.

### Event TMethod slots (poke `{code@off, Self@off+8}`; FL calls `code(ctrl, Self, …args)` on the UI thread)
| offset (code / Self) | event | args |
|---|---|---|
| `+0x144` / `+0x14c` | **OnClick / MouseDown** | button:char, shift:u16, x:int, y:int |
| `+0x154` / `+0x15c` | MouseUp / aux | |
| `+0x164` / `+0x16c` | MouseMove / aux | |
| `+0x1e4` / `+0x1ec` | secondary mouse / **OnChange (buttons)** | |
| `+0x1f4` / `+0x1fc` | **DblClick** | |
| `+0x214` / `+0x21c` | **MouseActivate** | |
| `+0x2bc` / `+0x2c4` | **OnHint** | |
| `+0x398` / `+0x3a0` (`ctrl[0x73]`/`[0x74]`) | **OnChange (wheels/values)** | |
| `+0x400` / `+0x408` (`ctrl[0x80]`/`[0x81]`) | **OnPressedChange** | |
| `+0x49c` / `+0x4a4` | GetText (aux) | |
| `+0x4ac` / `+0x4b4` | **OnGetText** (display string) | returns UStr |
| descriptor UStr `+0x328` · hint UStr `+0xe4` | (not events — config) | |

To install a handler from our injected DLL: alloc an RWX x64 thunk in FL, set `Self` first then `code`; the
`Self` pointer must outlive the control; the thunk must be SEH-safe and fast (it runs on FL's UI thread).

---

## 6. Skin descriptor + control-type-name registry (@0x761d00)

A control's look + the concrete subclass come from its **skin descriptor** UStr (`@ctrl+0x328`), format:
`controls.<group>;forms.<formpath>.<element>:<typename>` (the `;`-separated parts are fallbacks; the final
`:<typename>` token selects the WP control type/skin behavior).

The **type-name registry** is a UTF-16 length-prefixed UString array at **`0x761d00`** (each entry:
`-8 refcount=0xffffffff, -4 len, chars, null`). Enumerated tokens (the descriptor parser string-matches the
`:suffix` against these — no direct code xref):
```
thqwavscope, titlebar, tnewcaption, tnewmenu, toolbutton, tpaintboxselector, tpyformedit, …
quickbutton(@0x761c10), tquickcombo(@0x761e4c), tquickedit(@0x761e70),
tquickedittoolbar(@0x761e94), tquickedittoolbarpanel(@0x761ec4), tquicklabel(@0x761f24),
tquicktabselector(@0x762054), twpcontrol(@0x762248)
```
Plus behavior suffixes seen in live descriptors: `:quickbutton`, `:quickbtn`, `:wheel`, `:digiwheel`,
`:checkbox`, `:wpform`. Examples from the binary: `forms.msgform.combobox:quickbutton`,
`forms.toolbar.button:quickbutton.color2`, `controls.wheel;forms.channelrack.controls.wheel.volume:wheel`,
`forms.channelrack.controls.digiwheel.mixertrack:digiwheel`. (re/22 documents the chrome tokens titlebar /
tnewcaption / toolbutton — same table.)

---

## 7. Per-control catalog

### 7.1 Buttons / toggles / paint surface  [this agent]

#### TQuickBtn — push / toggle / icon button   (VMT 0x715508, size 0x4e8, base TCustomQuickBtn 0x714658)
- **Create:** `btn = FLwp_CreateButtonControl()@0xF0DDB0` (does CREATE with classRef `&LAB_00715520`,
  base-init `FUN_005d08c0`, and apply skin `vtbl[0x138]`). Then follow §4 steps 3-8.
- **Caption / icon:** driven by the **skin descriptor** (`@+0x328`, e.g.
  `forms.channelrack.controls.button.mutebtn:quickbtn`). A text button takes its label from the skin/descriptor;
  an icon button from the skin part. (No separate Caption UStr setter is used in the channel-rack builder.)
- **Value / state:** generic `int @+0xc4` via `FLwp_SetControlValue@0x5D0D10`. For a **toggle/latch** button,
  set behavior flags in `u32 @+0x48a` (e.g. `|= 0x4001` toggle, `|= 1` simple). `vtbl[0x1e8](ctrl, 2)` puts the
  button into 2-state mode. Press/armed byte `@+0x4cd` (set `0` so it doesn't auto-press during build).
- **Events:** OnClick `+0x144/+0x14c`; secondary/OnChange `+0x1e4/+0x1ec`; OnPressedChange `+0x400/+0x408`
  (`ctrl[0x80]/[0x81]`); GetText `+0x4ac/+0x4b4`; MouseActivate `+0x214/+0x21c`; Hint `+0xe4`.
- **Color:** `FUN_005d0e10(ctrl, 0xffeb)` sets a skin color index; `FUN_005d0a30(ctrl, font/colorObj)` sets
  the font/color object (`*(*0x14A8750 + 0xa78)` = default).
- **Reuse recipe (toggle button):**
  ```c
  b = FLwp_CreateButtonControl();
  *(u32*)(b+0x48a) |= 0x4001;            // toggle/latch
  vtbl[0x188](b, x,y, 0x55,0x40);        // bounds (w=0x55,h=0x40 default)
  *(u32*)(b+0x4cd)=0;  vtbl[0x1e8](b,2); // 2-state, not auto-pressed
  Delphi_UStrAsg(b+0x328, L"forms.x.button.mybtn:quickbtn");
  *(void**)(b+0x14c)=ctx; *(code**)(b+0x144)=onClickThunk;   // wire click
  parent(b); vtbl[0x200](b,1); FUN_0076aef0(b,1);
  FLwp_SetControlValue(b, 1);            // set ON
  ```
- **Action links:** `TCustomQuickBtn` supports right/middle-click via VCL action links
  (`TCustomQuickBtnRightClickActionLink` @0x7174ee, `…MiddleClickActionLink` @0x71777e,
  `…RightMouseUpActionLink` @0x71725e) — i.e. buttons can carry up to 3 mouse-button actions.

#### TQuickCheckBox — 2-state toggle   (VMT 0x715b10, size 0x4e8, base TCustomQuickBtn 0x714658)
- Same family/create path as `TQuickBtn` (shares the `TCustomQuickBtn` base) but with the checkbox VMT +
  a checkbox skin descriptor. **State** = generic `int @+0xc4` (0/1) via `FLwp_SetControlValue`; **OnChange** =
  `+0x1e4/+0x1ec`. className string @0x715d77. No dedicated factory wrapper — create via
  `FLwp_CreateControl(&(0x715b10+0x18),1,0)` then §4. (FL's own checkboxes appear as form fields, e.g.
  `TScriptDialog.PreviewCheckBoxClick`, `TPluginForm.FXPreampClipCheckBoxClick`.)

#### TQuickFocusBtn — keyboard-focusable button   (VMT 0x716a20, size 0x520, base 0x7fcb90 win-ctl)
- A windowed (focus-capable) button — used where the control must grab keyboard focus, e.g. the plugin
  window's "give keyboard to plugin" button (`TPluginForm.KeyboardFocusBtnClick@0xe91470`; skin
  `forms.pluginform.newcaption.keyboardfocusbtn`). Larger struct (0x520) than `TQuickBtn` because it carries a
  windowed-control (HWND/focus) payload. Create via `FLwp_CreateControl(&(0x716a20+0x18),1,0)` + §4; wire
  OnClick `+0x144/+0x14c` as usual. Use this (not `TQuickBtn`) when the button must participate in tab/focus.

#### TQuickPaintBox — custom-draw surface   (VMT 0x7a3258, size 0x3b8, base TQuickGraphicControl 0x7fbca8)
- The owner-draw canvas: you paint it yourself via an **OnPaint** handler. Create path seen in
  `FUN_00f90a20`: `pb = FUN_007ce7d0(&PTR_FLui_WP_AssignTo_007a3270 /*=0x7a3258+0x18*/, 1, 0);
  FLui_WP_SetAlign(pb, 0x0f)` (0x0f = align client/all sides). Then parent + show per §4.
- It is a `TQuickGraphicControl` (non-windowed) → cheap; FL uses it for meters/scopes and bespoke widgets
  (e.g. `TToolbarForm.FpsMeasurePaintBoxPaint`, `TRenderForm.ProgressPaintBoxPaint`,
  `TMsgForm.PluginsPaintBoxPaint`). **OnPaint** is wired as a TMethod paint slot on the paintbox (the form's
  `*PaintBoxPaint` published method is the handler); exact paint-TMethod offset to **live-confirm** (the
  `tpaintboxselector` token @0x761d.. is the selector variant). Reuse: create → SetAlign/SetBounds → wire
  OnPaint thunk that draws into the supplied canvas → parent → show.

### 7.2 Knobs / sliders / wheels / value controls
<!-- BEGIN group-B -->
**Class-name resolution:** the "wheel" = **`TVectorWheel`** (VMT 0x7a0c88 → cr 0x7a0ca0, size 0x538); the
"digiwheel" = **`TVectorDigiWheel`** (VMT 0x7a19c8 → cr 0x7a19e0, size 0x4e8). Family chain:
`TVectorWheel`/`TVectorSlider` → `TBaseVectorWheel`(0x7a05d0) → base wheel(0x79f348) → … → TWPControl. (Siblings
`TPaintWheel`/`TWAVWheel`.) **Correction:** `FUN_00f0e160` builds a `TQuickLabel` glyph icon (font `P_ILGlyphs`),
NOT a wheel.

#### TVectorWheel — rotary knob / value wheel (vol/pan)   (VMT 0x7a0c88 → cr 0x7a0ca0, size 0x538)
- **Create:** `FLui_Ctl_Wheel_CreateWithSkin(buf, skinDescrUStr)@0xF0DAF0`: `w=FUN_007adde0(&0x7a0ca0,1,0)` →
  base-init `FUN_005d08c0` → theme `vtbl[0x138]` → skin sub-colors `FUN_007bfee0(w,idx,color)` → bounds
  `vtbl[0x188]` → descriptor `Delphi_UStrAsg(w+0x328, descr)` → finalize `FUN_0076aef0(w,1)`. (Canonical:
  `FLwp_BuildChannelRackControls@0xF0E330` builds pan wheel @form+0x7ec, vol wheel @form+0x7f4.)
- **Value:** **int @+0x450** (= `w[0x8a]`), poked directly (NOT the +0xc4 path), clamped to [min@+0x3b8,
  max@+0x3bc].
- **Methods (vtbl + named):** `FLui_Ctl_Wheel_SetRange@0x7ACE70` `vtbl[0x1e0](w,idx,val)` idx 0=min/1=max (+ display
  scale); `FLui_Ctl_Wheel_SetRangeRaw@0x7A6950`; `FLui_Ctl_Wheel_SetDefaultValue@0x7AA7B0` `vtbl[0x1d8]`
  (+0x3c4; 0x7fffffff=none); `FLui_Ctl_Wheel_SetEditMode@0x7A6940` `vtbl[0x1e8]` (+0x3c8);
  `FLui_Ctl_Wheel_SetTickStepX/Y@0x7AC1F0/0x7AC210`; `FLui_Ctl_Wheel_RecalcRect@0x7AC1A0`;
  `FLui_Ctl_Wheel_SetArcSpan@0x7AE0A0` (arc @+0x500/+0x504).
- **Events:** **OnChange = code `+0x398`/Self `+0x3a0`** (`w[0x73]/[0x74]`); PressedChange `+0x400/+0x408`;
  GetText `+0x4ac/+0x4b4`; Hint `+0x2bc/+0x2c4`; DblClick `+0x1f4/+0x1fc`.
- **Fields:** +0x328 descr · +0xe4 hint · +0x3b8 min · +0x3bc max · +0x3c4 default · +0x3c8 edit-mode · +0x3dc
  raw scale(dbl) · +0x438 display scale(dbl) · +0x450 value(int) · +0x3f8/+0x3fc tick steps · +0x45c flags ·
  +0x500/+0x504 arc.
- **Encoding:** linear int in [min,max] — NO fixed-point on the stored value (float scales are for drawing only).
  Channel-rack pan & vol use **max = 0x3200 (12800)**, default 800.
- **Reuse:** `w=FLui_Ctl_Wheel_CreateWithSkin(buf,L"controls.wheel;…:wheel"); w->vtbl[0x1e0](w,0,MIN);
  w->vtbl[0x1e0](w,1,MAX); *(int*)(w+0x450)=v; w->vtbl[0x1d8](w,def); *(void**)(w+0x3a0)=self;
  *(code**)(w+0x398)=onChange; parent(w); FUN_0076aef0(w,1);`

#### TVectorDigiWheel — digit/spinner wheel (mixer-track / numeric select)   (VMT 0x7a19c8 → cr 0x7a19e0, size 0x4e8)
- **Create:** `FLui_Ctl_DigiWheel_CreateMixerTrack(buf)@0xF0DE00`: `d=FUN_007c07c0(&0x7a19e0,1,0)` → theme →
  size (FUN_005cf940=0x29 w, FUN_005cf9a0=0x18 h) → hint → `vtbl[0x1e0](1, mixerTrackCount-2)` max →
  `vtbl[0x1d8](0)` default → value@+0x450=2 → font `P_DigitWheel`. Same value/event model as TVectorWheel
  (value @+0x450, OnChange +0x398/+0x3a0, GetText +0x4ac/+0x4b4, PressedChange +0x400/+0x408).
- **Encoding:** signed int [min,max] @+0x450; negative sentinels = current/none (e.g. -3 for FX-route). Descriptor
  `:digiwheel`.

#### TSliderKnob — rotary knob / fader graphic   (VMT 0x6babe0 → cr 0x6babf8, size 0x3a8, parent 0x7fbca8)
- Direct TWPControl subclass (no wheel range/default virtuals). **Create:** `FUN_007ffbd0(&0x6babf8,1,parent)`;
  the fully-configured knob-with-range is the composite `FUN_006bb960` (embeds the knob at container+0x500; range
  fields container +0x568 min=0/+0x56c max=1000/+0x560 step=10/+0x564 page=100). **Value:** int @+0x398
  (**0x7fffffff = unset**) and/or generic +0xc4 via `FLwp_SetControlValue`. `FLui_Ctl_Knob_PaintIndicator@0x6BCF10`
  (vtbl[0x1a0]) draws the indicator.

#### TKnobInput — numeric value-entry knob (composite editor)   (VMT 0xceb0c0 → cr 0xceb0d8, size 0x80, parent 0xce85f0 TBaseInput)
- A composite numeric editor (siblings TComboInput/TTextInput/TCheckboxInput). **Create:**
  `FLui_Ctl_KnobInput_Create@0xCED910` via `FLui_Ctl_KnobInput_Spawn@0xCEC050(owner, …, &value, min:dbl,
  max:dbl, …, isFloat, hint)`. Builds 3 children: TQuickLabel caption @+0x5c, value-display @+0x64, and an
  **embedded TVectorWheel @+0x6c** (the drag control; OnChange → `FUN_00cee850(self=KnobInput)`). TBaseInput
  virtuals on it: vtbl[0]=SetMin(dbl), vtbl[8]=SetMax(dbl), vtbl[0x28]=SetValue(ptr). Fields: +0x44 active
  editor · +0x59 isFloat. **Reuse:** call `FLui_Ctl_KnobInput_Spawn` to pop a numeric editor over a control —
  or just reuse the TVectorWheel recipe (the actual draggable element is a plain TVectorWheel).

#### TQuickGauge — progress / level gauge   (VMT 0x7a5bd0 → cr 0x7a5be8, size 0x3d8, parent 0x7fbca8)
- Direct TWPControl subclass, control type-id **8** (skin `:gauge`). Instantiated via the generic skinned-control
  factory (no gauge-specific fn). **Value = generic int @+0xc4** (`FLwp_SetControlValue`/peek); range fields
  **+0x3a4 / +0x3a8** (min/max or lo/hi markers). Linear int.

#### TQuickBitTable — step / bit grid (step-sequencer cells)   (VMT 0x77a328 → cr 0x77a340, size 0x3c8, parent 0x7fbca8)
- **Create:** `FLui_Ctl_BitTable_Create@0x780760` (`FUN_007ffbd0` base-init; cols +0x3a0=0x20; `SetRange(0,0xf)`
  → 16 cells).
- **Methods:** `FLui_Ctl_BitTable_SetRange(self,first,last)@0x7806F0` (+0x3a4/+0x3a8; reallocs bit buffer
  @+0x3ac to `((last-first)+8)>>3` bytes); `FLui_Ctl_BitTable_GetBit(self,absIdx)@0x780890`;
  `FLui_Ctl_BitTable_SetBit@0x7808C0`; `FLui_Ctl_BitTable_ClearBit@0x7808F0`;
  `FLui_Ctl_BitTable_PaintCells@0x780B90` (off-color @+0xc4, on-color @+0x3b4);
  `FLui_Ctl_BitTable_DragPaint@0x7809D0` (cell = `x/cellW + (y/cellH)*cols - first`, drag-latch +0x3b8);
  `FLui_Ctl_BitTable_SnapBounds@0x780650`; `FLui_Ctl_BitTable_Destroy@0x780620`.
- **Fields:** +0x398 cellW · +0x39c cellH · +0x3a0 cols · +0x3a4 first · +0x3a8 last · +0x3ac → packed bit
  buffer (1 bit/cell) · +0x3b4 on-color · +0xc4 off-color · +0x3b8 drag latch · +0x304 canvas.
- **Encoding:** N=last-first+1 cells packed as a bit array; bit i → `buf[(i-first)>>3]`. ⚠️ SetBit/ClearBit mask
  uses `1<<(absIdx&7)` — consistent only when `first` is a multiple of 8 (usual `first=0`).
- **Reuse:** `bt=FLui_Ctl_BitTable_Create(&0x77a340,1,parent); FLui_Ctl_BitTable_SetRange(bt,0,nSteps-1);
  *(int*)(bt+0x3a0)=cols; *(int*)(bt+0x398)=cellW; *(int*)(bt+0x39c)=cellH; FLui_Ctl_BitTable_SetBit(bt,i);
  bt->vtbl[0x178](bt);` → read `FLui_Ctl_BitTable_GetBit(bt,i)`. User clicks auto-handled by DragPaint.

#### TQuickMIDIKb family — on-screen MIDI keyboard
- Chain: `TQuickMIDIKb_Visual`(0xb47788) → `TQuickMIDIKb`(0xb47f78) → `TQuickMIDIKbEx`(0xb48368) →
  `TQuickVectorMIDIKb`(0xb48800) → `TQuickVectorMIDIKbRange`(0xb48d40). Instantiated dynamically by host forms
  (no direct ctor xref). **Embedded + event-driven**: the host wires the keyboard's **NoteOn / NoteOff /
  LayerChange / MouseDown / MouseMove / MouseUp** TMethod callbacks — that is the reuse surface. Confirmed host
  handlers (named, not ours): `TEventEditForm.MIDIKb{MouseDown@0xd829f0,NoteOn@0xd82e20,NoteOff@0xd82eb0,…}`,
  `TDWPRenderForm.MIDIKb{NoteOn@0xb8d1a0,NoteOff@0xb8d170,LayerChange@0xb8d0e0}`.

> **Control-state type registry:** `FUN_00769770` (+ `FUN_0076bff0`) is the control snapshot/AssignTo serializer
> mapping a **type-id → classRef**: wheel=5, slider(TVectorSlider)=6, gauge=8, label=9, digiwheel=10,
> bittable=0x1a, selector=0x1b. Handy as a type registry (base framework — not renamed).
<!-- END group-B -->

### 7.3 Text / edit / combo / grid / label
<!-- BEGIN group-C -->
Factory pattern (all `*_Create`): `obj=NewInstance(&classRef,1)=FUN_00410320; ctorBody(obj,0,owner); …config…;
obj=AfterConstruction(obj)=FUN_00410360; return obj` — call with `(&classRef, 1, ownerForm)`.

#### TQuickCustomMemo — edit/memo base   (VMT 0x745840, size 0x730, parent 0x779078)
Abstract base of TQuickEdit/TQuickMemo; holds the text buffer + layout engine. Ctor body
`FLui_Ctl_CustomMemo_Ctor@0x747AC0(self,alloc,owner)` (inits text UStr @+0x624, MaxLength sentinel, autocomplete
list @+0x654). **Shared text fields:** **+0x624 = text (Delphi UnicodeString; GET = read directly)**; +0x62c
len cache; +0x630 SelStart; +0x634 SelEnd (refresh→length = select-all); +0x63c autocomplete match; +0x644
match idx; +0x654 autocomplete/history list; +0x65c alignment (0=L/1=C/2=R); +0x660 MaxLength (def 0x7fffffff);
+0x682 style word (bit0 single-line, bits1-2 wrap/multiline); +0x6cc..+0x72f memo line-layout arrays; +0x49c
font; +0xd4 caret/sel color.

#### TQuickEdit — single-line text edit   (VMT 0x7466a0 → cr 0x7466b8, size 0x730, parent 0x745790)
- **Create:** `FLui_Ctl_Edit_Create(&0x7466b8,1,owner)@0x74C400` (CustomMemo ctor + `FUN_0077d260(obj,3,0)`
  single-line mode).
- **Methods:** `FLui_Ctl_Edit_SetText(edit,UStr)@0x74C260` (UStrAsg into +0x624 → filter → rebuild
  autocomplete); **GET = read UStr @+0x624**; `FLui_Ctl_Edit_Refresh@0x74BB70`; `FLui_Ctl_Edit_FilterText@0x74C0C0`
  (char-replace + max-len); `FLui_Ctl_Edit_EnforceMaxLength@0x747A80`; `FLui_Ctl_Edit_SetStyleFlags@0x747920`
  (+0x682); `FLui_Ctl_Edit_UpdateRenderFlags@0x747830`; `FLui_Ctl_Edit_RebuildAutoComplete@0x748120`.
- **Events:** **OnChange/OnEditEnd = code `+0x3c0`/Self `+0x3c8`** (the slot the grid wires its inline editor
  into); OnCancel/OnKey `+0x6f4/+0x6fc`; standalone edits also use base change slots `+0x1e4/+0x1ec`.
- **Encoding:** UTF-16 Delphi UnicodeString @+0x624. Read-only/mode via base flag word @+0x490
  (`vtbl[0x270]` SetFlags; ctor ORs 0x400).
- **Reuse:** `e=FLui_Ctl_Edit_Create(&0x7466b8,1,form); e->vtbl[0x138](e,form+0x11c); e->vtbl[0x188](e,x,y,w,h);
  *(int*)(e+0x660)=maxlen; *(int*)(e+0x65c)=align; FLui_Ctl_Edit_SetText(e,ustr); e->vtbl[0x200](e,1);` →
  read back UStr @e+0x624.

#### TQuickMemo — multi-line text editor   (VMT 0x746298 → cr 0x7462b0, size 0x730, parent 0x745790)
- **Create:** `FLui_Ctl_Memo_Create(&0x7462b0,1,owner)@0x74C330` (CustomMemo ctor +
  `FLui_Ctl_Edit_SetStyleFlags(obj,2)` word-wrap + `FLui_Ctl_Memo_RecalcLayout`). Shares all CustomMemo methods;
  `FLui_Ctl_Memo_RecalcLayout@0x7486D0` = line-wrap/glyph layout (line table @+0x708, char-x @+0x6dc). Content =
  single UStr @+0x624 with embedded line breaks.

#### TQuickCombo — combobox / dropdown   (VMT 0x779ad0 → cr 0x779ae8, size 0x580, parent 0x714658 TCustomQuickBtn)
- **Create:** `FLui_Ctl_Combo_Create(&0x779ae8,1,owner)@0x77F240` (flags +0x48a |=0x41028; allocates **items
  list @+0x4e0** + parallel data list @+0x4e8, both class @0x4cd480 = the WP string-list).
- **Item model** (on the list @combo+0x4e0): `vtbl[0x28]`=Count, `vtbl[0x78]`=Add(ustr), `vtbl[0x90]`=Clear,
  `vtbl[0x18]`=GetString(out,idx). Bulk set from `"a,b,c"` via base `FUN_0050e8a0(list,text)` (TStrings
  SetCommaText). `FLui_Ctl_Combo_ItemsChanged@0x77FE20` fires on change → enables dropdown-arrow flag (+0x48a
  bit 0x40000 when ≥2 items) + sets value range `vtbl[0x1e0](combo,1,count-1)`.
- **Selected index** = generic value @+0xc4 (1-based, range tracks item count); set via `vtbl[0x1d8](combo,idx)`
  or `FLwp_SetControlValue`; selected text = `GetString(value)`. ⚠️ some combos repurpose +0xc4 for a color
  (GenreSelect pushes a color through SetControlValue) — confirm per use.
- **Events:** **OnChange (selection) = code `+0x398`/Self `+0x3a0`**. Hint @+0xe4; font @+0x340.
- **Reuse:** `c=FLui_Ctl_Combo_Create(&0x779ae8,1,form); c->vtbl[0x138](c,form+0x11c); c->vtbl[0x188](...);
  list=*(c+0x4e0); for each s: list->vtbl[0x78](list,s);  c->vtbl[0x1d8](c,startIdx);
  *(code*)(c+0x398)=onChange; *(self*)(c+0x3a0)=form; c->vtbl[0x200](c,1);`

#### TQuickStringGrid — editable string grid / table   (VMT 0x74cfe8 → cr 0x74d000, size 0x6f0, parent 0x779078)
Column-major: a collection of per-column string-lists @grid+0x628.
- **Create:** `FLui_Ctl_Grid_Create(&0x74d000,1,owner)@0x74DA40` (RowHeight +0x658=0x10, FixedRows +0x664=1,
  cell-pad +0x6b4=3, line-color +0x65c=0xc0c0c0, columns coll +0x628).
- **Methods:** `FLui_Ctl_Grid_SetColCount@0x74FA40` (+0x638); `FLui_Ctl_Grid_GetColumn@0x74E7D0(grid,col)`;
  `FLui_Ctl_Grid_SetCellText(grid,row,col,UStr)@0x74F330` (`col->vtbl[0x40]` Put); `FLui_Ctl_Grid_GetCellText(grid,
  &out,row,col)@0x74E7A0` (`col->vtbl[0x18]` GetString); `FLui_Ctl_Grid_SetFixedRows@0x74F830` (1 → creates
  TQuickStringGridHeader @+0x630); `FLui_Ctl_Grid_ShowInlineEditor@0x74E3D0` (lazily creates an embedded
  **TQuickEdit @grid+0x66c**, wires editor `+0x3c0/+0x3c8={FLui_Ctl_Grid_InlineEditChanged,grid}`);
  `FLui_Ctl_Grid_InlineEditChanged@0x74DCD0`; painters `FLui_Ctl_Grid_PaintCells@0x74DD20` /
  `FLui_Ctl_Grid_PaintFixedRow@0x74FCC0`.
- **Events:** **OnDrawCell = code `+0x6a4`/Self `+0x6ac`** (custom cell paint); **OnCanEdit/OnSelectCell = code
  `+0x640`/Self `+0x648`** (returns bool to allow inline edit).
- **Fields:** +0x628 columns coll · +0x638 ColCount · +0x63c RowCount · +0x658 RowHeight · +0x664 FixedRows ·
  +0x630 header · +0x66c inline editor · +0x6dc selected-cells · +0x49c font · +0x494 skin descriptor UStr.
  Each cell = a Delphi UStr in the column-list at index=row (columns auto-pad to RowCount).
- **Reuse:** `g=FLui_Ctl_Grid_Create(&0x74d000,1,form); g->vtbl[0x138](g,form+0x11c); g->vtbl[0x188](...);
  FLui_Ctl_Grid_SetFixedRows(g,1); FLui_Ctl_Grid_SetColCount(g,nCols); /*set RowCount +0x63c*/
  FLui_Ctl_Grid_SetCellText(g,row,col,ustr); *(code*)(g+0x6a4)=onDrawCell; *(self*)(g+0x6ac)=form;` → read
  `FLui_Ctl_Grid_GetCellText(g,&out,row,col)`.

#### TQuickStringGridHeader — grid column header   (VMT 0x74c7f8 → cr 0x74c810, size 0x508, parent 0x7fcb90)
The fixed top row owned by a grid (lives @grid+0x630); created only by `FLui_Ctl_Grid_SetFixedRows` via the
generic create `FUN_00804700`. Column captions = grid row-0 cells. No public methods beyond base TWPControl.

#### TQuickLabel — static caption / non-interactive text   (VMT 0x7a3d40 → cr 0x7a3d58, size 0x3c8, parent 0x7fbca8)
The **genuine** static-text control (unlike FL's many "labels" that are actually click-less buttons, re/13):
no input/click slots, pure caption renderer.
- **Create:** `FLui_Ctl_Label_Create(&0x7a3d58,1,owner)@0x7CEA80` (style @+0x74=0x110, alignment @+0x77=1).
- **Set caption:** base `FUN_005cf630(label,UStr)` (property msg 0xc → `…0xb012`); guarded `FUN_005d0ae0`,
  getter `FUN_005d0a70`. Font set directly: `FLui_Skin_LoadNamedFont(*(label+0x340),"P_NormalBold")` +
  `FLui_Skin_SetFontColor`. Fields: font @+0x340, style @+0x74, alignment @+0x77, pos @+0x90/+0x94. No events.
- **Reuse:** `l=FLui_Ctl_Label_Create(&0x7a3d58,1,form); l->vtbl[0x138](l,form+0x11c); FUN_005cf630(l,ustr);
  FLui_Skin_LoadNamedFont(*(l+0x340),"P_NormalBold"); l->vtbl[0x188](l,x,y,w,h);`

> **Module wrappers:** `TQuickComboBoxModule`(0x1045320) / `TQuickStringGridModule`(0x1044028) are VCL
> `TDataModule` design-time wrappers that host one TQuickCombo/TQuickStringGrid and forward designer properties —
> no extra runtime behavior. Real logic is in the controls above.
<!-- END group-C -->

### 7.4 Lists / trees / tabs / scrollbars / containers / toolbars
<!-- BEGIN group-D -->
Shared facts (this group): **classRef = VMT+0x18** (confirmed). `FUN_0040fe90(obj,classRef)` = `InheritsFrom`
(is-a), `FUN_0040feb0` = checked cast (`as`) — most PARAM xrefs to a classRef are these checks, not ctors.
Generic child enum: `FUN_005db950()`=count, `FUN_005db970(ctrl,i)`=child[i]; child collection = `*(ctrl+0x11c)`.

#### TQuickTree — tree view / browser tree   (VMT 0x973c80 → classRef 0x973c98, size 0xf00, parent 0x92edb8)
- **Create:** `FLui_Ctl_Tree_Create(&0x973c98, 1, parent)@0x974990` (base-init `FUN_009460a0`; child coll
  padding `+0x20=2/+0x21=2`, indent `FUN_005e9de0(coll,1)`).
- **Full scrolling-tree recipe (copy-paste):** `FLui_Ctl_TreeModule_Create@0x1058140` creates a TQuickTree
  (`module+0x674`) **+** a TQuickScroller (`module+0x67c`) and links them: create tree → `vtbl[0x138]` skin →
  `FLwp_SetControlValue(tree,color)` → row height `tree+0x778`(u16), content width `tree+0x77c`(u32), font
  `tree+0xb4` → node-model setup (`FUN_00935xxx`) → create scroller → `FLui_Ctl_Tree_AttachScroller(tree,scroller)`
  → hide tree's built-in scroller.
- **Key methods:** `FLui_Ctl_Tree_GetNodeModel(tree)@0x973AA0` → `*(tree+0x51c)` (node-model object, VMT
  **0x923970**) — **node add/clear/select/expand are methods on this model**, not the tree (configured by the
  `FUN_00935xxx` family). `FLui_Ctl_Tree_AttachScroller(tree,scroller)@0x9746B0` → `tree+0xee0=scroller` + scroll
  back-link (`scroller+0x668=FUN_009746e0`, `+0x670=tree`).
- **Fields:** +0x11c child coll; +0xb4 font; **+0x51c node-model ptr**; +0x518 active item/col; +0x778(u16) row
  height; +0x77c(u32) content width; +0xc4 value; +0xee0 linked TQuickScroller.
- **Events:** OnChange `+0x398/+0x3a0`; onClick `+0x144/+0x14c`; OnSelect/OnExpand on the node model (unconfirmed).
- **Open item:** node model class (VMT 0x923970) only partially mapped — add/select/expand API lives there.

#### TQuickScroller (+ Btn / Handle / ScrollBoxScroller) — scrollbars
- **Classes:** `TQuickScroller` 0x6ccd60→cr 0x6ccd78 (0x708, parent 0x7a4e58); `TQuickScrollerHandle` 0x6cdeb8→cr
  0x6cded0 (thumb); `TQuickScrollerBtn` 0x6cdb30→cr 0x6cdb48 (arrow); `TQuickScrollBoxScroller` 0x778000→cr
  0x778018 (scrollbox variant, parent 0x6d4650).
- **Create:** `FLui_Ctl_Scroller_Create(&0x6ccd78, 1, parent)@0x6CE1A0` — **auto-creates the thumb**
  (`FLwp_CreateControl(&0x6cded0,1,self)` → `self+0x6b8`), inits range doubles `+0x580=1.0`/`+0x588=10.0`, page
  `+0x6a8=1.0`, color `FLwp_SetControlValue(self,0xc0c0c0)`, `FLui_Ctl_Scroller_SetStyle(self,5)`. Arrow buttons
  at `self+0x6cc`/`+0x6d4`.
- **Methods:** `FLui_Ctl_Scroller_SetPageSize@0x6D0880`→`+0x5c0`; `FLui_Ctl_Scroller_SetRange@0x6D09F0`→`+0x638`
  (total content); `FLui_Ctl_Scroller_SetStyle@0x6CF550`→`+0x6c1`; **position = `FLwp_SetControlValue(self,pos)`
  → `+0xc4`**.
- **Event:** **OnScroll TMethod = code `+0x668` / Self `+0x670`** (default `FUN_009746e0`).
- **Fields:** +0xc4 position; +0x580/+0x588(double) range; +0x5c0 page; +0x638 total range; +0x6a8(float) thumb
  fraction; +0x6b8 thumb; +0x6c0(byte) orientation(0=vert); +0x6c1 style; +0x6c2 align; +0x6cc/+0x6d4 arrows.
- **Reuse:** create → SetRange(total) → SetPageSize(page) → set position via `FLwp_SetControlValue` → wire
  OnScroll `+0x668/+0x670`.

#### TQuickTabSelector / TQuickSheetSelector / TQuickCustomTabSelector / TQuickSheet — tab strips & pages
- **Classes:** `TQuickCustomTabSelector` 0x7405a8 (abstract base, no instantiation); `TQuickTabSelector`
  0x740de8→cr 0x740e00 (0x5a0); `TQuickSheetSelector` 0x7412d8→cr 0x7412f0 (0x5b0); `TQuickSheet` 0x741748→cr
  0x741760 (0x558, a page). Base = `TCustomSelector` (CommonQuickControls).
- **Create:** no dedicated wrapper — generic `FLwp_CreateControl(&classRef,1,parent)` / descriptor-built (tab
  strips are auto-populated by the owning pager from a sheet list).
- **Accessors** (from the shared skin dispatcher `FUN_0076a6e0`, which treats Tab & Sheet selectors alike):
  **tab[i]** = `vtbl[0x298](self, i)` (returns the tab button object, font @+0x340); **count** =
  `*(int*)(*(self+0x57c)+0x10)` (items list @ `self+0x57c`, count @ list+0x10); **active index** = generic
  `+0xc4` (set via `FLwp_SetControlValue`).
- **TQuickSheet:** `FLui_Ctl_Sheet_GetValueSlot(_,sheet)@0x744080` → `sheet+0x530` (value/state slot).
- **Events:** OnTabChange = `+0x398/+0x3a0`; onClick `+0x144/+0x14c`.
- **Fields:** +0xc4 selected index; +0x570/+0x574/+0x578 tab colors (active/text/hover); +0x57c items-list.
- **Reuse:** read active = peek `+0xc4`; set active = `FLwp_SetControlValue(self, idx)`; iterate tabs via
  `vtbl[0x298](self,i)` for `i<count`. (No standalone "add tab" fn — pager populates from a sheet/descriptor list.)

#### TQuickSplitter — splitter bar   (VMT 0x7a53f8 → cr 0x7a5410, size 0x3f0, parent 0x7fbca8)
- **Create:** `FLui_Ctl_Splitter_Create(&0x7a5410,1,parent)@0x7D06F0` (grip/min `+0x399=10`, max pos
  `+0x3e4=0x7fffffff`, orientation `+0x398=0`, auto ratios `+0x3a8/+0x3ac=-1.0`). Browser uses it between the
  two tree panes: range `+0x39c=0`/`+0x3a0=10000`, wire OnMoved/OnMoving.
- **Methods:** `FLui_Ctl_Splitter_SetOrientation@0x7D0800`→`+0x398`; `FLui_Ctl_Splitter_SetMinSize@0x7D0B80`→`+0x399`.
- **Events:** **OnMoved = code `+0x3b0`/Self `+0x3b8`**; **OnMoving = code `+0x3c0`/Self `+0x3c8`**.
- **Fields:** +0x398 orientation; +0x399 min/grip; +0x39c/+0x3a0(int) drag range; +0x3a8/+0x3ac(float) min/max
  ratio (−1=auto); +0x3e4 max pos; +0xc4 current position (within [+0x39c,+0x3a0]).

#### TQuickContainer / TQuickBorderedControl — panels / group containers
- **Classes:** `TQuickContainer` 0x7a4a88→cr 0x7a4aa0 (0x508, parent 0x7fcb90); `TQuickBorderedControl`
  0x7a4f08→cr 0x7a4f20 (0x538, parent 0x7fcf70). Both abstract-ish (DATA self-ref only) → generic
  `FLwp_CreateControl(&classRef,1,parent)`.
- **Add child (grouping):** parent a control via `child->vtbl[0x138](child, container)` (skin+parent) or register
  into `*(container+0x11c)` with `(*(child+0x11c))->vtbl[0x10](child+0x11c, *(container+0x11c))`. Enumerate with
  `FUN_005db950()`/`FUN_005db970(container,i)`.
- **Fields:** +0x11c child collection (padding @coll+0x20/+0x21, indent `FUN_005e9de0`); border/padding in the
  bordered subclass's extra 0x30 bytes; +0xc4 value. The natural PARENT container for our own grouped controls.

#### TQuickToolBar / TQuickControlBar — toolbars
- **Classes:** `TQuickToolBar` 0x90dff0→cr 0x90e008 (0x5c8, parent 0x7dd878); `TQuickControlBar` 0x90ea38→cr
  0x90ea50 (0x608, parent 0x807d98). No dedicated create wrapper (generic/descriptor-built).
- **Add button / layout:** a toolbar IS a container — add a button by creating it parented to the toolbar
  (`FLwp_CreateControl(&LAB_00715520,1,toolbar)`). `FLui_Ctl_ControlBar_LayoutToolbars@0x910830` iterates
  children (`FUN_005db950`/`FUN_005db970`), shows each, and for any child that is-a TQuickToolBar invokes its
  layout callback.
- **Events:** **TQuickToolBar layout callback = code `+0x584`/Self `+0x58c`** (`(*(tb+0x584))(*(tb+0x58c),
  controlbar)`). ControlBar children-visible flag `+0xa9`, cursor id `+0x5b4`.

> **TQuickList family disambiguation:** `TQuickList`(0x64eff0)/`TQuickGrowList`(0x64f120) = TList-style **data
> arrays, NOT visual** (e.g. the tab selector's `+0x57c` items list); `TQuickPaintList`(0x7fc830) = paint helper;
> the real visual list/tree = `TQuickTree` via `FLui_Ctl_TreeModule_Create`.
<!-- END group-D -->

---

## 8. Reuse cookbook quick-reference

| I want a… | class / create | set value | wire change |
|---|---|---|---|
| push/icon button | `FLwp_CreateButtonControl()@0xF0DDB0` | flags @+0x48a, state +0xc4 | OnClick +0x144/+0x14c |
| checkbox | `TQuickCheckBox` (0x715b10) via §4 | +0xc4 (0/1) | OnChange +0x1e4/+0x1ec |
| focus button | `TQuickFocusBtn` (0x716a20) via §4 | +0xc4 | OnClick +0x144/+0x14c |
| knob / wheel | §7.2 (`FLwp_CreateWheelWithSkin@0xF0DAF0`) | +0x450, range vtbl[0x1e0] | OnChange +0x398/+0x3a0 |
| text edit | §7.3 (`TQuickEdit` 0x7466a0) | text @+0x624 (FUN_0074c260) | OnChange (§7.3) |
| dropdown | §7.3 (`TQuickCombo` 0x779ad0) | sel idx @+0xc4 | OnChange (§7.3) |
| paint surface | `TQuickPaintBox` 0x7a3258 (§7.1) | — | OnPaint |
| tab pager | §7.4 (`TQuickTabSelector` 0x740de8) | active idx @+0xc4 | OnChange (§7.4) |
| scrollbar | §7.4 (`TQuickScroller` 0x6ccd60) | pos (§7.4) | OnScroll (§7.4) |
| tree | §7.4 (`TQuickTree` 0x973c80) | sel node (§7.4) | OnSelect (§7.4) |
| panel/group | §7.4 (`TQuickContainer` 0x7a4a88) | — (parent for children via child vtbl[0x10]) | — |
| static label | §7.3 (`TQuickLabel` 0x7a3d40) | caption (§7.3) | — |

---

## 9. Ghidra renames / tags applied  (this agent — buttons/toggles/paintbox/base)
- Tagged `UI_controls`: `FLwp_CreateControl@0x717A90`, `FLwp_SetControlValue@0x5D0D10`,
  `FLwp_CreateButtonControl@0xF0DDB0`, `FLwp_BuildChannelRackControls@0xF0E330` (kept their existing accurate
  names; tag makes the control infra discoverable via `search_functions_by_tag UI_controls`).
- Resolved base-class names from string evidence (documented in §1/§2; classes owned by ui-01 → not renamed):
  `TCustomQuickBtn@0x714658`, `TWPControl@0x7dd928`, `TQuickGraphicControl@0x7fbd58`,
  `TQuickCustomControl@0x7fcc40`, `TCustomSelector` (CommonQuickControls).
- (Sub-agent renames for groups B/C/D appended in §11.)

## 10. Cross-area finds (NOTED, not renamed)
- **Base framework (ui-01):** generic create thunks `FUN_00410320`/`FUN_00410360`, per-class create
  `FUN_007adde0`/`FUN_007c07c0`/`FUN_007ce7d0`, base-init `FUN_005d08c0`, geometry setters
  `FUN_005cf940`(SetWidth)/`FUN_005cf9a0`(SetHeight), notify `FUN_005d2870`, `FUN_0076aef0`(finalize). The WP
  base classes `TWPControl`/`TQuickGraphicControl`/`TQuickCustomControl`/`TQuickWinControl`.
- **Skin (ui-04):** `FLui_Skin_LoadNamedFont`, `FLui_Skin_SetFontColor`, `FLui_Skin_FontChanged`, skin parts
  `P_DigitWheel`/`P_TitleBar`; the descriptor/type-name registry @0x761d00 (also re/22).
- **Forms (ui-03):** `TWPForm`/`TWPControlForm`/`TWPFrame`/`TQuickDockForm`/`TQuickPopupMenuWindow`;
  `TQuickEditToolBar*` family (piano-roll edit toolbar — specialized composite).
- **Menus (ui-02 menu agent / re/16,23):** `TQuickMenuItem`/`TQuickPopupMenu`/`TQuickMenuProp` — left untouched.
- **Browser (re/14):** `TQuickWebBrowser`, `TQuickTree` (browser tree) — tree itself catalogued in §7.4.

## 11. Consolidated Ghidra renames (all tagged `UI_controls`)

**Buttons/toggles/paintbox/base (this agent):** tags only on `FLwp_CreateControl@0x717A90`,
`FLwp_SetControlValue@0x5D0D10`, `FLwp_CreateButtonControl@0xF0DDB0`, `FLwp_BuildChannelRackControls@0xF0E330`
(names already accurate).

**Knobs/sliders/wheels/value (group B — 22):**
`FLui_Ctl_Wheel_CreateWithSkin@0xF0DAF0`, `FLui_Ctl_DigiWheel_CreateMixerTrack@0xF0DE00`,
`FLui_Ctl_Wheel_SetRange@0x7ACE70`, `FLui_Ctl_Wheel_SetRangeRaw@0x7A6950`,
`FLui_Ctl_Wheel_SetDefaultValue@0x7AA7B0`, `FLui_Ctl_Wheel_SetEditMode@0x7A6940`,
`FLui_Ctl_Wheel_SetTickStepX@0x7AC1F0`, `FLui_Ctl_Wheel_SetTickStepY@0x7AC210`,
`FLui_Ctl_Wheel_RecalcRect@0x7AC1A0`, `FLui_Ctl_Wheel_SetArcSpan@0x7AE0A0`,
`FLui_Ctl_Knob_PaintIndicator@0x6BCF10`, `FLui_Ctl_KnobInput_Create@0xCED910`,
`FLui_Ctl_KnobInput_Spawn@0xCEC050`, `FLui_Ctl_BitTable_Create@0x780760`,
`FLui_Ctl_BitTable_SetRange@0x7806F0`, `FLui_Ctl_BitTable_GetBit@0x780890`,
`FLui_Ctl_BitTable_SetBit@0x7808C0`, `FLui_Ctl_BitTable_ClearBit@0x7808F0`,
`FLui_Ctl_BitTable_PaintCells@0x780B90`, `FLui_Ctl_BitTable_DragPaint@0x7809D0`,
`FLui_Ctl_BitTable_SnapBounds@0x780650`, `FLui_Ctl_BitTable_Destroy@0x780620`.

**Text/edit/combo/grid/label (group C — 24):**
`FLui_Ctl_Edit_Create@0x74C400`, `FLui_Ctl_CustomMemo_Ctor@0x747AC0`, `FLui_Ctl_Edit_SetText@0x74C260`,
`FLui_Ctl_Edit_Refresh@0x74BB70`, `FLui_Ctl_Edit_FilterText@0x74C0C0`, `FLui_Ctl_Edit_EnforceMaxLength@0x747A80`,
`FLui_Ctl_Edit_UpdateRenderFlags@0x747830`, `FLui_Ctl_Edit_SetStyleFlags@0x747920`,
`FLui_Ctl_Edit_RebuildAutoComplete@0x748120`, `FLui_Ctl_Memo_RecalcLayout@0x7486D0`,
`FLui_Ctl_Memo_Create@0x74C330`, `FLui_Ctl_Combo_Create@0x77F240`, `FLui_Ctl_Combo_ItemsChanged@0x77FE20`,
`FLui_Ctl_Grid_Create@0x74DA40`, `FLui_Ctl_Grid_GetCellText@0x74E7A0`, `FLui_Ctl_Grid_GetColumn@0x74E7D0`,
`FLui_Ctl_Grid_SetCellText@0x74F330`, `FLui_Ctl_Grid_SetColCount@0x74FA40`, `FLui_Ctl_Grid_SetFixedRows@0x74F830`,
`FLui_Ctl_Grid_PaintCells@0x74DD20`, `FLui_Ctl_Grid_PaintFixedRow@0x74FCC0`,
`FLui_Ctl_Grid_ShowInlineEditor@0x74E3D0`, `FLui_Ctl_Grid_InlineEditChanged@0x74DCD0`,
`FLui_Ctl_Label_Create@0x7CEA80`.

**List/tree/tab/scroll/container/toolbar (group D — 13):**
`FLui_Ctl_TreeModule_Create@0x1058140`, `FLui_Ctl_Tree_Create@0x974990`, `FLui_Ctl_Tree_GetNodeModel@0x973AA0`,
`FLui_Ctl_Tree_AttachScroller@0x9746B0`, `FLui_Ctl_Scroller_Create@0x6CE1A0`,
`FLui_Ctl_Scroller_SetPageSize@0x6D0880`, `FLui_Ctl_Scroller_SetRange@0x6D09F0`,
`FLui_Ctl_Scroller_SetStyle@0x6CF550`, `FLui_Ctl_Splitter_Create@0x7D06F0`,
`FLui_Ctl_Splitter_SetOrientation@0x7D0800`, `FLui_Ctl_Splitter_SetMinSize@0x7D0B80`,
`FLui_Ctl_ControlBar_LayoutToolbars@0x910830`, `FLui_Ctl_Sheet_GetValueSlot@0x744080`.

**Total: 59 functions renamed + tagged `UI_controls` (B 22 + C 24 + D 13), plus 4 infra functions tagged.**
Discover them all via `search_functions_by_tag UI_controls`.
