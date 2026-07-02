# Top menu/toolbar RE + plan to insert a custom "Plugins" top-level menu (task #16)

Goal: learn how FL's top bar (`File Edit Add Patterns View Options Tools Help`) is built so we can later
insert our OWN top-level **"Plugins"** entry that opens a plugin-manager UI. RESEARCH ONLY — no project code
touched, no FL runtime poked (a sibling agent owns FL-runtime testing). All findings are STATIC (Ghidra
decompile of `FLEngine_x64.dll`, image base 0x400000). Rebase at runtime: `runtime = ghidra - 0x400000 +
flEngineBase` (get flEngineBase from `flprobe bridge info`, per prior passes).

## TL;DR / verdict
- **Menu type = CUSTOM FL control, NOT a stock VCL `TMainMenu`.** (VCL `TMainMenu`/`TMenuItem` RTTI and the
  Win32 menu APIs `InsertMenuItemW`/`SetMenuItemInfoW`/… ARE linked in, but they are NOT what draws the top
  bar.) The visible bar is a custom skinned control named **`NewMainMenu`** living on **`TToolbarForm`**
  (descriptor `forms.toolbarform:wpform`), at field offset **`TToolbarForm+0x878`**. It renders a data-driven
  array of entries built from FL's **action/shortcuts list**. The dropdowns are FL's own custom popup windows
  (own HWND + `SetWindowsHookExW` WH_CALLWNDPROC/WH_MOUSE modal loop) — definitively not native Win32 menus.
