# FL UI — Input / events / focus / hints / drag-drop (re/ui-05)

Area: how mouse/keyboard/focus/hover/hints/drag-drop route through FL's **WP** widget framework.
Module `FLEngine_x64.dll`, image base `0x400000`. Runtime rebase = `ghidra - 0x400000 + flEngineBase`.
Builds on re/13 (WP controls), re/14 (chat-UI / forms / focus blocker), re/22 (window host).

**Headline result:** the WP keyboard-focus mechanism (the parked chat-input blocker) is RESOLVED, and the
`PTR_DAT_014ab950`/`FUN_009aeb40`/`FUN_009aeb00` triad the prior pass called "the focus mechanism" is
**NOT** the focus system — it is the *focused data-browser* + its tab-state queries (see §8). The real
focus API is `FLui_Focus_RequestFocus@0x5ddeb0` + the per-form focused-child pointer `root+0x4b0`.

---

## 0. Architecture — two input layers (why this is subtle)

FL's UI is a **hybrid**:
- **Top-level windows are VCL `TForm`s** (TFruityLoopsMainForm, TFXForm, TPluginForm, TNameEditForm, the
  browser `TSampleListForm`, etc.) — each is a real Win32 HWND with standard VCL message handling and the
  VCL events `FormShortCut` (OnShortCut) / `FormKeyDown` (OnKeyDown) / `FormMouseDown`.
- **Inside a form, the "WP" framework** draws custom controls (knobs/wheels/edits/lists/steps). A WP control
  that needs the OS (edits, the form root) is **windowed** (has its own HWND cached at `ctrl+0x45c`); most
  leaf controls are canvas-drawn and share an ancestor's HWND. WP adds its own message router + hit-test +
  focus chain on top of Win32.

Keyboard "focus" therefore exists at **two levels**:
1. **Win32 focus** — `GetFocus@0x424b70` / `SetFocus@0x4252e0` are just IAT thunks (confirmed). Whichever
   HWND has OS focus.
2. **WP focus** — the focus-root form tracks its focused child at `root+0x4b0`; a global focused control
   lives in the window/focus manager at `DAT_014bdc98+0xc4`. Setting WP focus also drives Win32 SetFocus on
   the windowed control's HWND.

---

## 1. WP message dispatch — the entry points

### `FLui_Input_WPWindowMsgProc @0x5fa520`  (windowed-control subclass WndProc wrapper)
`void(ctrl /*RCX*/, MSG* msg /*RDX*/, HWND hwnd /*R8*/, WNDPROC origProc /*R9*/)`.
`msg` is a VCL `TMessage`-ish record: `Msg@+0 (u32)`, `WParam@+8`, `LParam@+0x10`, `Result@+0x18`.
This is installed as a subclass over a windowed WP control; it routes then falls through to
`CallWindowProcW(origProc,...)`. Switch (relevant slices):
| msg | meaning | action |
|---|---|---|
| `0x100`/`0x104` | WM_KEYDOWN / WM_SYSKEYDOWN | `FLui_Input_DispatchKeyDown(ctrl,msg)` |
| `0x101`/`0x105` | WM_KEYUP / WM_SYSKEYUP | `FLui_Input_DispatchKeyUp(ctrl,msg)` |
| `0x102` | WM_CHAR | `FLui_Input_DispatchChar(ctrl,msg)`; Enter(0x0d)/Esc(0x1b) close-edit special |
| `0x07` | WM_SETFOCUS | `root=FLui_Focus_GetRootContainer(ctrl,1)`; `root->vtbl[0x2e0](root,ctrl)` (notify focus-in) |
| `0x200` | WM_MOUSEMOVE | `FUN_008434f0(*PTR_DAT_014aa6e8, ctrl, msg)` (mouse mgr — see §5) |
| `0x205` | WM_RBUTTONUP | context-menu via `ctrl->vtbl[0x150]` |
**Key fact:** when a key/char handler returns "handled", this proc returns **without** calling
`CallWindowProcW` → the key is **eaten and not bubbled** to the parent window / the main form. That is the
mechanism by which a focused WP edit "captures" a key (incl. space).

