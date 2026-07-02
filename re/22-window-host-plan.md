# Hosting the FL Agent window INSIDE an FL native window host (task #22) — phased plan

Goal: make our **FL Agent chat** look integrated by putting it **inside one of FL's native window
hosts** (the shared FL window chrome/border that every FL window has). Per the user's directive:

- **Phase 1 (pragmatic, do-soon):** create/obtain an FL window-host frame at runtime from our bridge
  and **`SetParent` our EXISTING Win32/WPF chat window as a CHILD** into the host's content area, so it
  sits inside FL's chrome. Reuse what already works (the `FlAgentChatWindow` WPF window from
  `src/FruityLink.Plugins.FlAgent`, and the proven form/widget plumbing from re/13 & re/14).
- **Phase 2 (native):** put native **WP-widget** content directly in the host (no Win32 child), the
  re/14 chat-tab approach, fully native.

This is **RESEARCH/DESIGN ONLY** — static Ghidra (`FLEngine_x64.dll`, image base `0x400000`); no source,
`.slnx`, or `.props` edits; no live FL; not a git repo.

> Sibling anchors `re/22-window-host.md` / `re/22-window-manager.md` were **not present** when this was
> written, so the host ctor / content-area / show-hide / View-wiring below were RE'd directly here
> (static decompile, high confidence). If those siblings appear later, reconcile field names with this.

**Rebase (every session):** `runtime = ghidra - 0x400000 + flEngineBase`; get `flEngineBase` from
`flprobe bridge info` (last seen `0x67F90000` → `runtime = ghidra + 0x67B90000`). Store ghidra addrs +
rebase at runtime; the base changes on FL restart.

---

## 0. What "the window host" actually is (RE'd)

Every FL window is a **WP form** — a Delphi class derived from the WP base hierarchy
`TCustomWPForm` → `TWPForm` / `TUnpaintedWPForm` / `TChildWPForm` (class strings @ `0x7ded4f`,
`0x7e0145`, `0x7dfca9`, `0x7e0623`). **That base class is the shared "host/border"** the user means: it
owns the real top-level Win32 window and FL's skinned chrome. Concrete windows (channel rack, mixer,
browser, plugin form, message box…) are subclasses that fill the client area.

Key per-form fields (confirmed by decompile of the factory + caption setter):

| field | meaning | source |
|------:|---------|--------|
| `form + 0x2b0` | **the real top-level Win32 HWND** (the host window) | `FLui_CreateFormCore@0x841EF0`, re/13/14 (live) |
| `form + 0x110` | **caption** (Delphi UnicodeString) | `FLwp_SetFormCaption@0x841690` |
| `form + 0x144` | u8 — "has caption / caption-capable" (gate for the caption setter) | `0x841690` |
| `form + 0x17b` | u8 — APPWINDOW-suppress flag (set in the factory; if !=0 → no taskbar/caption path) | `0x841EF0` |
| `form + 0x6e8` | descriptor UStr (skin, e.g. `forms.nameeditform:wpform`) | re/13/14 |

So **the host's content area = the client rect of `form + 0x2b0`**. WP forms paint all their controls on
that ONE HWND (WP controls are normally HWND-less, drawn on the form canvas), so there is no separate
"content-panel HWND" to find for a basic form — `GetClientRect(form+0x2b0)` is our content rect, and a
real Win32 child we `SetParent` onto it **clips above** the WP painting. For a near-empty repurposed
host form, our child simply covers the client area = looks like the window's content. (This is the same
"+0x2b0 is a normal HWND, standard `SetParent`/`ShowWindow` apply" fact proven live in re/13/14.)

### FL's own precedent for hosting a FOREIGN child HWND (de-risks Phase 1)
FL already embeds arbitrary Win32 child windows inside its WP UI via a dedicated **"windowed WP control"**
class (the WP analogue of VCL `TWinControl`). Two confirmed methods on it:
- **Create the owned child HWND:** `FUN_005d82c0(ctrl, &cwParams)@0x5d82c0` → `CreateWindowExW(...)` →
  stores the HWND at **`ctrl + 0x45c`** (`cwParams` is a CREATESTRUCT-like blob: exStyle@+0xc,
  className ptr@+0x78, style@+8, x/y/w/h, parent@+0x20, hInstance@+0x48, lpParam@+0x28).
