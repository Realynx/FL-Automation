# UI-REUSE COOKBOOK — build our own native-looking FL UI

The single task-oriented reference for reusing FL Studio's **WP widget framework** (FL's custom Delphi/VCL
UI layer in `FLEngine_x64.dll`) to host our own native-looking windows, controls, menus and chat. Every
recipe gives the **exact funcs / offsets / sequence** and cites the `re/ui-*` (and `re/13/14/16/22/23`) doc it
comes from. Use this to finish the **#80 window-embed** and the parked **#25 chat-input focus**.

> **Rebase rule (every session):** all addresses below are **Ghidra addresses at image base `0x400000`**.
> `runtime = ghidra - 0x400000 + flEngineBase` (get `flEngineBase` from `flprobe bridge info`; it changes on
> FL restart — store ghidra addrs and rebase at runtime). **All UI code is clean Delphi VCL + WP — NO
> VMProtect** anywhere on the form/control/skin/menu/input paths (confirmed by every source doc).

> **Threading:** EVERY call here runs on **FL's main/UI thread** (use the bridge's existing
> `SendMessage(WM_BRIDGE_*)` marshaling). Teardown (eject) also runs on the main thread, BEFORE `FreeLibrary`,
> in the order each recipe gives — a dangling WndProc / TMethod / hook / registry entry after unmap = guaranteed AV.

---