### `FLui_Input_WPFormWndProc @0x5d9460`  (WP form/container message body — from the mouse fork)
The form-level handler that owns mouse range `0x200–0x20e`, hover, `WM_MOUSELEAVE 0x2a3`, WM_NCHITTEST,
WM_MOUSEACTIVATE; delegated to by per-class WndProcs (`FUN_005fa930`, `FUN_00834150`, …). See §5.

### Per-class form message pre-handler `FUN_00834150` (NOT renamed — generic form msg handler)
Handles WM_SETFOCUS(7) → SetFocus to `form+0x4b0`; the `0xb047` "child got focus" path (HWND→WP via
`FUN_005cb4f0`, then `focused->vtbl[0x2e0]`, saving/killing previous focus); also NCPaint/SysCommand. Tail
calls `FLui_Input_WPFormWndProc`. Cross-area (form/window core) — left unnamed, plate context noted.

### `FLui_Focus_IsActiveForm @0x5d18c0`
`return DAT_014b9cd8 == self`. `DAT_014b9cd8` = the **active top-level WP form**; the form WndProc gates
keyboard handling on "am I the active form".

---

## 2. Keyboard dispatch (down / up / char)

### `FLui_Input_DispatchKeyDown @0x5dbf70`  (WP method selector `0xffb8`)
`char(ctrl, event)`. `event{+8 key:short, +0x10 flags(Win32 lParam-style)}`.
1. `root = FLui_Focus_GetRootContainer(ctrl,1)`; if `root` valid & `root+0x4c4` set → recurse into it first
   (this is the WP **KeyPreview**: the form/container sees the key before the leaf). Bubble order is
   root-first then `ctrl` itself.
2. `DAT_014b9cb4 = event.flags` (records last key flags globally).
3. Dispatch `ctrl`'s own KeyDown (selector `0xffb8`) unless `ctrl+0xa0 & 0x1000`.
4. Returns 1 = handled (consumed).

### `FLui_Input_DispatchKeyUp @0x5dc120`  (selector `0xffb7`) — same shape as KeyDown.

### `FLui_Input_DispatchChar @0x5dc2a0`  (selector `0xffb6`)
`char(ctrl, event)`. Recurses the focused child (children with `ctrl+0x4c4` set) first, then calls `ctrl`'s
char handler with `event+8` (the char; zeroed if handled). **A focused WP edit eats WM_CHAR here**, so the
char never reaches the default proc / the main form → typing (incl. space) stays in the edit.

### Globals
- `DAT_014b9cb4` (`PTR_DAT_014a7f10` points here) — last key-event flags. **Bit `0x40000000` = auto-repeat
  (prev-key-state).** `FormKeyDown` computes `bVar10 = (flags & 0x40000000)==0` = "first press" — space
  triggers play only on first press, not on auto-repeat. (Prior pass mis-read this as an edit-mode flag.)
- `+0x4c4` (control byte) — "this control/subtree participates in key routing (wants keys / KeyPreview)".

---

## 3. THE FOCUS MECHANISM (RESOLVED)

### Set focus on a control — `FLui_Focus_RequestFocus @0x5ddeb0`  ← **the public entry**
```c
void FLui_Focus_RequestFocus(ctrl){
  root = FLui_Focus_GetRootContainer(ctrl, 0);     // nearest focus-root form ancestor
  if (root) FLui_Focus_SetFocusedControl(root, ctrl);
  else if (*(ctrl+0x354)) SetFocus( GetHWND(ctrl) );   // fallback: raw Win32 focus
  else FUN_00830460(ctrl,1);                        // (no focus-root -> raises FL assertion)
}
```

### `FLui_Focus_SetFocusedControl @0x837d40 (root, ctrl)`
`root+0x4c5` = "root is the active window". If active & `ctrl == root+0x4b0` (already current) & CanFocus →
reassert Win32 SetFocus. Always → `FLui_Focus_StoreFocusedChild(root,ctrl)`; if root was inactive →
activate it (`root->vtbl[0x260]`).