- **Recommended insertion (most native, reuses FL's own funcs):** create a "Plugins" menu item via
  `FLmenu_CreateItem_CaptionClick@0x70e1a0`, append it to the master menu root, add child items (with our
  onClick TMethod), then rebuild the bar with `FLmenu_BuildBarFromActionList@0x7046b0` +
  `FLmenu_LayoutBar@0x7049f0`. Click dispatch = FL invokes the item's onClick TMethod stored at
  **item+0x100 (code) / item+0x108 (data)** — we point it at our RWX thunk (same pattern as re/13/14 events).
- **De-risked fallback (proven toolkit):** add a `TQuickBtn` "Plugins" to the toolbar form via the re/13/14
  WP widget calls; onClick → either open our manager directly or show our own popup via `FLmenu_ShowPopup
  @0x70ab80`. Avoids mutating FL's menu tree entirely (cleanest eject).
- Confidence: HIGH on the structure/APIs (all decompiled + cross-checked). The one runtime gap is obtaining the
  live `TToolbarForm`/bar pointer — solvable with a one-shot capture hook (mirrors re/14 C2c) or the form
  manager. No VMProtect seen on any of these functions.

---

## 1. How the top bar is built

### 1.1 String anchors (the way in)
- `TMainMenu` RTTI @0x818319, `TMenuItem` RTTI @0x8159c6/0x816915 — VCL classes linked but unused for the bar.
- Win32 menu imports: `InsertMenuItemW@0x424ea0`, `SetMenuItemInfoW@0x425320`, `GetMenuItemInfoW`, … (VCL
  TMenu plumbing; not the bar).
- Delphi published-method table of the main form / toolbar form (the real lead):
  - `MainMenuPopup` → handler **0x10c8db0** (on TFruityLoopsMainForm).
  - `NewMainMenuResize` → **0xcba060** = `TToolbarForm.NewMainMenuResize` (empty stub).
  - `NewMainMenuPopup` → **0xcb9ee0** = `TToolbarForm.NewMainMenuPopup`.
- Menu CAPTIONS are code/DFM string constants with `&` accelerators, e.g. `&MIDI settings` @0x1340728
  (ASCII, lives in the form's streamed data). Searching captions with `&` breaks the MCP string tool's
  param parser — search the word without the `&` (e.g. `MIDI settings`).
- Toolbar form skin/descriptor strings: `forms.toolbarform:wpform` @0xcb893c, `forms.toolbar.background`,
  `forms.toolbar.toptoolbar.*` (so the bar is part of the skinned toolbar form).

### 1.2 The control + where it lives
`TToolbarForm` (descriptor `forms.toolbarform:wpform`) hosts the bar. From `TToolbarForm.FormCreate@0xcb7f90`:
```
FLmenu_BuildBarFromActionList( *(form+0x878) /*the NewMainMenu bar control*/,
                               *(*PTR_DAT_014a8750 + 0x760) /*action/shortcuts list*/ );
```
- **`bar = *(TToolbarForm + 0x878)`** — the custom menu-bar control ("NewMainMenu").
- **`actionList = *(mainForm + 0x760)`** — FL's action/shortcuts list object (class `&PTR_FUN_00707240`);
  this doubles as the **popup engine**. `mainForm = *PTR_DAT_014a8750` (also == global `DAT_01581200`).

### 1.3 Construction: `FLmenu_BuildBarFromActionList@0x7046b0` (was FUN_007046b0)
Signature: `void FLmenu_BuildBarFromActionList(bar, actionList)`.
- Stores `actionList` at `bar+0x514`.
- `masterRoot = *(actionList + 0x7c)` (the container of the top-level menus).
- Iterates `masterRoot`'s children (count `FUN_0081dda0`, get `FUN_0081ddc0(masterRoot, i)`); for each child
  whose **`item+0x86 != 0`** ("show in main menu bar"), appends a record to the Delphi dynamic array at
  **`bar+0x538`**, **stride 0x18**:
  | off | type | meaning |
  |----:|------|---------|
  | +0x00 | ptr | the menu-item object (`&PTR_FUN_00706308` class) |
  | +0x08 | int | computed x position (set by LayoutBar) |
  | +0x0c | int | computed width (set by LayoutBar) |
  | +0x10 | UStr | caption (Delphi UnicodeString, derived from `item+0x78`) |
- Calls `FLmenu_LayoutBar(bar, -1, 0)`.
- **ONLY caller = `TToolbarForm.FormCreate`** → the bar array is built ONCE at form creation (it is NOT rebuilt
  on every popup). So an inserted entry persists until the toolbar form is recreated; if FL recreates the
  toolbar form (layout reset / rescale) we must re-insert.

### 1.4 Layout: `FLmenu_LayoutBar@0x7049f0` (was FUN_007049f0)
`void FLmenu_LayoutBar(bar, int forcedWidth, int sepWidth)` — walks `bar+0x538`, measures each caption with the
bar font (`bar+0x49c`) via `FUN_00653fb0`, and writes each entry's x (+0x8) and width (+0xc). Pass `(-1, 0)` to
auto-measure (the FormCreate call). vtbl `[0xd8]` on each item = "is separator?".

### 1.5 The menu-item object (class VMT `&PTR_FUN_00706308`)
Created by `FLmenu_NewItemObj@0x70e040(classRef=&PTR_FUN_00706308, alloc=1, owner)`; base-init
`FLmenu_ItemBaseInit@0x81a100` sets the defaults below. Confirmed field map:
| off | type | meaning | source |
|----:|------|---------|--------|
| +0x18 | i64 | tag/value | dynamic items |
| +0x78 | UStr | **caption** | `FLmenu_SetItemCaption@0x81daf0` |
| +0x81 | u8 | enabled (default 1) | ItemBaseInit |
| +0x86 | u8 | **show-in-bar / visible (default 1)** | ItemBaseInit; read by BuildBar |
| +0x87 | u8 | sort/group order | InsertChild sorts on it |
| +0x88 | i32 | group id (default -1) | ItemBaseInit |
| +0xa0 | u16 | **auto item id** (`FUN_008199c0`) | ItemBaseInit |
| +0xb0 | ptr | **child list** (submenu items; TList, count @+0x10, array @+0x8) | InsertChild lazily creates |
| +0xbc | ptr | parent back-pointer | InsertChild |
| +0x100 | ptr | **onClick TMethod code** | `FLmenu_CreateItem_CaptionClick` |
| +0x108 | ptr | **onClick TMethod data (Self)** | same |
| +0x140 | u8 | checkable/radio | dynamic items |
| +0x150 | u8 | (init 0) | NewItemObj |
| +0x151 | u16 | flags (0x100/0x200/0x400 used by click predicate) | dynamic items |
| vtbl+0xc0 | | invalidate/redraw | |
| vtbl+0xd8 | | isSeparator | |
| vtbl+0xe0 | | setChecked / setVisible (used by `FUN_010f20a0` to tick View items) | |

Helper APIs:
- **`FLmenu_CreateItem_CaptionClick@0x70e1a0(parentList, int index, captionUStr, &TMethod{code,data})` -> item**
  — the one-shot "make a menu item with caption + click handler". index<0 = append. Internally: NewItemObj →
  SetItemCaption → `item+0x100=code; item+0x108=data` → `FLmenu_InsertChild(parentList, index, item)`.
- `FLmenu_InsertChild@0x81e090(parent, index, child)` — inserts `child` into `parent+0xb0`, sets
  `child+0xbc=parent`. (A menu item IS a container — its children live at +0xb0.)
- `FUN_0081dda0(item)` = child count (`*(*(item+0xb0)+0x10)`); `FUN_0081ddc0(item, i)` = child i.
- Make a Delphi UnicodeString caption: `FUN_0054c5e0(&ustrSlot, L"Plugins")` (used throughout, e.g.
  `FUN_0108fd30`), or build a UStr const per the re/14 recipe (header at ptr-12).
- Worked example of FL building a dynamic menu (item-add API in the wild):
  `TFruityLoopsMainForm.AddInstrumentMenuPopup@0x10f0810` and `FUN_0108fd30` create items with
  `FLmenu_CreateItem_CaptionClick(list, idx, makeUStr(L"Categories"), &TMethod{FUN_0111f120, DAT_01581200})`
  — i.e. TMethod.code = handler, TMethod.data = the global controller. We do exactly this with our own thunk.

---

## 2. Click dispatch

1. **Bar click → dropdown.** `FLmenu_BarMouseDown@0x7059a0(bar, mouseEvt)` (a bar vtbl method): guards
   `bar+0x534` (reentrancy), sets the hovered entry index `bar+0x530` (from `FUN_00705770`), then calls
   `FLmenu_ShowTopLevelDropdown@0x705880(bar)`.
2. **Show the dropdown.** `FLmenu_ShowTopLevelDropdown` reads the selected entry `bar+0x538 + bar+0x530*0x18`,
   verifies `bar+0x514` is class `&PTR_FUN_00707240`, computes screen x/y from the entry rect, and calls the
   custom popup `FLmenu_ShowPopup@0x70ab80(actionList, x, y, bar)` (param_5=0 → root = `*(actionList+0x7c)`;
   the engine expands the clicked top-level item's children = its `+0xb0` list).
3. **Popup engine.** `FLmenu_ShowPopup@0x70ab80(engine, x, y, ownerCtrl, rootItem|0)` builds an FL popup window
   (`FUN_006ba4f0(&PTR_FUN_006b9b88,…)`), installs `SetWindowsHookExW(WH_CALLWNDPROC)` + `(WH_MOUSE)` for its
   modal loop, runs it, then tears the hooks down on close (`FUN_0070b640`). This is FL's own skinned popup —
   not `TrackPopupMenu`.
4. **Leaf click → handler.** When an item is chosen, the engine fires the item's **onClick TMethod at
   item+0x100 (code) / item+0x108 (data)** (the slot set by `FLmenu_CreateItem_CaptionClick`). FL's hundreds of
   static menu actions are published methods of `TFruityLoopsMainForm` bound into this slot (e.g.
   `CutMenuClick@0x10e5010`, `AddCloudPluginsMenuClick@0x10f25a0`, `Add_BrowseAllInstalledPluginsMenuClick
   @0x1130780`, `DeletePluginMenuClick@0x10f33d0` — plenty of plugin-related precedent). Our entry uses the
   identical mechanism: set code = our RWX thunk, data = our ctx.

State updates (for checkmarks/enabled) happen on popup via `MainMenuPopup`/`NewMainMenuPopup@0xcb9ee0` →
`FUN_010f20a0` which calls each item's vtbl[0xe0](checked) — informational; we don't need it for a simple
"open manager" item.

---

## 3. Insertion plan — add a top-level "Plugins" menu (RECOMMENDED, native)

All steps on FL's MAIN/UI thread (use the bridge's existing `SendMessage(WM_BRIDGE_*)` marshaling). Resolve
runtime pointers each session (rebase from `flprobe bridge info`).

**Resolve handles**
```
mainForm   = *(void**)rt(PTR_DAT_014a8750)        // == DAT_01581200
actionList = *(void**)(mainForm + 0x760)          // class &PTR_FUN_00707240
masterRoot = *(void**)(actionList + 0x7c)         // container of the 8 top-level menus
bar        = *(void**)(toolbarForm + 0x878)       // NewMainMenu control  (toolbarForm: see §5)
```

**Build the "Plugins" entry + dropdown** (reusing FL's own creators)
```
TMethod nullTM = {0,0};
pluginsItem = FLmenu_CreateItem_CaptionClick(masterRoot, -1, makeUStr(L"Plugins"), &nullTM);
// pluginsItem+0x86 defaults to 1 -> it will appear in the bar.

TMethod ourTM = { (void*)&PluginsMenuClickThunk, (void*)g_ourCtx };   // RWX x64 thunk + ctx
mgrItem = FLmenu_CreateItem_CaptionClick(pluginsItem, -1, makeUStr(L"Plugin Manager…"), &ourTM);
// add more children the same way (enable/disable toggles, etc.)
```

**Make it show in the bar** (the bar array is built once, so rebuild it)
```
FLmenu_BuildBarFromActionList(bar, actionList);   // re-scans masterRoot incl. our new top item
FLmenu_LayoutBar(bar, -1, 0);                      // re-measure x/width
// trigger a repaint of the bar (re/13: FUN_0077adb0(bar), or the bar's invalidate vtbl).
```

**The click thunk** (x64 fastcall, SEH-safe, fast — same contract as re/13/14 event thunks)
```
void __fastcall PluginsMenuClickThunk(void* data /*=g_ourCtx, RCX*/, void* item /*RDX*/){
    // set a flag / push a pipe event so the C# app opens the plugin-manager UI.
    g_openPluginManager = 1;   // app polls this, or post WM_BRIDGE_* to ourselves
}
```
FL calls this on its UI thread when "Plugins > Plugin Manager…" is selected (it fires item+0x100 with
item+0x108 in the data register). Keep it tiny; do the real work (open WPF/our panel) off this callback.

Notes:
- `makeUStr` = `FUN_0054c5e0(&slot, L"…")` (FL builds one for you), or a hand-built Delphi UStr const
  (re/14 §"Stage B1" recipe: header `codepage/elemsize`, `refcnt=-1`, `len`, UTF-16, double-null).
- If you prefer NOT to rebuild the whole bar, you can instead append directly to `bar+0x538` (grow the Delphi
  dynarray by one 0x18 record = {pluginsItem, 0, 0, captionUStr}) then `FLmenu_LayoutBar(bar,-1,0)`. Rebuild
  via `FLmenu_BuildBarFromActionList` is simpler and is FL's own code path — prefer it.

### Teardown (eject) — ordered, MAIN thread, BEFORE `FreeLibrary`
1. **Clear our TMethod first:** set `mgrItem+0x100 = 0; mgrItem+0x108 = 0` (and any other items we added) so
   FL can never call our thunk after the DLL unmaps.
2. **Remove our items from the tree:** detach `pluginsItem` from `masterRoot` (find its index via
   `FUN_0081ddc0` scan, remove with FL's list-remove — `FUN_0064f4d0(masterRoot+0xb0 list, idx)` /
   `FUN_0064f540` are the remove/index-of seen in `FUN_0070b640`; then free the item via its class destructor).
   If a clean destructor isn't resolved in time, the **safe-minimal** path is: set `pluginsItem+0x86 = 0`
   (hidden from bar) and leave it allocated (FL frees it at shutdown) — inert once removed from the bar.
3. **Rebuild the bar** without our entry: `FLmenu_BuildBarFromActionList(bar, actionList)` +
   `FLmenu_LayoutBar(bar,-1,0)` + repaint.
4. Only then `FreeLibrary`. (A dangling onClick TMethod or a bar entry pointing at a freed item = AV — order
   1→2→3 strictly.)

---

## 4. Fallback plan — `TQuickBtn` on the toolbar (de-risked, proven toolkit)

If mutating FL's menu tree proves fragile in live testing, host our OWN control — this is the re/13/14 path
that is already PROVEN live (we created/parented `TQuickEdit`/`TQuickBtn` on FL panels without crashing):
1. Create a button: `FLwp_CreateButtonControl@0xF0DDB0()` (re/13).
2. Parent it onto the toolbar form's content (vtbl `[0x138]` = SetParent) near the menu strip; size via
   vtbl `[0x188]` (SetBounds); caption via `FUN_005d0ae0(btn, L"Plugins")`; realize via `FUN_005ceef0(btn, 6)`;
   render `FUN_0077adb0(btn)` (the exact sequence used by FL's own toolbar buttons — re/14 C2b).
3. Hook its onClick TMethod at **btn+0x1e4 (code) / btn+0x1ec (data)** (FL buttons fire +0x1e4, confirmed in
   re/14 C2b) → our thunk.
4. The thunk either opens our plugin-manager UI directly, OR shows a dropdown we build ourselves:
   `root = FLmenu_NewItemObj(&PTR_FUN_00706308,1,0)`; add items with `FLmenu_CreateItem_CaptionClick(root,…)`;
   display with `FLmenu_ShowPopup(actionList, x, y, btn, root)` (same call `AddStuffBtnBeforePopup@0xcb5af0`
   uses for the "+" add menu).
Teardown: restore `btn+0x1e4`, detach (SetParent 0) + destroy the button, before unmap. No FL menu-tree state
to restore → cleanest eject. Trade-off: it's a button, not literally an entry inside the `NewMainMenu` strip.

(For reference, FL's own toolbar buttons that mirror this pattern: `TToolbarForm.AddStuffBtnBeforePopup
@0xcb5af0`, `ArrangeBtnBeforePopup@0xcb5b50`, `PatMenuBtnBeforePopup@0xcbb280` — each builds a popup via
`FLmenu_ShowPopup`.)

---

## 5. Getting the runtime `TToolbarForm` / bar pointer
The only piece not reachable from an already-known global (`mainForm`/`actionList` come from
`PTR_DAT_014a8750`). Options, pick one at impl:
- **One-shot capture hook (recommended; mirrors re/14 C2c):** briefly inline-hook `FLmenu_BarMouseDown@0x7059a0`
  (its `param_1` IS the bar = `toolbarForm+0x878`) or `TToolbarForm.NewMainMenuPopup@0xcb9ee0` (`param_1` = the
  toolbar form); on first fire, store the pointer and self-remove the hook. Cheap, robust, no global needed.
- **Form manager enumeration:** the toolbar form is an FL singleton wpform (`forms.toolbarform:wpform`);
  enumerate the form manager (`*0x14AA6E8`, re/13/14) and match the form whose `+0x878` bar has
  `bar+0x514 == actionList`. (Static lead; needs a live confirm of the manager's form list layout.)
- The bar is a WP control with `+0x2b0 == 0` (not a form HWND); it lives inside `TToolbarForm` whose HWND is at
  `toolbarForm+0x2b0` (re/13) — only needed for a Win32-child variant.

---

## 6. Risks / confidence
- **Confidence HIGH** on: menu type (custom, not VCL TMainMenu), the bar control location (`toolbarForm+0x878`),
  the build/layout funcs, the bar-entry record (0x18 stride), the item struct (caption +0x78, show flag +0x86,
  child list +0xb0, onClick +0x100/+0x108, auto id +0xa0), and the item-creation API. All decompiled and
  cross-checked against FL's own dynamic-menu code (`FUN_0108fd30`, `AddInstrumentMenuPopup`,
  `AddStuffBtnBeforePopup`). No VMProtect/obfuscation on any of these functions (clean Delphi).
- **Needs a live confirm (impl step 1, low risk):** (a) the runtime toolbar-form/bar pointer (use §5 capture);
  (b) that re-calling `FLmenu_BuildBarFromActionList` + `FLmenu_LayoutBar` cleanly re-renders with our extra
  entry (it's FL's own path, so expected fine); (c) the item destructor for the fullest eject (else use the
  `+0x86 = 0` hide-and-leave fallback in §3 teardown).
- **Eject safety is the main hazard** (shared with all prior bridge work): clear our onClick TMethod and remove
  our bar entry/menu item BEFORE the DLL unmaps, all on the main thread, or FL AVs on the next menu interaction.
  The `TQuickBtn` fallback (§4) has strictly simpler teardown (we own the control) and is the safer first ship.
- **Rebuild frequency:** FL builds the bar only at `TToolbarForm.FormCreate`; if FL recreates the toolbar form
  (UI scale change, "reset toolbars" — `TShortcutsModule.EditToolbarsActionExecute@0xe477d0` exists), our entry
  is lost and must be re-inserted. Re-assert on toolbar (re)create if we want persistence.

## Ghidra annotations made (static only; no runtime changes)
Renamed for future work: `FUN_007046b0→FLmenu_BuildBarFromActionList`, `FUN_007049f0→FLmenu_LayoutBar`,
`FUN_00705880→FLmenu_ShowTopLevelDropdown`, `FUN_007059a0→FLmenu_BarMouseDown`, `FUN_0070ab80→FLmenu_ShowPopup`,
`FUN_0070e1a0→FLmenu_CreateItem_CaptionClick`, `FUN_0070e040→FLmenu_NewItemObj`,
`FUN_0081a100→FLmenu_ItemBaseInit`, `FUN_0081daf0→FLmenu_SetItemCaption`, `FUN_0081e090→FLmenu_InsertChild`.
Plate comments added on `0x7046b0` and `0x70e1a0` summarizing the insertion plan.

## Key addresses (quick reference, ghidra / image base 0x400000)
| what | addr |
|------|------|
| main form global (ptr-to-ptr) | `PTR_DAT_014a8750` (==`DAT_01581200`) |
| action list / popup engine | `*(mainForm+0x760)`, class `&PTR_FUN_00707240` |
| master menu root | `*(actionList+0x7c)` |
| menu-bar control | `*(TToolbarForm+0x878)` |
| menu-item class VMT | `&PTR_FUN_00706308` |
| popup window class VMT | `&PTR_FUN_006b9b88` |
| build bar | `0x7046b0` |
| layout bar | `0x7049f0` |
| bar MouseDown | `0x7059a0` |
| show top dropdown | `0x705880` |
| show custom popup | `0x70ab80` |
| new item obj | `0x70e040` |
| create item (caption+click) | `0x70e1a0` |
| set item caption | `0x81daf0` |
| insert child | `0x81e090` |
| child count / get | `0x81dda0` / `0x81ddc0` |
| make UnicodeString | `0x54c5e0` |
| button create (fallback) | `0xF0DDB0` |
| TToolbarForm.FormCreate | `0xcb7f90` |
| TToolbarForm.NewMainMenuPopup | `0xcb9ee0` |