- **Set host / reparent the owned HWND:** `FUN_005d8e70(ctrl, hostHWND)@0x5d8e70` →
  `SetParent(*(ctrl+0x45c), hostHWND)` and tracks the host at **`ctrl + 0x354`**.
  (`FUN_007e3a20@0x7e3a20` wraps it with a `SetParent(0)` detach first.)

Callers (e.g. `FUN_007825a0` building a favorites bar host) prove FL routinely `SetParent`s a child HWND
into an FL window in-process. **Conclusion: reparenting OUR HWND into an FL host is exactly what FL does
to itself — the "hosting HWNDs is fragile" warnings are about cross-PROCESS hosts; we are in-process.**

### Factory + native show/hide (the host keystone — already proven live in re/14)
- `FLui_CreateFormFromClassRef(classRef, &outSlot)@0x10C2AA0` → `FLui_CreateFormCore(formMgr, classRef,
  &outSlot)@0x841EF0`: `inst = (*(classRef-0x30))(classRef)` (metaclass create) → `*outSlot=inst` →
  `(*(*inst+0x78))(inst, 0xFF, formMgr)` (init). `formMgr = *0x14AA6E8`. HWND lands at `inst+0x2b0`.
  **Live-proven** in re/14 (the Gopher/help browser factory call created a real windowed form).
- **NO blank-form classRef** → must **repurpose** an existing simple form class (candidates in §1.1).
- **Native show/hide:** `FLwp_FormSetAppWindowVisible@0x82FBB0(HWND, show, activate)` — toggles
  `WS_EX_APPWINDOW` and `ShowWindow(SW_SHOW/SW_MINIMIZE)`. This is FL's sanctioned show; standard
  `ShowWindow(form+0x2b0, …)` / `DestroyWindow` also work.
- **Caption setter:** `FLwp_SetFormCaption@0x841690(form, captionUStr)` → `SetWindowTextW(form+0x2b0,
  chars)` + `Delphi_UStrAsg(form+0x110, ustr)` (gated on `form+0x144!=0` and `form+0x17b==0`). It is the
  form's published `set Caption` setter (referenced from the VMT property table @0x18f2f3c). Call it with
  `makeUStr(L"FL Agent")`.

---

## 1. PHASE 1 — concrete sequence (embed our existing Win32/WPF window in an FL host)

All FL/UI calls run on **FL's main thread** via the bridge's existing `SendMessage(WM_BRIDGE_*)`
marshaling (same discipline as the re/14 chat-tab + re/16 menu work). New work lives in
`tools/bridge/dllmain.cpp` (native) + a thin managed glue in `src/FruityLink.Host` /
`src/FruityLink.Plugins.FlAgent` (to hand over our window's HWND).

### 1.0 Get our window's HWND (managed side)
`FlAgentChatWindow` is a WPF `Window` on the plugin's STA dispatcher (`UiHost`, which already spins a
private STA thread when no host `Application` is running — the in-FL case). Two ways to expose a child
HWND; **(B) is recommended**:

- **(A) Reparent the whole `Window`:** `var h = new WindowInteropHelper(win).EnsureHandle();` gives the
  top-level HWND. We then strip its top-level styles and reparent it (§1.3).
- **(B) `HwndSource` child (cleaner):** instead of a top-level `Window`, create an
  `HwndSource(new HwndSourceParameters{ WindowStyle = WS_CHILD|WS_VISIBLE, ParentWindow = hostContent,
  Width=…, Height=… })` on the STA thread and set `source.RootVisual =` the chat UI (`root` Grid factored
  out of `FlAgentChatWindow`). No top-level frame to strip; it's *born* a child HWND. `source.Handle` is
  the child HWND. This is the standard "host WPF content inside a foreign Win32 parent" pattern and avoids
  WPF's top-level activation/sizing baggage.

