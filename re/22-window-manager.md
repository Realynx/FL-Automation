# FL window manager: create / register / show-hide / dock-float + View-menu wiring (task #22)

Goal: map how FL CREATES, REGISTERS, SHOWS/HIDES, DOCKS and FLOATS its windows, and how the **View** menu
toggles drive them — so a future **native FL Agent chat window** can register + show/hide/dock as a first-class
FL window and wire into a View toggle. RESEARCH ONLY (static Ghidra decompile of `FLEngine_x64.dll`, image base
0x400000). No source edited, no live FL poked, no Ghidra edits (shared instance with siblings → left read-only).
Rebase at runtime: `runtime = ghidra - 0x400000 + flEngineBase` (flEngineBase from `flprobe bridge info`).

Builds on: re/13 (WP framework), re/14 (chat UI / browser tabs / form factory), re/16 (menu system). No
re/22-window-host.md sibling doc exists yet — this doc subsumes the window-host object mapping.

## TL;DR / verdict
- FL has **three** "window" tracking layers, not one:
  1. **Form manager `*0x14AA6E8`** — the master REGISTRY of every wpform. Forms live in a TList at
     **`formMgr+0x20`** (array @+0x8, count @+0x10). Enumerate via `FormMgr_GetItem(mgr,i)@0x526230` /
     `FormMgr_Count(mgr)@0x526280`. **A form is registered here automatically by going through the form
     factory** `FLui_CreateFormFromClassRef@0x10C2AA0` → `FLui_CreateFormCore(formMgr,classRef,&slot)@0x841EF0`
     (re/13/14). This is the registry that close-all / focus / lifetime treat as first-class.
  2. **The "windows" controller `*(*0x14ABCA8 + 0x40)`** — a higher-level object the **View menu drives**.
     Its virtual API is the show/hide/toggle dispatch (below). `*0x14ABCA8` is the app/engine general
     controller (+0x8 logger, +0x30 data-folders, **+0x40 = the window manager**).
  3. **Main-form workspace/dock host `mainForm+0xb18`** (`*0x14A8750 + 0xb18`) — the docking area; supplies
     dock origin (vtbl[0xe0]) + rect (vtbl[0xe8]); iterated by close-all.
- **The single show/hide/toggle dispatch is virtual:** `wm->vtbl[0x70](wm, windowId, mode, focus)` with
  **windowId: 0=Playlist, 1=Channel rack, 2=Piano roll, 4=Mixer**; the docked **Browser** uses a different slot
  `wm->vtbl[0x60](wm, 2, mode)`. Every View "show window" item calls exactly one of these.
- **The ✓ checkmark source = each window form's `+0xa9` "visible" byte.** `MenuCheckRefresh@0x10F20A0` (fired on
  main-menu popup, re/16) copies each form's `+0xa9` into the matching View menu-item's `.Checked` (item
  vtbl[0xe0]). Show/hide writes that same `+0xa9` (`FormSetVisible@0x833EC0`). So the ✓ and the window state are
  the one byte `+0xa9`.
- **Concrete primitives** (all clean Delphi, no VMProtect): show `FormShow@0x7EA5D0` → set-visible
  `FormSetVisible@0x833EC0` (flips +0xa9); hide `FormHide@0x83AF10`; close `FormClose@0x83ACF0`; app-window
  style+show `FLwp_FormSetAppWindowVisible@0x82FBB0`; dock/float one window `WindowSetAttached@0x1228CD0`;
  dock/float ALL editors `SwitchEditorsAttached@0x110C320`; close-all `CloseAllWindows@0x1113EE0`; arrange
  workspace `ArrangeWorkspace@0x10F5130`.
- **A new first-class window** = (a) create through the form factory so it joins `formMgr+0x20`; (b) give it the
  standard form struct (esp. a `+0xa9` visible byte + HWND@+0x2b0 + CanClose vtbl) so `FormShow/FormClose` and
  `WindowSetAttached` work on it; (c) add a View menu item (re/16) whose onClick toggles it and whose ✓ we drive
  ourselves on popup from our form's +0xa9 (FL's `MenuCheckRefresh` is hardcoded to the 5 built-ins).
