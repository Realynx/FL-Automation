# UI-WIN — Channel Rack / Step Sequencer (`TStepSeqForm`, forms.channelrack)

Wave-2 concrete-window RE of the **Channel Rack** window. Static Ghidra on `FLEngine_x64.dll`, image base
`0x400000` (all addrs absolute/Ghidra; runtime = `ghidra - 0x400000 + flEngineBase`). Builds on the Wave-1
framework: re/ui-01-wp-core (TFLWPControl lifecycle), ui-02-controls (TQuickBtn/wheel/digiwheel/bittable),
ui-03-forms (`TStepSeqForm` catalog row), ui-06-layout (SetBounds/SetParent/scroll). Clean Delphi VCL + WP
layer — NO VMProtect. Ghidra renames tag = **`UI_win_chanrack`**, func convention **`FLui_ChanRack_<Name>`**.

## TL;DR / verdict (HIGH confidence)
- The Channel Rack class is **`TStepSeqForm`** (VMT `0xf41a00`, classRef `0xf41a18`, descriptor
  `"controls.forms;forms.channelrack:wpform"`, family **TChildVectorForm** = a dockable vector editor).
  Singleton `*0x14A8BF8` (the create call is in the startup recipe `FUN_010b8240`, re/ui-03 §6). Caption text
  @form+0x110, content container @form+0x11c, HWND @form+0x2b0, descriptor @form+0x6e8 (all per ui-03).
- **The window splits into TWO side-by-side regions** (resizable by the `SelectButtonSplitter`):
  - **LEFT = the channel-strip / channel-list container** = the WP control at **`*(form+0x7a0)`**. Each
    channel's per-channel strip (name button, mute/solo, vol/pan wheels, FX-route + loop digiwheels, sample
    button, type glyph) is parented under it. It carries the **vertical scroller** (`+0x53c`).
  - **RIGHT = the step-sequencer grid** = the back WP control at **`form+0x7a8`** (the "BackWP" custom-painted
    surface). It is **horizontally scrollable** in step units (one scroll unit = one step cell, width
    `*0x14AB898` = round(16×dpi)). Its cell painting is the control's own class paint (msg 0xf), NOT a form
    method — see §6 (the one remaining gap).
- **There is NO per-step widget.** The whole grid is one custom-painted control; channels are *not* drawn as
  TQuickBitTable rows. Steps are blitted by the back-grid control reading each channel's step data, using the
  cached metrics: **step cell width `*0x14AB898`**, **channel row height `*0x14A8C48` (round(30×dpi))**, **grid
  content width `*0x14AB940` = visibleSteps × stepWidth**, **visible-step count `DAT_0141e584`**, **beat group
  `*0x14A9368`** (steps/beat for beat-line coloring).
- **The per-channel strip is built once per channel by `FLwp_BuildChannelRackControls@0xF0E330`** (the canonical
  WP-control worked example, re/ui-02 §3) and the control pointers are stored **on the channel object** at
  `chan+0x7bc..+0x80c` (§4). The strip is **arranged + shown/hidden every refresh by
  `FLui_ChanRack_ArrangeChannels@0xF50340`** (§5) — iterates the channel list and SetBounds-es every control by
  a column-X table + a row-Y cursor. This pair (build-once + arrange-every-refresh) is the channel layout model.
- **Layout entry = `FLui_ChanRack_Relayout@0xF46C90`** (called by `TStepSeqForm.FormResize`): BeginUpdate →
  ComputeColumnMetrics → UpdateVScrollRange → LayoutTopBar → RealignChildRegion → EndUpdate → Invalidate.

---

## 1. Identity + singletons