### `FLui_Focus_StoreFocusedChild @0x837c60 (root, ctrl)`  ← the actual store + notify
```c
if (root+0x4b0 != ctrl) {
   *(root+0x4b0) = ctrl;                  // <<< THE per-form focused-child pointer
   if (!(root+0x34 & 1)) {
      if (root+0x4c5 /*active*/) FLui_Focus_ApplyWin32Focus(root);  // Win32 SetFocus child HWND
      dispatch(root, 0xffac);            // OnFocusChanged notify
   }
}
```

### `FLui_Focus_ApplyWin32Focus @0x838270 (root)`
Picks the effective child, `SetFocus(GetHWND(child) /*child+0x45c*/)`, and on success sends WP msg `0xb029`
(focus-confirmed) via `FLui_Input_PerformControlMsg`.

### Supporting
- `FLui_Focus_GetRootContainer @0x830440 (ctrl, toTop)` → `FUN_008303e0`: walk parents (`ctrl+0x78`) up to the
  focus-root form (instanceof class `PTR_FUN_00826f38`). `toTop=1` forces top; `=0` nearest. Returns root|0.
- `FLui_Focus_CanFocus @0x5de520 (ctrl)` → `ctrl+0x45c != 0` (control is windowed / has an HWND).
- **GetHWND of a WP control** = `FUN_005ddf70(ctrl)` → ensures handle (`FUN_005ddf30` = HandleNeeded, vtbl
  `+0x1d0`) then returns `ctrl+0x45c`. (Widget-core; not renamed.)
- `vtbl[0x2e0]` = control "receive-focus / focus-in" method (called from WM_SETFOCUS + `0xb047`).

### GET the focused control
- Per-form / per-root: read **`root+0x4b0`** (cast to control*).
- App-global focused control: **`DAT_014bdc98 + 0xc4`** (the WP window/focus manager; +0xcc = secondary).
  Re-applied on form activation by `FUN_008412a0` (`SetFocus(GetHWND(*(mgr+0xc4)))`).
- Active top-level form: **`DAT_014b9cd8`**.

### Field map (WP control)
| off | meaning |
|---|---|
| `+0x34` (u16) | flags; bit0 suppresses focus-changed notify; bit4 (0x10) blocks focus traversal |
| `+0x45c` | cached HWND (windowed controls) — GetHWND result; CanFocus test |
| `+0x4b0` | **focused-child pointer** (on a focus-root form) |
| `+0x4c4` | "wants keys / KeyPreview participant" byte |
| `+0x4c5` | "is the active window" byte (on a focus-root form) |
| `+0x78` | parent control pointer |
| `+0x2b0` | (form) Win32 HWND (re/14) |

---

## 4. (reserved)

---

## 5. Mouse dispatch, capture, hover  (from the mouse fork — all renamed `FLui_Input_*`, tag `UI_input`)

### Dispatch chain
- **Router** `FLui_Input_MouseRouteToControl @0x5d91e0`: if `GetCapture()==form HWND` → target = the captured
  control; else hit-test. Dispatches with **control-local** coords (`x-ctrl+0x90`, `y-ctrl+0x94`).
- **Hit-test** `FLui_Input_HitTestPoint @0x5d90f0`: walks child lists `form+0x370` then `form+0x368` (count
  `@list+0x10`, item via `FUN_00508bc0(list,i)`), top-most first, recurses containers; per-child test
  `FLui_Input_PtInControl @0x5d9020` (rect via `ctrl->vtbl[0xe8]`, PtInRect, then WP msg `0xb00a`).
- **Per-control send** `FLui_Input_PerformControlMsg @0x5d2870`: builds `TMessage{msg,wParam,lParam,result}`
  and invokes the control's **WindowProc TMethod `{code @ctrl+0x80, Self @ctrl+0x88}`**. *This is the
  synthetic-event hook* (also used by `FLwp_SetControlValue@0x5d0d10` for `0xb00d` notify).
- HWND→WP object: `FLui_Input_WPObjectFromHwnd @0x5cb4f0` (`GetPropW` w/ FL atom `DAT_014b9cbe`);
  cursor→control `FLui_Input_ControlAtCursor @0x5cdb50`.