- **Confidence HIGH** on: the View-action→`wm.vtbl[0x70/0x60]` dispatch + window-ID table; the +0xa9 ✓ source +
  `MenuCheckRefresh` wiring; the show/hide/close/dock concrete functions; the form-manager registry + accessors.
  **MEDIUM / live-resolve**: the *concrete* wm class/vtbl address (the dispatch is virtual through
  `*(*0x14ABCA8+0x40)` — resolve live by reading that pointer then +0x70/+0x60); the standalone form-manager
  "Add" (registration happens inside the form's `vtbl[0x78]` init from CreateFormCore, not separately pinned).

---

## 1. The registry layer — form manager `*0x14AA6E8`

`FormMgr_Count@0x526280(mgr)`:
```
return mgr+0x20 ? *(int*)(*(mgr+0x20)+0x10) : 0;        // TList.Count
```
`FormMgr_GetItem@0x526230(mgr, i)`:
```
list = *(mgr+0x20);  return *(void**)(*(list+0x8) + i*8); // TList.Items[i]  (bounds-checked)
```
⇒ **all wpforms are tracked in a Delphi TList at `formMgr+0x20`** (array ptr @list+0x8, count @list+0x10).

Creation/registration (re/13/14, re-confirmed): `FLui_CreateFormFromClassRef(classRef,&slot)@0x10C2AA0`
calls `FLui_CreateFormCore(*0x14AA6E8 /*formMgr*/, classRef, &slot)@0x841EF0`:
```
inst = (**(code**)(classRef - 0x30))(classRef);   // Delphi metaclass create-instance
*slot = inst;
(**(code**)(*inst + 0x78))(inst, 0xFF, formMgr);   // vtbl[0x78] = init/setup; THIS is where the
                                                   //   form adds itself to formMgr's list + grabs HWND
// special-case: if inst is the MAIN app class (&PTR_FUN_00828d20):
//   formMgr+0xa4 = inst;  set WS_EX_APPWINDOW on formMgr+0x2b0;  FLwp_FormSetAppWindowVisible(...)
inst+0x2b0 = the Win32 HWND.                       // standard ShowWindow/DestroyWindow apply (re/13)
```
So the form's own `vtbl[0x78]` performs the registry insert (no standalone "RegisterForm" export). The main
application form is additionally cached at **`formMgr+0xa4`** and the main HWND at **`formMgr+0x2b0`**.

Note `DAT_014BDC90` is also the form manager (== `*0x14AA6E8`); `FormClose` checks `*(DAT_014bdc90+0xa4)` to
detect "is this the main form" → app-quit path.

## 2. The windows controller `*(*0x14ABCA8 + 0x40)` — the show/hide/toggle dispatch

`*0x14ABCA8` = the app/engine **general controller** (its members: +0x8 startup/log obj, +0x30 user-data-folder
obj, **+0x40 = the window manager (wm)**). Every View window-toggle resolves the wm the same way and calls one
virtual slot:

| slot | signature | meaning |
|------|-----------|---------|
| `wm->vtbl[0x70]` | `(wm, int windowId, int mode, bool focus)` | **show/hide/toggle a primary window by ID** |
| `wm->vtbl[0x60]` | `(wm, int panelId, int mode)` | **toggle a docked side panel** (Browser, panelId=2) |
| `wm->vtbl[0x30]` | `(wm)` | notify/activate (called from the form-activate path `FUN_00A899F0`) |

**Window-ID table (vtbl[0x70]):** `0 = Playlist`, `1 = Channel rack`, `2 = Piano roll`, `4 = Mixer`
(3 = unused/other). **mode**: `2` = toggle (the normal View click), `0`/`1` = force hide/show (Piano roll uses
this for its capture/focus special-case). **focus** byte = derived from the sender class (menu item vs toolbar
button) — controls whether the window also takes focus.

The concrete vtbl target was not pinned to a static address (the object is dynamic; `*0x14ABCA8` is initialized
at startup and the wm class wasn't resolvable purely statically). **To get it live:**
`wm = *(void**)(*(void**)rt(0x14ABCA8) + 0x40); showFn = (*(void***)wm)[0x70/8];` then it's a normal call.
The dispatch ultimately drives the per-window form singletons in §3 via the §4 primitives.

## 3. The window form singletons + the standard form/window struct

Each primary window is a global singleton form (a pointer-to-pointer global). All share the standard FL form
("window host") struct:

| window | global (ptr-to-ptr) | ✓ source |
|--------|---------------------|----------|
| Playlist | `*0x14AAB88` | form+0xa9 |
| Channel rack / Step seq | `*0x14A8BF8` | form+0xa9 |
| Piano roll | `*0x14A9B20` | `(form != 0)` (created on demand) |
| Browser | `*0x14ABFF8` | form+0xa9 |
| Mixer | `*g_MixerManagerPtr` | form+0xa9 |

**Standard form / window-host struct fields** (the object a "first-class window" must look like):
| off | type | meaning | evidence |
|----:|------|---------|----------|
| +0xa9 | u8 | **visible / open flag** (THE ✓ source) | written by FormSetVisible@0x833EC0; read by MenuCheckRefresh + Maximize |
| +0x34 | u16 | form style flags (bit3 = no-dock?) | FormSetVisible, WindowSetAttached |
| +0x4c2 | u8 | maximize state (==2 maximized) | Maximize actions |
| +0x66c | u8 | bit0 = construct guard, bit1 deferred-show, bit3 closing | FormSetVisible / FormClose |
| +0xb8 | ptr | **host/child-wrapper** (hosted editors); host+0x78 = parent (≠0 ⇒ docked) | WindowSetAttached |
| +0xc0 / +0xc4 | i32 | window x / y position | WindowSetAttached |
| +0xd4 | u32 | flags; **bit 0x4 = docked/attached** | WindowSetAttached |
| +0x2b0 | HWND | the Win32 handle | re/13 |
| +0x6e8 | UStr | skin descriptor (`forms.X:wpform`) | re/14 |
| vtbl[0x2d8] | | CanClose (returns bool) | FormClose |
| vtbl[0x340] | | set-maximized / toggle | Maximize actions |
| vtbl[0x358] | | bring-to-front / post-show | FormShow / Maximize |
| dyn 0xfff1 | | OnShow-ish (Delphi dynamic method) | FormShow |
| dyn 0xffaa | | close-query (CloseQuery) | FormClose |

`PTR_DAT_014A83E0` = the main-form / UI-globals object that holds the View **menu-item** object pointers at
fixed offsets (+0x5f8 Playlist, +0x600 Piano roll, +0x608 Channel rack, +0x610 Mixer, +0xb8 Browser) — see §5.
(Distinct global from `mainForm = *0x14A8750`; both are main-form-ish.)

## 4. The concrete create / show / hide / close / dock / float functions

| function | addr | signature | what it does |
|----------|------|-----------|--------------|
| `FLui_CreateFormFromClassRef` | 0x10C2AA0 | (classRef, &slot) | **create+register** a form (→ formMgr list, HWND@slot+0x2b0) |
| `FLui_CreateFormCore` | 0x841EF0 | (formMgr,classRef,&slot) | the core factory (re/13/14) |
| `FormShow` (FUN_007ea5d0) | 0x7EA5D0 | (form) | **show**: FormSetVisible(form,1) + base refresh `FUN_005d0ea0` + dyn 0xfff1 + vtbl[0x358] (front) |
| `FormSetVisible` (FUN_00833ec0) | 0x833EC0 | (form, vis) | **the show/hide primitive** — flips `+0xa9`; on show calls realize `FUN_00836670`; base show `FUN_005d08c0(form,vis)`; +0x66c deferred-vis guard |
| `FormHide` (FUN_0083af10) | 0x83AF10 | (form) | hide (used by close-mode1 + re-dock) |
| `FormClose` (FUN_0083acf0) | 0x83ACF0 | (form) | **close**: CanClose vtbl[0x2d8] + CloseQuery dyn 0xffaa → hide / minimize(`FUN_00836600`) / free(`FUN_0083b050`); main form → app-quit `FUN_00842220` |
| `FLwp_FormSetAppWindowVisible` | 0x82FBB0 | (HWND, vis, activate) | toggles `WS_EX_APPWINDOW` (0x40000) + ShowWindow on the HWND |
| `WindowSetAttached` (FUN_01228cd0) | 0x1228CD0 | (window, attachFlag) | **dock(1)/float(0) ONE window** — see below |
| `SwitchEditorsAttached` (FUN_0110c320) | 0x110C320 | (mainForm, attached) | **dock/float ALL editors** (the "Switch editors to Attached/Detached" macro) |
| `CloseAllWindows` (FUN_01113ee0) | 0x1113EE0 | (mainForm, tag) | iterate + close every window (per-window `FUN_01113d90`) |
| `ArrangeWorkspace` (FUN_010f5130) | 0x10F5130 | (mainForm) | arrange windows into the workspace |
| `MenuCheckRefresh` (FUN_010f20a0) | 0x10F20A0 | () | **set View ✓ from +0xa9** (§5) |

**`WindowSetAttached@0x1228CD0(window, attach)` — the dock vs float primitive:**
- **Attached-style editor** (`window+0xb8 == 0`): if the desired state differs from current bit `0x4` at
  `window+0xd4`: read the dock origin from `mainForm+0xb18` (vtbl[0xe0]); on **dock** set `+0xd4 |= 4` and add
  the dock origin to `+0xc0/+0xc4`; on **float** clear `+0xd4 &= ~4` and subtract it (snapping back to a free
  position). ⇒ **docked state lives in bit 0x4 of `window+0xd4`** for these.
- **Hosted editor** (`window+0xb8 != 0`): if the host's parent (`*(host+0x78)`) disagrees with `attach`,
  re-dock/float the host via `FormHide(host)` + `FUN_0114b810(host)` + `FormShow(host)`. ⇒ **docked state =
  host (`window+0xb8`) has a non-null parent at `+0x78`** (same `+0x78` parent convention as WP controls, re/14).

**`SwitchEditorsAttached@0x110C320(mainForm, attached)`** enumerates every channel plugin-editor
(`channelList = *0x14A98D8`, count @+0x10, item via `FLcr_ChannelListGetItem`) and every mixer effect-editor
(`g_MixerTrackArrayPtr`, track stride 0x1474, 10 slots at `+0x1324 + i*8`) and calls
`WindowSetAttached(editor, !attached)` on each — i.e. the global "make all editors docked / floating".

## 5. View-menu → window wiring (the native pattern to integrate with)

**View toggles are Delphi `TAction.OnExecute` methods on `TShortcutsModule`.** The menu item's onClick
(re/16: item+0x100/+0x108) is bound — via the VCL TAction mechanism — to one of these:

| View item | action (TShortcutsModule.*ActionExecute) | addr | effect |
|-----------|------------------------------------------|------|--------|
| Playlist | ShowPlaylistActionExecute | 0xE43D00 | `wm->vtbl[0x70](0, 2, focus)` |
| Channel rack | ShowChannelRackActionExecute | 0xE43AC0 | `wm->vtbl[0x70](1, 2, focus)` |
| Piano roll | ShowPianoRollActionExecute | 0xE43BF0 | `wm->vtbl[0x70](2, 0|1, focus)` (capture/focus special-case) |
| Mixer | ShowMixerActionExecute | 0xE43B50 | `wm->vtbl[0x70](4, 2, focus)` |
| Browser | ToggleBrowserActionExecute | 0xE43F70 | `wm->vtbl[0x60](2, 2|3)` |
| (dock) Switch editors → Attached | MacroSwitchEditorsToAttachedActionExecute | 0xE45990 | `SwitchEditorsAttached(mainForm, 1)` |
| (float) Switch editors → Detached | MacroSwitchEditorsToDetachedActionExecute | 0xE459B0 | `SwitchEditorsAttached(mainForm, 0)` |
| Close all windows | CloseAllWindowsActionExecute | 0xE43640 | `CloseAllWindows(mainForm, tag)` |
| Arrange into workspace | ArrangeWindowsIntoWorkSpaceActionExecute | 0xE43620 | `ArrangeWorkspace(mainForm)` |
| Set default desktop layout | SetDefaultDesktopLayoutActionExecute | 0xE43A30 | (workspace layout) |
| Maximize playlist / piano roll | Maximize{PlayList,PianoRoll}ActionExecute | 0xE481C0 / 0xE48110 | form vtbl[0x340](max) + vtbl[0x358] (state @+0x4c2) |

(Full window-related action set also includes ShowDownloadsPanel, ShowNewsPanel, ShowNotificationsPanel,
ShowPluginDatabase, ShowPluginPerformanceMonitor, ShowProjectPicker, ShowUndoHistory, ShowWelcomeWindow,
AlignAllChannelEditors, OpenAudioEditor — all `TShortcutsModule.*ActionExecute`.)

The common prologue in each Show*Execute is just a sender-class check (`FUN_0040fe90(sender, classRef)`) to set
the `focus` byte, then the single wm virtual call. There are **no `Show*ActionUpdate` methods** for these
window toggles → the ✓ is NOT a per-action OnUpdate; it is set centrally by **`MenuCheckRefresh@0x10F20A0`**:

```
// fired on main-menu / NewMainMenu popup (re/16):
menuHost = *0x14A83E0;
setChecked(menuHost+0x5f8 /*Playlist item*/ , *(Playlist  +0xa9));   // item vtbl[0xe0] = SetChecked
setChecked(menuHost+0x608 /*ChannelRack item*/, *(ChannelRack+0xa9));
setChecked(menuHost+0x600 /*PianoRoll item*/ , (PianoRoll != 0));     // PR: exists-check
setChecked(menuHost+0xb8  /*Browser item*/   , *(Browser   +0xa9));
setChecked(menuHost+0x610 /*Mixer item*/     , *(Mixer     +0xa9));
```
⇒ **the View ✓ is driven, each time the menu opens, from the window form's `+0xa9` visible byte** (PR uses a
form-exists check). This is the exact loop: `FormSetVisible@0x833EC0` writes +0xa9 → next popup,
`MenuCheckRefresh` reflects it as the ✓.

## 6. How a NEW window (FL Agent chat) registers as first-class

The native pattern, in order of "how integrated" you want to be:

**A. Register in the form registry (mandatory for focus/Z-order/close-all to treat it as a window):**
create it through the factory `FLui_CreateFormFromClassRef(classRef,&slot)` so the form's `vtbl[0x78]` init
inserts it into `formMgr+0x20` and grabs HWND@+0x2b0. Constraint (re/13/14): **no blank-form classRef** — either
repurpose an existing simple form class, or author a Delphi WP form class. (Our shipped chat tab in re/14 side-
steps this by hosting WP controls inside the existing browser content panel; that is NOT a registered window. To
be a real first-class *window* you need a real form here.)

**B. Make it behave like an FL window** so the existing primitives just work: give the form struct a `+0xa9`
visible byte that `FormSetVisible@0x833EC0` flips, an HWND@+0x2b0, and a CanClose vtbl[0x2d8] + CloseQuery
(dyn 0xffaa). Then:
- show  = `FormShow@0x7EA5D0(form)` (or `FormSetVisible(form,1)` for a bare toggle),
- hide  = `FormHide@0x83AF10(form)` / `FormSetVisible(form,0)`,
- close = `FormClose@0x83ACF0(form)`,
- dock/float = `WindowSetAttached@0x1228CD0(form, 1|0)` (set up `+0xb8` host or use the `+0xd4` bit-0x4 path).

**C. Wire a View menu toggle + ✓** (re/16 mechanics):
1. Add the menu item with `FLmenu_CreateItem_CaptionClick@0x70E1A0(parentList, -1, makeUStr(L"FL Agent"),
   &TMethod{ourClickThunk, ourCtx})` (under the View top-level item, or your own top-level "Plugins" item).
2. The onClick thunk toggles our form: `if (*(ourForm+0xa9)) FormClose(ourForm); else FormShow(ourForm);`
   (mirrors what `wm.vtbl[0x70](id,2,..)` does for built-ins, but directly on our form — no need to extend the
   wm dispatch / claim a new window ID).
3. For the ✓: FL's `MenuCheckRefresh@0x10F20A0` is **hardcoded** to the 5 built-in forms at fixed `menuHost`
   offsets, so we can't ride it. Instead make our item checkable (item+0x140, re/16) and **set its ✓ ourselves
   on popup** from our form's +0xa9: hook `TToolbarForm.NewMainMenuPopup@0xCB9EE0` (or the bar popup, re/16),
   and in it call our item's `setChecked` vtbl[0xe0] with `*(ourForm+0xa9)`. Same one-byte source FL uses.

**D. (Optional, heavier) be driven by the wm itself** — claim a new windowId in `wm.vtbl[0x70]` so the standard
dispatch shows/hides us. This needs hooking/extending the (virtual, runtime-resolved) wm at `*(*0x14ABCA8+0x40)`
and is unnecessary given C already gives a native-looking toggle + ✓. Not recommended for v1.

**Teardown (eject), main thread, BEFORE FreeLibrary** (same hazard class as re/14/16): clear our menu item's
onClick TMethod (item+0x100/+0x108=0) and our popup hook FIRST; `FormClose`/destroy our form (remove it from the
formMgr list); restore `NewMainMenuPopup` if hooked. A dangling onClick/hook or a registry entry pointing at a
freed form = AV on the next menu/focus interaction.

## 7. Confidence + VMProtect
- **No VMProtect / obfuscation** on any function here — all clean Delphi, fully decompiled
  (`Show*ActionExecute`, `FormSetVisible@0x833EC0`, `FormShow@0x7EA5D0`, `FormClose@0x83ACF0`,
  `WindowSetAttached@0x1228CD0`, `SwitchEditorsAttached@0x110C320`, `MenuCheckRefresh@0x10F20A0`,
  `CloseAllWindows@0x1113EE0`, `FormMgr_GetItem/Count`).
- **HIGH confidence**: the View-action → `wm.vtbl[0x70/0x60]` dispatch + window-ID table (0/1/2/4 + browser);
  the +0xa9 ✓ source + `MenuCheckRefresh` wiring + menuHost item offsets; the show/hide/close/dock concrete
  functions + the +0xa9 / +0xd4-bit0x4 / +0xb8-host dock-state fields; the form-manager TList registry
  (`formMgr+0x20`) + accessors; main HWND@formMgr+0x2b0, main-form cache@formMgr+0xa4.
- **MEDIUM / resolve live**: (a) the *concrete* wm class + vtbl address — dispatch is virtual through
  `*(*0x14ABCA8 + 0x40)`; read it live to get the actual `vtbl[0x70]`/`[0x60]` targets. (b) The standalone
  form-manager "Add" — registration is performed inside the form's `vtbl[0x78]` init (from CreateFormCore), not
  a separate export; confirm by creating a form and checking `formMgr+0x20` count grows. (c) Whether
  `PTR_DAT_014A83E0` (menu-item host) is literally `mainForm (*0x14A8750)` or a UI-globals alias — both are
  main-form-ish; treat the +0x5f8/+0x600/+0x608/+0x610/+0xb8 menu-item offsets as relative to `*0x14A83E0`.

## Key addresses (ghidra / image base 0x400000)
| what | addr |
|------|------|
| form manager (registry) global | `*0x14AA6E8` (== `DAT_014BDC90`) |
| form-mgr list | `formMgr+0x20` (TList) |
| form-mgr get / count | `0x526230` / `0x526280` |
| form factory (create+register) | `0x10C2AA0` → `0x841EF0` |
| app/engine controller global | `*0x14ABCA8` |
| window manager (wm) | `*(*0x14ABCA8 + 0x40)` |
| wm show/toggle window (id,mode,focus) | `wm->vtbl[0x70]` (ids 0=PL 1=CR 2=PR 4=Mix) |
| wm toggle docked panel (Browser) | `wm->vtbl[0x60](2,mode)` |
| main form | `*0x14A8750` ; menu-item host `*0x14A83E0` |
| workspace / dock host | `mainForm+0xb18` |
| form visible flag (✓ source) | `form+0xa9` |
| form HWND | `form+0x2b0` |
| FormShow / FormSetVisible | `0x7EA5D0` / `0x833EC0` |
| FormHide / FormClose | `0x83AF10` / `0x83ACF0` |
| FLwp_FormSetAppWindowVisible | `0x82FBB0` |
| WindowSetAttached (dock/float one) | `0x1228CD0` |
| SwitchEditorsAttached (dock/float all) | `0x110C320` |
| MenuCheckRefresh (View ✓) | `0x10F20A0` |
| CloseAllWindows / ArrangeWorkspace | `0x1113EE0` / `0x10F5130` |
| View actions (TShortcutsModule) | Show{Playlist 0xE43D00, ChannelRack 0xE43AC0, PianoRoll 0xE43BF0, Mixer 0xE43B50}; ToggleBrowser 0xE43F70; SwitchEditors{Attached 0xE45990, Detached 0xE459B0} |
| window singletons | Playlist `*0x14AAB88`, ChannelRack `*0x14A8BF8`, PianoRoll `*0x14A9B20`, Browser `*0x14ABFF8`, Mixer `*g_MixerManagerPtr` |
| menu-item ptrs on host | +0x5f8 PL, +0x600 PR, +0x608 CR, +0x610 Mix, +0xb8 Browser |
