# In-FL chat panel from FL's native UI (task #25, no web) — MVP plan

Goal: a chat panel INSIDE FL (text input + vertically-stacked responses) that appears when our bridge
DLL is injected and disappears when ejected. Constraint from user: **recycle FL's existing native UI —
NO WebView2 / HTML / web**. Builds on re/13 (WP framework). Module = FLEngine_x64.dll, image base
0x400000, runtime base HIGH (~0x67f90000); rebase runtime = moduleBase + (ghidra - 0x400000).

## What FL gives us (RE'd)
- **WP descriptor format:** `controls.<type>;forms.<form>.<path>:<skin>`. The form itself is
  `controls.forms;forms.<name>:wpform` (e.g. `forms.channelrack:wpform`, `forms.nameeditform:wpform`,
  `forms.paletteeditorform:wpform`). Set on the form at `form+0x6e8` (Delphi UStr).
- **Form HWND = inst+0x2b0** (re/13) → standard Win32 `CreateWindowEx`/`ShowWindow`/`DestroyWindow`/
  `SetParent` work on it. This is the key: once we have a host form, we can drop **standard Win32 child
  controls** onto its HWND and skip the hard WP-text RE for v0.
- **Text input — FL-native option:** `TQuickEdit` (WP editable-text control; class string `tquickedit`
  @0x761e70; also `tquickedittoolbar*`). Live pattern from `TNameEditForm` (FL's rename popup,
  `forms.nameeditform:wpform`):
  - Edit control instance lives at `form+0x790`. Set its text via `FUN_0074c260(editCtrl, ustr)@0x74c260`;
    refresh via `FUN_0074bb70@0x74bb70`.
  - **Enter detection:** `TNameEditForm.NameEditFormKeyDown@0x797e30` does `if (key==0x0D) { form+0x4E8 = 1; }`
    — i.e. pressing Return sets a commit flag we can poll (`form+0x4E8`). Last key cached at `form+0x820`.
  - **UNKNOWN (needs live RE):** the offset where TQuickEdit stores the typed text (a Delphi UStr) for
    READ-back. Find by: call `FUN_0074c260` to set known text, then scan the control object for that UStr
    ptr; or find the matching getter. (Not needed for the v0 Win32-control path below.)
- **Multi-line display — options:** standard `RICHEDIT20W` / `TRichEdit` (`TCustomRichEdit`,
  `RICHEDIT20W`@0x6347e6) and `TCustomEdit`/`TEdit`/`EDIT` are all linked in. A read-only multiline EDIT
  or a RICHEDIT child is the simplest scrolling, word-wrapping vertical text area. FL-native alternative:
  a WP list/tree (`DataBrowser.Controls.Tree`) — heavier, defer.
- **Browser tabs (for the "real tab" end-goal):** FL's data browser uses
  `DataBrowser.Controls.TabSelector` (strings @0x994fe5/0x995328/0x995451) + `DataBrowser.Controls.Tree`
  + `DataBrowser.Controls.TagsGrid` + `DataBrowser.Controls.Preview`. A genuine browser tab = register a
  new TabSelector entry whose panel is our content. NOT yet fully RE'd (tab registration/selection path) —
  this is the v2 target, not the MVP.
- **Form factory (re/13):** `FLui_CreateFormFromClassRef(classRef,&slot)@0x10C2AA0` →
  `FLui_CreateFormCore(formMgr=*0x14AA6E8, classRef, &slot)@0x841EF0`. **NO blank-form classRef** — must
  repurpose an existing form class. Candidates: picker (`&PTR_FUN_00dae368`@0xDAE2A0), nameeditform,
  paletteeditorform. Live-confirm the metaclass-create offset by calling the factory on a known classRef
  and checking `slot+0x2b0` is a valid HWND.

## Recommended MVP — staged

### v0 (ship first): FL `wpform` host + Win32 child controls
Lowest-risk path that is native (not web) and reuses FL's own window/skin chrome:
1. **Host form (main thread):** create a floating FL form by repurposing a simple existing classRef via
   `FLui_CreateFormFromClassRef`; grab its HWND at `inst+0x2b0`. (If form repurposing proves fragile in
   live testing, fall back to a bridge-owned top-level Win32 tool window owned by FL's main HWND — still
   native, still not web, fully under our WndProc; lose only the FL skin.)
2. **Output area:** `CreateWindowExW("RICHEDIT20W"/"EDIT", ES_MULTILINE|ES_READONLY|WS_VSCROLL|WS_VISIBLE|
   WS_CHILD, ...)` as a child of the host HWND, docked to fill the top. Append a line via `EM_SETSEL`(end,
   end)+`EM_REPLACESEL`, then `EM_SCROLLCARET` (auto-scroll). Vertical stacking = free (it's a text area).
3. **Input box:** a single-line `EDIT` child docked to the bottom. Detect Enter by subclassing it
   (`SetWindowSubclass`) and watching `WM_KEYDOWN`/`WM_CHAR` VK_RETURN, or use the WP form's existing
   `form+0x4E8` Enter flag if hosting via nameeditform. On Enter: `WM_GETTEXT` the input, clear it, hand
   the text to comms (below).
4. All control creation/destruction on FL's **main thread** (use the bridge's existing
   `SendMessage(WM_BRIDGE_CALL)` marshaling).

### v1: FL-native widgets (skin match)
Swap the Win32 input for `TQuickEdit` and the Win32 output for a WP list/tree, using the
create→skin→descriptor→SetBounds→parent→show sequence from re/13. Requires resolving the TQuickEdit
text-read offset and the WP list append/clear API. Pure cosmetics over v0 — defer until v0 works.

### v2: real FL browser tab
RE `DataBrowser.Controls.TabSelector` registration → add a "FruityLink" tab whose body is our v1 content,
shown/hidden with injection. This is the user's end-goal ("our own browser tab").

## Lifecycle (Q4)
- **On inject:** after the bridge attaches, marshal a "create chat panel" onto FL's main thread (the panel
  must be built on the UI thread). Show it.
- **On eject:** BEFORE `FreeLibrary`, on the main thread: `RemoveWindowSubclass` + `DestroyWindow` the
  controls/host (and free the form via the form mgr if we created one). **Critical:** if any window proc or
  subclass proc lives in the bridge DLL and the DLL unmaps while the window still exists, FL AVs on the next
  message → must tear down fully before unload. Cleanest: own all chat WndProc/subclass code in the bridge
  and destroy on `DLL_PROCESS_DETACH`'s main-thread teardown step.

## Comms: in-FL UI → C# app LLM → back (Q5)
Reuse the existing pipe (app = client, bridge = server). Add to the bridge:
- `g_chatIn` (string, set when user presses Enter) and `g_chatOut` (FIFO of response lines).
- New pipe commands: **`chat_poll`** → returns & clears any pending user input (empty if none);
  **`chat_say <utf8>`** → bridge appends the text to the output control (on main thread).
App side: a background loop polls `chat_poll` (~400 ms); on non-empty input, run `FlAgent` to get the
reply, then `chat_say` it (optionally stream partials as multiple `chat_say` calls). No new pipe, no
reverse-connection plumbing. (Later upgrade: a dedicated event/length-prefixed channel instead of polling.)

## Crash-risk honesty
- v0 Win32-controls-on-a-host-HWND is the safe path (standard USER32, our own WndProc). Main residual risk
  is the **eject teardown ordering** (destroy windows before unmap) and doing all UI ops on the **main
  thread** (the bridge already marshals there).
- Repurposing an FL form via the factory is the riskiest v0 step (metaclass create offset, form state) —
  live-confirm; the bridge-owned Win32 window is the fallback if it's flaky.
- v1 (driving TQuickEdit + WP list) carries the re/13 WP risks (RWX event thunks, value-field offsets) and
  one unknown (TQuickEdit text-read offset) — defer past a working v0.

## Open items needing live RE before/while building
1. A cheap repurposable form classRef whose `slot+0x2b0` HWND is a usable empty client area (or commit to
   the bridge-owned Win32 window).
2. TQuickEdit typed-text read offset (only for v1 FL-native input).
3. DataBrowser TabSelector add/select API (only for v2 real-tab).

## v0 proven recipe (live) — RE pass 2 (FL pid live, base 0x67f90000)

Verified by Ghidra decompile (static, high confidence) + one non-destructive live peek. I did NOT create/show
forms live (the user was actively in FL — creating/showing a form is inherently the first *implementation*
step, where FL flicker/restart is expected; doing it mid-session risked their unsaved work). Everything below
is static-proven unless marked "needs live".

