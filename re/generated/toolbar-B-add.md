# toolbar-B-add — Recipe: ADD our own square TOGGLE button to FL's main toolbar

Static Ghidra on `FLEngine_x64.dll`, image base `0x400000` (all addrs are Ghidra/absolute; runtime =
`ghidra - 0x400000 + flEngineBase`, `flEngineBase` from `flprobe bridge info`). Builds on re/16 §4-5
(proven-live TQuickBtn-on-toolbar), re/ui-win-shell (toolbar panel map), re/13 + re/ui-02 §7.1 (WP button
create/toggle field map), re/14 pass-6 (live-confirmed WP host contract: parent/bounds/show + rect offsets).
Companion: **`toolbar-A-buttons.md`** (sibling) owns the FL metronome/typing-kb TOGGLE widget internals
(exact lit/down state field + skin render + VMT) — this file consumes that and does the ADD / PLACE / WIRE /
TEARDOWN. Task = own-control button (NOT hooking FL's toggle cluster). Confidence HIGH on APIs (all
decompiled/cross-checked); live-confirm gaps flagged at the end.

## TL;DR verdict
- **Create** an FL `TQuickBtn` via `FLwp_CreateButtonControl@0xF0DDB0` (proven-live on the toolbar, re/16 §4).
  Make it a **TOGGLE** with flags `btn+0x48a |= 0x4001` + `vtbl[0x1e8](btn,2)` (2-state) + press byte
  `btn+0x4cd=0`; state = generic value `int@btn+0xc4` via `FLwp_SetControlValue@0x5D0D10` (setter repaints
  the lit/down look). Size **square** via `vtbl[0x188](btn,x,y,s,s)`.
- **Custom icon = YES, feasible.** Best route: install an **OnPaint/caption-overlay** TMethod at
  `btn+0x49c(code)/+0x4a4(data)` and blit our own HBITMAP/HICON with plain GDI onto the button's canvas HDC
  (`hdc = *(HDC*)(*(*(btn+0x304)+0xa0)+0x58)`) — this overlays a custom image on FL's native 2-state frame.
  FL's own helper for this is `FLui_Paint_DrawBitmap@0x58eb80` (StretchBlt onto that same HDC). De-risked
  fallback = a glyph char in FL's `P_ILGlyphs` icon font (built-in icons only) or a short text caption.
- **Place** on the ALWAYS-present primary top panel `*(toolbar+0x760)` (docked by
  `FLui_Shell_AttachToolbarPanels@0xcb5a20`); parent with `vtbl[0x138]`, position in the free right-strip,
  anchor **fix-right** via `FLui_WP_SetAlign(btn,4)`. Toolbar singleton is a **direct global**
  `*PTR_DAT_014aa4c8` (no capture hook needed). Do **NOT** use the `.tpr` customizable area `*(toolbar+0x9c0)`.
- **Wire** click via the button **OnChange** slot `btn+0x1e4(code)/+0x1ec(data)` (fires on toggle-state
  change; re/14 C2b confirmed FL buttons fire +0x1e4) → our RWX thunk → read `*(int*)(btn+0xc4)`, flip our
  flag, post a bridge event. Reflect host-driven state with `FLwp_SetControlValue(btn,0|1)`.
- **Persistence:** FL builds the toolbar ONCE in `TToolbarForm.FormCreate@0xcb7f90`; our button is not part of
  it → it survives until FL **recreates the toolbar form** (EditToolbars / skin / UI-scale). Re-assert on
  recreate (poll the singleton ptr).
- **Teardown (before FreeLibrary, main thread):** zero all our TMethods (paint +0x49c/+0x4a4, change
  +0x1e4/+0x1ec, mousedown +0x144/+0x14c) → hide `vtbl[0x200](btn,0)` → unparent `vtbl[0x138](btn,0)` →
  destroy → invalidate the panel → free our thunk/bitmap → FreeLibrary.