Either way, the managed side passes the resulting **child HWND** to the bridge (e.g. a new
`FlBridge_Command("winhost_embed <hwndHex>")`, same in-proc command surface as re/16's `plugins_*`).

### 1.1 Create / obtain the host frame (FL main thread)
```
formMgr  = *(void**)rt(0x14AA6E8);
slot     = 0;
classRef = rt(<simple-form classRef>);          // repurpose target, see below
FLui_CreateFormFromClassRef(classRef, &slot);   // 0x10C2AA0  → slot = our host form
hostForm = slot;
hostHWND = *(HWND*)(hostForm + 0x2b0);           // the FL-chrome window
```
**Repurpose target (pick the simplest, live-confirm `slot+0x2b0` is a real HWND first):**
- `TMsgForm` (`forms.msgform:wpform`) — a message-box form; tiny, plain client area, easy to cover.
- `TTestForm` (`forms.testform:wpform`) — a debug/log form; almost certainly a near-empty client area
  (a memo) = ideal to overlay.
- `TTapTempoForm` (`forms.taptempoform:wpform`) / `TPluginMonitorForm` (`forms.pluginmonitorform:wpform`)
  — also small/simple.
- Fallback already documented in re/14: the add-channel **picker** classRef `&PTR_FUN_00dae368@0xDAE2A0`
  (proven repurposable), but it carries plugin wiring — prefer a plainer form.

> We don't care about the repurposed form's own controls — our child HWND covers the client rect. We DO
> want a form whose subclass `FormCreate` doesn't immediately do heavy/stateful work; `TMsgForm`/`TTestForm`
> are the safest. Live step 1 of impl: create each candidate, check `IsWindow(slot+0x2b0)` + that it shows
> a clean empty frame.

### 1.2 Set the caption to "FL Agent"
```
FLwp_SetFormCaption(hostForm, makeUStr(L"FL Agent"));   // 0x841690
```
`makeUStr` = the re/14 Delphi-UStr-const builder (header `codepage/elemsize`, `refcnt=-1`, len, UTF-16,
double-null; pass ptr-to-chars) or `FUN_0054c5e0(&slot, L"FL Agent")`.

### 1.3 Make our window a child + parent it into the host (FL main thread)
```
// style fix-up (do once, on the managed/STA side BEFORE handing the HWND over, or here):
LONG_PTR s = GetWindowLongPtrW(ourHWND, GWL_STYLE);
s &= ~(WS_POPUP | WS_OVERLAPPEDWINDOW | WS_CAPTION | WS_THICKFRAME
       | WS_SYSMENU | WS_MINIMIZEBOX | WS_MAXIMIZEBOX);
s |=  (WS_CHILD | WS_VISIBLE | WS_CLIPSIBLINGS);
SetWindowLongPtrW(ourHWND, GWL_STYLE, s);
SetWindowLongPtrW(ourHWND, GWL_EXSTYLE,
   GetWindowLongPtrW(ourHWND, GWL_EXSTYLE) & ~(WS_EX_APPWINDOW | WS_EX_TOOLWINDOW));

SetParent(ourHWND, hostHWND);          // 0x425330 (FL's own SetParent target)

RECT rc; GetClientRect(hostHWND, &rc); // 0x424a90
SetWindowPos(ourHWND, HWND_TOP, 0, 0, rc.right, rc.bottom, SWP_SHOWWINDOW); // 0x425490-ish
```
- If using `HwndSource` (§1.0 B), the child is already `WS_CHILD`/parented — skip the style strip and the
  `SetParent` (pass `hostHWND` as `ParentWindow` at creation); just do the `SetWindowPos` sizing.
- **z-order:** `WS_CLIPSIBLINGS` + `HWND_TOP` so our child sits cleanly above any WP painting on the host.

### 1.4 Track host resize/move so the child fills the content rect
The host form is resizable (FL chrome). Keep our child glued to the client rect:
- **Preferred:** subclass `hostHWND` with `SetWindowSubclass` (the bridge already uses a window subclass
  for `WM_BRIDGE_*`; add a branch) — on `WM_SIZE`/`WM_WINDOWPOSCHANGED`, `GetClientRect(hostHWND)` and
  `SetWindowPos(ourHWND, …, w, h, …)`. Restore the subclass on eject (§1.6).
- **Or** (cleaner, fully native) host via the **windowed WP control** (§3 / Phase 1.5) so FL's layout
  engine sizes us — no subclass.

### 1.5 (Optional, "more native" embed) host via FL's windowed WP control
Instead of manual `SetParent` + manual resize, reuse FL's own foreign-HWND host control so FL tracks
bounds/reparenting for us:
1. Create the windowed control (class with `FUN_005d82c0`/`FUN_005d8e70`; resolve its classRef from the
   vtbl that contains `0x5d8e70`), add it to the host form's WP layout (re/13 create→parent
   `vtbl[0x138]`→bounds `vtbl[0x188]` sequence).