### Capture
- `FLui_Input_SetMouseCapture @0x5cb7f0(ctrl|0)` — sets WP captured-control global **`DAT_012c4350`**,
  resolves the owning form HWND (`form @ctrl+0x78`, HWND via `FUN_005ddf70`), calls Win32 `SetCapture`.
  Pass `0` to release.
- `FLui_Input_GetMouseCapture @0x5cb7b0`; modal-drag pair `FLui_Input_SetCaptureWithProc @0x5cbd20` /
  `…ReleaseCaptureWithProc @0x5cbd90`; `FLui_Input_EnsureWin32Capture @0x5e6f50`.
- Win32 thunks: `GetCapture@0x424a60`, `SetCapture@0x425290`, `ReleaseCapture@0x425170`.

### Hover / hot control
- Per-form hovered ("hot") control = **`form+0x444`**; TrackMouseEvent-armed flag = **`form+0x440`**. On
  WM_MOUSEMOVE the WndProc sends `0xb014` (mouse-LEAVE) to old, `0xb013` (mouse-ENTER) to new, arms
  `TrackMouseEvent(TME_LEAVE)`; on WM_MOUSELEAVE(0x2a3) sends `0xb014` + clears `form+0x444`.
- App-wide last-hover control = **`DAT_012c4338`**, cleared by `FLui_Input_ClearHotControl @0x5e11e0`
  (selector `0xffd2`) and the WP control dtor.

### Mouse TMethod event slots (fired by the control's WindowProc)
MouseDown/Click `+0x144/+0x14c` (button:char, shift:u16, x,y:int); secondary/change `+0x1e4/+0x1ec`;
MouseActivate `+0x214/+0x21c`; DblClick `+0x1f4/+0x1fc`; wheel OnChange `+0x398/+0x3a0`.

---

## 6. Hints / tooltips

FL shows hints in its OWN skinned **floating hint bar**, not the OS tooltip window (no `TApplication.OnHint`
popup path is used for the main UI).

- **Hint-bar form singleton = `DAT_0157c980`** (`TFLHintBarForm`). Lifecycle: `FormCreate@0xb64f60`,
  `FormDestroy@0xb65420`, `FormPaint@0xb65730`, `FormMouseDown@0xb65460`. Repaint =
  **`FLui_Hint_BarRefresh@0xb654a0`** (calls its `vtbl[0x178]`; gated by visibility flags).
- **Current status/hint text global = `DAT_015817d0`** (Delphi UStr). Auto-clear timer state in
  `DAT_015817d8` / `DAT_015817e0` (`0x5dc` = 1500 ticks).
- **Setter:** `FLui_SetStatusHintCore(?,hintUStr,flag)@0x10ec570` — writes `DAT_015817d0`, pushes the text to
  the hint-bar control `*(app+0xbd0)` via `FUN_005d0ae0`, then refreshes. `+refresh` wrapper
  `FLui_SetStatusHintAndRefresh@0x10ec870`. (Also injects license-expiry warnings when `param_2==0`.)
- **Python API (reveals the globals):** `FLpy_ui_getHintMsg@0xe13450` (reads via `FUN_010ec800`),
  `FLpy_ui_setHintMsg@0xe13540` (worker `FUN_00e13500`), `FLpy_ui_getHintValue@0xe13af0`.
- **Per-control hint text** is stored at **`ctrl+0xe4`** (re/13 sets it via descriptor
  `FUN_004133f0(ctrl+0xe4, "|^^hint")`); the control also has a **Hint TMethod at `ctrl+0x2bc/+0x2c4`** for
  dynamic hints. **Hover→hint flow:** on hover the WP form WndProc sends `0xb013` (mouse-ENTER) to the hot
  control (`form+0x444`); the control's enter handling pushes its hint text up to the status bar
  (→ `FLui_SetStatusHint*` → `DAT_015817d0` → `DAT_0157c980`). On leave (`0xb014`) the hint is cleared/timed out.
- **Hint-string format:** FL hint strings use the `|` separator and `^^` markup prefixes (sections /
  value-format hints); exact grammar not fully enumerated this pass.
- `FL_UpdateTransportStatusHint@0x12637d0` is an example producer (transport position → status hint).