| thing | value |
|---|---|
| Class | `TStepSeqForm` VMT `0xf41a00`, classRef `0xf41a18`, family `TChildVectorForm` (CVF, dockable vector editor) |
| Descriptor (form+0x6e8) | `"controls.forms;forms.channelrack:wpform"` (set in FormCreate) |
| Singleton (ptr→ptr→form) | `*0x14A8BF8` (`PTR_DAT_014a8bf8`); `*PTR_DAT_014a8bf8` = the live form |
| Cached form ptr | `DAT_0157f708` (= the form; used by layout globals) |
| Theme/skin container | `*(*0x14A8BF8 + 0x7a0)` = the LEFT channel-strip container (also the skin parent for cloned strip controls) |
| Created at startup | `FUN_010b8240` → `FLui_CreateFormFromClassRef(&0xf41a18, *0x14A8BF8)` (re/ui-03 §6) |
| Main app form | `*0x14A8750` (`PTR_DAT_014a8750`, `TFruityLoopsMainForm`) — owns shared state + many strip event Selfs |
| Channel list (all) | `*0x14A98D8` (`FLcr_ChannelListGetItem(list, idx)`; count @list+0x10) |
| Channel list (filtered/visible) | `*0x14A7968` — the **display-ordered** list arranged by `ArrangeChannels` (groups/filters applied) |
| Pattern/track context | `PTR_DAT_014aa0c8 + patIdx*0xc0` (patIdx = `*0x14AB580`); `+0x54` = step count for pattern |

---

## 2. Construction — `TStepSeqForm.FormCreate@0xF45870` (RTTI-named; tag UI_win_chanrack)

Sets the form-level fields, the grid model, fonts, and the cached pixel metrics. Does **not** build any
per-channel strip (those are lazy, §4). Key steps:
```c
UStrAsg(form+0x6e8, L"controls.forms;forms.channelrack:wpform");  FLui_WP_FreeSkinDescriptors(form,1);
// back-grid hint provider: model+0x2c4 = form (backptr); model+0x2bc = FLui_ChanRack_BackGridHintDispatch
lVar = form[0x101](=form+0x808);  *(lVar+0x2c4)=form;  *(lVar+0x2bc)=FLui_ChanRack_BackGridHintDispatch;
FLui_WP_SetAlign(*(form+0x7d0), 1);
form->vtbl[0x138](form, *(*0x14A8750 + 0xb18));   // SetParent INTO THE DOCK HOST (re/ui-06 §5) = docked
FUN_005ddf30(*(form+0x7a0));                       // init the LEFT channel-strip container / grid model
FormShow(0x7EA5D0);                                // realize
// cache layout metrics from dpi factor f = *(*(form+0x304)+0xb4)+0xc :
*0x14AB898 = round(f*16);   // STEP CELL WIDTH  (also mirrored to *0x14A7618)
*0x14A8C48 = round(f*30);   // CHANNEL ROW HEIGHT (also mirrored to *0x14AA868)
*0x14A8A30 = *0x14A9140 = <a skin color>;  form[0x13e](+0x9f0) = <computed strip color>;
// ... tail: decode L"__\\_" into a 56-byte default groove/shuffle template (not UI-layout) ...
```
Fonts: `form[0x127]`(+0x938) and `form[0x116]`(+0x8b0) get dpi-scaled font sizes (font obj @ctrl+0x340).