- **Safest first ship:** own-control TQuickBtn (NOT hooking FL's native toggle cluster). No FL model/DFM
  mutation, cleanest eject.

---

## 0. Reconciliation of prior docs (important — read before implementing)
- **`vtbl[0x138]` = SetParent** (the arg is the parent/owner; its skin cascades to the child). Proven three
  ways: `FLui_Shell_AttachToolbarPanels@0xcb5a20` docks `tb+0x760`/`tb+0x9b8` via `vtbl[0x138](panel,band)`;
  re/14 pass-6 live-parents chat controls via `vtbl[0x138](ctrl,panel)`; `FLwp_CreateButtonControl` calls it
  once with the channel-rack root as a temp owner (for theme). ⇒ re/13/ui-02's "apply skin/theme (vtbl0x138)"
  and re/16 §4's "SetParent vtbl[0x138]" are the SAME slot — treat it as **SetParent**, and RE-parent onto the
  toolbar after create.
- The re/16 §4 "realize/render" fns are misnamed: **`FUN_005ceef0` = `FLui_WP_SetAlign(ctrl,alignByte)`**
  (anchor role @+0xb3; `(btn,6)` = fix top+right) and **`FUN_0077adb0` = `FLui_WP_RecalcContentWidth`**
  (autosize width). True repaint = **`vtbl[0x178]` = FLui_WP_Invalidate**.
- **WP rect offsets** (live-confirmed, re/14 pass-6): x=`+0x90`, y=`+0x94`, w=`+0x98`, h=`+0x9c` (all int).
  (Supersedes re/13's +0x474/+0x476.)

---

## 1. CREATE a square TOGGLE button (looks like FL's toolbar toggles)

### 1a. Create + base (proven-live, re/16 §4)
```
btn = FLwp_CreateButtonControl();          // @0xF0DDB0  -> TQuickBtn (classRef &LAB_00715520);
                                           //   sets showing=0; vtbl[0x138] applies channel-rack theme
```
`FLwp_CreateButtonControl` internals (decompiled): `FLui_WP_CreateControl(&LAB_00715520,1,0)` →
`FLui_WP_SetShowing(btn,0)` → `vtbl[0x138](btn, *(*PTR_DAT_014a8bf8+0x7a0))`. Class = `TQuickBtn` VMT
`0x715508`, size `0x4e8`, base `TCustomQuickBtn 0x714658`.

### 1b. Make it a TOGGLE (checkable/down-state, not momentary)  — [fields per ui-02 §7.1; confirm vs sibling toolbar-A-buttons.md]
```
*(u32*)(btn+0x48a) |= 0x4001;              // behavior flags: toggle/latch (0x4001)
(*(vtbl[0x1e8]))(btn, 2);                  // 2-state mode (idx 0x1e8 in shared WP vtbl, ui-02 §4)
*(u8 *)(btn+0x4cd)  = 0;                   // press/armed byte 0 -> don't auto-press during build
```
State = generic value `int@btn+0xc4`. Set/flip with **`FLwp_SetControlValue@0x5D0D10(btn,val)`** (writes
+0xc4, clears dirty +0xac, redraws via vtbl[0x238], notifies) — this is what makes the button paint its
lit/down look. Read state = peek `*(int*)(btn+0xc4)`. (The exact skin field that renders "lit" for FL's own
toolbar toggles — likely +0xc4 value and/or a pressed flag like the +0x461 flag TempoSelectPaint reads — is
mapped by the sibling's `toolbar-A-buttons.md`; the value-setter path above drives it for a 2-state button.)

### 1c. Square size + anchor
```
int s = squareSize;                        // match a sibling toggle: read its w/h @ctrl+0x98/+0x9c live
(*(vtbl[0x188]))(btn, x, y, s, s);         // SetBounds -> square
FLui_WP_SetAlign(btn, 4);                  // @0x5ceef0  fix-RIGHT (4). Use 6 = top+right to pin the corner.
FLui_WP_RecalcContentWidth(btn);           // @0x77adb0 (autosize; harmless) then repaint via vtbl[0x178]
```

### 1d. ICON — custom bitmap (recommended) vs glyph/caption (de-risked)
Feasibility: **custom bitmap = YES.**

**Route A — custom HBITMAP/HICON overlay (recommended for custom art).** Install a paint TMethod:
```
*(void**)(btn+0x4a4) = ourCtx;             // paint data(owner)  -- set data FIRST
*(code**)(btn+0x49c) = IconPaintThunk;     // paint code (OnPaint / caption-overlay slot)
```
FL calls it on the UI thread as **`paint(owner /*=data, RCX*/, btn /*=ctrl, RDX*/, double* rect /*[x,y,w,h], R8*/)`**
(signature from `TToolbarForm.TempoSelectPaint@0xcb8b80`, whose paint slot FormCreate sets at +0x49c). Inside:
```
void __fastcall IconPaintThunk(void* owner, void* btn, double* rect){
    void* canvas = *(void**)(*(void**)((char*)btn+0x304) + 0xa0);  // WP canvas
    HDC  hdc     = *(HDC*)((char*)canvas + 0x58);                  // canvas+0x58 = HDC
    int on = *(int*)((char*)btn+0xc4);                             // toggle state -> pick icon
    // plain GDI: BitBlt/StretchBlt from our memDC, or DrawIconEx(hdc, x,y, on?hOn:hOff, s,s,0,0,DI_NORMAL);
}
```
This overlays our image on FL's native 2-state button frame (the +0x49c slot is the button's
caption/overlay paint — compare `RecBtnPaintCaption@0xcbbb70` / `PatBtnPaintCaption@0xcbb020`). Alt: wrap our
bits in an FL image object and call **`FLui_Paint_DrawBitmap@0x58eb80(canvas,destRect,img,srcRect,mode)`**
(it StretchBlts onto the same HDC, transparency via mask 0xe20746) — but raw GDI on `canvas+0x58` is simpler
and fully bridge-owned.

**Route B — glyph font (built-in icons only, zero paint-thunk risk).** Mirror
`FLui_ChanRack_CreateTypeGlyph@0xf0e160`: `FLui_Skin_LoadNamedFont(*(btn+0x340),"P_ILGlyphs")` then caption =
the glyph char via `FUN_005d0ae0(btn, ustr)@0x5d0ae0`. Only FL's built-in glyph set.

**Route C — text caption.** `FUN_005d0ae0(btn, makeUStr(L"AI"))` — guaranteed, ugly. Good v1 placeholder.
(No skin-image-index route gives custom art — the image must be in the skin.)

### 1e. Parent + show (proven-live sequence, re/16 §4 corrected)
```
(*(vtbl[0x138]))(btn, targetPanel);        // SetParent onto toolbar panel (§2)
(*(vtbl[0x200]))(btn, 1);                   // Show
(*(vtbl[0x178]))(btn);                      // Invalidate/repaint
```
Optional hint tooltip: `Delphi_UStrAsg(btn+0xe4, makeUStr(L"|^^AI assistant"))`.

---

## 2. PLACE it — target container + free space

### 2a. Runtime pointer to the toolbar (no capture hook needed)
Toolbar form is a **direct singleton global**: `toolbar = *(void**)rt(PTR_DAT_014aa4c8)` (ui-win-shell §1;
`PTR_DAT_014aa4c8` ghidra). All panels below are `toolbar+OFF`. (re/16 §5's capture-hook —
`NewMainMenuPopup@0xcb9ee0` param_1=toolbar, or `FLmenu_BarMouseDown@0x7059a0` param_1=bar=toolbar+0x878 — is
a fallback if the global reads null pre-FormCreate.)

### 2b. Panel map (top band = two docked rows)
`FLui_Shell_AttachToolbarPanels@0xcb5a20` docks two panels into the main-form top band (`mainForm+0xb10`):
| panel field | role | use as host? |
|---|---|---|
| `*(toolbar+0x760)` | **PRIMARY top row** (menu strip + transport + right-side widgets), docked at y=0 | **YES — always present/visible; recommended host** |
| `*(toolbar+0x9b8)` | secondary row (~41px*scale below), docked | OK alt (2nd row) |
| `*(toolbar+0x9c0)` | **customizable quick-edit toolbar (.tpr presets)** | **NO — serializes to user preset / rebuilds** |
| `*(toolbar+0x870)` menu panel · `+0x878` menu bar | menu strip | no (menu) |
| `*(toolbar+0x9c8)` OnlineToolBar panel / `+0x9d0` child | news/online btns (right) | conditional-visible (`+0xa9`); skip |
| `+0x888/+0x890/+0x898` | SysBtns (min/max/close chrome, right edge) | landmark for right-side placement |

### 2c. Where the free space is + placement recipe
The primary panel packs left→right: menu → transport/tempo/pattern/time → CPU/scope/online → min/max/close.
The safe, non-overlapping spot is the **right strip just left of the SysBtns** (min/max/close). Recommended
first-ship placement:
```
panel = *(void**)(toolbar+0x760);
(*(vtbl[0x138]))(btn, panel);
// x = panel width - sysBtnsWidth - s - gap ; y = center vertically; size s x s
(*(vtbl[0x188]))(btn, x, y, s, s);
FLui_WP_SetAlign(btn, 4);                   // fix-RIGHT -> stays pinned as the window resizes (no overlap
                                            //   with the left-growing transport widgets)
```
**Read the exact `s` (square size), `y`, and the free `x` live from a sibling toolbar toggle** (metronome /
typing-kb): grab a cluster button (fields `+0xaa8/+0xab0/+0xa10/+0xa18/+0xb48`) and copy its `h=+0x9c`
(=`s`), `y=+0x94`, and place just inside the SysBtns' `x`. This matches FL's toggles pixel-for-pixel.

**Most-native alt (Option B):** parent into the SAME container as the metronome cluster and ride its layout:
that container = a sibling toggle's **parent ptr @ `ctrl+0x78`** (read live). Appending there makes our button
flow with the cluster, but you must match its layout (it's DFM-streamed; either set matching bounds or hook
its resize). Heavier — defer past first ship.

**Fully self-managing alt (Option C):** author our own toolbar SECTION (class `&PTR_FUN_0090e008`) with a
self-layout TMethod at `+0x584(code)/+0x58c(data)`, parent into `+0x760`;
`FLui_Ctl_ControlBar_LayoutToolbars@0x910830` auto-positions sections left→right. Cleanest flow, most code.

---

## 3. WIRE click + toggle state

### 3a. Install our click handler (OnChange = fires on toggle flip)
```
*(void**)(btn+0x1ec) = g_ourCtx;           // data(Self)  -- set FIRST
*(code**)(btn+0x1e4) = ClickThunk;         // OnChange/secondary-mouse code (re/14 C2b: buttons fire +0x1e4)
// (optionally also MouseDown +0x144/+0x14c if you want raw down events)
```

### 3b. The thunk (RWX, x64 fastcall, SEH-safe, fast — same contract as re/13/14)
```
void __fastcall ClickThunk(void* btn /*RCX*/, void* self /*RDX = g_ourCtx*/ /*, args*/){
    int state = *(int*)((char*)btn+0xc4);  // FL already flipped the toggle value before firing OnChange
    g_toggleState = state;                 // flip our flag
    g_toggleEvent = 1;                     // app polls this / post WM_BRIDGE_* to open our host feature
}
```
Do the real work (open panel / start feature) OFF this callback (it runs on FL's UI thread). Keep tiny.

### 3c. Reflect host-driven state back onto the button
When the host toggles programmatically (not via the button):
```
FLwp_SetControlValue(btn, on ? 1 : 0);     // @0x5D0D10 -> sets +0xc4, marks dirty, repaints lit/down look
```
For a custom-icon (Route A) button, `SetControlValue` also triggers our IconPaintThunk (via the redraw), so
the correct on/off bitmap is drawn from `*(int*)(btn+0xc4)`. No extra invalidate needed; if in doubt call
`vtbl[0x178](btn)`.

---

## 4. PERSISTENCE + TEARDOWN

### 4a. Persistence (does FL rebuild + lose our button?)
- FL builds the toolbar **once** in `TToolbarForm.FormCreate@0xcb7f90` (it only *binds* the DFM-streamed
  widgets; it does not touch our added control). Our button therefore **persists for the toolbar form's
  lifetime**.
- FL **recreates the toolbar form** on: **EditToolbars/reset** (`TShortcutsModule.EditToolbarsActionExecute
  @0xe477d0`), skin change, UI-scale change → our button is destroyed with the old form → **re-assert**.
- Detect + re-assert: poll the singleton `*PTR_DAT_014aa4c8` each bridge tick; if the pointer changed (new
  form) **or** our stored `btn`'s parent (`*(btn+0x78)`) no longer equals `*(toolbar+0x760)`, re-run §1-§3.
  (Alt: hook `TToolbarForm.FormCreate@0xcb7f90` to re-add on every rebuild.)

### 4b. Teardown / eject — ORDERED, MAIN thread, BEFORE FreeLibrary
1. **Clear ALL our TMethods first** so FL can never call into the unmapped DLL:
   `btn+0x49c=0; btn+0x4a4=0` (paint) · `btn+0x1e4=0; btn+0x1ec=0` (change) · `btn+0x144=0; btn+0x14c=0`
   (mousedown, if wired).
2. **Hide:** `vtbl[0x200](btn, 0)`.
3. **Unparent:** `vtbl[0x138](btn, 0)` (SetParent null).
4. **Destroy the button:** call its destructor (Delphi `TObject.Free`/`Destroy` via `vtbl[0]`, or the WP
   control free). If the exact free fn isn't confirmed at impl time, **safe-minimal** = leave it allocated but
   hidden+unparented+TMethods-cleared (inert; FL frees it at form destroy) — do NOT skip step 1.
5. **Repaint the panel:** `vtbl[0x178](*(toolbar+0x760))` so the hole redraws.
6. **Free our resources:** the RWX thunks (only after step 1+4 — nothing can call them now) and the
   HBITMAP/HICON/memDC (only after the paint TMethod is cleared).
7. **Then `FreeLibrary`.** (A dangling paint/OnChange TMethod or a live control after unmap = AV.)

---

## 5. Safest approach for a first ship
**Own-control `TQuickBtn`, NOT hooking FL's toggle cluster.** Rationale:
- The create → parent (`vtbl[0x138]`) → bounds (`vtbl[0x188]`) → show (`vtbl[0x200]`) path is **proven live**
  (re/13/14 created/parented `TQuickEdit`/`TQuickBtn` on FL panels without crashing; re/16 §4 is the toolbar
  variant of it).
- Zero mutation of FL's DFM-streamed toggle cluster / its bound value objects / the command bus → **cleanest
  eject** (we own the control; teardown is §4b).
- Hooking FL's toggle cluster (adding a value object + a `FL_DispatchCommand` target id) is far riskier
  (model wiring, id collisions) with no upside for our own feature toggle.
- **Icon for v1:** ship Route B (P_ILGlyphs glyph) or Route C (text caption) — zero paint-thunk risk — then
  upgrade to Route A (custom HBITMAP overlay) once the +0x49c overlay ordering is live-confirmed (see gaps).

---

## 6. Live-confirm gaps (do at impl; all low risk)
1. **Toggle flag bits** `btn+0x48a |= 0x4001` + `vtbl[0x1e8](btn,2)` + `+0x4cd=0`: from ui-02 §7.1 (static).
   Cross-check against the sibling's **`toolbar-A-buttons.md`** (the FL metronome/typing-kb toggle field map)
   and confirm the button renders a persistent lit/down look, not a momentary press.
2. **+0x49c/+0x4a4 = the button's caption/overlay paint slot** (draws OVER FL's 2-state frame). Confirmed at
   +0x49c for the TempoSelect widget in FormCreate; confirm for a `FLwp_CreateButtonControl` `TQuickBtn`
   (compare `RecBtnPaintCaption@0xcbbb70` / `PatBtnPaintCaption@0xcbb020` offsets). If it turns out to REPLACE
   (not overlay) the frame, either draw the frame ourselves or use Route B/C for v1.
3. **Square size / free x / y**: read a live sibling toggle (cluster fields `+0xaa8/+0xab0/+0xa10/+0xa18/
   +0xb48`) for `w/h @+0x98/+0x9c`, `y @+0x94`, and the SysBtns' x — to place pixel-matched without overlap.
4. **Anchor value**: `FLui_WP_SetAlign(btn, 4)` (fix-right) vs `6` (top+right) — pick by how the strip
   reflows; verify no overlap on window resize / toolbar mode change (`mainForm+0x4c2` 1=hidden/2=compact).
5. **Button free/destructor fn** for the fullest eject (step 4.4); else use the hide-and-leave safe-minimal.
6. **Re-assert trigger**: confirm EditToolbars/skin/scale actually recreates `*PTR_DAT_014aa4c8` (pointer
   changes) so the poll in §4a fires.

## 7. Key addresses (Ghidra / base 0x400000)
| what | addr |
|---|---|
| create button (TQuickBtn) | `FLwp_CreateButtonControl@0xF0DDB0` |
| TQuickBtn VMT / classRef | `0x715508` / `&LAB_00715520` |
| set control value (toggle state) | `FLwp_SetControlValue@0x5D0D10` (value int@+0xc4) |
| set align/anchor | `FLui_WP_SetAlign@0x5ceef0` (role@+0xb3; 4=fix-right,6=top+right) |
| autosize width | `FLui_WP_RecalcContentWidth@0x77adb0` |
| caption (guarded) | `FUN_005d0ae0@0x5d0ae0` |
| custom-bitmap draw (FL helper) | `FLui_Paint_DrawBitmap@0x58eb80` (StretchBlt onto canvas HDC) |
| glyph-icon example (P_ILGlyphs) | `FLui_ChanRack_CreateTypeGlyph@0xf0e160` |
| paint sig reference | `TToolbarForm.TempoSelectPaint@0xcb8b80` `(owner,ctrl,double*rect)` |
| toolbar singleton (global) | `*PTR_DAT_014aa4c8` |
| toolbar dock/attach | `FLui_Shell_AttachToolbarPanels@0xcb5a20` (vtbl[0x138]=SetParent) |
| primary top panel (host) | `*(toolbar+0x760)` |
| secondary row / .tpr area | `*(toolbar+0x9b8)` / `*(toolbar+0x9c0)` (avoid 0x9c0) |
| sys btns (right-edge landmark) | `toolbar+0x888/+0x890/+0x898` |
| toolbar section layout | `FLui_Ctl_ControlBar_LayoutToolbars@0x910830` (section class `&PTR_FUN_0090e008`, self-layout +0x584/+0x58c) |
| toolbar FormCreate (rebuild) | `TToolbarForm.FormCreate@0xcb7f90` |
| toolbar recreate trigger | `TShortcutsModule.EditToolbarsActionExecute@0xe477d0` |
| capture-hook fallbacks | `NewMainMenuPopup@0xcb9ee0` (param=toolbar) / `FLmenu_BarMouseDown@0x7059a0` (param=bar=tb+0x878) |

### WP field/vtbl cheat-sheet (TQuickBtn)
- Rect: x`+0x90` y`+0x94` w`+0x98` h`+0x9c` · align/anchor `+0xb3` · parent ptr `+0x78` · skin ctx `+0x304`
  (canvas=`*(+0x304)+0xa0`, HDC=canvas+0x58) · font `+0x340` · descriptor UStr `+0x328` · hint UStr `+0xe4`
  · value(state) `+0xc4` · toggle flags `+0x48a` · press byte `+0x4cd`.
- TMethods: OnChange/click `+0x1e4/+0x1ec` · MouseDown `+0x144/+0x14c` · **paint/caption-overlay
  `+0x49c/+0x4a4`** · Hint `+0x2bc/+0x2c4` · GetText `+0x4ac/+0x4b4`.
- vtbl slots: SetParent `[0x138]` · SetBounds `[0x188]` · Show `[0x200]` · Invalidate `[0x178]` · 2-state
  mode `[0x1e8]` · redraw(after value) `[0x238]`.
</content>
</invoke>
