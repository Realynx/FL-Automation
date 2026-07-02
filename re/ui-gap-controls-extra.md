# ui-gap-controls-extra — G7/G8/G9 residual control-type polish (Wave 4)

Closes the three LOW/LOW-MED control gaps left open by re/ui-02-controls.md §7 and re/ui-gaps.md:
**G7** `TQuickMIDIKb` family construction + note/mouse interface wiring, **G8** `TQuickTabSelector`/Sheet
programmatic tab-add, **G9** `TQuickPaintBox` exact OnPaint TMethod offset. Static Ghidra on
`FLEngine_x64.dll`, image base `0x400000` (all addrs are Ghidra addrs at that base; runtime =
`ghidra - 0x400000 + flEngineBase`). RE only — no source edits. All new function names use the established
`FLui_Ctl_*` convention + tag `UI_controls`.

---

## G9 — TQuickPaintBox OnPaint TMethod offset  ✅ RESOLVED (corrects the ui-02 candidate)

`TQuickPaintBox` VMT 0x7a3258, size **0x3b8**, base `TQuickGraphicControl` 0x7fbca8 (non-windowed).

**OnPaint TMethod = code `+0x398` / Self `+0x3a0`.**  Confirmed from the one host that wires it in **explicit
code** (all other `*PaintBoxPaint` handlers are DFM-bound via published-method tables, so only this site shows
the store):

`TMsgForm` plugin-list builder `FUN_00a88d70` (paintbox is `*(form+0x7d0)`):
```c
lVar9 = *(longlong *)(param_1 + 0x7d0);            // the "PluginsPaintBox" TQuickPaintBox
*(void **)(lVar9 + 0x3a0) = param_1;               // OnPaint Self  (= the form)
*(code **)(lVar9 + 0x398) = TMsgForm_PluginsPaintBoxPaint;   // OnPaint code
```

Why the ui-02 §7.1 candidate **+0x49c/+0x4a4 is WRONG**: `0x4a4+8 > 0x3b8` (the instance size) — it is out of
bounds for a paintbox. `+0x398/+0x3a0` fits (`0x3a0+8 = 0x3a8 < 0x3b8`). This is the **`TQuickGraphicControl`
"primary callback" slot** — the same physical TMethod that wheels use for **OnChange** (ui-02 §5 lists
`+0x398/+0x3a0` as OnChange `ctrl[0x73]/[0x74]`). Each graphic-control subtype repurposes that one slot for its
primary event: wheel→OnChange, **paintbox→OnPaint**.

Handler signature: `void OnPaint(Self /*+0x3a0*/, paintbox /*Sender*/, …)` — draws into the paintbox canvas
(`ctrl+0x304`, the WP canvas; ui-04). Other confirmed paintbox handlers (all DFM-bound to the SAME `+0x398`
slot): `TRenderForm.ProgressPaintBoxPaint@0xdd1940`, `TToolbarForm.FpsMeasurePaintBoxPaint@0xcbc300`,
`TThemeEditorForm.PalettePaintboxPaint@0x1036330`, `TCloudAccountsForm.{SoundCloud,YouTube,Status}PaintBoxPaint`.

**Reuse (create → wire OnPaint):**
```c
pb = FUN_007ce7d0(&PTR_..._007a3270 /*=0x7a3258+0x18*/, 1, 0);   // TQuickPaintBox.Create (ui-02 §7.1)
FLui_WP_SetAlign(pb, 0x0f);                                       // or vtbl[0x188] SetBounds
*(void**)(pb + 0x3a0) = myCtx;                                    // Self  (write first)
*(code**)(pb + 0x398) = myOnPaintThunk;                           // code  (draws into pb->canvas @+0x304)
/* parent + vtbl[0x200](pb,1) show, per ui-02 §4 */
```

---

## G7 — TQuickMIDIKb family: constructors + NoteOn/NoteOff/Mouse wiring  ✅ RESOLVED