2. Replace its owned child HWND: `SetParent(ourHWND, *(ctrl+0x45c)'s parent)` — or simpler, after the
   control creates its own HWND, `SetParent(ourHWND, ctrl+0x45c)` and size to it; or point `ctrl+0x45c` at
   our HWND and let `FUN_005d8e70` reparent it. Then FL's WP layout drives position/size, and docking
   (§2) comes "for free". This is the bridge between Phase 1 and Phase 2 — try it once §1.1–1.4 works.

### 1.6 Show + teardown (eject-safe)
- **Show:** `FLwp_FormSetAppWindowVisible(hostHWND, 1, 1)` (or `ShowWindow(hostHWND, SW_SHOW)`).
- **Hide:** `FLwp_FormSetAppWindowVisible(hostHWND, 0, 0)`.
- **Eject (BEFORE `FreeLibrary`, on FL main thread), strict order:**
  1. **Un-reparent our child first:** `SetParent(ourHWND, NULL)` (restore it as a top-level window) — or
     destroy our window entirely on its STA thread. This detaches us from FL's host so nothing dangles.
  2. **Remove our host-form subclass** (§1.4) and restore the original WndProc.
  3. Destroy/free the host form we created (hide via `FLwp_FormSetAppWindowVisible(…,0,0)` then free via
     the form manager; the WP-form free fn is still not fully RE'd — re/14 §8.4 — so the safe-minimal path
     is `ShowWindow(hostHWND, SW_HIDE)` + `DestroyWindow(hostHWND)` + null our refs; live-confirm).
  4. Only then unload. **Our WndProc is WPF's, living in the CLR host (pinned, never unloaded — see
     re/integration-pending-proxy)**, so the classic "dangling WndProc after unmap" risk is lower than the
     bridge's RWX thunks; but the host-form subclass (if any) and any bridge thunk MUST be restored first.

### 1.7 Where the HWND comes from / glue summary
```
managed (STA, our UI)                      bridge (FL main thread, dllmain.cpp)
  HwndSource child  ──childHwnd──►  FlBridge_Command("winhost_embed <hwnd>")
     RootVisual = chat UI               └─ WM_BRIDGE_WINHOST_OPEN: create host form, set caption,
                                            SetParent(childHwnd, hostHWND), size, subclass, show
  (close)           ─────────────►  FlBridge_Command("winhost_close")
                                        └─ un-reparent, unsubclass, destroy host form
```

---

## 2. Caption + View ▸ FL Agent toggle integration

- **Caption** = §1.2 (`FLwp_SetFormCaption(hostForm, "FL Agent")`) → the FL chrome shows "FL Agent".
- **View toggle wiring.** We already add menu items natively (re/16: `FLmenu_CreateItem_CaptionClick
  @0x70e1a0` with an onClick TMethod thunk; the existing "Plugins" submenu in
  re/integration-pending-plugins-toolbar proves the whole add-item + click-thunk + eject path works).
  Add a **View ▸ FL Agent** item the same way (append to the "View" master-menu child, or keep it under
  Tools ▸ Plugins). Its onClick thunk toggles our hosted window. **Two implementations:**
  - **Simplest (recommended):** the thunk flips our own state and calls
    `FLwp_FormSetAppWindowVisible(hostHWND, vis, vis)` (proven). No dependency on FL's view registry.
    Optionally set/clear the item's check glyph (re/16 caption-✓ trick).
  - **Most native (mirror FL's own View items):** FL's View toggles go through a **window/view manager**.
    RE'd from `TShortcutsModule.ShowChannelRackActionExecute@0xE43AC0`:
    ```
    mgr = *(void**)(*(void**)rt(0x14ABCA8) + 0x40);   // the view/window manager
    mgr->vtbl[0x70](mgr, group, viewId, visible);      // ShowView(group, id, bool)
    // channel rack uses (group=1, id=2, toggleState)
    ```
    To use this for us we'd have to register our host as a managed "view" (group/id) — heavier and not
    required. Document it as the future "dockable, behaves-exactly-like-a-built-in-window" path; **for now
    drive `FLwp_FormSetAppWindowVisible` directly.**
  - The View item's check state can be refreshed on popup (re/16: `MainMenuPopup`/`FUN_010f20a0` ticks
    items via `vtbl[0xe0]`), or just re-asserted each toggle.