## Table of contents
0. [Foundations — the model you must understand first](#0-foundations)
1. [Recipe 1 — Create a native host window](#recipe-1--create-a-native-host-window) (#80 host)
2. [Recipe 2 — Embed a foreign (Win32/WPF) child window natively](#recipe-2--embed-a-foreign-win32wpf-child-window-natively) (#80 the answer)
3. [Recipe 3 — Create + wire each common control](#recipe-3--create--wire-each-common-control)
4. [Recipe 4 — Layout: parent / bounds / anchor / scroll / batch / dock](#recipe-4--layout)
5. [Recipe 5 — Skin to match FL (fonts, theme colors, icons, custom paint)](#recipe-5--skin-to-match-fl)
6. [Recipe 6 — Keyboard focus (resolve #25 space-capture)](#recipe-6--keyboard-focus-resolve-25)
7. [Recipe 7 — Hints / tooltips + drag-drop](#recipe-7--hints--tooltips--drag-drop)
8. [Recipe 8 — Menus: add Tools/View items + a Settings category](#recipe-8--menus)
9. [Appendix A — VMT slot quick-reference](#appendix-a--vmt-slot-quick-reference)
10. [Appendix B — address quick-reference](#appendix-b--address-quick-reference)
11. [Confidence map — concrete vs needs-live-confirm](#confidence-map)

---

## 0. Foundations

**Two object families, one ABI** (ui-01, ui-03):
- A **WP control** (`TFLWPControl` / `TQuick*` / `TVector*`) — base ctor `FLwp_CreateControl@0x717A90`.
  Its **classRef = VMT + 0x18**. Its own (windowed) HWND, when realized, is at **`ctrl+0x45c`**; host/parent
  HWND at **`ctrl+0x354`**. Bounds rect is **x@+0x90 y@+0x94 w@+0x98 h@+0x9c (int)** (ui-06; this SUPERSEDES
  the old +0x474/+0x476 read, which is the *preferred* size u16).
- A **WP form** (`TCustomWPForm` subtree) — a Delphi VCL `TForm` sub-classed into FL's skinned frame.
  Its **VCL HWND is at `form+0x2b0`** (shared by every form), content container at **`form+0x11c`**, caption
  text at **`form+0x110`**, skin descriptor at **`form+0x6e8`**, visible byte at **`form+0xa9`/+0x6a9**.
  A form **is** a control, so the control offsets/vtable apply to it too.

**The universal control vtable slots** (ui-01 §2, ui-06 "Key vtable slots") — always dispatch through the slot
so the per-class override runs: `vtbl[0x138]`=**SetParent**, `vtbl[0x188]`=**SetBounds(x,y,w,h)**,
`vtbl[0x178]`=**Invalidate**, `vtbl[0x200]`=SetVisible/Show, `vtbl[0xe8]`=GetClientRect,
`vtbl[0xe0]`=GetAbsoluteOrigin, `vtbl[0x110]`=RelayoutHook, `vtbl[0x1e0]`=SetRange(idx,val),
`vtbl[0x1d8]`=SetDefault, `vtbl[0x270]`=SetStyleFlags, `vtbl[0x1d0]`=HandleNeeded/CreateWnd. (Full table = Appendix A.)

**Values + events** (ui-02 §5, ui-05 §5):
- generic value `int @ctrl+0xc4` (set `FLwp_SetControlValue@0x5D0D10`, read = peek); knob/wheel value `int @ctrl+0x450`.
- Events are Delphi **TMethod pairs** `{code @off, Self @off+8}` poked directly on the instance. We install our
  own by writing **Self first, then code**, pointing code at an RWX x64 thunk (fastcall, SEH-safe, fast; `Self`
  must outlive the control). Key slots: OnClick/MouseDown `+0x144/+0x14c`; button OnChange `+0x1e4/+0x1ec`;
  wheel OnChange `+0x398/+0x3a0`; DblClick `+0x1f4/+0x1fc`; Hint `+0x2bc/+0x2c4`; GetText `+0x4ac/+0x4b4`.

**The form factory** (ui-03 §6, re/13/14, re/22-window-manager §1) — the one way to create+register a form:
```c
FLui_CreateFormFromClassRef(classRef, &slot)@0x10C2AA0
  → FLui_CreateFormCore(formMgr=*0x14AA6E8, classRef, &slot)@0x841EF0:
       inst = (*(void*(**)(void*))(classRef - 0x30))(classRef);  // Delphi metaclass NewInstance (size @classRef-0x80)
       *slot = inst;
       (*(void(**)(void*,int,void*))(*(void**)inst + 0x78))(inst, 0xFF, formMgr);  // vtbl[0x78] init
       //  ^ this registers the form into formMgr's TList @formMgr+0x20 and creates the HWND @inst+0x2b0
```
**There is NO blank-form classRef** — repurpose an existing simple form class (Recipe 1) or author a Delphi WP
class (heavy). Factory create + show/hide is **live-proven** (re/14 pass 4).

---

## Recipe 1 — Create a native host window
*(sources: re/ui-03-forms §7, re/ui-win-dialogs §5, re/22-window-host, re/22-window-host-plan)*

Goal: a window WE own with FL's skinned titlebar/border/buttons, an HWND, and an empty content area to fill.

**1a. Pick the host class.** The cleanest empty shell is **`TScriptDialog`** — VMT `0xcf3870`, classRef
**`0xcf3888`**, descriptor `forms.pianorollscriptform`, size `0x7f8`, TVectorForm-derived. Its
`FormCreate@0xcf42c0` bakes **zero child widgets** → content container `+0x11c` is a clean canvas, and it is a
full skinned WP window (real `titlebar`+`tnewcaption`+`toolbutton` chrome, HWND@+0x2b0). On-demand ctor =
`FUN_00cf4180(&PTR_FUN_00cf3888, 1, 0)` (== `TVectorForm.Create`). Alternatives ranked: `TInfoForm`
(0xb29df0, auto-closes on deactivate — strip that), `TBrowserForm` (0x9e0320), `TQuickEditToolbarContainer`
(0xb4fdb0), `TBaseGlassForm` (0x6d4eb0, frameless overlay base). For Phase-1 reparent hosts re/22-host-plan
suggests `TMsgForm`/`TTestForm` (near-empty client).

**1b. Create + realize.**
```c
void* form = 0;
FLui_CreateFormFromClassRef(rt(0xcf3888), &form);   // 0x10C2AA0  → form created + registered
HWND hwnd  = *(HWND*)((char*)form + 0x2b0);          // the FL-chrome window (created by the VCL ctor)
```
The VCL ctor realizes `+0x2b0` already; no extra HandleNeeded for the *form*. (For a WP *control's* own child
HWND you'd call `FLui_WP_HandleNeeded@0x5ddf30`/`FLui_WP_GetHandle@0x5ddf70` → `ctrl+0x45c`; the WP host paint
HWND is `widget+0x45c`, distinct from the VCL `+0x2b0` — ui-04 §2.3.)

**1c. Set the caption** (ui-03 §3, re/22-host-plan §1.2):
```c
FLwp_SetFormCaption(form, makeUStr(L"FL Agent"));   // 0x841690 — stores UStr@form+0x110 + SetWindowTextW(hwnd)
```
Gated on `form+0x144 != 0` (has-caption) and `form+0x17b == 0` (appwin-suppress). `makeUStr` =
`FUN_0054c5e0(&slot, L"…")` or a hand-built Delphi UStr const (header at ptr-12: codepage `0x04B0`/elemsize 2,
refcnt `-1`, len u32, UTF-16, double-null; pass ptr-to-chars).

**1d. (optional) Re-skin** — override the descriptor then re-apply:
```c
Delphi_UStrAsg((char*)form + 0x6e8, makeUStr(L"forms.pianorollscriptform:wpform"));  // 0x4133f0
FLui_WP_FreeSkinDescriptors(form, 1);   // 0x76aef0 — re-applies skin from the new descriptor
```

**1e. Show modeless** (NOT `vtbl[0x2e8]` ShowModal):
```c
FormShow(form);                                  // 0x7EA5D0  (sets +0xa9, refresh, dyn 0xfff1, front vtbl[0x358])
// or low-level:  FLwp_FormSetAppWindowVisible(hwnd, 1, 1);   // 0x82FBB0 (toggles WS_EX_APPWINDOW + ShowWindow)
```

**1f. Make it dockable** (ui-03 §7, ui-06 §5/§6): set the TChildVectorForm flag then dock through the host:
```c
*(unsigned short*)((char*)form + 0x6d0) |= 2;                 // dockable-child flag
// EMBED route (cleanest): become a workspace child of the dock host
void* dockHost = *(void**)(*(void**)rt(0x14A8750) + 0xb18);   // mainForm+0xb18 (the workspace container)
(*(void(__fastcall**)(void*,void*))(*(void**)form + 0x138))(form, dockHost);   // SetParent → docked
// WINDOW route (behave like an FL editor): FLui_Dock_WindowSetAttached(form, 1)@0x1228CD0
//   attached-style: leave form+0xb8=0, owns bit 0x4 @form+0xd4 + pos +0xc0/+0xc4
```

**1g. Register as a first-class View window** (re/22-window-manager §6): the factory already inserted it into
`formMgr+0x20`. Give it the standard fields (`+0xa9` visible, HWND@+0x2b0, CanClose `vtbl[0x2d8]`). Then
show=`FormShow@0x7EA5D0`, hide=`FormHide@0x83AF10`, close=`FormClose@0x83ACF0`. The View ✓ is hardcoded to the
5 built-ins (`MenuCheckRefresh@0x10F20A0`), so drive your own ✓ on popup (Recipe 8e).

**1h. Teardown (eject):** clear any onClick/hook fn-ptrs first; `FormHide`/`FormClose` (removes from
`formMgr+0x20`); the clean WP-form destructor is not fully RE'd, so the safe-minimal path is
`ShowWindow(hwnd,SW_HIDE)` + `DestroyWindow(hwnd)` + null our refs (re/14 §8.4, re/22-host-plan §1.6).

---

## Recipe 2 — Embed a foreign (Win32/WPF) child window natively
*(sources: re/ui-win-dialogs §1–2, §2.3, §2.6; re/22-window-host-plan §1; re/ui-05 GetHWND)*

**This is THE answer for #80.** FL embeds foreign windows by giving a WP content control a real Win32 child
HWND, then Win32 **`SetParent`**-ing it under a target. `SetParent` import = **`0x425330`**; in the whole
binary there are exactly **two** callers — `FLui_WP_SetParentWindow` (the embed workhorse) and the bridge
detach path. So all embedding flows through the primitives below.

**The primitives** (ui-win-dialogs §2.3):
| function | addr | what |
|---|---|---|
| **`FLui_WP_EmbedForeignWindow`** | **0x7e3a60** | dispatcher: `target==0`→clear; else detach `ctl.vtbl[0x138](ctl,0)`, `hwnd=FLui_WP_GetHandle(targetObj)`, `FLui_WP_SetParentWindow(ctl,hwnd)` |
| **`FLui_WP_SetParentWindow`** | **0x5d8e70** | the workhorse: stores host HWND @`ctl+0x354`; if `ctl` owns an HWND (`ctl+0x45c`) → `SetParent(ctl+0x45c, host)` + re-realize; else recreates the handle under the new parent |
| `FLui_WP_SetParentWindowEx` | 0x7e3a20 | `if(detach) ctl.vtbl[0x138](ctl,0)` then `FLui_WP_SetParentWindow` |
| `FLui_WP_GetHandle` (`FUN_005ddf70`) | 0x5ddf70 | `HandleNeeded(ctl); return *(ctl+0x45c)` — realize + return the control's own HWND |
| `FLui_WP_HandleNeeded` (`FUN_005ddf30`) | 0x5ddf30 | if `ctl+0x45c==0`: realize parent (`ctl+0xf`) then `ctl.vtbl[0x1d0](ctl)` (CreateWnd) |

**2a. Embed OUR HWND (simplest, robust — loses WP skin on the child).** Mirror FL's own in-process pattern:
```c
// 1. host form from Recipe 1; hwnd = form+0x2b0
// 2. style-fix OUR child (managed/STA side, or here): strip top-level styles, add WS_CHILD|WS_VISIBLE|WS_CLIPSIBLINGS;
//    clear WS_EX_APPWINDOW|WS_EX_TOOLWINDOW (re/22-host-plan §1.3)
SetParent(ourChildHwnd, hwnd);                       // 0x425330 (FL's own SetParent target)
RECT rc; GetClientRect(hwnd, &rc);
SetWindowPos(ourChildHwnd, HWND_TOP, 0, 0, rc.right, rc.bottom, SWP_SHOWWINDOW);
// 3. track host resize: subclass hwnd (SetWindowSubclass), on WM_SIZE/WM_WINDOWPOSCHANGED re-SetWindowPos the child
```
The "hosting HWNDs is fragile" warnings are about CROSS-process hosts — we are **in-process** (injected), so
they do not apply (re/22-host-plan §0). For WPF use an `HwndSource` child (born `WS_CHILD`) instead of a
top-level `Window` to skip the style strip (re/22-host-plan §1.0 option B).

**2b. Embed via the WP layer (native feel + FL drives resize/dock).** Create a WP content/container control,
parent it into `form+0x11c` (Recipe 3/4), realize its HWND, then reparent our child under it — or hand our
content object straight to the dispatcher:
```c
void* hostCtl = /* a WP container parented into form+0x11c, sized */ ;
HWND  hostHwnd = FLui_WP_GetHandle(hostCtl);          // 0x5ddf70 → realize hostCtl+0x45c
SetParent(ourChildHwnd, hostHwnd);                    // our child rides FL's WP layout
//  — OR, if our content is itself a WP/foreign object FL can realize:
FLui_WP_EmbedForeignWindow(hostCtl, ourContentObj, 0);   // 0x7e3a60
```
FL's own precedent: `FLui_PluginEditor_SetupHost@0xbdd530` creates a `TBridgedEditorForm` and embeds the plugin
GUI into its content WP control `*(form+0x700)` via `FLui_WP_EmbedForeignWindow`. That is the canonical
template if you want a dedicated host form (ui-win-dialogs §2.2).

**2c. Teardown:** un-reparent our child FIRST (`SetParent(ourChildHwnd, NULL)` or `HWND_MESSAGE`), remove the
host subclass, then destroy the host form (Recipe 1h). FL itself parks detached plugin windows under
`HWND_MESSAGE` (`FLui_PluginBridge_WrapMsgWindow@0xbc0470`).

---

## Recipe 3 — Create + wire each common control
*(sources: re/ui-01 §7, re/ui-02 §4–§7 (per-type), re/13, re/14 Stage B1 (live-proven))*

**The generic create→configure→wire→show sequence** (ui-01 §7; the **live-proven** variant is from re/14 B1):
```c
// 1. CREATE — classRef = VMT+0x18 selects the class (or use a typed wrapper, table below)
void* c = FLwp_CreateControl(rt(VMT_plus_0x18), 1, 0);   // 0x717A90
// 2. PARENT (vtbl[0x138]) into a WP container (form+0x11c, or a panel)
(*(void(__fastcall**)(void*,void*))(*(void**)c + 0x138))(c, parent);          // SetParent
// 3. BOUNDS (vtbl[0x188]) — WP-canvas coords, parent-relative
(*(void(__fastcall**)(void*,int,int,int,int))(*(void**)c + 0x188))(c, x,y,w,h); // SetBounds
// 4. SKIN DESCRIPTOR (UStr @c+0x328) — "controls.<grp>;forms.<path>:<role>"  (Recipe 5)
Delphi_UStrAsg((char*)c + 0x328, makeUStr(L"controls.button;forms.x.button.y:quickbutton"));  // 0x4133f0
// 5. REALIZE / ALIGN (role: 0=none, 3/0xf=client/fill, bits 1/2/4/8 = anchor edges)
FLui_WP_SetAlign(c, role);                               // 0x5ceef0  (a.k.a. FUN_005ceef0(c, role))
// 6. WIRE EVENTS — write Self first, then code (RWX thunk)
*(void**)((char*)c + 0x14c) = ourCtx;  *(void**)((char*)c + 0x144) = onClickThunk;   // OnClick
// 7. RENDER / first draw
FLui_WP_RecalcContentWidth(c);                           // 0x77adb0  (FL's refresh; live-proven renders it)
//    documented alt: vtbl[0x178] Invalidate ; vtbl[0x180] Repaint for an immediate sync redraw
```
> **LIVE CAVEAT (re/14 Stage B2):** `vtbl[0x200]` is **not** a reliable Show for every class — the browser
> tree/edit classes render with **no `vtbl[0x200]` call** (parenting + refresh draws them) and `vtbl[0x200](.,0)`
> did NOT hide them. The live-proven way to **hide** a control is to **reparent it away** (`SetParent(NULL)` +
> move offscreen) and to **show** it is to reparent it back to the panel + render. Treat `vtbl[0x200]` as best-effort.

**Typed create wrappers + per-type value/event map** (ui-02 §7; classRef = VMT+0x18):

| want a… | create | value / state | wire change |
|---|---|---|---|
| push/icon/toggle button | `FLwp_CreateButtonControl()@0xF0DDB0` (TQuickBtn) | toggle flags `@+0x48a\|=0x4001`; state `+0xc4`; press `+0x4cd`=0 | OnClick `+0x144/+0x14c`; OnChange `+0x1e4/+0x1ec` |
| checkbox | `FLwp_CreateControl(&(0x715b10+0x18),1,0)` | `+0xc4` (0/1) | OnChange `+0x1e4/+0x1ec` |
| focus button (tab/focus) | `FLwp_CreateControl(&(0x716a20+0x18),1,0)` (TQuickFocusBtn, windowed) | `+0xc4` | OnClick `+0x144/+0x14c` |
| knob / wheel (vol/pan) | `FLui_Ctl_Wheel_CreateWithSkin(buf, descrUStr)@0xF0DAF0` | value `int@+0x450`; range `vtbl[0x1e0](idx 0=min/1=max,val)`; default `vtbl[0x1d8]` | OnChange `+0x398/+0x3a0` |
| digit/spinner wheel | `FLui_Ctl_DigiWheel_CreateMixerTrack(buf)@0xF0DE00` | value `int@+0x450` | OnChange `+0x398/+0x3a0`; GetText `+0x4ac/+0x4b4` |
| single-line edit | `FLui_Ctl_Edit_Create(&0x7466b8,1,owner)@0x74C400` | text UStr `@+0x624` (set `FLui_Ctl_Edit_SetText@0x74C260`; read peek) | OnChange/EditEnd `+0x3c0/+0x3c8` |
| multi-line memo | `FLui_Ctl_Memo_Create(&0x7462b0,1,owner)@0x74C330` | text UStr `@+0x624` (wrap flag `+0x682`) | as edit |
| dropdown / combo | `FLui_Ctl_Combo_Create(&0x779ae8,1,owner)@0x77F240` | sel idx `@+0xc4`; items list `@+0x4e0` (`vtbl[0x78]`=Add) | OnChange `+0x398/+0x3a0` |
| static label | `FLui_Ctl_Label_Create(&0x7a3d58,1,owner)@0x7CEA80` | caption `FUN_005cf630(l,ustr)`; font `@+0x340` | — (no events) |
| paint surface | `FUN_007ce7d0(&0x7a3270,1,0)` (TQuickPaintBox) | — | OnPaint (paint TMethod — live-confirm slot) |
| editable grid/table | `FLui_Ctl_Grid_Create(&0x74d000,1,owner)@0x74DA40` | `SetCellText@0x74F330`/`GetCellText@0x74E7A0`; cols `SetColCount@0x74FA40` | OnDrawCell `+0x6a4/+0x6ac`; OnSelectCell `+0x640/+0x648` |
| tree / list | `FLui_Ctl_TreeModule_Create@0x1058140` (tree+scroller) or `FLui_Ctl_Tree_Create@0x974990` | node-model `@tree+0x51c`; selected node `@tree+0x540`; node API ui-win-browser §4 | OnChange `+0x398/+0x3a0` |
| scrollbar | `FLui_Ctl_Scroller_Create(&0x6ccd78,1,parent)@0x6CE1A0` | pos = `FLwp_SetControlValue(s,pos)`@+0xc4; range `SetRange@0x6D09F0`; page `SetPageSize@0x6D0880` | OnScroll `+0x668/+0x670` |
| splitter | `FLui_Ctl_Splitter_Create(&0x7a5410,1,parent)@0x7D06F0` | pos `@+0xc4`; orientation `@+0x398` | OnMoved `+0x3b0/+0x3b8`; OnMoving `+0x3c0/+0x3c8` |
| tab pager | `FLwp_CreateControl(&(0x740de8+0x18),1,parent)` (TQuickTabSelector) | active idx `@+0xc4`; tab[i] `vtbl[0x298](self,i)` | OnTabChange `+0x398/+0x3a0` |
| panel / group container | `FLwp_CreateControl(&(0x7a4a88+0x18),1,parent)` (TQuickContainer) | — (parent for children) | — |
| step/bit grid | `FLui_Ctl_BitTable_Create(&0x77a340,1,parent)@0x780760` | `SetBit@0x7808C0`/`GetBit@0x780890`; range `SetRange@0x7806F0` | clicks auto-handled (DragPaint) |

(Worked examples in the binary: `FLwp_BuildChannelRackControls@0xF0E330` builds a full 7-control channel strip
(ui-win-channelrack §4); `FLbrz_BuildBrowserControls@0x9a5790` builds ~20 browser controls (ui-win-browser §3);
`FLui_Mixer_BuildLayoutStrips@0x1183c80` builds mixer strips (ui-win-mixer §4) — all use this exact sequence.)

**Destroy a control we made** (ui-01 §6.1): `SetParent(c,0)` (`vtbl[0x138]`) then `c->vtbl[-0x20](c,1)`
(`FLui_WP_Destroy`, outerFlag=1 — runs BeforeDestruction → free child list/buffers → render-node teardown →
FreeInstance; do NOT also FreeMem). Clear any TMethod first. Main thread, before unmap.

---

## Recipe 4 — Layout
*(sources: re/ui-06 (whole), re/ui-01 §4)*

FL layout is **VCL-style explicit SetBounds** — there is no flow/grid engine. You set each child's bounds; the
container only re-anchors + re-clips on resize.

**4a. Parent + size + anchor a control:**
```c
FLui_WP_SetParent(c, container);                         // 0x5d0850 = vtbl[0x138]; sets c+0x78, adds to child list
(*(...vtbl[0x188]))(c, x, y, w, h);                      // FLui_Layout_SetBounds @0x802ef0 (writes +0x90/+0x94/+0x98/+0x9c)
*(unsigned char*)((char*)c + 0xb3) = anchorBits;         // 0/1 = anchor X left/right; 2/3 = Y top/bottom; 3 = centered
```
(Raw-node alt to SetParent: `(*(c+0x11c))->vtbl[0x10](c+0x11c, *(container+0x11c))` = `FLui_Layout_ChildNode_SetParent@0x50adb0`.)

**4b. Batch many SetBounds** to avoid relayout/repaint thrash (ui-06 §6.A.6):
```c
FLui_Layout_BeginUpdate(container);   // 0x5d72b0 (inc suspend counter +0x318)
//   ... issue all child SetBounds ...
FLui_Layout_EndUpdate(container);     // 0x5d72c0 (flushes one deferred relayout at 0)
```

**4c. Make a scrollable viewport** (ui-06 §4, §6.B, §7):
```c
*(unsigned int*)((char*)vp + 0x320) |= 0x2000;          // client rect uses scroll offset
*(unsigned int*)((char*)vp + 0xa0)  |= 0x100000;        // edge accessor applies the offset
void* sm = *(void**)((char*)vp + 0xd0);                  // scroll/metrics model
*(int*)((char*)sm + 0x18) = extraW; *(int*)((char*)sm + 0x1c) = extraH;   // content extent beyond w/h
// parent content children into vp (Recipe 4a); to scroll: set sm+0x10 (scrollX)/sm+0x14 (scrollY) then
(*(...vp->vtbl[0x110]))(vp);                             // RelayoutHook → ComputeClientRect re-fits children
```
Add a `TQuickScroller` (descriptor `…scrollbar:tquickscroller`) and wire its OnScroll (`+0x668/+0x670`) to set
`sm+0x10/+0x14` + `FLui_Layout_ScrollContentRect@0x803430` for a pixel blit (ui-06 §7).

**4d. Relayout on resize:** override the control's Delphi dynamic method **`0xffcf` (OnResize)**, or a form's
`OnResize`, and re-issue child `SetBounds` (batched). There is no separate WM_SIZE pass — resize flows through
`SetBounds` → the §2 cascade (ui-06 §"Resize handling").

**4e. Dock our window** (ui-06 §5/§6-dock, re/22-window-manager §4): docking == parenting into the workspace.
- **Embed route (cleanest):** `FLui_WP_SetParent(ourCtrl, *(*0x14A8750+0xb18))` — become a workspace child;
  position with `SetBounds` in host-client coords; to float: `SetParent(ourCtrl, topLevelForm)` or `(.,0)`.
- **Window route:** `FLui_Dock_WindowSetAttached(ourForm, 1|0)@0x1228CD0` (set up `+0xb8` host wrapper or use
  the `+0xd4` bit-0x4 + pos `+0xc0/+0xc4` path). Then it responds to "Switch editors to Attached/Detached" and
  "Arrange into workspace". Teardown: float/detach (`ChildNode_SetParent(node,0)` / `WindowSetAttached(.,0)`)
  before destroy so the host child list holds no dangling pointer.

---

## Recipe 5 — Skin to match FL
*(sources: re/ui-04 (whole), re/ui-02 §6, re/22-window-host §2)*

**5a. Descriptor / role.** A control's look + behavior come from its descriptor UStr `@ctrl+0x328`:
`"controls.<group>;forms.<formpath>.<element>:<role>"`. The `:<role>` token selects the skin part-set
(`quickbutton`/`wheel`/`digiwheel`/`quickedit`/`slider`/`titlebar`/`tnewcaption`/`toolbutton`/…). Forms store
`"forms.<name>:wpform"` `@form+0x6e8`. Write via `Delphi_UStrAsg@0x4133f0`; then `FLui_WP_FreeSkinDescriptors@0x76aef0`
re-applies. (The 52-name type registry + the name→typeId binary search `FLui_Skin_LookupElementTypeId@0x76b5a0`
→ class-VMT switch are in ui-01 §5 / ui-04 §6.1.)

**5b. Fonts** — each text widget has a font object `@widget+0x49c`; set a `P_*` role:
```c
FLui_Skin_LoadNamedFont(*(void**)((char*)w + 0x49c), "P_Normal");   // 0x653690
```
`P_*` catalog (ui-04 §3): `P_Small`/`P_SmallBold`/`P_Tiny` (9/9/7pt), `P_Normal`/`P_NormalBold`/`P_NormalLight`
(11pt body; use `…Light` when global scale `*(*0x14a9878+0xc) >= 2.0`), `P_Large`/`P_LargeBold` (13pt),
`P_TitleBar`/`P_LargeTitleBar` (caption), `P_DigitWheel` (18pt numeric readout), `P_Monospaced` (code),
`P_ILGlyphs`/`P_WebSymbols` (icon fonts — §5d). Color: `+0x78` of the font obj (`-1`=inherit), set with
`FLui_Skin_SetFontColor@0x653c30`.

**5c. Theme colors** — pull live theme ARGB (tracks the user's Hue/Sat/Light/Contrast):
```c
int argb = FLui_Skin_GetThemeColor(0xFF808080, L"quickbutton.background", 0xf);   // 0x76b6f0 (controls pass flags=0xf)
```
Element.role keys: `quickbutton.{background,buttoncolor,buttonpressedcolor,textcolor}`,
`slider.{fillcolor,handlecolor,railcolor}`, `wheel/knob.{background,fillcolor,gearcolor,railcolor}`,
`checkbox.{checkoncolor,…}`, `titlebar.background`, `label.textcolor` (full role sets ui-04 §6.1). Derive
hover/press/disabled tints with `FLui_Skin_BlendColor(base, accent, t/*0..256*/)@0x626f90`. For an ARGB you
computed, re-tint with the theme via `FLui_Skin_ApplyColorTransform(argb, 0xf)@0x7636f0`. ARGB byte order is
Delphi `TColor` (`0x00BBGGRR`).

**5d. Native icon glyphs:** set a widget's font to `P_ILGlyphs` (or `P_WebSymbols`) and emit the icon
codepoint (the codepoint table is still TODO — ui-04 §9). FL draws its toolbar/window-button glyphs this way.

**5e. Custom paint** (own-drawn control): implement `vtbl+0x1a0` (paint-self) + `vtbl+0x178` (invalidate →
`RedrawWindow(HWND@widget+0x45c)` → folds into the DIB). Inside paint use `canvas = *(widget+0x304)` (a
`TQuickControlCanvasEx`, VMT 0x7fc980) and the `FLui_Paint_*` primitives — `FillRect@0x58f530`,
`FrameRect@0x58f620`, `RoundRect@0x58fa70`, `Ellipse@0x58f4b0`, `DrawBitmap@0x58eb80` (skin sprites),
`TextOut@0x58fbb0`, `DrawTextRect@0x58fde0` (multiline/align), `TextExtent@0x58ff20` — pulling defaults from
`FLui_Paint_GetSkinCtx@0x7fdac0` (`ctx+0x24` bk, `+0x28` text, `+0x20` base metric). These are the exact calls
FL uses ⇒ pixel-identical output (ui-04 §2.2, §7). **Easiest:** host inside a TCustomWPForm (Recipe 1) and you
inherit the skinned border + `P_TitleBar` caption + `toolbutton` window buttons for free (ui-04 §7, re/22-host §2).

---

## Recipe 6 — Keyboard focus (resolve #25)
*(sources: re/ui-05 (whole, esp. §3, §8, §9.A), re/14 Stage C2–C2d (live history))*

**The blocker:** FL's main form eats space (and other keys). `TFruityLoopsMainForm.FormShortCut@0x114de10`
(OnShortCut, fires FIRST via VCL KeyPreview) has THREE shortcut-dispatch blocks; `mainForm+0x4c5` gates only
**block 1**, but **block 3 dispatches on keydown regardless** → space=play still fired (re/14 C2d). So the
per-control text-capture flag and `+0x4c5` are necessary but **not sufficient**.

**The clean fix (ui-05 §9.A) — host the input on a SEPARATE WP form, not as a child of the main form:**
1. Create a standalone WP form via the factory (Recipe 1) and a `TQuickEdit` on it (Recipe 3). Because that
   form becomes the **active VCL form** when clicked, the main form's `FormShortCut`/`FormKeyDown` never run for
   those keystrokes — exactly how FL's own rename (`TNameEditForm`) / browser-search / picker edits type space.
2. Give the edit WP focus so WM_KEYDOWN/WM_CHAR route to it:
   ```c
   FLui_Focus_RequestFocus(ourEdit);   // 0x5ddeb0
   ```
   It resolves the focus-root (`FLui_Focus_GetRootContainer@0x830440`), then
   `FLui_Focus_SetFocusedControl@0x837d40` → `FLui_Focus_StoreFocusedChild@0x837c60` writes `root+0x4b0=ourEdit`
   and Win32-SetFocuses its HWND. **Pre-req:** the edit must be windowed (`ourEdit+0x45c != 0`) — the create+show
   path triggers HandleNeeded (`vtbl[0x1d0]`); `FLui_Focus_CanFocus@0x5de520` = `ctrl+0x45c != 0`.
3. A focused WP edit eats WM_CHAR locally in `FLui_Input_DispatchChar@0x5dc2a0` (returns "handled" → no
   bubbling) → space + every printable key type normally.
4. Read text @ `edit+0x624`; detect Enter in the edit's keydown TMethod (or poll a commit flag like
   `TNameEditForm`'s `form+0x4e8`). Also set the text-capture style bit: `FUN_00802820(edit, 1)@0x802820` sets
   `edit+0x490 |= 0x8` (marks it keyboard-capturing; what FL's own search box does — ui-win-browser §7).

**If you MUST keep the input inside the main form / a browser tab** (re/14 C2d shipped path): inline-hook
`FormShortCut@0x114de10` and, while our content is active, return without dispatching (leave `*handled==0`);
install on show, remove on hide, restore on eject. This is **broad** (suppresses all FL shortcuts while active).
A focus-scoped refinement (only while our edit holds WP focus, via `root+0x4b0`) is the future cleanup.
Get/set focus quick ref: set = `FLui_Focus_RequestFocus`; get (per-form) = `root+0x4b0`; get (global) =
`*(DAT_014bdc98+0xc4)`; control HWND = `FUN_005ddf70(ctrl)`→`ctrl+0x45c`.

---

## Recipe 7 — Hints / tooltips + drag-drop
*(sources: re/ui-05 §6 (hints), §7 (drag-drop); re/ui-win-browser §8)*

**7a. Per-control hint** (ui-05 §6): static text UStr `@ctrl+0xe4`, format `"|^^text"` (set via
`Delphi_UStrAsg(ctrl+0xe4, makeUStr(L"|^^My tooltip"))`); dynamic hint TMethod `@ctrl+0x2bc/+0x2c4`. On hover the
WP form WndProc sends `0xb013` (enter) to the hot control (`form+0x444`); its enter handling pushes the hint up
to the status bar; `0xb014` (leave) clears it.

**7b. Push a status/toast message** (ui-05 §6, ui-win-shell §5.4): the FL hint bar is the skinned
`TFLHintBarForm` singleton `*0x14A7580` (status UStr global `DAT_015817d0`). Surface an AI message via:
```c
FLui_SetStatusHintAndRefresh(*(void**)rt(0x14A8750), makeUStr(L"…"));   // 0x10ec870 (repaints InfoLabel + hint bar)
```

**7c. Accept a drop on OUR window** (ui-05 §7): register an `IDropTarget` on our HWND with `RegisterDragDrop`
(mirror FL's `FUN_006e2630`), or for plain files `DragAcceptFiles(hwnd, TRUE)` + handle `WM_DROPFILES`
(`DragQueryFileW`/`DragFinish`). To feed a dropped sample/path into FL, route it to FL's own target handlers
(channel rack `FUN_00f4a240`, browser `FUN_00f8c0c0`, main form `DragWAVDrop@0x10caa70`) rather than
re-implementing import.

**7d. Drag a browser item into the project programmatically** (no real drag — ui-win-browser §8):
`FLui_Browser_SelectMenuItem_worker@0xe14ff0` → `FLgl_GlobalCommandDispatch(0x5b|0x50, …)` (0x5b=add as new,
0x50=replace); preview = `FLui_Browser_PreviewMenuItem_worker@0xe151c0`.

---

## Recipe 8 — Menus
*(sources: re/16 (model + insertion), re/23 (categories), re/22-window-manager §5–6 (View toggle + ✓))*

The top bar is a **custom WP control `NewMainMenu`** (NOT VCL `TMainMenu`), at `*(toolbarForm+0x878)`; its
dropdowns are FL's own skinned popups. One item class `&PTR_FUN_00706308` for everything; kinds distinguished
by data: caption (onClick `+0x100/+0x108`), submenu (has `+0xb0` child list), separator (caption `"-"`).

**8a. Resolve the handles** (re/16 §3):
```c
void* mainForm   = *(void**)rt(0x14A8750);            // == DAT_01581200
void* actionList = *(void**)((char*)mainForm + 0x760); // class &PTR_FUN_00707240
void* masterRoot = *(void**)((char*)actionList + 0x7c);// container of the top-level menus
void* bar        = *(void**)((char*)toolbarForm + 0x878);   // NewMainMenu control (toolbar ptr: re/16 §5)
```
Get `toolbarForm` from the global `*PTR_DAT_014aa4c8` (ui-win-shell §1) or a one-shot capture hook on
`TToolbarForm.NewMainMenuPopup@0xcb9ee0` (re/16 §5).

**8b. Add an item to an existing menu** (the workhorse — index arg lets you target a category, re/23 §4):
```c
TMethod tm = { (void*)onClickThunk, (void*)ourCtx };  // {code, data}; index<0 = append, >=0 = insert-at
void* item = FLmenu_CreateItem_CaptionClick(parentList, index, makeUStr(L"My item"), &tm);  // 0x70e1a0
```
`makeUStr` = `FUN_0054c5e0(&slot, L"…")`. The click thunk (x64 fastcall, SEH-safe, fast) receives
`(data /*RCX*/, item /*RDX*/)` and should just set a flag / push a pipe event — do real work off-callback.

**8c. Add a TOP-LEVEL menu (e.g. "Plugins")** (re/16 §3): create the top item under `masterRoot` (its `+0x86`
show-in-bar defaults to 1), add children, then **rebuild the bar** (it's built once at FormCreate):
```c
void* plugins = FLmenu_CreateItem_CaptionClick(masterRoot, -1, makeUStr(L"Plugins"), &(TMethod){0,0});
FLmenu_CreateItem_CaptionClick(plugins, -1, makeUStr(L"Plugin Manager…"), &(TMethod){thunk, ctx});
FLmenu_BuildBarFromActionList(bar, actionList);   // 0x7046b0 — re-scans masterRoot incl. our top item
FLmenu_LayoutBar(bar, -1, 0);                     // 0x7049f0 — re-measure x/width; then repaint (FUN_0077adb0(bar))
```

**8d. Categories + a Settings category** (re/23): a "category" is just a run of children between **separator
items** (caption `"-"`); there is no category object. To create a Settings section / new category:
```c
void* sep = FLmenu_CreateItem_CaptionClick(parent, boundaryIdx,   makeUStr(L"-"),        &(TMethod){0,0});
void* it  = FLmenu_CreateItem_CaptionClick(parent, boundaryIdx+1, makeUStr(L"Settings…"),&(TMethod){thunk,ctx});
```
To insert at the **top of View's "Windows" category** (above Playlist): scan View's children
(`flChildCount`=`FUN_0081dda0` / `flChildAt`=`FUN_0081ddc0`), read each caption (`item+0x78`), strip `&`,
lower-case, and pass the index of the first of `{playlist, piano roll, channel rack, mixer, browser}` (re/23 §3,
§6; already implemented in `dllmain.cpp`'s `viewWindowsTopIndex`). **Don't raw-poke `+0x80/+0x81/+0x140`**
before the popup realizes (the `+0x45c` null-deref crash, re/23 §4) — use separators for grouping.

**8e. A View ▸ <our window> toggle + ✓** (re/22-window-manager §6): add the item under View (8b); its onClick
thunk toggles our form `if(*(ourForm+0xa9)) FormClose(ourForm); else FormShow(ourForm);`. FL's `MenuCheckRefresh@0x10F20A0`
is hardcoded to the 5 built-ins, so make the item checkable (`item+0x140`) and set its ✓ yourself on popup —
hook `TToolbarForm.NewMainMenuPopup@0xcb9ee0` and call the item's `vtbl[0xe0]` (setChecked) with
`*(ourForm+0xa9)` (the same one-byte source FL uses).

**8f. De-risked fallback (cleanest eject):** instead of mutating FL's menu tree, drop a `TQuickBtn` on a FIXED
toolbar panel (Recipe 3) and on click open our manager or show our own popup with `FLmenu_ShowPopup@0x70ab80`
(re/16 §4). No menu-tree state to restore.

**8g. Teardown (strict order):** clear our item TMethods (`item+0x100/+0x108=0` and any popup-hook) FIRST;
remove our items from the tree (or safe-minimal: `item+0x86=0` hides from bar, leave allocated); rebuild the bar
(8c) without our entry; restore any hook. Re-assert on toolbar (re)create (EditToolbars / skin change rebuilds
the toolbar form — re/16 §6).

---

## Appendix A — VMT slot quick-reference
*(WP control vtable, byte offsets; resolved from ui-01 §2, ui-06 "Key vtable slots", ui-04 §2.3)*

| slot | meaning | base impl |
|---|---|---|
| `-0x20` | **vmtDestroy** (`Destroy(self, outerFlag)`) | 0x717cc0 |
| `0x078` | **constructor** | 0x717a90 |
| `0x0c0` | create `+0x11c` child node | 0x5d4760 |
| `0x0e0` | GetAbsoluteOrigin (dock origin) | 0x5cfef0 |
| `0x0e8` | GetClientRect (`+0x330` rect) | 0x801140 |
| `0x110` | RelayoutHook | 0x801200 |
| `0x138` | **SetParent(ctrl,parent)** | 0x5d0850 |
| `0x150` | WndProc (message router) | 0x5d2a30 |
| `0x178` | **Invalidate** | 0x8016a0 |
| `0x188` | **SetBounds(x,y,w,h)** | 0x802ef0 |
| `0x1a0` | paint-self (custom draw) | per-class |
| `0x1c8` | SetClientRegion | 0x7ffdf0 |
| `0x1d0` | HandleNeeded / CreateWnd | per-class |
| `0x1d8` | set default value (knob/wheel) | per-class |
| `0x1e0` | set range `(idx 0=min/1=max, val)` | 0x7a6950 |
| `0x200` | Show/SetVisible (best-effort — see Recipe 3 caveat) | 0x71d300 |
| `0x228` | form/window Paint (background) | per-class |
| `0x270` | SetStyleFlags | per-class |
| `0x2e8` | (form) **ShowModal** — blocks, result==1=OK | per-class |

Form chrome/lifecycle: `vtbl[0x2d8]`=CanClose, `vtbl[0x358]`=bring-to-front; dyn methods `0xffcf`=OnResize,
`0xfff1`=OnShow, `0xffaa`=CloseQuery, `0xffb8/0xffb7/0xffb6`=KeyDown/Up/Char.

---

## Appendix B — address quick-reference
*(Ghidra @ base 0x400000; rebase at runtime)*

**Form factory / window:** `FLui_CreateFormFromClassRef 0x10C2AA0` · `FLui_CreateFormCore 0x841EF0` (formMgr
`*0x14AA6E8`) · `FLwp_SetFormCaption 0x841690` (text@form+0x110) · `FormShow 0x7EA5D0` · `FormSetVisible 0x833EC0`
· `FormHide 0x83AF10` · `FormClose 0x83ACF0` · `FLwp_FormSetAppWindowVisible 0x82FBB0` ·
`FLui_Dock_WindowSetAttached 0x1228CD0` · `MenuCheckRefresh 0x10F20A0` · TScriptDialog cr `0xcf3888`
(ctor `FUN_00cf4180`, FormCreate `0xcf42c0`).

**Embed (#80):** `FLui_WP_EmbedForeignWindow 0x7e3a60` · `FLui_WP_SetParentWindow 0x5d8e70` ·
`FLui_WP_SetParentWindowEx 0x7e3a20` · `FLui_WP_GetHandle 0x5ddf70` · `FLui_WP_HandleNeeded 0x5ddf30` ·
`SetParent` import `0x425330` · `FLui_PluginEditor_SetupHost 0xbdd530` (TBridgedEditorForm template).

**Control core:** `FLwp_CreateControl 0x717A90` · `FLwp_SetControlValue 0x5D0D10` · `FLui_WP_SetParent 0x5d0850`
(vtbl 0x138) · `FLui_WP_SetBounds 0x802ef0` (vtbl 0x188) · `FLui_WP_SetAlign 0x5ceef0` (realize) ·
`FLui_WP_RecalcContentWidth 0x77adb0` (refresh) · `FLui_WP_FreeSkinDescriptors 0x76aef0` (finalize) ·
`FLui_WP_Destroy` via `vtbl[-0x20]` · `Delphi_UStrAsg 0x4133f0` · `makeUStr FUN_0054c5e0 0x54c5e0`.
(Typed wrappers: buttons `0xF0DDB0`, wheel `0xF0DAF0`, digiwheel `0xF0DE00`, edit `0x74C400`, memo `0x74C330`,
combo `0x77F240`, grid `0x74DA40`, label `0x7CEA80`, tree `0x974990`/`0x1058140`, scroller `0x6CE1A0`,
splitter `0x7D06F0`, bittable `0x780760` — Recipe 3 table.)

**Layout:** `FLui_Layout_BeginUpdate 0x5d72b0` · `EndUpdate 0x5d72c0` · `ScrollContentRect 0x803430` ·
`ChildNode_SetParent 0x50adb0` · dock host `*(*0x14A8750+0xb18)`.

**Skin:** `FLui_Skin_LoadNamedFont 0x653690` · `FLui_Skin_SetFontColor 0x653c30` · `FLui_Skin_GetThemeColor 0x76b6f0`
· `FLui_Skin_BlendColor 0x626f90` · `FLui_Skin_ApplyColorTransform 0x7636f0` · `FLui_Skin_LookupElementTypeId 0x76b5a0`
· paint primitives `FillRect 0x58f530`/`RoundRect 0x58fa70`/`TextOut 0x58fbb0`/`DrawTextRect 0x58fde0`/`DrawBitmap 0x58eb80`
· `FLui_Paint_GetSkinCtx 0x7fdac0` · canvas obj `widget+0x304`, font obj `widget+0x49c`.

**Focus / input:** `FLui_Focus_RequestFocus 0x5ddeb0` · `FLui_Focus_CanFocus 0x5de520` · `FLui_Input_DispatchChar 0x5dc2a0`
· `FLui_Input_PerformControlMsg 0x5d2870` · text-capture flag `FUN_00802820 0x802820` (sets edit+0x490 bit 0x8) ·
`TFruityLoopsMainForm.FormShortCut 0x114de10` (gate `mainForm+0x4c5`) · `FormKeyDown 0x10c9920` · edit text `@+0x624`.

**Hints / drop:** `FLui_SetStatusHintAndRefresh 0x10ec870` · hint bar `*0x14A7580` · `FLui_Browser_SelectMenuItem_worker 0xe14ff0`.

**Menus:** `FLmenu_CreateItem_CaptionClick 0x70e1a0` · `FLmenu_BuildBarFromActionList 0x7046b0` · `FLmenu_LayoutBar 0x7049f0`
· `FLmenu_ShowPopup 0x70ab80` · `flChildCount 0x81dda0` · `flChildAt 0x81ddc0` · item class `&PTR_FUN_00706308`
(caption +0x78, show-in-bar +0x86, child list +0xb0, onClick +0x100/+0x108, checkable +0x140) ·
`NewMainMenuPopup 0xcb9ee0` · bar `*(toolbarForm+0x878)` · actionList `*(mainForm+0x760)` · masterRoot `*(actionList+0x7c)`.

**Globals:** mainForm `*0x14A8750` (==`DAT_01581200`) · formMgr `*0x14AA6E8` · toolbar `*0x14aa4c8` ·
window mgr `*(*0x14ABCA8+0x40)` (vtbl[0x70] ids 0=PL/1=CR/2=PR/4=Mix) · dock host `mainForm+0xb18` ·
focused browser `*0x157ffb8`.

---

## Confidence map

**Fully concrete (decompiled; several live-proven) — build straight from these:**
- **Recipe 1 (host window):** HIGH. Factory + caption + show/hide + descriptor + dock flag all decompiled;
  factory create + show/hide **live-proven** (re/14). Open: the clean WP-form destructor (use hide+DestroyWindow).
- **Recipe 2 (foreign embed, #80):** HIGH. `SetParent`-via-`FLui_WP_SetParentWindow`/`EmbedForeignWindow`
  primitives + `+0x45c`/`+0x354` fields decompiled; FL's own in-process precedent proves the mechanism.
- **Recipe 3 (controls):** HIGH. Create→parent→bounds→realize→refresh + text set/read **live-proven** for
  TQuickEdit on a panel (re/14 B1). Per-type value/event offsets decompiled.
- **Recipe 4 (layout):** HIGH. SetParent/SetBounds/anchor/scroll-flags/Begin-EndUpdate/dock decompiled; `0x138`/
  `0x188` confirmed live.
- **Recipe 5 (skin):** HIGH. Font catalog, `LoadNamedFont`, `GetThemeColor`, `BlendColor`, paint primitives,
  descriptor format/offsets all decompiled.
- **Recipe 6 (focus, #25):** HIGH on mechanism. `FLui_Focus_RequestFocus`, the separate-form rule, the
  `FormShortCut` 3-block gate, and the `edit+0x490` capture flag are decompiled; the FormShortCut-suppression
  workaround is **shipped/live**. The separate-WP-form path is the recommended clean fix.
- **Recipe 8 (menus):** HIGH. Item create/insert, bar rebuild, category=separator model, View top-index scan all
  decompiled; the View-insert + eject path is **shipped** in `dllmain.cpp`.

**Needs a live confirm (low risk; do as impl step 1):**
- `vtbl[0x200]` Show is **not** reliable per-class — use the **reparent-to-show / reparent-away-to-hide** pattern
  (Recipe 3 caveat, re/14 B2). Confirm per control class you use.
- Recipe 3: TQuickPaintBox **OnPaint TMethod slot**; multi-line rendering of a read-only TQuickEdit (`+0x682`
  wrap flag did NOT preserve newlines live → use a tree/list for multi-line — re/14 Stage C).
- Recipe 1/2: the chosen repurpose classRef shows a clean empty frame; cross-thread focus + DPI of a reparented
  WPF child (re/22-host-plan §4).
- Recipe 5: the `P_ILGlyphs`/`P_WebSymbols` icon-glyph **codepoint table** (not dumped); the theme bitmap-parts
  loader (behind the dynamic theme-obj vtable).
- Recipe 8: the menu-item **destructor** for fullest eject (else `+0x86=0` hide-and-leave); the live
  toolbar/bar pointer (one-shot capture hook).
- The window-manager **concrete vtbl** (`*(*0x14ABCA8+0x40)`) is runtime-resolved — read it live for ids 0/1/2/4
  if you want FL's dispatch to drive our window (Recipe 1g; not required — direct FormShow/FormClose works).

**Cross-references for deeper per-window worked examples:** channel strip = ui-win-channelrack §4–5; mixer strip
+ command-bus ids = ui-win-mixer §4/§10; PR/Playlist/Event canvas draw + hit-test = ui-win-eventedit §3c;
browser tree node API + custom tab hook = ui-win-browser §4/§10 + re/14 "REAL TAB"; app shell/toolbar =
ui-win-shell.