### MAKE-OR-BREAK SOLVED: TQuickEdit typed-text read-back = `editCtrl + 0x624`
`FUN_0074c260(editCtrl, ustr)@0x74c260` (set-text) does `Delphi_UStrAsg(editCtrl+0x624, ustr)` — so **+0x624
is THE text model** (a Delphi UnicodeString ptr). Confirmed by `FUN_0074bb70@0x74bb70` (refresh) reading
`*(editCtrl+0x624)-4` as the text length for caret layout.
- READ typed text: peek the UStr ptr @ `editCtrl+0x624`; if non-null, length = `*(ptr-4)` (u32), chars =
  UTF-16 at `ptr` (Delphi UStr header at ptr-12: codepage/elemsize/refcnt/len). Empty if ptr==0.
- SET text: `FUN_0074c260(editCtrl, ustrPtr)` (ustr = ptr-to-chars, header at ptr-12; build like other Delphi
  consts). Other fields: caret/len @+0x62c, sel start/end @+0x630/+0x634, flags(ushort) @+0x682 (bit0 used).
- This is the input read-back AND the basis for a TQuickEdit-based output area.

### Host form factory (static-confirmed)
`FLui_CreateFormFromClassRef(classRef,&slot)@0x10C2AA0` → `FLui_CreateFormCore(formMgr,classRef,&slot)@0x841EF0`:
- `inst = (*(code**)(classRef - 0x30))(classRef)` — metaclass create (offset -0x30 CONFIRMED in decompile).
- `*slot = inst`.
- `(*(code**)(*inst + 0x78))(inst, 0xFF, formMgr)` — init/setup with the form manager.
- **`inst+0x2b0` = the Win32 HWND** — CONFIRMED: CreateFormCore passes `param_1+0x2b0` to
  `GetWindowLongPtrW`/`SetWindowLongPtrW(-0x14,...)` and ORs `WS_EX_APPWINDOW (0x8000000)`, then calls
  `FLwp_FormSetAppWindowVisible@0x82FBB0(inst+0x2b0, vis, vis)` to show. So standard ShowWindow/DestroyWindow
  apply, and FLwp_FormSetAppWindowVisible is the FL-native show.
- `formMgr = *0x14AA6E8`. LIVE: =0x6904dc90 (inside FLEngine data); `formMgr+0x2b0` held 0x33b85b0 (an HWND).
- Plate comment's "hosting HWNDs is fragile" warning is about EXTERNAL/cross-process apps — we're IN-PROCESS
  (injected), so it does NOT apply to us.
- **NO blank-form classRef** → must repurpose an existing form class (candidates below).

### wpform controls are descriptor-built (key insight for "recycle")
A wpform's child controls are auto-instantiated by the framework from the form's descriptor
(`form+0x6e8` = Delphi UStr like `"forms.nameeditform:wpform"`). `TNameEditForm.FormCreate@0x798090` only
*configures* already-existing controls (edit@+0x790, buttons@+0x778/+0x7a8/+0x7b8, list@+0x760). ⇒ The
cheapest "recycle FL UI" path is to **repurpose a form that already contains an input + a scrolling list**,
rather than hand-building controls. Hand-building is possible via re/13's create→skin→descriptor→bounds→
parent→show sequence + the `tquickedit` VMT (class string @0x761e70; methods clustered @0x746000-0x74c000).

### TNameEditForm layout + Enter (static-confirmed)
- Edit (TQuickEdit) @ `form+0x790`. Enter: `NameEditFormKeyDown@0x797e30` → `if(key==0x0D){ form+0x4e8=1;
  *key=0; }` so **form+0x4e8 is a pollable commit flag**; last key cached @ form+0x820. (KeyPress@0x798040.)
- So: poll form+0x4e8==1 → read editCtrl(+0x624) text → reset form+0x4e8=0 → send to LLM.