---

## 3. PHASE 2 sketch — native WP-widget content (no Win32 child)

This is the re/13/14 path, **already implemented and live-proven** for the in-browser chat tab — reuse it
to fill the host form's content with native WP widgets so there is no foreign HWND at all:
- Build native widgets on `form+0x2b0`'s WP layout (or on the windowed control of §1.5): a single-line
  **`TQuickEdit`** input + a multi-line/read-only display (re/14 "Stage B1" recipe, all live-proven):
  create `FUN_0074c400(classRef,1,0)@0x74c400` → parent `vtbl[0x138]` → bounds `vtbl[0x188]` → setup
  `FUN_005ceef0`/`FUN_005d0d90`/`FUN_005d0c50` → refresh `FUN_0077adb0@0x77adb0`; text set
  `FUN_0074c260@0x74c260`, read `@ctrl+0x624`. A **Send** `TQuickBtn` (`FLwp_CreateButtonControl@0xF0DDB0`,
  onClick `+0x1e4/+0x1ec`, re/14 C2b) avoids the Enter problem.
- Comms unchanged: `chat_poll` / `chat_say` over the in-proc `FlBridge_Command` (re/14 Stage C); the C#
  agent loop drives it.
- **Carry-over caveats from re/14 (must solve for full native):** WP `TQuickEdit` is **single-line**
  (`FUN_0074c260` sanitizes `\r`/`\n`→space) → multi-line history needs a TREE/LIST widget (node
  add/clear API still to RE); and the **space-key capture** (FL's `FormShortCut@0x114de10` eats space)
  was worked around in re/14 C2d by suppressing FL shortcuts while our content is active — broad but
  functional. **These WP-input pains are exactly why Phase 1 (a real Win32/WPF child with its own focus)
  ships first.**

---

## 4. Risks, fallback, and confidence

### Going INTERNAL (Phase 1 reparent) — risks
- **Threading / message loop (LOW-MED).** Our WPF child runs its Dispatcher on its **own STA thread**
  (`UiHost`), not FL's main thread. Cross-thread `SetParent` (child on thread A under a parent on thread B)
  is permitted and is what we rely on; the child keeps pumping its own messages, so our UI stays
  responsive even if FL is busy. **Upside:** because FL's VCL message pump never sees our child's
  keystrokes, **FL's shortcut dispatcher (`FormShortCut`) does NOT eat our space** — the parked
  WP-focus space-capture issue (re/14 C2*) **does not apply** to a Win32/WPF child. This is the core
  reason to do Phase 1 first. Residual: activation/focus hand-off between FL's frame and our child can be
  finicky (clicking the FL chrome vs our child); mitigate with `WM_MOUSEACTIVATE`/explicit `SetFocus`,
  and consider `AttachThreadInput(ourTid, flTid, TRUE)` only if focus misbehaves (un-attach on eject).
- **DPI (MED).** WPF is (per-monitor) DPI-aware; FL may be system-DPI. A child inherits a DPI context
  from its parent chain — a mismatch → blurry/wrong-sized content. Mitigate: set the thread/window DPI
  awareness context to match FL, or accept minor Phase-1 cosmetic imperfection and tune later.
- **Resize tracking (LOW).** Handled by subclassing the host (§1.4) or by the windowed-control path
  (§1.5). Subclass must be restored on eject.
- **Eject safety (MED — the main hazard, as with all bridge work).** Un-reparent our child + restore the
  host subclass + destroy the host form, **on the FL main thread, before unload**, in the §1.6 order. The
  WPF WndProc lives in the pinned CLR host (not unloaded in production), which de-risks the dangling-proc
  AV relative to the RWX thunks; the host-form subclass is the thing that must be reverted.
- **Repurposed-form behavior (LOW-MED).** A repurposed `TMsgForm`/`TTestForm` may run subclass logic on
  create/close. Live-confirm a clean empty frame; pick the plainest candidate; the §5 fallback covers it.

### Fallback (explicit)
**Keep the current external Win32/WPF top-level window** (today's `FlAgentChatWindow`, shown via
`Show()`/`Hide()`), optionally **owned** by FL's main HWND (`GWLP_HWNDPARENT`) so it floats with FL and
gets FL's taskbar grouping — *without* reparenting. This is zero-risk and already works; the only thing
lost is sitting literally inside FL's border. Ship Phase 1 behind a flag; if reparenting proves unstable
on a user's machine, fall back to owned-top-level automatically.

### Confidence
- **HIGH** (static decompile, cross-checked, + prior live proofs in re/13/14): the host = a WP form with
  HWND@`+0x2b0` and standard `SetParent`/`ShowWindow` working; the factory (`0x10C2AA0`/`0x841EF0`,
  live-proven in re/14); native show/hide (`FLwp_FormSetAppWindowVisible@0x82FBB0`); caption setter
  (`FLwp_SetFormCaption@0x841690`, fields `+0x110/+0x144/+0x17b`); FL's own foreign-HWND host control
  (`FUN_005d82c0`/`FUN_005d8e70`, HWND@`+0x45c`, host@`+0x354`); the View-toggle manager pattern
  (`*(*(0x14ABCA8)+0x40)->vtbl[0x70](group,id,vis)`).
- **MED** (needs the impl-step-1 live confirm): the best repurpose classRef for a clean empty host frame;
  the WP-form free fn for cleanest eject (else hide+`DestroyWindow`); cross-thread focus behaviour and DPI
  fidelity of the reparented WPF child; whether to take the manual-SetParent (§1.3) or windowed-control
  (§1.5) route.
- **Overall:** Phase 1 (reparent our existing window into a repurposed FL host form) is **buildable and
  the right first ship** — it reuses the proven factory/caption/show plus FL's own in-process
  SetParent-foreign-HWND precedent, and it sidesteps the WP-input pains (space capture, single-line edit)
  that block full-native. Phase 2 (native WP widgets) is already largely proven (re/14) and is the
  cosmetic end-state.

---

## Key addresses (ghidra / image base 0x400000)
| what | addr |
|------|------|
| `FLui_CreateFormFromClassRef` | `0x10C2AA0` |
| `FLui_CreateFormCore` (formMgr=`*0x14AA6E8`) | `0x841EF0` |
| `FLwp_FormSetAppWindowVisible(HWND,show,act)` | `0x82FBB0` |
| `FLwp_SetFormCaption(form, ustr)` | `0x841690` |
| windowed-control: create owned HWND (→ctrl+0x45c) | `0x5D82C0` |
| windowed-control: set host / SetParent(+0x45c) | `0x5D8E70` (detach-wrapper `0x7E3A20`) |
| View toggle pattern (`mgr=*(*(0x14ABCA8)+0x40); vtbl[0x70]`) | `TShortcutsModule.ShowChannelRackActionExecute@0xE43AC0` |
| menu item create + onClick (re/16) | `FLmenu_CreateItem_CaptionClick@0x70E1A0` |
| `SetParent` / `GetClientRect` / `SetWindowTextW` imports | `0x425330` / `0x424A90` / `0x4253F0` |
| WP-form base class strings | `TCustomWPForm@0x7DED4F`, `TWPForm@0x7E0145`, `TChildWPForm@0x7E0623` |
| simple repurpose forms | `forms.msgform/testform/taptempoform/pluginmonitorform:wpform` |
| form fields | HWND `+0x2b0`, caption UStr `+0x110`, caption-flag `+0x144`, appwin-suppress `+0x17b`, descriptor `+0x6e8` |

(No Ghidra edits made — read-only static pass. Existing re/13/14/16 annotations relied upon.)