The on-screen keyboard classes are **DFM/descriptor-created** (VMTs 0xb47788..0xb48d40 have **no code xref**, and
the classRefs `VMT+0x18` are only referenced by the class hierarchy self-links — confirmed). So there is no
direct ctor call from a host; the class's **virtual constructor lives in VMT slot `+0x90`** and is invoked by the
generic Delphi create path (`FUN_00410320` NewInstance → ctor → `FUN_00410360` AfterConstruction). The full
constructor chain (each calls its parent's ctor) — now renamed + tagged:

| class | VMT | size | ctor (VMT+0x90) | new name |
|---|---|---|---|---|
| `TQuickMIDIKb_Visual` | 0xb47788 | 0x3d0 | 0xb4ae80 | `FLui_Ctl_MIDIKbVisual_Ctor` |
| `TQuickMIDIKb` | 0xb47f78 | 0x400 | 0xb4b5d0 | `FLui_Ctl_MIDIKb_Ctor` |
| `TQuickMIDIKbEx` | 0xb48368 | — | 0xb4be10 | `FLui_Ctl_MIDIKbEx_Ctor` |
| `TQuickVectorMIDIKb` | 0xb48800 | — | 0xb4ca60 | `FLui_Ctl_VectorMIDIKb_Ctor` |
| `TQuickVectorMIDIKbRange` | 0xb48d40 | — | 0xb4ed10 | `FLui_Ctl_VectorMIDIKbRange_Ctor` |

The two base VMTs (Visual/MIDIKb) are byte-identical **except slot +0x90** (the ctor) — the keyboard does NOT
override the WP mouse VMT slots; it processes notes inside its own overridden mouse handlers (below) after
calling the base mouse dispatch (which fires the standard WP mouse TMethods).

### Constructor field setup
- `FLui_Ctl_MIDIKbVisual_Ctor@0xb4ae80`: base-init `FUN_007ffbd0`; **+0x3a8 = -1** (last-velocity/none);
  flag `FUN_00801520(self, *(self+0x320)|1)`; **+0x39c = 0x7f** (max note = 127); **+0x3a0 = active-notes list**
  (`FUN_0064f2c0(&PTR_FUN_0064f008,1,0)` = a TQuickList of u16 note numbers).
- `FLui_Ctl_MIDIKb_Ctor@0xb4b5d0`: parent ctor + **+0x3e8 (dec 1000) = -1** (current note), **+0x3ec = -1**
  (current layer/velocity var).
- `FLui_Ctl_MIDIKbEx_Ctor@0xb4be10`: font `P_SmallBold`; **+0x400/+0x404 = note-range low/high** (default
  0x100? set 0x100/0x3c here as label metrics); mode-flags **+0x3f4 = 3**.
- `FLui_Ctl_VectorMIDIKb_Ctor@0xb4ca60`: builds the **geometry helper @+0x3b0** (`FUN_00b49dd0(&…_00b47568,1,
  self,*(self+0x420))` — a piano-key layout object; vtbl[0x90]=KeyRect, vtbl[0x98]=PointToNote, +0x88=orientation
  byte, +0x80=key-count, +0x84=span); key colors **+0x434/+0x438/+0x43c/+0x448**; calls
  `FLui_Ctl_VectorMIDIKb_RecalcLayout@0xb4ce40`.
- `FLui_Ctl_VectorMIDIKbRange_Ctor@0xb4ed10`: **+0x460 = 4** (layer count), **+0x464 = 2** (layer index) — the
  velocity-layer strip.

### The reuse surface — event TMethods poked on the instance (`{code@off, Self@off+8}`)
Found from the keyboard's **own** internal note dispatch (it fires these when a key is hit):

| event | code / Self | fired by | invocation |
|---|---|---|---|
| **OnNoteOn** | `+0x3c8` / `+0x3d0` | `FLui_Ctl_MIDIKb_SetActiveNote@0xb4b750` | `code(Self, keyboard, note:int, velocity:int)` |
| **OnNoteOff** | `+0x3d8` / `+0x3e0` | `FLui_Ctl_MIDIKb_MouseDown@0xb4b8f0` (all-notes-off loop) | `code(Self, keyboard, note:int, velocity=0x40)` |
| MouseDown | `+0x144` / `+0x14c` (inherited WP) | base dispatch `FUN_005d34b0` inside `…_MouseDown@0xb4b8f0` | `code(Self, kb, btn, shift, x, y)` |
| MouseMove | `+0x164` / `+0x16c` (inherited WP) | base dispatch `FUN_005d3b20` inside `…_MouseMove@0xb4bb50` | " |
| MouseUp | `+0x154` / `+0x15c` (inherited WP) | base WP mouse-up | " |

`SetActiveNote@0xb4b750` is THE note engine: clamps to `[+0x398 min, +0x39c max]`, and when the active note
changes it calls `NoteOnInternal@0xb4b290` then fires OnNoteOn `(*(self+0x3c8))(*(self+0x3d0), self, note, vel)`;
old notes go through `NoteOffInternal@0xb4b3b0`. `MouseDown@0xb4b8f0` on right-button clears all held notes
firing OnNoteOff `(*(self+0x3d8))(*(self+0x3e0), self, note, 0x40)` per note in the `+0x3a0` list.

**LayerChange:** host handlers exist (`TDWPRenderForm.MIDIKbLayerChange@0xb8d0e0`,
`TMEWAVPropForm@0xba8190`, `TPRKeyLimitForm@0xcd9c90`, `TPluginForm@0xe8c1d0`, `TSpeechForm@0xefc510` — all
DFM-bound). It is an **Ex/Vector-level** callback (not present in the base `TQuickMIDIKb` ≤0x400 struct, which
holds only the two note TMethods above); its exact TMethod slot lives in the Ex/Vector extension region and was
not pinned this pass (no explicit code store — DFM-bound only; not needed for basic keyboard reuse). NoteOn/Off +
mouse fully cover the on-screen-keyboard reuse surface.

### Supporting methods renamed (all tagged UI_controls)
`FLui_Ctl_MIDIKb_PointToNote@0xb4b4e0` (x/y → note via geometry-helper vtbl[0x98], clamped to +0x398/+0x39c;
origins +0x3c0/+0x3c4) · `…_ProcessTouchInput@0xb4bc50` (multi-touch → SetActiveNote per point) ·
`…_AllNotesOff@0xb4b470` · `…_ScrollToNote@0xb4b120` (sets +0x398 + scroll) · `…_SetScrollOffset@0xb4b0f0`
(+0x3c0) · `…_Paint@0xb4eed0` (Ex/plain key draw) · `FLui_Ctl_VectorMIDIKb_DrawKeys@0xb4dbb0` (vector-range key
+ velocity-layer draw) · `…_RecalcLayout@0xb4ce40` · `…_NoteOnInternal@0xb4b290` · `…_NoteOffInternal@0xb4b3b0`.

### Reuse recipe (on-screen keyboard)
```c
// 1. CREATE via the descriptor/generic path (VMT selects the variant); pick the class you need:
//    plain keyboard = TQuickMIDIKb (0xb47f78); with velocity layers = TQuickVectorMIDIKbRange (0xb48d40).
kb = FLwp_CreateControl(&(0xb47f78 + 0x18), 1, parentForm);   // routes to VMT+0x90 ctor
// 2. RANGE + BOUNDS:  *(int*)(kb+0x398)=firstNote; *(int*)(kb+0x39c)=lastNote; vtbl[0x188](kb,x,y,w,h);
// 3. WIRE (Self first, then code):
*(void**)(kb+0x3d0)=ctx; *(code**)(kb+0x3c8)=onNoteOnThunk;   // OnNoteOn(ctx, kb, note, vel)
*(void**)(kb+0x3e0)=ctx; *(code**)(kb+0x3d8)=onNoteOffThunk;  // OnNoteOff(ctx, kb, note, vel)
// (optional raw mouse: +0x144/+0x14c, +0x164/+0x16c, +0x154/+0x15c)
// 4. parent + vtbl[0x200](kb,1) show.  Keyboard fires OnNoteOn/Off itself on click/drag.
```

---

## G8 — TQuickTabSelector / Sheet programmatic tab-add  ✅ RESOLVED

`TQuickTabSelector` VMT 0x740de8 (cr 0x740e00, size 0x5a0); base `TQuickCustomTabSelector` (init
`FLui_Ctl_CustomTabSelector_BaseInit@0x742350`); ctor `FLui_Ctl_TabSelector_Ctor@0x743a40` (VMT+0x90). Neither
has a code xref (descriptor/DFM-built — confirms the gap). **The pager auto-populates from a caption
string-list.**

### The two lists (do not confuse them)
- **`selector+0x57c` = the built tab-BUTTON objects** (visual). A TQuickList allocated by
  `…_BaseInit@0x742350` (`FUN_0064f2c0(&PTR_FUN_0064f008,1,1)`). Count = `*(int*)(*(sel+0x57c)+0x10)`; iterate
  buttons with `sel->vtbl[0x298](sel, i)` (each button: font @+0x340). This is the read/skin side (ui-02 §7.4,
  and the shared skin dispatcher `FUN_0076a6e0`).
- **`selector+0x58c` = the caption ITEMS list** — a WP string-list (**class 0x4cd480**, the same class the
  combo uses for its items, ui-02 §7.3). Created in `…_Ctor@0x743a40`, with a **change-callback**
  `FLui_Ctl_TabSelector_RebuildTabs@0x743de0` stored at `list+0x50` (back-ref `list+0x58=selector`).

### Add-a-tab = add a caption to the +0x58c list → auto-rebuild
`FLui_Ctl_TabSelector_RebuildTabs@0x743de0` (fires whenever the +0x58c list changes):
1. Frees every existing tab-button in `+0x57c`, then clears that list (`vtbl[0]`/`vtbl[8]`).
2. `n = capList->vtbl[0x28]()` (Count) on `*(sel+0x58c)`.
3. For each i: `capList->vtbl[0x18](capList,&s,i)` (GetString) → **`btn = FLui_Ctl_TabSelector_CreateTabButton
   @0x743160(sel, sel, s)`** → append `btn` into the `+0x57c` list → **`FLui_Ctl_TabSelector_PlaceTabButton
   @0x743460(sel, btn, 0)`** (parent/position it).
4. Clamps the active index (`vtbl[0x2c0]`) into `[0, n-1]`.

So there is **no standalone `AddTab(caption)`** — you drive the caption list and the selector rebuilds. The WP
string-list API (class 0x4cd480, ui-02 §7.3): `vtbl[0x78]`=Add(ustr), `vtbl[0x90]`=Clear, `vtbl[0x28]`=Count,
`vtbl[0x18]`=GetString; bulk `FUN_0050e8a0(list, "a,b,c")` (SetCommaText).

```c
// add tabs to a TQuickTabSelector:
list = *(void**)(sel + 0x58c);            // the caption string-list (class 0x4cd480)
list->vtbl[0x78](list, L"Tab A");         // -> RebuildTabs@0x743de0 builds+places the button
list->vtbl[0x78](list, L"Tab B");
// or in one shot: FUN_0050e8a0(list, "Tab A,Tab B,Tab C");
// read/set active: FLwp_SetControlValue(sel, idx)  /  peek int @sel+0xc4
```

### Sheet variant (TQuickSheetSelector / TQuickSheet)
`TQuickSheet` VMT 0x741748 (cr 0x741760), ctor `FLui_Ctl_Sheet_Ctor@0x744860` (VMT+0x90): base container init
`FUN_007d02f0`, flags `+0xa0 |= 0x401`, `SetAlign(0xd)`. A sheet does **not** self-register with a selector; a
`TQuickSheetSelector` (0x7412d8) is the tab strip paired with sheet pages, driven the same way (its own
`+0x58c`-style item list). `FLui_Ctl_Sheet_GetValueSlot@0x744080` maps a page object to its value slot
(`TQuickSheet → +0x530`, the sibling sheet class 0x741da8 → +0x644). For the browser's concrete clone-tab flow
see `FLbrz_AddTabCloneOfSource@0x9ac910` (re/14) — a specialization, not the generic path.

---

## Ghidra changes applied this pass (24 funcs renamed `FLui_Ctl_*` + tagged `UI_controls`)

MIDIKb (18): `FLui_Ctl_MIDIKbVisual_Ctor@0xb4ae80`, `FLui_Ctl_MIDIKb_Ctor@0xb4b5d0`,
`FLui_Ctl_MIDIKbEx_Ctor@0xb4be10`, `FLui_Ctl_VectorMIDIKb_Ctor@0xb4ca60`,
`FLui_Ctl_VectorMIDIKbRange_Ctor@0xb4ed10`, `FLui_Ctl_MIDIKb_PointToNote@0xb4b4e0`,
`FLui_Ctl_MIDIKb_SetActiveNote@0xb4b750`, `FLui_Ctl_MIDIKb_MouseDown@0xb4b8f0`,
`FLui_Ctl_MIDIKb_MouseMove@0xb4bb50`, `FLui_Ctl_MIDIKb_ProcessTouchInput@0xb4bc50`,
`FLui_Ctl_MIDIKb_AllNotesOff@0xb4b470`, `FLui_Ctl_MIDIKb_ScrollToNote@0xb4b120`,
`FLui_Ctl_MIDIKb_SetScrollOffset@0xb4b0f0`, `FLui_Ctl_MIDIKb_Paint@0xb4eed0`,
`FLui_Ctl_VectorMIDIKb_RecalcLayout@0xb4ce40`, `FLui_Ctl_VectorMIDIKb_DrawKeys@0xb4dbb0`,
`FLui_Ctl_MIDIKb_NoteOnInternal@0xb4b290`, `FLui_Ctl_MIDIKb_NoteOffInternal@0xb4b3b0`.

Tab/Sheet (6): `FLui_Ctl_TabSelector_Ctor@0x743a40`, `FLui_Ctl_CustomTabSelector_BaseInit@0x742350`,
`FLui_Ctl_TabSelector_RebuildTabs@0x743de0`, `FLui_Ctl_TabSelector_CreateTabButton@0x743160`,
`FLui_Ctl_TabSelector_PlaceTabButton@0x743460`, `FLui_Ctl_Sheet_Ctor@0x744860`.

G9 needed no rename (the OnPaint offset is a fact; the wiring site `FUN_00a88d70` is TMsgForm-owned, out of lane).
Program saved. Discover all via `search_functions_by_tag UI_controls`.

## Verdict
G7, G8, G9 closed for **reuse**: paintbox owner-draw wiring, on-screen-keyboard create+note/mouse wiring, and
generic pager tab-add are now concrete recipes. Only residual = the MIDIKb **LayerChange** exact TMethod slot
(Ex/Vector-level, DFM-bound, non-blocking). This exhausts the G5-G11 controls-lane polish.