## 7. Drag-drop

Three drop paths, all converging on per-window drag-over / drop handlers.

**OLE drop target (drops from Explorer / other apps):** imports `RegisterDragDrop@0x468bc0`,
`RevokeDragDrop@0x468bd0`, `DoDragDrop@0x468be0`. FL registers an `IDropTarget` for its windows — registration
callers: `FUN_006e2630`, `FUN_006e26d0`, `FUN_006e2d20`, `FUN_00935da0`, `FUN_00951ab0`.
- **DragOver:** `TFruityLoopsMainForm.DropFileTargetDragOver@0x1150130` → **`FLui_Drag_ResolveDropWindow(dragObj)
  @0x6e2bb0`** picks the FL sub-window currently under the cursor, then dispatches to that window's drag-over:
  channel rack `FUN_00f4a240`, event editor `TEventEditForm.CheckDragOver@0xd4c6c0`, browser `FUN_00f8c0c0`.
  Current drop-target window is cached at **`mainForm+0x3360`**.
- **Drop:** `TFruityLoopsMainForm.DragWAVDrop@0x10caa70` (the actual file/WAV drop). Sibling drop handlers on
  other forms: `TMEnvEditor.DragWAVDrop@0xc24390`, `TPluginForm.DragWAVDrop@0xe95400`.

**Shell file drop (`WM_DROPFILES`):** `DragAcceptFiles@0x55f2c0`, `DragQueryFileW@0x55f2e0`,
`DragQueryPoint@0x55f2f0`, `DragFinish@0x55f2d0`.

**Internal drag (browser node → channel rack / playlist / mixer):** uses a drag-image
(`ImageList_BeginDrag@0x55ee20`, `ImageList_DragMove@0x55ee60`, `ImageList_DragShowNolock@0x55ee70`,
`ImageList_EndDrag@0x55ee30`) and the same per-window DragOver/Drop handlers above.
`TFruityLoopsMainForm.EEDragToRearrangePatternsMenuClick@0x114bbc0` is one internal-drag mode; bit-table drag
paint helper `FLui_Ctl_BitTable_DragPaint@0x7809d0` (control-area find, not renamed).

**Reuse — accept a drop on OUR window:** register an `IDropTarget` on our window's HWND via
`RegisterDragDrop` (mirror `FUN_006e2630`), or for simple file drops call `DragAcceptFiles(hwnd, TRUE)` and
handle `WM_DROPFILES` with `DragQueryFileW`/`DragFinish`. To feed a dropped sample/path into FL, route it to
the same target handlers (e.g. the channel-rack/browser drop funcs) rather than re-implementing import.

---

## 8. FormShortCut / FormKeyDown / space-capture — relationship (the parked blocker)

### What the prior pass mislabeled
`PTR_DAT_014ab950` → `DAT_0157FFB8` = the **focused data browser** (the `TVirtualDataBrowser` with focus),
NOT a WP focus global. `FUN_009aeb40`/`FUN_009aeb00` operate on *that browser*: they query its current tab
state (`FUN_009aeb00` = "current tab kind byte `tab+0x10d`==4"). They are browser tab-focus helpers, used by
`FormShortCut`/`FormKeyDown` to decide whether the focused browser should eat arrow/typing keys. Not the
control focus mechanism. (This pass renamed them `FLui_Focus_BrowserCanReceiveKey@0x9aeb40` /
`FLui_Focus_BrowserTabContentIsKind4@0x9aeb00` + plate-commented `PTR_DAT_014ab950` to flag the re/14
mis-identification; they remain browser-area for the browser doc.)

### The two VCL gates on the main form (fire FIRST via KeyPreview when the main form is the active form)
- **`TFruityLoopsMainForm.FormShortCut @0x114de10`** (OnShortCut): branch A `if (mainForm+0x4c5==0)` →
  dispatch the shortcut/action module (`mainForm+0x760`, selector `0xffef`) which contains **space=play** and
  all global hotkeys → consumes. (Branches B/C re-dispatch for `+0x3310` and the focused browser on
  WM_KEYDOWN/SYSKEYDOWN.)