### 2.1 `TStepSeqForm` instance field map (offsets confirmed from FormCreate + layout + handlers)
| off | as `form[idx]` | field |
|----:|---|---|
| +0x110 | — | caption text (VCL FCaption) |
| +0x11c | — | content/layout container (ui-03) |
| +0x2b0 | — | HWND |
| +0x304 | — | WP paint/skin helper; `*(+0x304)+0xb4` → metrics blk (`+0xc` dpi factor, `+0x10` px-scale, `+0x20/+0x34` insets) |
| +0x6e8 | [0xdd] | skin descriptor UStr |
| +0x760 | [0xec] | a content panel (right/grid side base; width set in ArrangeChannels) |
| +0x768 | [0xed] | a scroll/region control (Invalidate+BeginUpdate'd in ArrangeChannels) |
| +0x770 | [0xee] | **horizontal content panel** — width set = grid width `*0x14AB940` (the scrolled step area) |
| +0x798 | [0xf3] | a header/cap control (shown when wide enough) |
| **+0x7a0** | [0xf4] | **LEFT channel-strip container + grid scroll model** (`+0x53c` vscroller, `+0x550` vscroll, `+0x54c` hscroll, `+0x5fc/+0x600` pan anchor, `+0x604` gesture state, `+0x558` user height, `+0x328` child node, `+0xd0` scroll metrics, `+0x98` width, `+0x548`) |
| **+0x7a8** | [0xf5] | **BackWP = step-grid control** (`+0xa9` showing, `+0x664` fixed left-col width, `+0x560` h-scroll value in steps, `+0x94` y) |
| +0x7b8 | [0xf7] | a control positioned in ArrangeChannels (right of grid) |
| +0x7c8 | [0xf9] | width-driven control (set to visibleSteps×scale) |
| +0x7e0 | [0xfc] | **loop-button TEMPLATE** (strips clone style/value/color from it; via `*0x14A8BF8+0x7e0`) |
| +0x808 | [0x101] | back-grid hint host (carries `+0x2bc` hint cb, `+0x2c4` form backptr) |
| +0x870 | — | loop-button color source (`*0x14A8BF8+0x870`) |
| +0x818 | — | FX-route color source (`*0x14A8BF8+0x818`) |
| +0x8a0 | [0x114] | a mode/state object (`+0x80` flag gates loop/mixer column visibility) |
| +0x8b0 | [0x116] | font/control |
| +0x918 | [0x123] | scrollable child (flag `+0x320 |= 0x1000` set in FormCreate) |
| +0x920 | [0x124] | a panel (`+0xa9` showing → triggers LayoutTopBar) |
| +0x938 | [0x127] | **sample/"replace-all-samples" button TEMPLATE** (+ font @+0x340) |
| +0x950 / +0x9b0 / +0x9c8 / +0x988 | — | top-bar controls chained by LayoutTopBar |
| +0x9d4 | — | selection index init = `0xffffffff` (none) |
| +0xa04 / +0xa0c | — | saved vscroller OnChange code/data (so OnVScroll can chain it) |
| +0xa14 / +0xa15 | — | flags (a15=1 enables loop/mixer column area) |

---

## 3. Cached layout metrics (globals; written by FormCreate / ComputeColumnMetrics)
| global | meaning | value |
|---|---|---|
| `*0x14AB898` (=`*0x14A7618`) | **step cell width** (px) | round(dpi×16), forced even |
| `*0x14A8C48` (=`*0x14AA868`) | **channel row height** (px) | round(dpi×30) |
| `*0x14AB940` | **grid content width** = visibleSteps×stepWidth (+ insets) | computed in ArrangeChannels |
| `DAT_0141e584` | **visible step count** (steps drawn across the grid) | ComputeColumnMetrics |
| `DAT_0141e588` | step-count target (after `FLui_ChanRack_UpdateVisibleSteps`) | |
| `*0x14A9368` | **beat group** = steps per beat (beat-line coloring / rounding) | |
| `*0x14AC1A8` | **visible channel-row count** (height ÷ rowHeight) | UpdateVScrollRange |
| `DAT_0157f738[]` | **column-X table** (per strip-slot X offsets; 16-byte stride) | ComputeColumnMetrics; read by GetColumnX |
| `DAT_0157f710` | running X cursor = right edge of strip controls (= grid start X) | ComputeColumnMetrics |
| `DAT_0141e584`/`DAT_0141e570` | step count / "fixed-width mode" flag | |

The **column-X table @`0x0157f738`** holds the per-slot left offsets for the strip controls. Accessor:
`FLui_ChanRack_GetColumnX(buf,slot)@0xF50320` = `*(buf+0xf8) + ((int*)0x0157f738)[slot*2]`. Slot→control map
(from ArrangeChannels SetBounds calls): `0`=pan wheel, `1`=vol wheel, `2`=mute btn, `3`=name button, `4`=
select panel, `5`=grid/right region, `7`=FX-route digiwheel + type glyph, `8`=loop btn, `9`=mixer-track
digiwheel, `0xb`=sample button.

---

## 4. The per-channel strip — `FLwp_BuildChannelRackControls@0xF0E330` (build-once, lazy)

Builds the channel's "front controls" on first display (guard `if (chan+0x7bc == 0)`) and stores the WP control
pointers **on the channel object**. All parented under the LEFT container `*(form+0x7a0)`'s child node
(`ctrl+0x11c → node->vtbl[0x10](node, container+0x11c)`), styled from the theme container `*(*0x14A8BF8+0x7a0)`.
Canonical WP create recipe (re/ui-01 §7 / ui-02 §4). Control pointers (offsets on the **channel** object):

| chan off | control | class / wrapper | descriptor / hint | event TMethods (slot → FLui_ChanRack_*) |
|----:|---|---|---|---|
| +0x7bc | **row wrapper** (sub-form) | `FUN_011d5890(&0x11d1570,1)` | — | (container; backptr @+0x3b4) |
| +0x7d4 | **select panel** (channel row body) | `FLui_WP_CreateControl(&0xefca08)` (TWPControl base) | `...selectbutton:quickbutton`, hint `Up/Down^Select` | +0x144 SelectPanelMouseDown · +0x164 SelectPanelMouseMove · +0x154 SelectPanelMouseUp |
| +0x7c4 | **name button** | `FLui_WP_CreateControl(&0xefc690)` | flags `+0x48a\|=0x5009` | +0x144 NameBtnClick · +0x1e4 NameBtnChange · +0x49c NameBtnGetText · +0x214 `TFruityLoopsMainForm.CtrlKeyBtnMouseActivate` |
| +0x7ec | **pan wheel** | `FLui_Ctl_Wheel_CreateWithSkin` | `...wheel.pan:wheel`, max `0x3200` | OnChange +0x398 `…FrontVolWheelChange` · hint cb +0x2bc `…FrontVolWheelHint` |
| +0x7f4 | **vol wheel** | `FLui_Ctl_Wheel_CreateWithSkin` | `...wheel.volume:wheel`, max `0x3200`, def `800` | OnChange +0x398 `…FrontVolWheelChange` |
| +0x7cc | **mute / solo button** | `FLwp_CreateButtonControl@0xF0DDB0` | `...button.mutebtn:quickbtn`, hint `(shift/ctrl+)0..9 Mute/solo` | +0x144 MuteBtnClick · +0x1e4 MuteBtnChange |
| +0x7dc | **loop on/off button** | `FLwp_CreateButtonControl` | toggle `+0x48a\|=0x4001`, hint `Enable or disable looping` | +0x1e4 LoopBtnChange |
| +0x804 | **loop-point digiwheel** | `FLui_Ctl_DigiWheel_CreateMixerTrack` | hint `Set loop point`, range[-3..0x100] def -3 | OnChange +0x398 LoopPointChange · GetText +0x4ac LoopPointGetText · pressed +0x400 `…FrontFXRouteSelectPressedChange` |
| +0x7fc | **FX-route / mixer-track digiwheel** | `FLui_Ctl_DigiWheel_CreateMixerTrack` | `...digiwheel.mixertrack:digiwheel` | OnChange +0x398 `…FrontFXRouteSelectChange` · GetText +0x4ac `…GetText` · DblClick +0x1f4 `…DblClick` · pressed +0x400 `…PressedChange` |
| +0x7e4 | **sample / "replace sample" button** | `FLwp_CreateButtonControl` | toggle `0x4001`, hint `shift Replace the channel's sample` | +0x164 SampleBtnMouseMove (clone style from template `*0x14A8BF8+0x938`) |
| +0x80c | **channel-type glyph** | `FLui_ChanRack_CreateTypeGlyph@0xF0E160` (TQuickLabel, font `P_ILGlyphs`) | icon set per type in ArrangeChannels: Automation/Layer/MIDI/Internal-controller | — |

Other channel fields used by the strip: `+0x7a4` = channel index (passed to `FLcr_SelectOneChannelByIndex`),
`+0x35d`/`+0x35c` = collapsed/selected flags, `+0x9c` = current row height, `+0x190`(400) = channel type,
`+0x288` = mixer-track value, `+0x748` = sample-data ptr.

### Handler semantics (verified)
- **NameBtnClick** `0xF1BBA0`: single-click → press; on release inside → posts WP msg 0x200/0x202 (open
  channel settings / context); double-click (`btn==4`) → open the channel's plugin editor
  (`TPluginForm.FXRouteSelectDblClick`); shift/ctrl → `FLcr_SelectOneChannelByIndex(chan+0x7a4)` +
  `FLui_ChanRack_ArrangeChannels(form,1,1)`.
- **SelectPanelMouseDown** `0xF1C2B0`: plain click → `FLcr_SelectOneChannelByIndex`; ctrl-click (`shift&0x40`)
  → range-select `FUN_010E4080`; otherwise `FLcr_SetChannelSelectedFlag` (toggle multi-select).
- **MuteBtnClick** `0xF1BE70`: left → solo (`FUN_00F12AE0`); right/middle → toggle mute (`FUN_00F12940`),
  wrapped in an undo step "knob tweak (mute / solo)".
- **LoopBtnChange** `0xF1C030`: enable/disable channel looping (`FUN_011D1B20` / `FUN_011D1C00` on chan+0x7a4).

`FLwp_CreateButtonControl@0xF0DDB0` = the channel-rack button helper: `FLui_WP_CreateControl(&0x715520,1,0)` →
`FLui_WP_SetShowing(0)` → SetParent into theme container `*(*0x14A8BF8+0x7a0)`. (re/ui-02 §3.)

---

## 5. The channel layout / arrange engine (the reuse core)

### `FLui_ChanRack_Relayout@0xF46C90` — relayout entry (FormResize → here)
Guarded by `*0x14A98D8 != 0` (any channels). `BeginUpdate(form)` → `ComputeColumnMetrics(form)` →
`UpdateVScrollRange()` → `LayoutTopBar(form)` → `FLui_Layout_RealignChildRegion(form,0)` → `EndUpdate(form)` →
`form->vtbl[0x178]` (Invalidate). This is the master relayout for the whole window.

### `FLui_ChanRack_ComputeColumnMetrics@0xF48C00` — column-X table + panel widths
Computes the per-slot left offsets (`DAT_0157f738[]`) and the running X cursor `DAT_0157f710` (= grid start X)
from dpi-scaled column widths (`round(dpi×{4,6,16,18,41,…})`), the vscroller width (`+0x53c+0x664`), the
add-channel button width (`FUN_006b89c0`), and a "show advanced controls" flag (`FUN_010ED270`). Also computes
`DAT_0141e584` (visible step count) and sets the LEFT container `form+0x770` / back-grid `form+0x7a8` widths.

### `FLui_ChanRack_ArrangeChannels@0xF50340` — **per-channel arrange + show/hide (the "draw channels" routine)**
Signature `(form, bDoExtra, bAutoSize)`. Called from ~45 sites (channel add/clone/zip/delete, pattern change,
send-to-playlist, mixer assign, etc.) — it is the canonical "rebuild + re-lay-out all channel strips" pass.
Flow:
1. Invalidate+BeginUpdate the scroll controls (`form[0xed]`, `form[0xf4]`); compute grid width into `*0x14AB940`.
2. `ComputeColumnMetrics(form)`.
3. **Pass A** over the full channel list `*0x14A98D8`: per channel set each strip control's *value/state*
   (`chan+0x18` = channel id into control's value slot; `FLwp_SetControlValue`/`FUN_005d1480` redraw): name
   button id, pan/vol wheel values, mixer-track digiwheel target + hint (linked vs unlinked), loop/sample/glyph.
4. **Pass B** over the **display list `*0x14A7968`** (filtered/grouped order): maintain a row-Y cursor
   `local_dc` and (for collapsed/grouped layout) a multi-column wrap (`local_e0`/`local_c4`). For each channel:
   - `SetBounds` every strip control via `vtbl[0x188]` using `GetColumnX(buf,slot)` for X and
     `rowY + ((rowHeight - ctrlH)>>1)` for vertical centering; widths from the column table / control defaults.
   - `FLui_WP_SetShowing(ctrl, !collapsed)` per control; conditionally show FX-route/loop digiwheels by the
     pattern mode (`FUN_011D3590(...)==2/3`) and the `form[0x114]+0x80` advanced flag.
   - Set the type glyph char + tooltip by channel type (`chan+0x190`): 5=Automation clip, 3=Layer, MIDI, else
     Internal controller.
   - Advance `rowY` by the wrapper height; the row wrapper (`chan+0x7bc`) is SetBounds to the full row.
5. Size the horizontal content panel `form[0xee]` to grid width; position the back-grid `form[0xf5]`, header
   `form[0xf3]`, and right control `form[0xf7]`; EndUpdate; optional `AutoSizeWindow` (bAutoSize) +
   `LayoutTopBar` (if `form[0x124]` shown).

So **"how the rack lays out channels"** = build-once (`FLwp_BuildChannelRackControls`, §4) + arrange-every-
refresh (`ArrangeChannels`): an explicit VCL-style `SetBounds` per control per row, X from a column table, Y
from a row cursor, visibility from channel flags. No flow/auto-layout (consistent with re/ui-06).

### `FLui_ChanRack_LayoutTopBar@0xF44EE0` — top control-bar chain
Chains the top-bar controls (`form+0x938 → +0x950 → +0x9b0 → +0x9c8` via `FUN_00F44EA0`), clamps the
genre/pattern selector width (max round(dpi×164)), shows/hides by available width. (Top bar widgets =
GenreSelect/KeySelect/RootNote/Scale/KitPopup/PatternLengthSelect/Shuffle/etc. — see §7 handlers.)

### `FLui_ChanRack_GetLoopColWidth@0xF53990` — extra width reserved for the loop/mixer column
Returns extra strip width (loop digiwheel / mixer-track width) depending on pattern mode, + the add-channel
button width (`FUN_00CAFDF0(*0x14A8EA0)`).

### `FLui_ChanRack_AutoSizeWindow@0xF46CF0` — fit the rack window to content
Computes the preferred window size: width = (steps rounded UP to the beat group `*0x14A9368`) × step width
`*0x14A7618` + strip width + insets; height from visible rows; applies via `FUN_007E42D0` (vector-form set
size). Honors docked vs floating (`form+0x78` parent) and a max-width cap (round(dpi×128) per beat-group chunk).

---

## 6. Step grid: scroll / pan / input (and the paint gap)

The back-grid (`form+0x7a8`, "BackWP") is one custom-painted scrolling surface. The form's published handlers
(RTTI-named, tagged) cover **scroll/pan**, not per-step toggling:
| handler | addr | role |
|---|---|---|
| `TStepSeqForm.BackWPMouseDown` | 0xF47A30 | → `FLui_ChanRack_GridPanBegin` |
| `FLui_ChanRack_GridPanBegin` | 0xF47AE0 | **right-button (btn==2) drag-pan begin**: snapshots anchor `model+0x5fc = x + hScrollSteps×stepWidth`, `model+0x600 = y + vscroll`, sets pan state `model+0x604=1`, sets hand cursor |
| `TStepSeqForm.BackWPMouseMove` | 0xF47A70 | during pan: `hScroll = round((anchorX - x)/stepWidth)` → `FUN_006CFAC0(backgrid, steps,1,1)` |
| `TStepSeqForm.BackWPGesture` | 0xF47CB0 | touch/gesture 0x104 pan (same anchor math, uses `model+0x53c` scroller) |
| `TStepSeqForm.BackWPAdjustScroller` | 0xF479B0 | wires the vscroller (`model+0x53c`) thumb size (`*0x14A8C48`) + OnChange → `FLui_ChanRack_OnVScroll`; saves the original cb @form+0xa04/+0xa0c |
| `FLui_ChanRack_OnVScroll` | 0xF456F0 | vscroll changed → chain saved cb + reposition the docked Graph Editor (`*0x14AB2C8`, `TGraphEditorForm.RepositionEditor`) |
| `FLui_ChanRack_BackGridHintDispatch` | 0xF47990 | back-grid hint/value provider: `FL_DispatchCommand(id@+0x18, val@+0x3c0, 4)` |
| `TStepSeqForm.FormMouseWheel` | 0xF49480 | wheel over grid (scroll) |

**Open gap (the only one): the per-pixel STEP CELL paint + the LEFT-click step toggle.** These are NOT form
methods — they live in the **back-grid control's own class** (its WP message-0xf paint handler + its mouse
message handler), which reads each channel's step data and blits cells using the §3 metrics
(`*0x14AB898` cell width, `*0x14A8C48` row height, `*0x14AB940` content width, `DAT_0141e584` visible steps,
`*0x14A9368` beat group for accent coloring). The grid-width global `*0x14AB940` is read only by ArrangeChannels
and the GetColumnX accessor only by ArrangeChannels, confirming the cell drawing is delegated to the control
class. Resolving that control's class VMT + paint is the follow-up (needs the back-grid creation site, which is
DFM/resource-driven, or a live probe of `*(form+0x7a8)`'s vtbl).

---

## 7. Add-channel + the rest of the published UI surface (RTTI-named, tag UI_win_chanrack)

**Add channel:** `TStepSeqForm.AddChanBtnMouseDown@0xF48B70`: left → `FLcr_OpenAddChannelPicker(0,0,-10)` (the
`TPlugListForm` glass picker, re/ui-03 §5); right → main-form add-channel menu (`*0x14A8750+0x1f98`).
`TStepSeqForm.AddChanBtnBeforePopup@0xF48B10` → `FLmenu_ShowPopup` of the add-channel menu (`*0x14A8750+0x760`).

Full published-method surface (all RTTI-named `TStepSeqForm.*`, tagged): lifecycle `FormCreate/FormActivate
@0xF45580 /FormDeactivate@0xF45410 /FormDestroy@0xF454D0 /FormResize@0xF47EC0 /FormCanResize@0xF457D0
/FormKeyDown@0xF467D0 /FormKeyUp@0xF46BB0 /FormMouseWheel@0xF49480`; grid `BackWP*` (§6); channel-list/grid
**splitter** `SelectButtonSplitter{Change@0xF4E300, Changing, DblClick, MouseDown, MouseHover, MouseUp,
Paint@0xF4EE10}` (drag = resize the LEFT strip vs RIGHT grid boundary; Change also repositions the docked Graph
Editor); top bar `GenreSelect* / KeySelect* / KeyBtnMouseUp / RootNoteClick / Scale{MenuClick,PopUpPopup} /
KitPopupPopup / PatternLengthSelect{Change@0xF47630,GetText,Release} / PatLengthResetMenuClick / ShuffleSliderChange
/ TempoSyncBoxClick / TimeScrollerChange / PosLEDPanelPaint / DropPosIndicatorPaint`; loop bar `LoopCtrlBtnClick /
LoopMenuPopup / LoopOptionsMenuPopup / LoopSelectClick / SSLoopDisabledMenuClick`; transport/util `SSPlayBtn{Click,
MouseDown} / SongStarterBtn* / GraphEditorBtnClick@0xF46C10 / SendToPlaylistBtnClick / ReplaceAllSamplesBtnMouseUp /
IntSSBtnClick`; group/filter `FilterSelect{BeforePopup,Change,GetItemFlags,MouseDown} / SSFilter{Add,Del,Rename}MenuClick
/ SSGroupMenuPopup`; burn-to-pattern `{BurnAllLoops,CtrlBurnTo,SimpleBurnTo}PatternClick / CtrlAutoNextStepMenuClick /
LSGenerateSelectedStepsMenuClick / LSReplaceSelectedSamplesMenuClick / LSShowWhenEnablingMenuClick /
ShowAdvancedControlsMenuClick`; panels `TopPanel{Gesture,MouseDown,MouseMove,MouseUp,Resize} / BottomPanel{MouseDown,
MouseMove}`.

---

## 8. Reuse notes (render similar UI / drive/read the rack visually)

- **Drive the rack visually (read state):** the per-channel strip controls are at fixed offsets on the channel
  object (`FLcr_ChannelListGetItem(*0x14A98D8, idx)` + §4 table). Read e.g. vol wheel value @`chan+0x7f4+0x450`,
  pan @`chan+0x7ec+0x450`, mute via the mute btn `chan+0x7cc+0xc4`, FX-route digiwheel @`chan+0x7fc+0x450`
  (mixer track), selected/collapsed via `chan+0x35c/+0x35d`, type via `chan+0x190`. To **set** values reuse the
  control's setters (wheel value @+0x450, `FLwp_SetControlValue` @+0xc4) then call its OnChange thunk — or call
  the engine handlers directly (mute `FUN_00F12940`, solo `FUN_00F12AE0`, select `FLcr_SelectOneChannelByIndex`,
  loop `FUN_011D1B20/…1C00`). After mutating, call `FLui_ChanRack_ArrangeChannels(*0x14A8BF8, 1, 1)` to refresh.
- **Render a similar channel strip in OUR UI:** copy the §4 build sequence — create each control with its
  classRef + skin descriptor (`forms.channelrack.controls.{button.mutebtn:quickbtn, wheel.{pan,volume}:wheel,
  digiwheel.mixertrack:digiwheel}`), parent under a TQuickContainer, then position with explicit `SetBounds`
  (column-X + row-Y) exactly like `ArrangeChannels` — there is no auto-layout to lean on.
- **Render a step grid:** one scrollable WP custom control sized to `visibleSteps×stepWidth (*0x14AB898) ×
  rows×rowHeight (*0x14A8C48)`; scroll horizontally in step units; paint cells yourself from channel step data
  (FL's own back-grid paint is the model — gap §6). Beat accent every `*0x14A9368` steps.
- **Window plumbing:** `TStepSeqForm` docks by `SetParent(form, *(*0x14A8750+0xb18))` (the dock host) in
  FormCreate — same primitive as re/ui-06 §6; it is a registered singleton `*0x14A8BF8` shown/hidden via the
  window manager (re/22). The metrics globals (§3) are dpi-scaled at FormCreate and on relayout.

---

## 9. Ghidra annotations made this pass (tag `UI_win_chanrack`, 44 funcs)
**Created tag** `UI_win_chanrack` (with description). **Renamed** (unnamed `FUN_*` → `FLui_ChanRack_*`):
`Relayout@0xF46C90`, `ComputeColumnMetrics@0xF48C00`, `UpdateVScrollRange@0xF47DF0`, `LayoutTopBar@0xF44EE0`,
`ArrangeChannels@0xF50340`, `GetColumnX@0xF50320`, `GetLoopColWidth@0xF53990`, `AutoSizeWindow@0xF46CF0`,
`GridPanBegin@0xF47AE0`, `OnVScroll@0xF456F0`, `BackGridHintDispatch@0xF47990`, `UpdateVisibleSteps@0xF450B0`,
`CreateTypeGlyph@0xF0E160`, and the strip event handlers `NameBtnClick@0xF1BBA0`, `NameBtnChange@0xF1BA60`,
`NameBtnGetText@0xF1BB60`, `SelectPanelMouseDown@0xF1C2B0`, `SelectPanelMouseMove@0xF1C350`,
`SelectPanelMouseUp@0xF1C370`, `MuteBtnClick@0xF1BE70`, `MuteBtnChange@0xF1BE00`, `LoopBtnChange@0xF1C030`,
`LoopPointChange@0xF1C080`, `LoopPointGetText@0xF1C240`, `SampleBtnMouseMove@0xF51DB0`. **Tagged** (kept their
authoritative RTTI names): `TStepSeqForm.FormCreate@0xF45870` + the builder `FLwp_BuildChannelRackControls@0xF0E330`
+ helper `FLwp_CreateButtonControl@0xF0DDB0` + the lifecycle/grid/splitter/add-channel published methods (§6/§7).
Program saved. Convention warnings (non-PascalCase) are expected — the `FLui_<Area>_<Name>` style matches Wave-1.

Cross-area finds (noted, NOT renamed — for other agents): `FLcr_ChannelListGetItem`, `FLcr_SelectOneChannelByIndex`,
`FLcr_SetChannelSelectedFlag`, `FLcr_OpenAddChannelPicker` (channel-rack engine, re/control catalog);
`TFruityLoopsMainForm.Front{VolWheelChange,VolWheelHint,FXRouteSelect*}` + `CtrlKeyBtnMouseActivate` (main-form
shared strip callbacks); `TGraphEditorForm.RepositionEditor` (graph editor); `FUN_007E42D0` vector-form set-size
(ui-03/ui-04). Mute/solo/loop engine ops `FUN_00F12940/00F12AE0/011D1B20/011D1C00`.

## 10. Confidence + open items
- **HIGH:** class/descriptor/singleton; the two-region split (LEFT strip container `*(form+0x7a0)` + RIGHT
  back-grid `form+0x7a8`); the build-once (`FLwp_BuildChannelRackControls`, full strip control table @chan+0x7bc..)
  + arrange-every-refresh (`ArrangeChannels`) layout model; the relayout chain; the column-X table + cached
  pixel metrics; grid scroll/pan input; add-channel; the strip event-handler map (verified by decompile).
- **MED:** exact identity of a few form sub-controls (`form[0xec/0xed/0xf3/0xf7/0xf9]` — positioned but role
  inferred); the `+0x804` "loop-point" digiwheel vs a sample-start control (named by its hint).
- **OPEN (one gap):** the back-grid control's class + its **step-cell paint** and **left-click step-toggle**
  (the control is DFM/resource-created; not reachable from the form methods). All metrics it consumes are mapped
  (§3); resolving needs the back-grid creation site or a live vtbl probe of `*(form+0x7a8)`.