### Display widget (native, vertical, scrolling) — recommendation
No dedicated WP "memo/log" widget confirmed. Best NATIVE options, ranked:
1. **Recycle a scrolling LIST** (e.g. the picker's results list / `DataBrowser.Controls.Tree`) — append chat
   lines as rows. Most native + scrolls free. Needs: the list append/clear API (NEEDS LIVE RE).
2. **Read-only multi-line TQuickEdit** — same widget family as input (guaranteed seamless), append via
   read+concat+`FUN_0074c260`. Needs: confirm TQuickEdit supports multi-line (the +0x682 flag) (NEEDS LIVE RE).

### Recommended v0 (most "recycle", most seamless)
**Repurpose the add-channel/plugin PICKER form** — it natively has BOTH a search text input AND a scrolling
results list = exactly chat shape. classRef `&PTR_FUN_00dae368@0xDAE2A0`; opener `FLcr_OpenAddChannelPicker
@0xDAE920`. Plan: open/repurpose it as the chat panel → its search edit = chat input (poll commit flag, read
+0x624) → replace its list contents with chat lines. Fallback if the picker is too wired to plugins: create
our OWN form (repurpose a simple classRef) + two TQuickEdits (single-line input + multi-line read-only output)
via re/13's control-create sequence.

### Lifecycle
Create on inject (MAIN THREAD via the bridge's existing SendMessage marshaling). On eject, BEFORE FreeLibrary
and on the main thread: hide (FLwp_FormSetAppWindowVisible ...,0,0) + free the form via the form mgr (the
form-FREE/close fn is NOT yet RE'd — `TNameEditForm.FormDestroy@0x798380` is only the OnDestroy *event*, not
the freer; find the real destructor for clean teardown — NEEDS LIVE RE).

### Still needs live (do as step 1 of implementation, FL-restart tolerated)
1. Create a form via the factory and confirm `slot+0x2b0` is a live HWND (IsWindow) + it shows.
2. Picker list append/clear API (for display option 1) OR TQuickEdit multi-line support (option 2).
3. The form free/close fn for eject teardown.
4. Control-create sequence live-confirm if going the own-form route (re/13).
Biggest remaining risk: clean eject teardown ordering (destroy windows/free form before DLL unmap, all on the
main thread) — a dangling WndProc after unmap AVs FL.

## Gopher (native LLM browser tab) — recycle path (RE pass 3, coordinator pivot)

FL 2025 ships "Gopher", its own LLM helper, whose UI is FL's **detachable data-browser window with a tab
system** — Gopher is just one tab in it. This hands us the browser-tab-registration mechanism that was the
v2 unknown. Static-RE'd; I did NOT open it live (user active — see "needs live" below).

### Open / show path (static-confirmed)
- `TShortcutsModule.HelpShowGopherActionExecute@0xe45150` → `FUN_00fc5e80(5)`.
- `FUN_00fc5e80(source)@0xfc5e80`:
  - Singleton browser form in global `DAT_015800d0` (@0x15800d0). If null:
    `FLui_CreateFormFromClassRef(&PTR_FUN_00fc57b8 /*@0xfc57b8*/, &DAT_015800d0)` → skin
    `(*(*form+0x138))(form, *(*0x14A8750+0xb18))` → setup `FUN_00fc6420(form)`.
  - `tabId = FUN_00fc5e00(source)` (map: 0→current, 1→0, 2→1, 3→2, 4→3, **5→4 = Gopher**; the browser has
    ≥5 tabs, Gopher is tab id 4).
  - `if (current != tabId) FLbrz_SelectTabById(browser, tabId, 0)` then show `FUN_007ea5d0(form)` +
    `FUN_00fc61e0(form, 1)`.
  - **browser object = `*(DAT_015800d0 + 0x778)`** (`[0xef]`); **tab container = `browser + 0x158`**.
- Hide/close: `FUN_00fc61e0(DAT_015800d0, 0)` (sets +0x770=0, hides).
- OPEN LIVE: `callabs FUN_00fc5e80(5)` on the main thread (FL's own sanctioned action — low crash risk).

### Browser TAB API (the registration mechanism — native, reusable)
- `FLbrz_CreateConfiguredTab@0x9acc90(browser, titleUStr, id, p4, kind, p6, p7, viewId/p8, p9..p13, flag)` →
  validates via `FUN_009c1360(browser+0x2f4, viewId)` then `FLbrz_TabContainerCreateTab(browser+0x158, ...)`.
  Title is a Delphi UStr (ref-counted).
- `FLbrz_TabContainerCreateTab@0x993fd0` — low-level: appends a tab slot to the container array
  (`container+0x10`, count @arr-8), stores kind @slot+0x10, calls `FUN_0098aff0(slot, title, id-0xf400, p4)`,
  names config `Tab_<n>` @slot+0xec, builds the view (`FUN_0098d0c0` if kind==3 else `FUN_0098d260`).
  **The kind (param_5) selects the view class** — this is the tab→view binding.
- `FLbrz_AddTabCloneOfSource@0x9ac910(browser, srcTabId)` — copies a source slot's view-config
  (+0x14/+0x1c/+0x30/+0x34/+0x50/+0x44/+0x134/+0x6c/+0x64) into a new `CreateConfiguredTab` with a new id
  (src+0xf400). **Simplest way to spawn another tab OF THE SAME TYPE.**
- Also: `FLbrz_SelectTabById@0x9ac590`, `FLbrz_GetCurrentTab@0x994770`, `FLbrz_GetRawTabCount@0x994790`,
  `FLbrz_GetVisibleTabCount@0x994b00`, `FLbrz_FindTabSlotById@0x994870`, `FLbrz_BrowseToPathInTab@0xf8c2d0`.

### Native vs web — IMPORTANT
- Gopher VIEW = `TBrowserGopherView`/`DataBrowser.View.Gopher`/`.view.gopher` (@0xc8e862/0xc8e8a7/0xc8eccc),
  endpoint **`https://gopher-fls.image-line.com`** (@0x9e4b4c, in the web/browser code region). ⇒ The Gopher
  view CONTENT is **strongly likely web/remote-backed** (renders cloud HTML), NOT native widgets. So **cloning
  the Gopher view would give a WEB view → violates the user's "no web" rule.** (CONFIRM live: inspect the view
  for a CEF/Edge child HWND.)
- A NATIVE ask-input widget DOES exist: `TAskGopherModule`/`forms.welcomewizard.askgophermodule` uses
  `:tquickbtn` (native button) + `:tcustomvectorpanel` (native custom-drawn panel) — reusable native pattern.
  (`TWelcomeWizard.AskGopherClick@0x104ef50` → `FUN_0104e700` → `FUN_0083acf0` = ask/submit; real work in
  0x83acf0, not chased.)

### RECOMMENDED recycle path: native tab + native content (hybrid)
**Register OUR OWN sibling tab** ("FruityLink AI") in the Gopher browser via `FLbrz_CreateConfiguredTab` on
`*(DAT_015800d0+0x778)` → a genuine, seamless FL browser tab (the user's exact ask). For the tab CONTENT, do
**NOT** clone the Gopher web view; host OUR native widgets (the TQuickEdit input + a native display from the v0
recipe above) driven by our glm LLM over the existing pipe. Net: native FL browser tab + native chat + our
model, no web.

### Biggest unknown
The **tab→content binding**: how a created tab instantiates its view (the `kind` + view ptrs →
`FUN_0098d0c0`/`FUN_0098d260`). Hosting OUR content likely needs either (a) registering a custom view
"kind"/class (heavier — akin to the no-blank-form problem), or (b) creating a tab of a generic/panel kind and
dropping our widgets on its content HWND after creation. NEEDS LIVE RE.

### Needs live (impl step 1) + biggest risk
- Live: `callabs FUN_00fc5e80(5)` (main thread) → confirm Gopher view is web (child CEF/Edge HWND), dump the
  tab-container slot array (browser+0x158 → +0x10), find a generic/panel tab kind we can host native widgets
  in; then `FUN_00fc61e0(DAT_015800d0,0)` to hide. (User active → do when tolerated.)
- Biggest risk: (1) tab→custom-view binding may require authoring a view class (heavy); (2) eject teardown —
  remove our tab + destroy widgets on the main thread BEFORE DLL unmap or FL AVs on the dangling view; (3) the
  Gopher browser is an FL-owned singleton — add/remove our tab reversibly so FL's own browser state isn't
  corrupted.

## v0 IMPLEMENTATION RECIPE (live-proven) — RE pass 4 (LIVE on FL pid 34948, base 0x67f90000)

Ran live via flprobe (callabs = FL main thread), user-approved, FL left responsive (opened browser, walked it,
hid it, ping=pong). Rebase: runtime = flEngineBase + ghidra - 0x400000 (flEngineBase from `flprobe bridge info`).

### LIVE-PROVEN
- **Form factory works live.** `callabs FUN_00fc5e80(5)` created the Gopher/help browser via
  `FLui_CreateFormFromClassRef(&PTR_FUN_00fc57b8, &DAT_015800d0)` → `DAT_015800d0` went 0 → **0x67fcb70** (a
  real form with a window). `callabs FUN_00fc61e0(form, 0)` hid it cleanly (form+0x770 → 0). So the
  factory + show/hide lifecycle (the v0 keystone) is confirmed on the live process.
- **Browser/tab structure (live ptrs):** form `*0x15800d0`; browser `*(form+0x778)` = 0x9045df0; tab container
  `*(browser+0x158)` = 0x172875460; slot array `*(container+0x10)` = 0x1727fb770; count `*(arr-8)` = **5 tabs**
  (matches Gopher = source 5 → tab id 4). Per-slot: kind @slot+0x10, viewIdx @slot+0xe0, file-tree view
  @slot+0x90+viewIdx*8.

### KEY NEGATIVE RESULT (course-correction)
All 5 tabs in the Gopher/help browser are **kind=3** (the lightweight `FUN_0098d0c0` path; their file-tree
view slot @+0x90 is NULL). kind=3 = a web/special view, NOT a native widget container. Combined with the
static finding that **every tab's content is a registered VIEW CLASS** (file-tree `&PTR_FUN_0056f298` for
kind!=3, web for the help browser) and there is **no "blank panel" tab kind**:
⇒ **Putting our OWN native widgets inside a browser tab requires authoring/registering a custom view class**
(the same class of problem as the no-blank-form factory). That is HEAVY and is NOT de-risked. The literal
"our own browser tab with native chat" is a v2 effort, not v0.

### RECOMMENDED v0 (de-risked, native, build this now): standalone FL form + native widgets
Don't fight the browser-tab view system for v0. Use the proven form factory to make our OWN FL form (native
window + FL skin) and host native TQuickEdit widgets on it:
1. **Create host form (main thread):** `FLui_CreateFormFromClassRef(classRef, &slot)` with a repurposable
   classRef → HWND @ `*slot+0x2b0`; show via `FLwp_FormSetAppWindowVisible@0x82FBB0` (or the form's vtbl
   show). Set descriptor `form+0x6e8` for skin. (Factory live-proven above; pick a simple classRef + live-
   confirm — the picker `&PTR_FUN_00dae368@0xDAE2A0` or a minimal form.)
2. **Input:** a single-line TQuickEdit (class `tquickedit`, methods @0x746000-0x74c000) via re/13's control-
   create sequence. Read typed text by peeking the Delphi UStr @ `editCtrl+0x624` (PROVEN); set via
   `FUN_0074c260@0x74c260`. Detect commit via the form Enter flag (`form+0x4e8`-style) or the keydown TMethod.
3. **Output (vertical, scrolling):** a read-only multi-line TQuickEdit (append = read+0x624, concat, set) —
   confirm multi-line support live; else a WP list. Both native/seamless.
4. **Comms:** existing pipe — bridge buffers input, app polls `chat_poll` → glm LLM → `chat_say` back.
5. **Teardown (eject):** main thread, before FreeLibrary: hide + free the form. The form-FREE fn is still not
   RE'd (FormDestroy@0x798380 is only the OnDestroy event) — find the destructor OR just hide+DestroyWindow
   the HWND@+0x2b0 and null our refs.

### Is v0 safe to implement in the bridge? YES — via the standalone-form path.
Proven: factory create + show/hide (live), HWND@+0x2b0 (static+live), TQuickEdit text@+0x624 (static), Enter
flag (static). Remaining live-confirms during impl (low risk): the control-create sequence for a standalone
TQuickEdit (re/13), TQuickEdit multi-line for the output, and the form-free fn for clean eject.
NOT v0: the literal browser tab (needs a custom native view class — defer to v2; revisit if we choose to
author a DataBrowser view class).

### Still needs visual confirmation by the user
The standalone form + widgets actually rendering correctly + looking native (I can verify structure/HWNDs but
not pixels). Do at first bridge build.

## REAL TAB (native view) — live recipe + verdict (RE pass 5, MAIN browser, live)

User chose the REAL native browser tab over a standalone window. Investigated the MAIN data browser live.

### The MAIN browser (live-confirmed, read-only)
- **MAIN browser object = `*DAT_0157ffb8`** (the focused `TVirtualDataBrowser`; live = 0x33951c0). This is the
  left-panel browser — DISTINCT from the help/Gopher browser (`*0x15800d0`). browser vtbl = ghidra 0x99b858.
- tabsObj = `*(browser+0x158)`; tab dynarray = `*(tabsObj+0x10)` (count @arr-8); current idx @ `tabsObj+0x18`.
  Live: **7 tabs**, kinds = {0,1,2,4,5,6,8}, content types {0,1,4,7} — i.e. each built-in tab is a DIFFERENT
  renderer (Plugin/Packs/Projects/Samples/…), not a uniform list.
- Tab fields (from FLbrz_SelectTabById plate): +0x10 kind, +0x1c base id (new=+0xf400), +0x20 path/list obj,
  +0x30 content type, +0x34 sel node, +0x44 data-folders dynarray, +0x64 root path (UStr), +0x6c name (UStr),
  +0x74 filter (UStr), +0x8c visible(0=shown), +0x10c subtype, +0xf4 tab id.

### Tab API (known) + how content is rendered
- Create: `FLbrz_CreateConfiguredTab@0x9acc90(browser, captionUStr, p3,p4, type=2, p6,p7, contentId,
  pathListObj, pathLen-1, p11, name@+0x6c, root@+0x64, 1)` → `FLbrz_TabContainerCreateTab@0x993fd0`.
  Clone: `FLbrz_AddTabCloneOfSource@0x9ac910(browser, srcId)`. Select: `FLbrz_SelectTabById@0x9ac590`.
- **CRITICAL: a tab is a CONFIG, NOT a content host.** Selecting a tab calls the browser's content-switch
  `vtbl[0xd0]=FUN_009abbc0`, which REPOPULATES a single SHARED tree/content control (`browser[0x32]`, +0xee0
  scroller) from the tab's data-folders/type — and web views (News/Help/Gopher) use a SEPARATE control
  (`browser[0x34c]`). Switching is by the tab's kind/subtype (+0x10/+0x10c) + content type (+0x30).

### VERDICT — the real native tab is HEAVY and NOT yet safe to implement
There is **no blank/hostable tab kind** and **no per-tab content area**. Built-in kinds render either the
shared file/plugin/sample TREE or a web control — none hosts arbitrary native widgets. To put OUR native chat
in a real browser tab we must introduce a **NEW content control for a new kind** into the browser's
content-switch path. That means either:
- (A) **Hook FL's browser vtbl** (subclass `*browser` = 0x99b858): intercept `vtbl[0xd0]`(content switch),
  the tab-select/show path, paint/resize, so that when OUR tab's kind is active we show + drive our own
  child control instead of the tree. Invasive (we own a patched vtbl for an FL singleton; must restore on
  eject) and must keep FL's other tabs working.
- (B) **Author a custom DataBrowser view class** registered as a new kind/content-type — a Delphi class whose
  VMT implements the view contract FL calls (create/init, populate-for-tab, paint, resize, activate, destroy,
  get content HWND). Same difficulty class as the no-blank-form problem; needs the view base VMT fully RE'd.

### SINGLE BLOCKING UNKNOWN
The exact extension point to inject a NEW content control/renderer for a new kind: i.e. the view/content-
control base contract the browser drives (the slots called from `FUN_009abbc0`/`FUN_009b8e10`/`FLbrz_SelectTabById`
on `browser[0x32]` and the web control `browser[0x34c]`) AND how kind/content-type selects which control. Until
that's mapped, neither (A) nor (B) is de-risked. Estimated: a multi-pass RE + a careful in-process vtbl-hook
or Delphi-view-class build — substantially more than the standalone-form v0.

### RECOMMENDATION
Two honest options for the lead/user:
1. **Pragmatic:** ship the **standalone native FL form** chat first (re/14 v0 — proven, native look/skin, fully
   works, low risk) so the feature is usable now; pursue the real in-browser tab as a separate dedicated effort.
2. **Committed:** treat the real tab as its own project — next RE step = map the browser content-control
   contract (decompile `FUN_009b8e10` + the web-control path on `browser[0x34c]` + how kind→control is chosen),
   then prototype a vtbl-hook (route A) live. Higher risk to FL stability; needs the user's tolerance for
   browser-state experiments + restarts.
Did NOT create tabs in the user's main browser live (it would mutate their real browser tab set without proving
hostable content, which doesn't exist). All findings above are read-only/static + the earlier open/hide of the
help browser. No Ghidra edits — nothing to save.

## REAL TAB — vtbl-hook implementation spec (RE pass 6, decompile + light live, MAIN browser)

Verdict update: **this IS buildable as a real native browser tab via a single vtbl-slot hook.** The pass-5
blocker ("no per-tab content host; browser[0x34c] is a web control") was based on a misread. Corrected below.
All offsets are CONFIRMED by decompile + live peek unless marked (LIVE-TODO). Live session: FL pid 34948,
flEngineBase `0x67F90000`. **Rebase: runtime = ghidra + 0x67B90000** (= ghidra − 0x400000 + flEngineBase).
Re-read `flprobe bridge info` each run; the base can change on restart — store ghidra addrs + rebase at runtime.

### 0. KEY CORRECTION to prior passes
- `browser+0x190` (= `browser[0x32]`, index*8) = the **shared TREE content control** (class vtbl ghidra
  `0x9897a8`). `browser+0x198` = a 2nd tree; `+0x1a0` = scrollbar; `+0x180`/`+0x238` = toolbar panels.
- **`browser+0x34c` is NOT a web control — it is the CONTENT-AREA PARENT PANEL** that hosts the tree +
  toolbars. Proof: `FLbrz_BuildBrowserControls@0x9a5790` creates every content child with
  `FUN_0098a130(treeClass,1,0, *(browser+0x34c))` / `FLwp_CreateControl(...)` then **parents via
  `vtbl[0x138](child, *(browser+0x34c))`** and sizes via `vtbl[0x188](child, x,y,w,h)`. Live: `tree+0x78`
  (parent ptr) == `*(browser+0x34c)`. ⇒ The main browser has **no web view**; "News" renders as tree nodes.
  Gopher/Help is a *separate* browser instance (re/14 pass-3, `*0x15800d0`).
- ⇒ Hosting our chat = **parent our WP controls into `*(browser+0x34c)`**, exactly like the tree. The pass-5
  "needs a custom view class" conclusion is WRONG for this approach — we bypass the view system entirely by
  hooking the content-switch and drawing our own children on the existing content panel.

### 1. FUN_009abbc0 = `FLbrz_ContentSwitch` — the hook point (browser vtbl[0xd0])
Signature (confirmed): `void __fastcall FLbrz_ContentSwitch(longlong* browser /*RCX*/, int contentId
/*EDX*/, char p3 /*R8B*/, char p4 /*R9B*/)`. Returns void. MS x64 fastcall.
- Called by `FLbrz_SelectTabById@0x9ac590` as `vtbl[0xd0](browser, curTab+0x30, 0, 0)` **after** it writes
  the new index to `tabsObj+0x18` — so when the hook runs, the current tab IS already the just-selected tab.
- Behavior: guard `FUN_009abb80`; `idx=FUN_009b6630(browser)=*(int*)(*(browser+0x158)+0x18)`; bail if -1.
  `cur=FLbrz_GetCurrentTab(browser+0x2b)`; **acts only if `*(int*)(cur+0x30)==contentId`**. Then it
  REPOPULATES the shared tree (`*(browser+0x190)`) by calling
  **`FLbrz_PopulateTreeFromTab@0x9b8e10(browser, *(browser+0x190), cur+0x34 /*selNode*/, cur+0x44
  /*dataFolders dynarray*/, cur+0x10 /*kind*/, p3)`** — THIS is the call that would touch our tab's
  (empty/foreign) data and is what we must suppress. Tail work: scroller save/restore on `tree+0xee0`,
  `FUN_009ba740`, optional `vtbl[0x110]`, and status text via `vtbl[0x140](browser,"Browser refreshed")`.
- vtbl index math: `vtbl[0xd0]` = byte offset 0xd0 = index 0x1a. Class vtbl ghidra `0x99b858`; slot ghidra
  `0x99b858+0xd0 = 0x99b928`. **LIVE-CONFIRMED**: runtime vtbl = `0x6852b858` (=`*browser`), slot
  `0x6852b928` holds `0x6853bbc0` (= rebased FUN_009abbc0). This is the exact 8-byte slot to save+patch.

### 2. Content-region geometry (where to put our panel) — LIVE-CONFIRMED
- Content parent panel = `*(browser+0x34c)` (live `0x8e47930`). Its rect (in the form canvas space):
  x=`*(int*)(panel+0x90)`, y=`+0x94`, **w=`*(int*)(panel+0x98)`, h=`+0x9c`** (live 0,0,247,1322).
- Tree rect (the region our chat should fill, sits below the toolbar): tree=`*(browser+0x190)` (live
  `0x8e42a00`): x=`*(int*)(tree+0x90)`=13, y=`+0x94`=60, w=`+0x98`=234, h=`+0x9c`=1126.
- **WP control rect offsets for these classes: x=+0x90, y=+0x94, w=+0x98, h=+0x9c (all int).** (This SUPERSEDES
  re/13's +0x474/+0x476 w/h, which read 0 for this class — those are a different sub-field.)
- These are WP-canvas coords, NOT Win32 client coords. We host **WP controls** (same coord space), so we
  just copy the tree's rect onto our control with `vtbl[0x188]`. No HWND math. (browser, tree, panel all have
  `+0x2b0 == 0` live — none is a Win32 form; the browser is a WP control inside form `TSampleListForm`
  / "forms.browser:wpform", HWND only at that form's +0x2b0 — needed ONLY for the Win32 fallback in §7.)

### 3. Tree/toolbar hide-show (so our tab looks clean)
- WP show/hide = `vtbl[0x200](ctrl, 1|0)` (re/13). Our opaque control parented LAST to the panel draws on top,
  so hiding FL's controls is optional; for cleanliness hide the tree (`vtbl[0x200](*(browser+0x190),0)`) and,
  if covering full panel, the toolbars (`+0x180`,`+0x238`,`+0x1a8`) + scrollbar (`+0x1a0`). (LIVE-TODO: confirm
  vtbl[0x200] is Show for the tree class `0x9897a8` and that hiding/restoring doesn't desync FL's layout — the
  safe default is to NOT hide them and just cover with an opaque full-panel child.)

### 4. Creating OUR tab (won't crash) — RECOMMENDED = clone-then-rewrite
Tab fields are set by `FLbrz_TabInitFields@0x98aff0`: +0x10 kind, **+0x30 contentType (==what vtbl[0xd0]
gets)**, +0x34 baseNodeType, +0x50/+0x4c viewIndex, +0x14 color, +0x1c baseId, +0x20/+0x28 caption,
+0x6c name/filterText, +0x64 root, +0x74 filterExt, +0x44 dataFolders dynarray, +0x8c visible(0=shown),
+0xf4 tab id (= insert index), +0xf8 flag. Both view-builders are crash-free for a fresh tab:
`FUN_0098d0c0` (kind==3) only reads registry; `FUN_0098d260` (kind!=3) does NOTHING when the tab has no
`.tabconfig` file. ⇒ creation never populates; populate only happens on select (which we intercept).
PITFALL: `FLbrz_CreateConfiguredTab@0x9acc90` **validates the viewIndex (param_8) via
`FUN_009c1360(browser+0x2f4, viewIndex)` and creates NOTHING if invalid.** Avoid guessing it:
- **Clone path (preferred):** `FLbrz_AddTabCloneOfSource@0x9ac910(browser, srcTabId)` copies a valid
  viewIndex/kind from an existing simple tab (e.g. PLUGINS id `0xf116-0xf400`=…; just use a current
  `tab+0xf4`) and auto-selects the new tab. Order of operations:
  1. (hook NOT yet installed) clone → FL briefly shows the cloned content (harmless flash).
  2. New tab = last raw slot: `arr=*(tabsObj+0x10); n=*(int*)(arr-8); ourTab=arr[n-1]`. **Store `ourTab`
     pointer (stable — the array holds heap ptrs that only get reordered, not moved).** Read id `ourTab+0xf4`.
  3. Rename: `Delphi_UStrAsg(ourTab+0x20,"FruityLink AI")` (tab-bar caption) and `+0x6c` (filter name);
     clear data: set `ourTab+0x44` to an empty dynarray and `ourTab+0x64` (root) to empty/0 so the pre-select
     work in SelectTabById takes its trivial branch.
  4. Build our WP content controls (§6), hidden.
  5. Install the vtbl[0xd0] hook (§5).
  6. `FLbrz_SelectTabById(browser, idx, 0)` → hook fires, `cur==ourTab` → shows our panel.
- **Fresh path (alt):** `FLbrz_CreateConfiguredTab(browser, captionUStr, baseId, color, kind, contentType,
  baseNodeType, viewIndex, pathListPtr, count-1=-1, viewsArr=0, nameUStr, rootUStr=0, flag=1)` — only if you
  copy `viewIndex` from an existing tab's `+0x50` so the validation passes. Caption=param2→+0x20, kind→+0x10,
  contentType→+0x30 (pick a private value), name→+0x6c, root→+0x64. 14 args: 4 in regs, 10 on stack
  (param5 kind @[rsp+0x20] … param14 flag @[rsp+0x68]).

### 5. The vtbl hook + thunk contract
- **Where:** one class vtbl, shared by ALL TVirtualDataBrowser instances (main + help/Gopher). Two options:
  - (A) Patch the **class** slot `vtblRuntime+0xd0` (=`0x6852b928` this session) → thunk. Bridge does
    `VirtualProtect(slot,8,PAGE_READWRITE)`, save old qword, write thunk addr, restore protect. Thunk MUST
    guard `browser==g_browser` (so the help browser is unaffected). Reversible: write the saved qword back.
  - (B) Per-instance: alloc a copy of the full vtbl (size ≥ 0x300; copy generously within `.rdata`), patch
    the copy's [0x1a], set `*browser = copy`. Only the main browser is affected; restore `*browser=orig`.
    Cleaner (no `.rdata` write, no help-browser exposure) but must copy enough slots. **(A) is simplest and
    matches the task's "VirtualProtect+write" plan; use (A) with the browser guard.**
- **Thunk (RWX, x64, fastcall, SEH-safe, fast):**
  ```
  void __fastcall thunk(void* browser, int contentId, char p3, char p4){
    if (browser == g_browser){
      void* tabs = *(void**)((char*)browser+0x158);
      int idx   = *(int*)((char*)tabs+0x18);
      void** arr= *(void***)((char*)tabs+0x10);
      void* cur = (arr && idx>=0) ? arr[idx] : 0;
      if (cur == g_ourTab){
        void* tree=*(void**)((char*)browser+0x190);
        int x=*(int*)((char*)tree+0x90), y=*(int*)((char*)tree+0x94);
        int w=*(int*)((char*)tree+0x98), h=*(int*)((char*)tree+0x9c);
        (*(void(__fastcall**)(void*,int,int,int,int))(*(void**)g_ourCtrl+0x188))(g_ourCtrl,x,y,w,h);
        (*(void(__fastcall**)(void*,int))(*(void**)g_ourCtrl+0x200))(g_ourCtrl,1);     // show ours
        (*(void(__fastcall**)(void*,int))(*(void**)tree+0x200))(tree,0);               // hide tree (opt)
        return;                                                                         // skip populate
      } else {
        (*(void(__fastcall**)(void*,int))(*(void**)g_ourCtrl+0x200))(g_ourCtrl,0);     // hide ours
      }
    }
    ((void(__fastcall*)(void*,int,char,char))g_origContentSwitch)(browser,contentId,p3,p4);
  }
  ```
  Identity by stored **tab pointer** (`g_ourTab`), not id (ids reshuffle). Runs on FL's UI thread (FL calls it),
  so the WP calls are thread-legal. `g_origContentSwitch` = the saved qword (rebased FUN_009abbc0).

### 6. Hosting our chat content on the content panel (native, no web)
- Parent target = `*(browser+0x34c)` (content panel). Attach via `vtbl[0x138](ctrl, panel)`; position via
  `vtbl[0x188](ctrl, x,y,w,h)` (copy the tree rect from §2); show/hide via `vtbl[0x200](ctrl, 1|0)`.
- **Input (FL-native):** TQuickEdit — the browser's own search box (`browser+0x1b0`) is one, created by
  `FUN_0074c400(&PTR_FUN_007466b8, 1, 0)`. Read typed text @ `editCtrl+0x624` (Delphi UStr; len `*(ptr-4)`);
  set via `FUN_0074c260(editCtrl, ustr)@0x74c260`; Enter via the keydown TMethod (re/14 pattern). Single-line
  at the bottom of our rect.
- **Output (vertical, scrolling):** two native choices — (a) a 2nd TREE control (class `0x9897a8`, via
  `FUN_0098a130(treeClass,1,0,panel)`) used as a line list (most native, scrolls free; needs the node
  add/clear API — LIVE-TODO); or (b) a read-only multi-line TQuickEdit (append = read +0x624, concat,
  `FUN_0074c260`). Pick one at impl; both are native and seamless.
- Comms unchanged: bridge buffers Enter→`g_chatIn`; app polls `chat_poll` → LLM → `chat_say` appends to output
  (all UI mutations on FL's main thread via the existing SendMessage marshaling).

### 7. Win32-child fallback (if WP hosting proves flaky)
Host an opaque Win32 child (EDIT/RICHEDIT) on the browser FORM's HWND. Form = `TSampleListForm`
("forms.browser:wpform"), HWND at `form+0x2b0` (re/13). The browser control is NOT a form (its +0x2b0==0), so
walk to its owning form to get the HWND (LIVE-TODO: resolve the control→form ptr). Position via the tree rect
converted to client coords. Same hook/show-hide logic; lose the FL skin. Use only if §6 stalls.

### 8. Teardown ordering (eject — all on FL main thread, BEFORE FreeLibrary)
1. **Restore the vtbl slot first**: write the saved qword back to `vtblRuntime+0xd0` (or restore `*browser`
   for option B). After this, no FL code can enter our thunk.
2. Hide + destroy our WP controls (set invisible, detach from panel, free). If any TMethod handler (Enter/
   keydown) points into the bridge, clear it before free so FL can't call freed code.
3. Re-show FL's tree/toolbars if §3 hid them (`vtbl[0x200](...,1)`), and `FLbrz_SelectTabById(browser, 0, 1)`
   to move off our tab.
4. **Remove our tab.** No named delete fn was found (searched Delete/Close/Remove). Options:
   - Safe-minimal: set `ourTab+0x8c = 1` (hidden) + refresh tab bar (`FUN_009b2120(browser)`); leave the tab
     object allocated (FL frees it at browser shutdown). Inert once the vtbl is restored. (RECOMMENDED for v1.)
   - Full removal (LIVE-TODO): mirror `FUN_00993f50` (the array shifter) to compact the slot dynarray
     `*(tabsObj+0x10)` past our index, shrink its length, and free the tab object via its destructor — find
     the real close/destructor first (the tab-bar right-click "Close" handler is the lead).
5. Only then `FreeLibrary`. A dangling thunk/WndProc after unmap = guaranteed AV, so 1→2 ordering is critical.

### 9. Remaining unknowns to nail during bridge impl (live)
- (a) Confirm `vtbl[0x200]` = Show for tree class `0x9897a8` and that our `vtbl[0x138]`/`[0x188]` create+parent
  on `*(browser+0x34c)` renders (re/13 sequence; the search-edit at `browser+0x1b0` is the exact template).
- (b) The pre/post-select work in `FLbrz_SelectTabById` runs for our tab even with the hook (FUN_009b89b0 with
  our name/empty root → trivial branch; FUN_009ab5b0 provider loop on `cur+0x34`). Verify it doesn't fault on a
  cloned-then-cleared tab (set `cur+0x34=0`, `cur+0x44`=empty, `cur+0x64`=0 to be safe).
- (c) Output widget API (tree node add/clear, or multi-line TQuickEdit support).
- (d) Win32 fallback only: control→form HWND resolution.
- (e) Full tab removal fn (else use the hide-and-leave teardown).
**Biggest residual risk:** eject teardown ordering (restore vtbl → destroy widgets/handlers → unmap), and that
hiding/restoring FL's own tree/toolbar doesn't desync the browser layout — mitigated by the "cover with opaque
full-panel child, don't hide FL's controls" default. Everything structural is confirmed; this is buildable.

Ghidra annotated + saved: renamed FUN_009abbc0→FLbrz_ContentSwitch_vtbl0xd0 (+ full hook spec plate comment),
FUN_009b8e10→FLbrz_PopulateTreeFromTab, FUN_009a5790→FLbrz_BuildBrowserControls,
FUN_0098aff0→FLbrz_TabInitFields, FUN_00f8d580→FLbrz_MainBrowserCtor.

## Stage B1 — WP widgets on content panel (LIVE-PROVEN, RE pass 7)

PROVEN live (FL pid 59552, flEngineBase 0x67F90000, rebase = ghidra + 0x67B90000; re-resolve every run): a native FL **TQuickEdit** can be created and hosted on the MAIN browser's content panel `*(browser+0x34c)`, with text set+read round-tripping, NO crash. Two edits left visible on the content panel for the user to eyeball — input @(13,200,234,90)="HELLO FRUITYLINK"; display @(13,320,234,160)=3-line text. Live ptrs this session: browser=*(0x157ffb8)=0x31451c0; content panel=0x8d28e20.

### Create + host a TQuickEdit (exact, mirrors the search-box build in FLbrz_BuildBrowserControls@0x9a5790)
1. CREATE: `ctrl = FUN_0074c400(classRef, 1, 0)@0x74c400`, classRef = ghidra 0x7466b8 (TQuickEdit VMT = &PTR_FUN_007466b8). Verify: `*(ctrl) == rt(0x7466b8)`. Returns instance ptr (live 0x61eff00).
2. PARENT: `vtbl[0x138](ctrl, panel)` — **vtbl[0x138] = SetParent** (CONFIRMED: after call, `*(ctrl+0x78) == panel`). Every browser child is parented this way.
3. BOUNDS: `vtbl[0x188](ctrl, x, y, w, h)` (int, WP-canvas coords relative to parent) — **vtbl[0x188] = SetBounds** (CONFIRMED: writes ctrl+0x90/+0x94/+0x98/+0x9c).
4. SETUP (match the search box): `FUN_005ceef0(ctrl, 7)@0x5ceef0`, `FUN_005d0d90(ctrl,0)@0x5d0d90`, `FUN_005d0c50(ctrl,0)@0x5d0c50`.
5. REFRESH/render: `FUN_0077adb0(ctrl)@0x77adb0`. **No vtbl[0x200] "show" needed** — parenting + refresh renders it (BuildBrowserControls never calls vtbl[0x200] on these). (Supersedes re/14 §3/§6's vtbl[0x200] guess.)
Note `FUN_0074c400(classRef,1,0)` signature: (classRef, char allocFlag=1, p3=0); allocs the instance + base-inits internally — no separate FUN_005d08c0 needed for the edit.

### Set / read text — ROUND-TRIP PROVEN
- SET: `FUN_0074c260(ctrl, ustr)@0x74c260`; ustr = ptr-to-chars of a Delphi UnicodeString. Build in a scratch buffer S (`flprobe bridge scratch` → bare hex addr): bytes = `B0 04 02 00`(codepage 0x04B0+elemsize 2) `FF FF FF FF`(refcnt -1) `<len u32 LE>` `<UTF-16LE chars>` `00 00`; pass ustr = S+12. Then `FUN_0074bb70(ctrl)@0x74bb70` (edit-specific refresh) to repaint.
- READ: `ptr=*(ctrl+0x624)`; if !=0: len=`*(ptr-4)` u32, chars=UTF-16 at ptr. PROVEN: set "HELLO FRUITYLINK" → read len=16 identical.

### Display (response area) = 2nd TQuickEdit, multi-line
- Same create/host. Multi-line flag = ushort @ `ctrl+0x682`, set bit0 (`|=1`) — set live (now 0x1). A 3-line "\n"-joined text set+read back (len 48, newlines preserved).
- APPEND a line = read +0x624 → concat "\n"+line → FUN_0074c260 → FUN_0074bb70. CLEAR = set empty UStr.
- ⚠ VISUAL multi-line rendering NEEDS THE USER'S EYES (confirm the display shows 3 lines, not 1; if single-line, +0x682 isn't the wrap flag and another field controls it).

### Teardown (eject) — LIVE-TODO (not destroyed; left visible for confirm)
Find the WP control destructor (Delphi Free via class destructor, or re-parent to 0 then free). Must run on FL main thread BEFORE DLL unmap. Stray edits auto-clear on FL restart (not saved).

### VERDICT: widget hosting is PROVEN and safe to bake into the bridge.
create→parent(0x138)→bounds(0x188)→setup→refresh(0x77adb0) + text set(0x74c260)/read(+0x624) all work live, no crash. Remaining: visual multi-line confirmation + the destructor for clean eject (both low-risk, impl-time). NEEDS USER'S EYES: do the 2 edits render natively on the content panel, and does the display show 3 lines.

## Stage B2 — bridge content-switch hook (IMPLEMENTED + functionally tested)

Implemented in `tools/bridge/dllmain.cpp`. Built (`cmake --build tools/bridge/build --config Release` → FlBridge.dll), injected, all functional tests passed; our widgets show on our tab without crashing FL, on the user's LIVE main browser.

**New pipe commands** (FL/UI work runs on the MAIN thread via new WM_BRIDGE_CHATOPEN/CHATCLOSE handled in subProc):
- `chattab_open` → `DoChatTabOpen()`: g_browser=*(rb 0x157ffb8); clone FLbrz_AddTabCloneOfSource@0x9ac910(browser, arr[0]+0xf4); g_ourTab=arr[count-1]; rename +0x20 & +0x6c via Delphi_UStrAsg@0x4133f0(field, makeUStr(L"FruityLink AI")); create input + multi-line display TQuickEdits on *(browser+0x34c) via the B1 recipe; install hook (save *(*(browser)+0xd0)→g_origContentSwitch, VirtualProtect+write &ContentSwitchThunk); FLbrz_SelectTabById@0x9ac590(browser, ourTabId, 0). Returns {ok,tabId,browser,input,display}.
- `chattab_close` → `DoChatTabClose()`: restore vtbl slot FIRST, hide ctrls, re-show tree (*(browser+0x190)), select tab0, null g_*.
- `chattab_status` → {open,tabId,vtblSlot,orig}.
- **ContentSwitchThunk** [x64 fastcall, SEH-wrapped]: if browser==g_browser && current tab(*(tabs+0x18) index into *(tabs+0x10))==g_ourTab → SetBounds both ctrls to the tree rect (display top, input 26px bottom) + show + hide tree + RETURN (skip populate); else hide ours + tailcall g_origContentSwitch. Identity by g_ourTab POINTER (ids reshuffle).

**Eject safety (wired + CONFIRMED):** BridgeStop sends WM_BRIDGE_CHATCLOSE (main thread) then forceRestoreHook() (direct vtbl restore); DLL_PROCESS_DETACH calls forceRestoreHook() (loader-lock safe, no SendMessage).

**Functional tests (FL pid 59552, live, no crash/restart):** inject→pong; chattab_open→{ok:1,tabId:8}, ping pong, status vtblSlot=0x6852b928 orig=0x6853bbc0; select tab0→pong, select ours→pong; close→status open:0 vtblSlot:0x0, pong; **eject WITH hook installed → FL alive, reinject→pong, post-reinject tab-select→pong (restored vtbl exercised, no dangling-thunk AV) = eject-safety CONFIRMED**; reopen→pong. Tab left open (tabId=10) for visual confirm.

**KNOWN WART:** close HIDES but does NOT remove the tab (WP/tab destructor not RE'd — §8.4). NEXT: B3 layout polish + Stage C comms (chat_poll/chat_say) + glm loop + create-once-on-inject lifecycle.

## Stage B2 FIX (2 bugs from visual test: widgets showed on ALL tabs; repeated opens duplicated tabs)

**BUG 1 — hide was a no-op.** vtbl[0x200] is NOT SetVisible (B1: widgets render with no [0x200] call), so the thunk's `wpShow(ctrl,0)` did nothing → widgets stayed parented+drawn on every tab. FIX = gate visibility by PARENTING (vtbl[0x138]=SetParent, B1-confirmed: ctrl+0x78==parent after):
- New helpers: `wpSetParent(ctrl,parent)` (vtbl[0x138]), `wpReparentIf(ctrl,panel)` (re-parent only if ctrl+0x78!=panel), `wpRender(ctrl)` (FUN_0077adb0). Removed `wpShow`.
- `showOurChat()`: reparent both ctrls to *(browser+0x34c), SetBounds to FULLY cover the tree rect (display = top, input = bottom 26px, NO gap), render LAST (drawn on top). `hideOurChat()`: SetBounds offscreen (-30000) + SetParent(NULL) both → detached so the original's repaint is clean.
- Thunk: our tab → showOurChat()+handled; other tab → hideOurChat() then tail-call original.
- **DoChatTabOpen calls showOurChat() directly at the end** — its SelectTabById is a no-op (the clone already made our tab current), so relying on the thunk to fire missed the first show.

**BUG 2 — open now idempotent.** DoChatTabOpen: if g_ourTab still present in the tab array (scan *(*(browser+0x158)+0x10) for the ptr) → just re-select, no clone, no new widgets. Only clone/create what's missing. Hook installed ONCE (guard g_vtblSlot, so g_origContentSwitch is never overwritten with the thunk).

**Retest (rebuilt, live):** open → `input.parent==panel` IMMEDIATELY (0x8cfdc90) ✓; select tab0 → `input.parent==0` (detached) ✓; select ours → `==panel` ✓; open AGAIN → same tabId, tab count UNCHANGED ✓ (idempotent); eject WITH hook → FL alive → reinject + tab-select → pong ✓ (eject-safe). Then FL restarted CLEAN + one chattab_open → exactly ONE "FruityLink AI" tab, hook installed (vtblSlot=0x6852b928), app relaunched — left open+selected for the lead's visual confirm.

**Still the close-doesn't-remove wart** (orphans a tab across bridge re-inject, since a fresh bridge forgets g_ourTab); within one bridge session, open is idempotent (no accumulation). Real tab removal still needs the tab destructor (§8.4). **NEEDS USER EYES:** does our tab now show our widgets ONLY on it (other tabs clean), and is there exactly one "FruityLink AI" tab + does the layout look native (B1 was "native but awful" — now sized to the full content rect).

## Stage C — comms (bridge): transport DONE; submit + multi-line display need a follow-up

Implemented in `tools/bridge/dllmain.cpp` (new pipe commands, all UI on the main thread via WM_BRIDGE_CHATSAY/CHATPOLL):
- **`chat_say <utf8>`** → appends `<utf8>` + a separator to the display: read display UStr @+0x624, concat, set via FUN_0074c260@0x74c260, caret @+0x62c = len, refresh FUN_0074bb70@0x74bb70. Returns `{"ok":1}`.
- **`chat_poll`** → returns the user's submitted message (raw UTF-8 body; **empty string** if none) and clears it. (Gated on a newline in the input — see the BLOCKER below.)
- **`chat_submit`** → forces submit of whatever's in the input (read +0x624 → return UTF-8 → clear input). Robust/manual path (also what a Send button would call).
- Helpers: `readEditW` (SEH read of +0x624 via an object-free `readEditRaw` → C2712-safe), `setEditText` (build UStr const → FUN_0074c260 → caret → refresh), utf8↔utf16, `takeInput(force)`, `sayMain`.
- Teardown: no keydown TMethod was hooked (see below), so only the vtbl hook is restored on close/eject (unchanged).

**WIRE FORMAT (for the C# poll loop):** request `chat_poll` → response is the raw UTF-8 user message, or `""` (empty) when nothing pending. request `chat_say <utf8 text>` (everything after the first space is the text) → response `{"ok":1}`. request `chat_submit` → same as poll but force-reads the input. So the C# loop: every ~400 ms send `chat_poll`; if non-empty, run FlAgent, then `chat_say <reply>`.

**TESTED (live, FL alive):** chat_say "hello" then "world" → display +0x624 holds both ✓; chat_poll with nothing pending → `""` ✓; set input text programmatically (FUN_0074c260) then chat_submit → returned `"test123"` + cleared the input ✓; chat_poll after → `""` ✓. So the read/append/submit/clear TRANSPORT all work.

**BLOCKER found (needs a follow-up — this TQuickEdit is SINGLE-LINE):** probe — set `"a\rb\nc"` via FUN_0074c260 → stored as `"a b c"` (BOTH 0x0D and 0x0A → 0x20 space), even with the multi-line flag `+0x682 == 0x1`. So:
1. **Display can't show multi-line history** — FUN_0074c260 sanitizes line breaks to spaces → `chat_say` lines run together on one line. Fix: use a TREE/LIST widget for the display (rows), per re/14 B1 option 1 (needs the tree node add/clear API — RE), OR find a set-path that preserves newlines.
2. **User Enter-submit doesn't work via newline-poll** — a single-line edit won't insert `\n` on Enter, so `chat_poll`'s newline gate never trips. The keydown-TMethod slot wasn't cleanly found (a live field scan errored), so I did NOT hook it. **Recommend the Send-button fallback**: add a TQuickBtn (FLwp_CreateButtonControl@0xF0DDB0) next to the input, hook its onClick TMethod (+0x144 code / +0x14c data) → the same read-input→g_chatIn→clear logic (factor `takeInput(true)` into it). A click is unambiguous and avoids the single-line Enter problem. (`chat_submit` already proves that read→clear logic works; the Send button just triggers it from the UI.)

**NET:** comms transport + the C# wire contract are done and testable now (the C# loop can be written against chat_poll/chat_say). The two UI gaps (multi-line display via a tree; Send-button for submit) are the next bridge pass. FL state: tab open; orphan tabs accumulated from my reinject testing (restart FL for a single clean tab).

## Stage C2 — keyboard focus (space fix) + Send button

User report: native tab + widgets work + LETTERS type, but SPACES are eaten ("test data"→"testdata") because FL's spacebar=transport shortcut fires before our edit; FL's own search box (browser+0x1b0, same TQuickEdit class) accepts spaces.

**SPACE-FIX mechanism (found via FLbrz_BuildBrowserControls@0x9a5790):** the search box build does everything our makeEdit did PLUS `FUN_00802820(searchbox, 1)@0x802820`, which is `vtbl[0x270](ctrl, *(uint*)(ctrl+0x490) | 8)` — i.e. it sets **flag bit 0x8 at ctrl+0x490** (a control style/behavior flag) via the WP set-style vtbl[0x270]. That bit is the "keyboard-capturing text field" marker FL's shortcut dispatcher checks to suppress transport shortcuts. **Fix applied:** `DoChatTabOpen` now calls `FUN_00802820(g_inputCtrl, 1)` after creating the input. (GetFocus/SetFocus@0x424b70/0x4252e0 are just the Win32 user32 imports — not the WP mechanism; the focus is this style flag.) **Verified structurally:** input+0x490 = 0x60d → **bit 0x8 set**. ⚠ Whether it actually lets spaces through needs a HUMAN keypress to confirm (can't test typing headlessly). If it's insufficient, the other search-box-only diffs to try next: `*(*(searchbox+0x11c)+0x21)=0`, `FUN_0077d930(ctrl,0,8)`, and the search box's TMethods @+0x664 (FUN_009b1760) / @+0x3e0 (FUN_009b17a0).

**SEND BUTTON (works around Enter-intercept):** `makeButton(panel)` = `FLwp_CreateButtonControl@0xF0DDB0()` → SetParent vtbl[0x138] → caption `FUN_005d0ae0(btn, L"Send")` → render FUN_0077adb0 → hook onClick TMethod (`btn+0x144`=code=&SendButtonClick, `btn+0x14c`=data). `SendButtonClick` (x64 fastcall, SEH) calls `takeInput(true)` → fills g_chatIn → chat_poll drains it. Created + laid out in showOurChat (display top, input bottom-left, **Send bottom-right 56px**), detached in hideOurChat. **Verified structurally:** sendBtn non-null; sendBtn+0x144 points into the bridge DLL (our handler installed).

**TEARDOWN:** DoChatTabClose restores `btn+0x144 = g_sendBtnOrigClick` BEFORE detach; forceRestoreHook (eject/detach backstop) also clears it — so no click can enter the unmapped DLL. (Plus the existing vtbl-hook restore.)

**Refactor:** takeInput now SETS g_chatIn (drained by chat_poll) instead of returning, so both the Send button (async, main thread) and chat_poll/chat_submit feed the same buffer.

**Build green; tested live (FL alive):** open→ok; space-fix flag bit 0x8 set on input; Send button + onClick installed; chat_say appends; chat_submit round-trips. Clean FL restart + one open left for the lead's visual confirm. **NEEDS USER EYES:** (1) can you now type spaces in the input? (2) does clicking "Send" submit (the app's poll loop would then pick it up)?

## Stage C2b — space (deeper) + Send-button render

Both C2 fixes failed live. Re-RE'd the exact mechanisms.

### SPACE — the gate is `mainForm+0x4c5` (NOT the per-control 0x8 flag)
Decompiled `TFruityLoopsMainForm.FormShortCut@0x114de10` (the form's Delphi OnShortCut, runs BEFORE the focused control gets the key): its FIRST branch is `if (*(char*)(mainForm+0x4c5)==0) { dispatch the action-list shortcut → if consumed, *handled=1 }`. So **`mainForm+0x4c5` is the "editing mode / suppress global shortcuts" flag** — when 0, space=play fires; when non-zero, it's skipped and the key goes to the focused edit. (Two later branches also dispatch via `mainForm+0x3310`/`*0x14ab950` for special focus targets, but +0x4c5 is the primary gate. `GetFocus/SetFocus@0x424b70/0x4252e0` are just the Win32 imports — irrelevant.) The 0x8 flag from C2 is necessary (marks the control text-capable) but NOT sufficient — the form-level +0x4c5 must also be set, which FL does when its own edits focus and our cloned edit doesn't trigger.
- **BLOCKER (not fixed this pass):** getting the main-form pointer to set +0x4c5. FormCreate stores it as a TMethod `*(*(0x14aa6e8)+0x228)=mainForm` (code FUN_010ee240@+0x220), BUT live that slot = 0x32a83d0 whose +0x760 (action list) = 0x72 and +0x2b0 = 0 → NOT the main form (the TMethod slot was repurposed after startup). **Did NOT poke an unverified object (corruption risk).**
- **RECOMMENDED next:** (a) reliably resolve the main form — verify a candidate by `cand+0x760` looking like a real action-list ptr (and/or matching the main HWND), e.g. via Application.MainForm or by walking up from a known docked control; then in showOurChat set `mainForm+0x4c5=1`, in hideOurChat/teardown set `=0`. OR (b) inline-hook FormShortCut@0x114de10 itself (param_1=mainForm is PASSED IN, so no global needed): when our tab is the active content and the key is space, leave *handled=0 and skip the dispatch — needs a proper trampoline (relocate the prologue), higher risk. (a) is cleaner if the pointer can be verified.

### SEND BUTTON — render FIXED (was missing the layout call)
Root cause: makeButton created+parented+rendered but SKIPPED `FUN_005ceef0(btn, role)` — the WP layout/realize call FL's own buttons use (e.g. the browser "Search" glyph button @+0x1c8: FUN_005ceef0(btn,6) + onClick @+0x1e4). Without it the control object exists but isn't laid out → invisible. Also the click slot was wrong: FL buttons fire **+0x1e4** (code)/+0x1ec (data), not +0x144. **Fixed makeButton:** FLwp_CreateButtonControl@0xF0DDB0 → SetParent vtbl[0x138] → FUN_005d0c50(0)/FUN_005d0d90(0) → caption FUN_005d0ae0(btn,L"Send") → **FUN_005ceef0(btn,6)** → render FUN_0077adb0 → hook onClick **@+0x1e4**. Teardown restores +0x1e4. **Verified structurally:** sendBtn parent==content panel; bounds x=191 y=1160 w=56 h=26 (inside the content rect 13..247 / 60..1186, bottom-right, on-screen); onClick→bridge DLL. Should now render — NEEDS USER EYES to confirm "Send" is visible + clickable.

**Build green; FL alive; clean restart + one open left open.** Net this pass: Send button render is fixed (verified structurally); the space gate is precisely identified (mainForm+0x4c5) but its safe fix needs a verified main-form pointer (a focused follow-up) — not poked blind.

## Stage C2c — space fix APPLIED (one-shot capture of the main form + toggle +0x4c5)

The two static main-form candidates both FAILED verification live: formMgr `*(*(0x14aa6e8)+0x228)` → +0x760 = 0x72; project-ctrl `*(*(0x14abca8)+0x20)` = 0x200000000 (garbage, so its +0x2c was never set). The only guaranteed-correct source is `FormShortCut`'s `param_1`. **Solution = one-shot self-removing inline hook to capture it:**
- `installShortcutHook()` (in DoChatTabOpen): save FormShortCut@0x114de10's first 12 bytes, write `mov rax,&ShortcutCapture ; jmp rax` (48 B8 <8> FF E0), VirtualProtect RWX + FlushInstructionCache. (Runs on the UI thread; FormShortCut isn't executing then → no race.)
- `ShortcutCapture(form,key,handled)` [x64, entered via the jmp on the first shortcut keypress]: `g_mainForm = form`; **removeShortcutHook()** (restore the 12 bytes → un-hook, one-shot, minimal exposure on this hot fn); if `ourTabActive()` set `mainForm+0x4c5=1`; then tail-call the now-restored original. So FormShortCut is patched only until the first keypress.
- `showOurChat()` sets `mainForm+0x4c5=1`; `hideOurChat()` sets `=0` → suppress shortcuts only while our chat tab is the active content (transport works normally elsewhere). DoChatTabClose + forceRestoreHook set it 0 + removeShortcutHook() (so the jmp never points into the unmapping DLL).

**VERIFIED LIVE (structural):** after open, status `scHooked=1 mainForm=0`; post a key (`flprobe bridge key 41`) → `mainForm=0x495E050 scHooked=0 x4c5=1`; **mainForm+0x760 = 0x4978af0 (a real action-list ptr → genuine main form)**; select another tab → `x4c5=0`; select ours → `x4c5=1`; eject → FL alive → reinject + key → pong (FormShortCut restored, no dangling jmp). This is the exact flag FormShortCut checks, so space should now type into our edit. **NEEDS USER EYES: does space now type (not play) when our chat input is focused?** (Note: if FL ever clears +0x4c5 between keypresses for a non-FL edit, upgrade the one-shot to a persistent hook that re-asserts +0x4c5 each keypress — but FL only writes +0x4c5 on its own edits' focus, so the show/hide toggle should hold.)

Send button (C2b) re-confirmed structurally: parent==content panel, on-screen bounds, onClick→bridge. Build green; clean FL + one tab open.

## Stage C2d — space fix via full FormShortCut suppression (the correct one)

ROOT CAUSE (decompiled FormShortCut@0x114de10): it has THREE shortcut-dispatch blocks and `mainForm+0x4c5`
only gates block 1. Block 3 dispatches on keydown (`*param_2==0x100`) REGARDLESS of +0x4c5 → space (a keydown)
still played. So the +0x4c5 flag could never fully fix it.

FIX (tools/bridge/dllmain.cpp): the inline hook on FormShortCut (`ShortcutCapture`) now REPLACES it entirely —
returns without dispatching any shortcut, leaving `*handled==0` so the key falls through to our focused edit —
and is installed ONLY while our chat tab is the active content: `installShortcutHook()` in `showOurChat`,
`removeShortcutHook()` in `hideOurChat` (teardown/eject restore as before). Net: while our tab is active, ALL
FL main-form shortcuts are suppressed, so space (and every key) types into the edit; off our tab the hook is
removed and FL shortcuts work normally.

VERIFIED (live, structural): chattab_open → scHooked=1; select another tab → scHooked=0; select our tab →
scHooked=1. Eject-safe (removeShortcutHook in DoChatTabClose + forceRestoreHook/DETACH). Build green (Release),
re-injected, ping ok, one tab left open.

TRADEOFF / FUTURE REFINEMENT: this is BROAD — while the chat tab is the *active browser tab*, ALL FL keyboard
shortcuts are suppressed (not just space). Proper refinement = focus-based (suppress only while our input is the
focused WP control) via the WP focused-control global (FormShortCut block 3: PTR_DAT_014ab950 / FUN_009aeb40 /
FUN_009aeb00), not yet resolved. Workaround meanwhile: select a different browser tab to restore shortcuts.

NEEDS USER CONFIRM: space now types into the chat input (not play) while our tab is active.