- **`TFruityLoopsMainForm.FormKeyDown @0x10c9920`** (OnKeyDown): **unconditionally** consumes space (0x20) →
  `FUN_010c3c30` (play/stop), gated only by `bVar10` = first-press (not auto-repeat). There is **no
  "is a text edit focused" check** here.

### Why FL's own edits CAN type space, and why the chat edit could not
FL's editable UIs (rename `TNameEditForm`, the browser search `TQuickEdit` on `TSampleListForm`, the picker)
live on **separate WP forms**. When focus is in one of them, *that* form is the active VCL form, so the
**main form's FormShortCut/FormKeyDown never run** for those keystrokes; the focused WP edit eats WM_CHAR
locally via `FLui_Input_DispatchChar` (which returns "handled" → no bubbling). Confirmed concretely by
`TNameEditForm.NameEditFormKeyDown@0x797e30`: it handles Enter/F2-F5 itself and lets all printable keys
(incl. space) reach the edit — because the main form is out of the loop.

The parked chat input failed because its keystrokes still reached the **main form's** FormShortCut/FormKeyDown
(it was effectively on / forwarding to the main form, and/or it never actually held WP focus, i.e.
`root+0x4b0` was not our edit), so space hit the play shortcut before any WM_CHAR.

---

## 9. REUSE RECIPE (for the injected bridge)

### A. Make OUR control receive keyboard input  ← solves the space-capture blocker
1. **Host the input on a SEPARATE WP form**, not as a child of `TFruityLoopsMainForm`. Use the proven
   standalone-form path from re/14 (form factory `FLui_CreateFormFromClassRef@0x10C2AA0`; HWND at
   `form+0x2b0`) and create a `TQuickEdit` on it via the re/13 create→skin→descriptor→bounds→parent→show
   sequence. Because that form becomes the active VCL form when clicked, the main form's
   FormShortCut/FormKeyDown do not see the keys → **space and all printable keys type normally**.
2. **Give it WP focus** (so WM_KEYDOWN/WM_CHAR route to it): call **`FLui_Focus_RequestFocus(ourEdit)`**
   `@0x5ddeb0` on FL's UI thread. (Ensure the edit is windowed first — it must have `ourEdit+0x45c != 0`;
   the standard create path + show triggers HandleNeeded `vtbl[0x1d0]`.)
3. **Detect Enter / read text**: TQuickEdit text model is the Delphi UStr at `edit+0x624` (re/14); Enter is
   seen in the edit's KeyDown TMethod (or poll a commit flag like NameEditForm's `form+0x4e8`). WM_CHAR is
   eaten by `FLui_Input_DispatchChar` so it never plays.
4. If you must instead host a **Win32 child** edit (no FL skin): same separate-top-level-window rule applies
   — give it its own HWND on a tool window owned by FL's main HWND (not a child of the main form's client),
   so FL's FormShortCut isn't the active-form handler. Then standard `WM_CHAR`/`WM_GETTEXT` work.

   *Do NOT* try to keep the edit as a child of the main form and "suppress" shortcuts — there is no clean
   public flag for it (the main-form `+0x4c5` is a VCL form field, and FormKeyDown's space handler has no
   edit bypass). The separate-form path is how FL itself solves this.

### B. Focus set / get (quick reference)
- **Set focus:** `FLui_Focus_RequestFocus(ctrl)` @0x5ddeb0.
- **Get focus (per form):** read `root+0x4b0`. **Get focus (global):** `*(DAT_014bdc98+0xc4)`.
- **Get a control's HWND:** `FUN_005ddf70(ctrl)` → `ctrl+0x45c`.

### C. Receive / inject mouse
- Real mouse is automatic once the control is parented into a WP form (the form's
  `FLui_Input_WPFormWndProc` hit-tests `form+0x370/0x368` and dispatches).
- Inject a synthetic mouse event: `FLui_Input_PerformControlMsg(ctrl, msg, wParam, lParam)` @0x5d2870 on the
  UI thread (`msg` = 0x201/0x202/0x200/0x204/0x20a; `lParam` = control-local `((y<<16)|(x&0xffff))`;
  `wParam` = MK_* / wheel delta in HIWORD).
