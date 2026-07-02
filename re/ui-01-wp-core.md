# WP widget framework CORE + control lifecycle (ui-01)

Area: the BASE widget/control class + the create→parent→bounds→realize→render→destroy lifecycle that
EVERY FL UI control is built on. Module `FLEngine_x64.dll`, image base 0x400000 (Ghidra). Builds on
re/13 (WP widgets), re/14 (forms/HWND), re/22 (window host). Convention: funcs `FLui_WP_*`, tag
`UI_wpcore`, plate-commented in Ghidra. Rebase at runtime: `runtime = ghidra - 0x400000 + flEngineBase`.

FL's UI = a custom **"WP" widget framework written in Delphi** (Object Pascal classes, Delphi VMTs,
Delphi UnicodeStrings, TMethod event slots). Every on-screen control (button, knob/wheel, edit, tree,
toolbar, panel, the data-browser, even forms' client widgets) is a subclass of one base control class,
here called **TFLWPControl** (FL's internal name family: TQuickBtn / TQuickEdit / TMyTransBtn / ...).

---

## 1. Class hierarchy (Delphi)

```
TObject
  └─ (FUN_007ffbd0 ctor)           generic component root (TWPComponentBase)
       └─ FLui_WP_BaseComponentCtor @0x7a6850   (TWPComponent: +0x3c4=MAXINT, +0x3c8=2)
            └─ FLui_WP_CreateControl @0x717a90   **TFLWPControl = THE BASE CONTROL CLASS**
                 ├─ TQuickBtn         VMT @0x715520  ctor=FLui_WP_CreateControl (no ctor override)
                 ├─ TMyTransBtn       VMT @~0x715780 (RTTI name follows TQuickBtn VMT)
                 ├─ TQuickEdit        VMT @0x7466b8  ctor=FUN_0074c400 @0x74c400  (text control)
                 ├─ wheel/knob        VMT @0x7a0ca0  (re/13 FLwp_CreateWheelWithSkin)
                 └─ tree / toolbar / panel / browser / ... (ui-02 controls catalog)
```

- A **classRef** (Delphi class pointer) == the VMT base address. `NewInstance(VMT)` (VMT-0x30) reads the
  instance size from VMT-0x80. The **constructor is VMT slot +0x78** (per class). During construction the
  chain also installs the **WndProc TMethod** (ctor FUN_005ce910: `inst+0x80 = VMT[0x150]`, `inst+0x88 =
  inst`) and the per-control **canvas** (`FLui_Paint_CreateFormCanvas@0x7fde80` via FUN_007ffbd0).
- The base-control VMT (TFLWPControl) spans byte-offsets **0x000..0x240** (~73 virtual slots). Concrete
  subclasses keep these slots (overriding some) and **append their own vmethods past 0x240** (e.g.
  TQuickEdit extends to 0x288+ with text/edit methods).
- Two reference VMTs used throughout this doc: **TQuickBtn @0x715520** (thin subclass ≈ the base ABI) and
  **TQuickEdit @0x7466b8** (shows which base slots get overridden).

---

## 2. Base VMT slot map (byte offsets; idx = offset/8)

Slot = stable ABI across ALL controls (virtual dispatch); the listed address is the BASE/TQuickBtn impl.
"ovr" marks a slot TQuickEdit overrides (same meaning, different impl) — proof the slot is virtual.

| slot | base impl (TQuickBtn) | meaning / name | notes |
|---|---|---|---|
| +0x000 | 0x5d4cd0 `FLui_WP_AssignTo` | AssignTo(src,dst) — clone props | copies hint/value/bounds/enabled/events |
| +0x078 | 0x717a90 `FLui_WP_CreateControl` | **constructor** | per-class (TQuickEdit=0x74c400) |
| +0x0d8 | 0x5ceed0 | (geometry get) | ovr in TQuickEdit |
| +0x0e8 | 0x801140 | set color/region | used by AssignTo |
| +0x0f8 | 0x5ceda0 | get color/region | used by AssignTo |
| +0x110 | 0x801200 `FLui_WP_Realign` | Realign / recalc child layout + vtbl[0x1c0] | ovr (TQuickEdit 0x5db400) |
| +0x118 | 0x5d0160 | set enabled | used by AssignTo (+0xa9) |
| +0x138 | 0x5d0850 `FLui_WP_SetParent` | **SetParent(ctrl,parent)** | ovr (TQuickEdit 0x5e1550) |
| +0x150 | 0x5d2a30 `FLui_WP_WndProc` | **WndProc (message router)** | ovr (TQuickEdit 0x77b9d0) |
| +0x178 | 0x8016a0 `FLui_WP_Invalidate` | **Invalidate (full-rect repaint req)** | ovr (TQuickEdit 0x8030c0) |
| +0x180 | 0x5d1520 `FLui_WP_Repaint` | Repaint (Invalidate+Update; immediate paint if flag 0x40) | ovr |
| +0x188 | 0x802ef0 `FLui_WP_SetBounds` | **SetBounds(ctrl,x,y,w,h)** | ovr (TQuickEdit 0x802b80) |
| +0x190 | 0x5d14e0 `FLui_WP_Update` | Update (flush pending paint, bubble to root) | ovr |
| +0x1c0 | 0x71c9b0 | post-realign hook | called by Realign |
| +0x1e0 | 0x7a6950 | SetRange(idx,val) (min/max) | wheel/knob; ovr TQuickEdit 0x804c70 |
| +0x200 | 0x71d300 `FLui_WP_SetVisible` | **SetVisible/Show(ctrl,bool)** | ovr (TQuickEdit 0x5d89e0) |
| +0x270 | (TQuickEdit 0x8046c0) | set style flags | TQuickEdit-area, not base spine |
| +0x020 | 0x71cc00 `FLui_WP_ReleaseResources` | release render node/canvas (+0x304) + unlink + free child list | "destroying" vmethod |

There is **no dedicated Paint slot** — painting is message-driven (msg 0xf), see §6.2.

### 2a. NEGATIVE (Delphi system) VMT slots — classic old-Delphi/C++Builder layout, x64-doubled
The VMT base (0x715520) = offset 0 of the **user** virtual methods; Delphi RTL methods are at NEGATIVE
byte offsets. This is why the ctor is at +0x78 and the destructor is NOT at -8.

| neg off | meaning | button value |
|---|---|---|
| -0x20 | **vmtDestroy** = `FLui_WP_Destroy` | 0x717cc0 |
| -0x28 | FreeInstance (FUN_00410340) | |
| -0x30 | **NewInstance** | (= re/13/14's "metaclass create classRef-0x30") |
| -0x38 | DefaultHandler = `FLui_WP_DefaultHandler` | 0x5d2da0 |
| -0x40 | **Dispatch** (FUN_00410090, message-table lookup) | |
| -0x78 | parent-class VMT ptr | 0x714658 (button's parent class) |
| -0x80 | **instance size** | 0x4e8 (1256 bytes) |

Read the full button VMT raw at 0x715520 (688 bytes) / TQuickEdit at 0x7466b8 to re-derive any slot.

---

## 3. Base control struct field map (TFLWPControl instance)

Offsets are byte offsets into the control instance. Confirmed by decompile (re/13/14 + this pass).

| field | type | meaning |
|---|---|---|
| +0x00 | ptr | VMT (classRef) |
| +0x34 | u16 | base state flags (bit0 = "destroying/csDestroying"; gates layout) |
| +0x78 | ptr | **parent control** (set by SetParent) |
| +0x80 / +0x88 | TMethod | **WndProc** code / data (code = VMT[0x150], data = self; set by ctor FUN_005ce910) |
| +0x90 / +0x94 | int / int | **x / y** (rect left / top) |
| +0x98 / +0x9c | int / int | **w / h** (rect width / height) — SUPERSEDES re/13's +0x474/+0x476 |
| +0xa9 | byte | "showing/active in parent" flag (FLui_WP_SetShowing) |
| +0xb3 | byte | **align / layout role** (FLui_WP_SetAlign; bits 1/2/4/8 = fixed edges, 3=client) |
| +0xc4 | int | **generic control value** (FLwp_SetControlValue@0x5D0D10; wheel uses +0x450) |
| +0xd0 | ptr | geometry/region helper sub-object (vtbl[0x20]=getEdge(idx 0..3)) |
| +0xe4 | UStr | hint text (Delphi UnicodeString) |
| +0x11c | obj | **layout/site node** — re/13's lower-level link: `(*(ctrl+0x11c))->vtbl[0x10](&ctrl+0x11c, &parent+0x11c)` registers into the form layout (input routing). SetParent (vtbl[0x138]) is the high-level equivalent. |
| +0x144 / +0x14c | TMethod | OnClick code / Self |
| +0x1e4 / +0x1ec | TMethod | OnChange code / Self |
| +0x2a0 | buffer | caption/text buffer (DefaultHandler text msgs 0xc/0xd/0xe) |
| +0x2dc.. | int[] | cached anchor/center point (FLui_WP_RecalcAnchor: +0x2dc,+0x2e4,+0x2d4,+0x2d8) |
| +0x304 | ptr | render node / canvas resource (freed by FLui_WP_ReleaseResources vtbl[0x20]) |
| +0x340 | ptr | **render/draw node** (alloc'd by FLui_WP_InitRenderNode; node+0x70=back-ptr, node+0x68=cb FUN_007ffe80) |
| +0x368 | list | **child-control list** (walked by FLui_WP_PaintChildren) |
| +0x3f0/+0x3f8/+0x400 | ptr×3 | descriptor/string buffers (FreeMem'd by destructor) |
| +0x434 | list | child/aux list (freed by FLui_WP_FreeChildList in destructor) |
| +0x398 / +0x3a0 | TMethod | wheel OnChange code / Self |
| +0x474 / +0x476 | u16 / u16 | DEFAULT preferred size (ctor writes 0x55/0x40) — NOT the live rect |
| +0x492 | byte | **visible flag** (FLui_WP_SetVisibleCore) |
| +0x4bc / +0x4c4 | TMethod | OnVisibleChanged code / Self (fired by SetVisible) |
| +0x4cd | byte | press state |

(TQuickEdit-specific: typed text UStr @+0x624, see re/14. Wheel value @+0x450. These belong to ui-02.)

---

## 4. The lifecycle functions (create / parent / bounds / realize / render / destroy)

All renamed `FLui_WP_*`, tagged `UI_wpcore`, plate-commented. All run on FL's **main/UI thread**.

### CREATE — `FLui_WP_CreateControl(classVMT, allocFlag=1, parent=0)` @0x717a90
Generic base ctor. `if allocFlag: inst = NewInstance(VMT) (FUN_00410320)`; chains super ctor
`FLui_WP_BaseComponentCtor@0x7a6850` → `FUN_007ffbd0`; `FLui_WP_InitRenderNode@0x7ffdb0` allocs the
draw node @+0x340; sets base fields (default size 0x55×0x40 @+0x474/+0x476, +0x493=0xFF, +0x491=2,
calls vtbl[0x188] with (0,0,0x20)); `AfterConstruction (FUN_00410360)`. Concrete controls call their
own slot-+0x78 ctor (which internally calls this) — e.g. button wrapper `FLwp_CreateButtonControl
@0xF0DDB0`, TQuickEdit `FUN_0074c400(&PTR_FUN_007466b8,1,0)@0x74c400`.

### PARENT — `FLui_WP_SetParent(ctrl, parent)` = vtbl[0x138] @0x5d0850
Stores `parent@ctrl+0x78`; removes from old parent (FUN_005d7970), adds to new (FUN_005d7800),
re-layout (FLui_WP_RecalcAnchor). The parent (a WP container/panel) also carries the theme/skin — so
re/13's "apply skin via vtbl[0x138]" and re/14's "parent into the browser content panel via vtbl[0x138]"
are the SAME call. (Lower-level layout/input link also available via the +0x11c node, vtbl[0x10].)

### BOUNDS — `FLui_WP_SetBounds(ctrl, x, y, w, h)` = vtbl[0x188] @0x802ef0
No-ops if unchanged. Writes rect **x@+0x90, y@+0x94, w@+0x98, h@+0x9c**; stores TRect(x,y,x+w,y+h) via
FUN_005d29d0; sends WP msg 0x47; calls vtbl[0x110]=Realign. (Set bounds in the parent's WP-canvas
coords, NOT Win32 client coords.)

### REALIZE / ALIGN — `FLui_WP_SetAlign(ctrl, alignByte)` @0x5ceef0  (the task's "realize(ctrl,role)")
Sets layout role @+0xb3; if the role changes the effective rect it re-applies vtbl[0x188]=SetBounds, and
drives `FLui_WP_RecalcAnchor@0x5cf670` (computes anchor/center from the +0xd0 geometry sub-object). Role
bits: 1/2 fix left/top, 4/8 fix right/bottom, 3 = client/fill. Also: `FLui_WP_SetShowing(ctrl,byte)
@0x5d08c0` flips the "showing/active in parent" flag @+0xa9 (re/13's "base init", sends WP msgs
0xffcd/0xb00b/0xffef, gates Realign).

### SHOW — `FLui_WP_SetVisible(ctrl, bool)` = vtbl[0x200] @0x71d300
Calls `FLui_WP_SetVisibleCore@0x717d50` (sets visible byte @+0x492, triggers Invalidate) then fires
OnVisibleChanged cb @+0x4bc/+0x4c4.

### RENDER / REDRAW — `FLui_WP_Invalidate(ctrl)` = vtbl[0x178] @0x8016a0
The real repaint trigger: marks the whole control rect dirty (rect MININT..MAXINT → FUN_008015f0); the
framework's paint pump then calls the control's paint vmethod (see §6). **Call this after create+parent+
bounds+show to force the first draw.** NOTE: the address the task called "render FUN_0077adb0" is actually
`FLui_WP_RecalcContentWidth@0x77adb0` — a content AUTOSIZE helper (measures content, calls Invalidate
twice), not the generic paint.

### DESTROY — see §6 (mapped by sub-agent). Teardown must run on the main thread; for our own controls
the safe path is SetParent(ctrl,0) then the destructor/Free. (re/14 §8 teardown ordering for eject.)

### FINALIZE/RELEASE — `FLui_WP_FreeSkinDescriptors(ctrl, flag)` @0x76aef0
Releases the control's skin-descriptor UStrs (offset varies by class) and recurses children; re/13 calls
it as the last step of the build sequence. (Deep skin internals → ui-04.)

---

## 5. WP element TYPE registry (name → typeId → class VMT)

CORRECTION to the task's "registration table @0x761d00": **0x761d00 is just string char-data**, part of a
const literal pool (Delphi UStr StrRec, cp=0x04b0) spanning 0x761afc..0x7622a4. The actual **records
array is @0x12e2998** (Ghidra `PTR_u_checkbox_012e2998`): **52 entries × 12 bytes = `{char* name, int32
typeId}`, alphabetically sorted**. This is the **WP/skin element type registry** — it maps a type-name to
a small integer "kind", which a hardcoded `switch` then maps to a control **class VMT (classRef)**. It is
**NOT a name→Create factory**: the VMT is used only for Delphi `as`-cast (FUN_0040feb0) / `is`
(FUN_0040fe90) to read/write themeable fields during skinning. Instantiation is the separate
`FLui_WP_CreateControl(VMT,1,0)` path (§4). (But the typeId→VMT switch below DOES give us the
name→class-VMT mapping useful for instantiation.)

### 5a. The 52 control type-names (typeId in parens) — type token → kind
`checkbox(d) digiwheel(a) focusbutton(f) gauge(8) graphiccontrol(b) label(9) memo(e) mutebtn(18)
paintbox(4) quickbutton(2) quickwincontrol(7) slider(6) tcustomvectorpanel(7) tcustomwavscope(10)
themewheel(19) thqwavscope(10) titlebar(1) tnewcaption(1) tnewmenu(7) toolbutton(11) tpaintboxselector(b)
tpyformedit(e) tquickbittable(1a) tquickbtn(2) tquickcombo(2) tquickedit(e) tquickedittoolbar(3)
tquickedittoolbarpanel(7) tquickgauge(8) tquicklabel(9) tquickmemo(e) tquickpaintbox(4) tquickscroller(12)
tquicksheetselector(7) tquicksplitter(b) tquickstringgrid(7) tquicktabselector(7) tswitchselector(1b)
tsysbtn(2) tvectorbevel(b) tvectorcheckbox(d) tvectordigiwheel(a) tvectormultidigiwheel(a) tvectorpanel(7)
tvectorsheet(7) tvectorslider(6) tvectorwheel(5) twavscope(10) twpcontrol(3) wheel(5) wpcontrol(3)
wpform(c)`  (names are LOWERCASE in the table; callers must lowercase input. String addrs 0x761afc.. per
sub-agent.) NOTE: 124 trailing `vw*/vs*/tr*` strings after `wpform` are vector-widget property-enum
tables (→ ui-04 skin), not control types.

### 5b. typeId → class VMT (the name→class map) + lookup
- **Lookup: `FLui_Skin_LookupElementTypeId@0x76b5a0`** — binary search `LookupElementTypeId(name,
  0x12e2998, 0x33)` (0x33=51 top idx); UStr compare FUN_00439460; returns typeId or -1.
- **typeId → classRef switch** (from consumers; these classRefs are the WP control class VMTs — many names
  share one): `1→0x7dc090 · 2/0x11→0x714720(button) · 3→0x7dd940 · 4→0x7a3270 · 5/0x19→0x7a0ca0(wheel) ·
  6→0x7a28c8(slider) · 7→chain 0x7786e0/0x703e28/0x740e00/0x7412f0 else 0x7fc270/0x74d000(panels) ·
  8→0x7a5be8(gauge) · 9→0x7a3d58(label) · 0xa→0x7a19e0(digiwheel) · 0xb→0x7fbd70 · 0xc→0x7de918(wpform) ·
  0xd/0x18→0x716118(checkbox/mute) · 0xe→0x745858(edit/memo) · 0xf→0x716a38(focusbtn) · 0x10→0x750f58
  (wavscope) · 0x12→0x6ccd78(scroller) · 0x1a→0x77a340(bittable) · 0x1b→0x79e258(switchsel)`.
  (These are base/skin class VMTs used by `as`-cast; a concrete instance may be a leaf subclass — e.g.
  buttons created by `FLwp_CreateButtonControl` use leaf VMT 0x715520 which derives from button base
  0x714720.)
- **Consumers (renamed, tag UI_wpcore):** `FLui_WP_ApplyControlSkinByName@0x76bff0` (applies skin
  colors/fonts per typeId), `FLui_WP_CaptureControlThemeData@0x769770` (snapshots themeable fields).
  Also `FLui_Skin_BuildElementCatalog@0x1033060`, `FUN_007644d0` (further consumers → ui-04).
- **Resolve-by-name recipe:** `id = FLui_Skin_LookupElementTypeId(lower(name), 0x12e2998, 0x33)` → switch
  `id` → classRef. To INSTANTIATE: pass that classRef (or a known leaf VMT) to
  `FLui_WP_CreateControl(classRef,1,0)` (§4/§7). No Create happens inside the lookup itself.

---

## 6. Destroy/free, paint, message/event pump

### 6.1 DESTROY / FREE
- **Destructor `FLui_WP_Destroy` @0x717cc0 = vmtDestroy (VMT-0x20)**, signature `Destroy(self, outerFlag)`:
  1. `FUN_004103c0(self,flag)` Delphi **BeforeDestruction**.
  2. if global mgr `DAT_014bccb8 != 0` → `mgr->vtbl[0x50](mgr,self)` — **detach from global focus/capture mgr**.
  3. `FLui_WP_FreeChildList@0x717e80(self)` — frees child/aux list @**+0x434**.
  4. **FreeMem ×3** (FUN_0040faa0) on descriptor/string buffers @**+0x3f0/+0x3f8/+0x400**.
  5. chain parent dtor `FUN_007ffcc0(self, flag&~3)` (form/canvas-host base — cross-area, not renamed); this
     also triggers slot +0x20 `FLui_WP_ReleaseResources@0x71cc00` → frees the render node/canvas @**+0x304**
     (FUN_007fe850), unlinks from parent (FUN_0071c8c0).
  6. if outermost (`flag>0`) → `FUN_00410340` Delphi **FreeInstance** (releases the heap block).
- **No WP-layer `Free` wrapper** — use standard Delphi `TObject.Free` semantics.
- **DESTROY RECIPE (our controls):** call `ctrl->vtbl[-0x20](ctrl, 1)` (FLui_WP_Destroy, outerFlag=1). It
  runs BeforeDestruction → detach-mgr → free child list + 3 buffers → parent dtor (render node/canvas
  teardown) → FreeInstance. **Do NOT also FreeMem the object** (outerFlag=1 already frees it). Optional but
  clean: `SetParent(ctrl,0)` (vtbl[0x138]) first to detach. All on the **main thread**, before DLL unmap.

### 6.2 PAINT (message-driven — NO dedicated paint vtbl slot)
- Host/container paint pump **`FLui_WP_PaintChildren@0x5da0a0`** walks its child list (@**+0x368**); per
  visible child: `SaveDC` → `IntersectClipRect`(child rect +0x90/+0x94/+0x98/+0x9c) →
  **`FLui_Input_PerformControlMsg(child, 0xf, hdc, 0)`** → `RestoreDC`. **msg id 0xf = WM_PAINT-equiv**;
  the actual skin blit runs inside the control's `message 0xf` handler.
- Paint TRIGGERS (not the draw itself): +0x178 `FLui_WP_Invalidate` (mark dirty) → +0x180
  `FLui_WP_Repaint` (Invalidate+Update; immediate-paint branch GetDC→PaintChildren→ReleaseDC) → +0x190
  `FLui_WP_Update` (flush, bubble to root host). For our reuse: after show, call **Invalidate (+0x178)**;
  to force an immediate synchronous redraw call **Repaint (+0x180)**.
- Skin internals (→ ui-04, noted not renamed): per-control canvas `FLui_Paint_CreateFormCanvas@0x7fde80`
  (wired in base ctor FUN_007ffbd0); root skin painter via `root->vtbl[0x340]->[0x40]` +
  FUN_005cdc70/FUN_0058ca30.

### 6.3 MESSAGE / EVENT PUMP
- **Dispatch entry `FLui_Input_PerformControlMsg@0x5d2870`**: builds a msg record `{msgId,wparam,lparam,
  result}` and calls the control's **WndProc TMethod @+0x80 (code)/+0x88 (data)**.
- **WndProc is virtual: `FLui_WP_WndProc@0x5d2a30 = vtbl[0x150]`** (base ctor FUN_005ce910 sets
  `+0x80 = VMT[0x150]`, `+0x88 = self`). Routing:
  - global filter peek (root+0x4ec vtbl[0x50]); if handled → swallow.
  - **keyboard** 0x100–0x109 → root container vtbl[0x2f0].
  - **mouse** 0x200–0x20e (0x200 move→FUN_008434f0; 0x202 LBUP; 0x207 drag/dock); matched gesture →
    resolve **dynamic method 0xffc8** via GetDynaMethod (FUN_0040ffe0) and call; dbl-click (0x203 + flag
    @+0xaf) → dynamic method **0xffee**.
  - default → `vtbl[-0x40]` Delphi **Dispatch** (FUN_00410090) → message-method table → `message NN`
    handler; if none → DefaultHandler `FLui_WP_DefaultHandler@0x5d2da0` (VMT-0x38) (text msgs 0xc/0xd/0xe
    vs caption @+0x2a0).
- **msg id → handler** uses two Delphi mechanisms: (a) **dynamic-method table** (GetDynaMethod
  FUN_0040ffe0) for ids called directly (0xffc8/0xffee, and SetShowing's 0xffcd/0xffcf/0xffef); (b)
  **message-method table** (Dispatch) for `message` handlers — paint **0xf** + the **0xb0xx** notification
  family (0xb008 skin/font-changed; 0xb009/0xb023/0xb035/0xb03d/0xb050/0xb058 burst-fired by FUN_005cf200
  on parent/skin attach; 0xb00b inline → FUN_005d01f0).
- **Reaching the event TMethods:** the CONCRETE control's message handlers (e.g. TQuickBtn mouse-up/click)
  hit-test then invoke event pairs — OnClick +0x144/+0x14c, OnChange +0x1e4/+0x1ec (these live in the
  concrete-control message methods, → ui-02/ui-05, not the base).

---

## 7. REUSE RECIPE — create ANY control natively, parent, size, realize, render

Generalizes re/13/14. All on FL's **main thread**. `&VMT` = the target class's VMT (table §5 / a known
classRef). `parent` = a WP container (a form's client panel, the browser content panel `*(browser+0x34c)`,
the channel-rack panel `form+0x7d4`, etc.).

```c
// 1. CREATE (base ctor; or the class's +0x78 ctor / a wrapper like FLwp_CreateButtonControl)
void* ctrl = FLui_WP_CreateControl(&VMT, 1, 0);          // @0x717a90

// 2. (optional) base init / mark showing
FLui_WP_SetShowing(ctrl, 0);                              // @0x5d08c0  (re/13 base-init)

// 3. SKIN/DESCRIPTOR (optional, for themed look) — set the skin descriptor UStr then parent carries theme
//    FUN_004133f0(ctrl+0x328, L"controls.button;forms.<f>.<path>:quickbutton");   // re/13

// 4. PARENT  (vtbl[0x138])
(*(void(__fastcall**)(void*,void*))(*(void**)ctrl + 0x138))(ctrl, parent);   // FLui_WP_SetParent

// 5. BOUNDS  (vtbl[0x188])  — WP-canvas coords
(*(void(__fastcall**)(void*,int,int,int,int))(*(void**)ctrl + 0x188))(ctrl, x, y, w, h); // SetBounds

// 6. REALIZE / ALIGN (role: 0=none, 3=client/fill, bits 1/2/4/8 = anchor edges)
FLui_WP_SetAlign(ctrl, role);                            // @0x5ceef0

// 7. SHOW  (vtbl[0x200])
(*(void(__fastcall**)(void*,char))(*(void**)ctrl + 0x200))(ctrl, 1);          // FLui_WP_SetVisible

// 8. RENDER / first draw  (vtbl[0x178])
(*(void(__fastcall**)(void*))(*(void**)ctrl + 0x178))(ctrl);                  // FLui_WP_Invalidate

// 9. (interactivity) install event TMethods: poke ctrl+OFF+8 = ourCtx; ctrl+OFF = thunkAddr
//    OnClick +0x144/+0x14c, OnChange +0x1e4/+0x1ec, wheel OnChange +0x398/+0x3a0  (re/13)

// 10. DESTROY (eject): SetParent(ctrl,0) then destructor/Free  (see §6)
```

Always dispatch through the vtbl slot (steps 4/5/7/8) so the correct per-class override runs.

### 7a. Confirmed worked example (this binary): `FLwp_CreateButtonControl@0xF0DDB0`
```c
ctrl = FLui_WP_CreateControl(&LAB_00715520, 1, 0);   // button (TQuickBtn) VMT @0x715520
FLui_WP_SetShowing(ctrl, 0);                          // base init
(*(vtbl+0x138))(ctrl, *(*PTR_DAT_014a8bf8 + 0x7a0));  // SetParent -> theme/skin container
return ctrl;
```
Proves steps 1-4: the ctor takes (VMT, allocFlag, 0); SetShowing(...,0) is the base init; and SetParent
(vtbl[0x138]) with the global theme container `*(*0x14A8BF8+0x7a0)` is how a control gets skinned. The
caller (e.g. `FLwp_BuildChannelRackFrontControls@0xF0E330`) then does SetBounds/descriptor/parent-into-
form/Show.

---

## 8. Ghidra renames / tags (this pass) — all tag `UI_wpcore`, plate-commented

| addr | name | role |
|---|---|---|
| 0x717a90 | FLui_WP_CreateControl | base ctor (vtbl+0x78) |
| 0x7a6850 | FLui_WP_BaseComponentCtor | super ctor |
| 0x7ffdb0 | FLui_WP_InitRenderNode | alloc draw node @+0x340 |
| 0x5d0850 | FLui_WP_SetParent | vtbl[0x138] |
| 0x802ef0 | FLui_WP_SetBounds | vtbl[0x188] |
| 0x5ceef0 | FLui_WP_SetAlign | realize/align (+0xb3) |
| 0x5cf670 | FLui_WP_RecalcAnchor | anchor recompute |
| 0x5d08c0 | FLui_WP_SetShowing | showing flag (+0xa9) |
| 0x71d300 | FLui_WP_SetVisible | vtbl[0x200] |
| 0x717d50 | FLui_WP_SetVisibleCore | visible flag (+0x492) |
| 0x801200 | FLui_WP_Realign | vtbl[0x110] |
| 0x8016a0 | FLui_WP_Invalidate | vtbl[0x178] (repaint) |
| 0x77adb0 | FLui_WP_RecalcContentWidth | content autosize (task's "render") |
| 0x5d4cd0 | FLui_WP_AssignTo | vtbl[0x0] clone |
| 0x76aef0 | FLui_WP_FreeSkinDescriptors | finalize/release |
| 0x717cc0 | FLui_WP_Destroy | destructor (vmtDestroy, VMT-0x20) |
| 0x717e80 | FLui_WP_FreeChildList | frees child list +0x434 |
| 0x71cc00 | FLui_WP_ReleaseResources | vtbl[0x20], frees render node/canvas +0x304 |
| 0x5d2a30 | FLui_WP_WndProc | message router (vtbl[0x150]) |
| 0x5d2da0 | FLui_WP_DefaultHandler | vmtDefaultHandler (VMT-0x38) |
| 0x5da0a0 | FLui_WP_PaintChildren | host child-paint pump (sends msg 0xf) |
| 0x5d1520 | FLui_WP_Repaint | vtbl[0x180] |
| 0x5d14e0 | FLui_WP_Update | vtbl[0x190] |
| 0x5d2870 | FLui_Input_PerformControlMsg | message dispatch entry (tagged) |
| 0x76bff0 | FLui_WP_ApplyControlSkinByName | typeId→skin apply |
| 0x769770 | FLui_WP_CaptureControlThemeData | typeId→theme snapshot |
| 0x76b5a0 | FLui_Skin_LookupElementTypeId | name→typeId binary search (tagged) |

Prototypes set on: CreateControl, SetParent, SetBounds, SetVisible, SetAlign. All tagged `UI_wpcore`.
27 functions renamed/tagged total (15 lead + 9 sub-B + 3 sub-A).

---

## 9. Cross-area notes (NOT renamed — for other agents)
- `FUN_0074c400`/`FUN_00747ac0` = TQuickEdit ctor/init (concrete control → **ui-02**).
- Skin/descriptor parse `FUN_004133f0`, theme containers `*0x14A8BF8`, FreeSkinDescriptors deep paths → **ui-04**.
- Form factory `FLui_CreateFormFromClassRef@0x10C2AA0` / `FLui_CreateFormCore@0x841EF0`, HWND@+0x2b0 → **ui-03**.
- Event TMethod thunk install, mouse/key routing → **ui-05**; align/anchor + container layout → **ui-06**.

## 10. Status — sub-areas covered vs left
- COVERED (lead): base class hierarchy + Delphi VMT model (positive user slots 0x000..0x240 AND negative
  system slots -0x20..-0x80, §2/§2a), base VMT slot map, struct field map (§3), the full create→parent→
  bounds→realize→render lifecycle + reuse recipe + a confirmed worked example (§4/§7/§7a).
- COVERED (sub-agent A): the WP element type registry (52 type-names @0x12e2998, the name→typeId binary
  search, the typeId→class-VMT switch = the name→class map) — §5; corrected the task's "table @0x761d00".
- COVERED (sub-agent B): destructor/free path + the destroy recipe, the message-driven paint pump (msg 0xf,
  no dedicated paint slot), and the WndProc/Dispatch/DefaultHandler message-event pump — §6.
- LEFT for later (other agents): per-class extended vmethods >0x240 per concrete control (ui-02);
  concrete-control event-slot handlers / input routing detail (ui-05); deep skin paint + the vector-widget
  property-enum tables (ui-04); form-host base destructor FUN_007ffcc0 + canvas (ui-03/ui-04).