- Drag with capture: `FLui_Input_SetMouseCapture(ctrl)` on down, `(0)` on up.

### D. Hints/tooltips & drop — see §6/§7.

---

## 10. Address table (Ghidra, base 0x400000)

| addr | name | role |
|---|---|---|
| 0x5fa520 | FLui_Input_WPWindowMsgProc | windowed-control subclass WndProc; key/char/setfocus/mouse routing |
| 0x5d9460 | FLui_Input_WPFormWndProc | WP form message body (mouse range, hover, leave) |
| 0x5dbf70 | FLui_Input_DispatchKeyDown | WP keydown (sel 0xffb8), KeyPreview+focus chain |
| 0x5dc120 | FLui_Input_DispatchKeyUp | WP keyup (sel 0xffb7) |
| 0x5dc2a0 | FLui_Input_DispatchChar | WP char (sel 0xffb6) — focused edit eats key here |
| 0x5ddeb0 | FLui_Focus_RequestFocus | **public set-focus on a WP control** |
| 0x837d40 | FLui_Focus_SetFocusedControl | set focus-root's focused child |
| 0x837c60 | FLui_Focus_StoreFocusedChild | writes root+0x4b0; fires 0xffac |
| 0x838270 | FLui_Focus_ApplyWin32Focus | Win32 SetFocus child HWND + 0xb029 |
| 0x830440 | FLui_Focus_GetRootContainer | walk up to focus-root form |
| 0x5de520 | FLui_Focus_CanFocus | ctrl+0x45c != 0 |
| 0x5d18c0 | FLui_Focus_IsActiveForm | DAT_014b9cd8 == self |
| 0x5d91e0 | FLui_Input_MouseRouteToControl | capture-or-hittest mouse router |
| 0x5d90f0 | FLui_Input_HitTestPoint | point→control |
| 0x5d2870 | FLui_Input_PerformControlMsg | send TMessage to a control (synthetic-event hook) |
| 0x5cb7f0 | FLui_Input_SetMouseCapture | set/release WP mouse capture (DAT_012c4350) |
| 0x424b70 / 0x4252e0 | GetFocus / SetFocus | Win32 IAT thunks (NOT the WP mechanism) |
| 0x114de10 | TFruityLoopsMainForm.FormShortCut | global-hotkey gate (space=play); pre-empts child edits |
| 0x10c9920 | TFruityLoopsMainForm.FormKeyDown | unconditional space=play; no edit bypass |

Globals: `DAT_014bdc98+0xc4` global focused control; `root+0x4b0` per-form focused child;
`DAT_014b9cd8` active form; `DAT_014b9cb4` last key flags (0x40000000=auto-repeat); `DAT_012c4350` mouse
capture; `form+0x444` hot control; `PTR_DAT_014ab950`→`DAT_0157FFB8` focused **browser** (not focus).

---

## 11. ANSWER — focus set/get + how to make our control receive keyboard

- **Set focus on a control:** `FLui_Focus_RequestFocus(ctrl) @0x5ddeb0`. (Internals: resolve focus-root via
  `FLui_Focus_GetRootContainer@0x830440`, then `FLui_Focus_SetFocusedControl@0x837d40` →
  `FLui_Focus_StoreFocusedChild@0x837c60` writes `root+0x4b0=ctrl` and Win32-SetFocuses its HWND.)
- **Get focus:** per-form `root+0x4b0`; app-global `*(DAT_014bdc98+0xc4)`; control HWND `ctrl+0x45c`.
- **Make OUR control receive keyboard (and type space):** put a windowed WP `TQuickEdit` on a **separate WP
  form** (not the main form), call `FLui_Focus_RequestFocus(ourEdit)`. Keys then route via
  `FLui_Input_WPWindowMsgProc@0x5fa520` → `FLui_Input_DispatchKeyDown/Char` to our edit, which eats WM_CHAR
  locally; because the main form is not the active VCL form, `FormShortCut`/`FormKeyDown` never fire, so
  space (and every printable key) is typed instead of triggering play. That is exactly how FL's own
  rename/search/picker edits work, and it resolves the parked space-capture blocker.
