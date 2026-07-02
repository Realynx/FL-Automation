# FL data Browser — full UI map (re/ui-win-browser, Wave 2)

The **Browser** = FL's left-panel data browser: a WP control tree (`TVirtualDataBrowser`) hosted inside the
form **`TSampleListForm`** ("forms.browser:wpform"). Module `FLEngine_x64.dll`, image base `0x400000`
(all addrs Ghidra at that base; runtime = `ghidra - 0x400000 + flEngineBase`). Builds on re/14 (chat-tab
hook into this browser — read it for the tab/content-switch hook + WP-widget-hosting recipe), re/23 (menu
categories), ui-02 (control types — **this doc RESOLVES ui-02's flagged "TQuickTree node API" gap**), ui-05
(input / drag-drop), ui-03 (forms). All findings STATIC (Ghidra decompile); no live FL poked this pass.

Convention: funcs renamed `FLui_Browser_*` (browser-specific) / `FLui_Ctl_Tree*` (generic tree, matching
ui-02's prefix — the resolved gap), tag **`UI_win_browser`** (`search_functions_by_tag UI_win_browser`).

---

## TL;DR
- **Two objects, don't confuse them.** (1) The **form** `TSampleListForm` (VMT 0xf8a4f0, classRef 0xf8a508,
  FormCreate 0xf8f200, ctor `FLbrz_MainBrowserCtor@0xf8d580`, descriptor `forms.browser:wpform`, HWND at
  `form+0x2b0`, singleton `*0x14ABFF8`). (2) The **browser control** `TVirtualDataBrowser` (class vtbl
  **0x99b858**), the focused instance is the global **`*0x157ffb8`** (= `PTR_DAT_014ab950`). The form hosts
  the control; almost everything below is on the control.
- **The tree is a VIRTUAL / data-source control, NOT a manual "AddNode" widget.** There is no public
  `AddNode(caption)`. Children are pulled on demand from **provider objects** via two tree vtbl callbacks
  (`vtbl[0x6f0]` ProvideChildren, `vtbl[0x6f8]` InitNode). To populate it you supply a provider / dataFolders;
  FL never builds the node list eagerly. (This is the key reuse fact — see §4 + §10.)
- **The TQuickTree node API IS now mapped** (ui-02 gap resolved): the node *struct*, the **node-model options
  object** at `tree+0x51c` (VMT 0x923970, 4 flag setters), and the traverse / select / expand / toggle
  functions — all in §4.
- **A "tab" is a CONFIG, not a content host** (re/14 confirmed): selecting a tab calls the content-switch
  `vtbl[0xd0]` which **repopulates the single SHARED tree** (`browser+0x190`) from the tab's providers. No
  per-tab content area. §5.
- **Search/filter** flips the tree into a **flat list** (node-model option `+0x10 bit 0x40000`); the search
  edit (`browser+0x1b0`) debounces into an async refresh. §7.
- **Drag-SOURCE** (browser item → project) = OLE `DoDragDrop` from `FLui_Ctl_Tree_BeginNodeDragDrop`, with an
  `IDataObject` built by `FLui_Ctl_Tree_CreateNodeDataObject`. Programmatic equivalent = the "open selected
  item" global command. §8.

---

## 1. The browser control object — field map (`TVirtualDataBrowser`, vtbl 0x99b858)
Built by `FLbrz_BuildBrowserControls@0x9a5790` (the canonical worked example — creates EVERY child control).
Offsets confirmed from that decompile + re/14's live peeks.

| off | what | notes |
|----:|------|-------|
| +0x34c | **content panel** | parent of the tree + toolbars (re/14). Where to host custom WP content. |
| +0x190 (`[0x32]`) | **main content TREE** | the shared file/plugin/sample tree; class vtbl **0x9897a8**. Created `FUN_0098a130(&PTR_FUN_009897a8,1,0,panel)`. |
| +0x198 | secondary tree | 2nd tree pane (e.g. plugin-DB split); same class |
| +0x1a0 | splitter | between the two trees; `FLui_Ctl_Splitter_*`, OnMoved `FUN_009bd990`/OnMoving `FUN_009bdbe0` |
| +0x180 | top header panel | `TQuickControl` (0x779140); +0x188 = the **tab/category strip** (`FUN_009959a0(&DAT_00995420,…)`) wired to browser via TMethods +0x7c/+0x9c/+0x8c |
| +0x238 | secondary header/label panel | "Barlow-Medium" |
| +0x1a8 | **search-bar panel** | `TQuickControl` (0x779140) — holds the search row (see §7) |
| +0x1b0 | **SEARCH edit** (`TQuickEdit`) | onChange = `edit+0x664` → `FLui_Browser_SearchTextChanged`; onKey = `edit+0x3e0`; text @ `edit+0x624` (ui-02). `FUN_00802820(edit,1)` sets the kbd-capture flag (edit+0x490 bit 0x8) |
| +0x1e8 | "TAGS" toggle button | search-by-tags; onChange `FUN_009b0b40` |
| +0x1b8 | search-progress gauge | `TQuickGauge` |
| +0x1c0 | "Indexing" label | shown while indexing |
| +0x1c8 | Search glyph button | onClick `FUN_009b15d0` (P_ILGlyphs) |
| +0x1f0 | "Search in selected folder" button | onClick `FUN_009b1620` |
| +0x1d0 | bottom footer panel | (+0x1d8 sub-panel, +0x1e0/+0x200 containers) |
| +0x208 | "Show items with **any** tags" toggle | onClick `FUN_009b08c0` (tag=0) |
| +0x210 | "Show items with **all** tags" toggle | onClick `FUN_009b08c0` (tag=1) |
| +0x248 | "TYPE" file-extension filter button | dropdown menu `FUN_009ab2c0`; popup obj @+0x250 (item `FUN_009ab280`) |
| +0x218 | Tags grid/control | class 0x997f68; handlers +0x438 `FUN_009be3e0` / +0x448 `FUN_009be4a0`; Delete-context popup @+0x220 (`FUN_009b6e30`) |
| +0x158 | **tab container** (tabsObj) | `*(tabsObj+0x10)`=slot dynarray (count @arr-8), `*(int*)(tabsObj+0x18)`=current idx (re/14) |
| +0x2ec | **content-PROVIDERS dynarray** | iterated by `FLbrz_PopulateTreeFromTab`; each provider's `vtbl[0x70]` adds its nodes |
| +0x2f4 | view-index validator | `FUN_009c1360(browser+0x2f4, viewIdx)` (re/14) |
| +0xe4 | **selected node's data object** | `*(browser+0xe4)`; compared vs `*PTR_DAT_014a91a8` to choose add-vs-replace |
| +0x49 | view/sort mode byte | 0/1/2 (BrowserMenuPopup) |
| +0xa4 (float) | preview/volume state | `0 < (float)` ⇒ a menu item enabled |
| +0x17c | auto-hide enabled byte | gates `FLui_Browser_AutoHideOnLeave` |
| +0x8c4 | "auto-hide active" byte | drag-hover / leave behavior |
| +0x33c | last-search tick | `GetTickCount()` stamp for debounce |

**Key control vtbl slots** (on the browser object, vtbl 0x99b858):
`vtbl[0xd0]`=ContentSwitch (re/14 `FLbrz_ContentSwitch`); `vtbl[0xf8]`=**GetSelectedNode(mode)** (mode 1 →
the selected tree node, or 0 → current); `vtbl[0xf0]`=get secondary content; `vtbl[0x100]`=is-web-content
(bool); `vtbl[0x108]`=reveal/select-node-in-tree; `vtbl[0x260]`=activate/focus; `vtbl[0x338]`=expand/contract
on drag-hover.

## 2. The form `TSampleListForm` (host) — quick facts
- VMT 0xf8a4f0, classRef 0xf8a508; `FormCreate@0xf8f200`; the browser control ctor is
  `FLbrz_MainBrowserCtor@0xf8d580`. HWND at `form+0x2b0` (re/13/14). Singleton `*0x14ABFF8`.
- `TBrowserForm` (0x9e0320, FC 0x9e0920) is a SEPARATE small embeddable browser shell (one child browser
  control, nothing else) — not the main one.
- **Right-click context menu** = `TSampleListForm.BrowserMenuPopup@0xf8eee0`: checks/enables the menu items at
  `form+0x780 … +0x8b0` from the focused browser's state (`*0x157ffb8 + {0xb,0xc,0xd,0xe,0xf,0x10,0x11,0x18,
  0x49,0xa4,0xb2}`) — open/preview/replace/send-to/sort-mode/etc. (`FUN_0081dbb0`=setChecked,
  `FUN_0081dc30`=setEnabled — re/23 menu item API).
- **Key routing** `TSampleListForm.OnBrowserKeyDown@0xf8ed30`: space/`+`(0x6b)/`-`(0x6d) are forwarded to
  `TFruityLoopsMainForm.FormKeyDown` (transport/zoom) and swallowed; everything else →
  `FLui_Input_DispatchKeyAsHotkey` on the project controller. (This is WHY the chat-tab needed the
  FormShortCut suppression in re/14 — the browser form deliberately hands space to the main form.)

---

## 3. Control build sequence (reuse template)
`FLbrz_BuildBrowserControls@0x9a5790` is a full worked example of building ~20 WP controls onto
`browser+0x34c`. Per control it does (matches ui-02 §4 / re/14 B1):
`create → SetParent vtbl[0x138](panel) → SetBounds vtbl[0x188](x,y,w,h) → style/color → align FLui_WP_SetAlign
→ wire TMethod handlers → FLui_WP_RecalcContentWidth`. The two trees use `FUN_0098a130(&PTR_FUN_009897a8,1,0,
panel)`; the search box uses `FLui_Ctl_Edit_Create(&PTR_FUN_007466b8,1,0)`; buttons use
`FLui_WP_CreateControl(&LAB_00715520,1,0)` (TQuickBtn). Align constants seen: 3=left, 6=right, 7=client.

---

## 4. TQuickTree node API — **RESOLVED ui-02 gap**
Two tree create paths, both end at `FLui_Ctl_Tree_BaseInit@0x9460a0`:
- **Generic scrolling tree**: `FLui_Ctl_TreeModule_Create@0x1058140` (tree @module+0x674 + scroller
  @module+0x67c) — ui-02.
- **Browser tree**: `FUN_0098a130@0x98a130` (browser subclass, vtbl 0x9897a8) — same base-init + same
  node-model config.

### 4a. The node-model "options" object (`tree+0x51c`, VMT **0x923970**) — this is the ui-02 "node model"
It is **not** a node container — it holds the tree's behavior flags; `model+0x08` back-points to the tree.
Configured right after create (defaults from both create paths):
```
m = FLui_Ctl_Tree_GetNodeModel(tree)@0x973aa0;         // = *(tree+0x51c)
FLui_Ctl_TreeModel_SetStyleFlags   (m, 8);       @0x935d30  // ushort @m+0x15
FLui_Ctl_TreeModel_SetBehaviorFlags(m, 0x10020); @0x935da0  // uint   @m+0x19  (bit0x1 = RegisterDragDrop!)
FLui_Ctl_TreeModel_SetOptions      (m, 0x8005);  @0x935f10  // int    @m+0x10  (bit 0x40000 = FLAT/SEARCH list)
FLui_Ctl_TreeModel_SetDisplayFlags (m, 0x25);    @0x935f60  // ushort @m+0x17  (bits 0x2/0x20/0x88 relayout)
```
- `m+0x10 bit 0x40000` = **flat-list mode** (every traversal fn checks it): when set, the tree renders the
  visible nodes as a flat list (used by search). When clear, hierarchical.
- `m+0x19 bit 0x1` = "is a drop target": setting it calls `RegisterDragDrop(treeHWND, …)` with the
  `IDataObject` from `FLui_Ctl_Tree_CreateNodeDataObject@0x9485d0`; clearing it `RevokeDragDrop`.

### 4b. Node struct (a tree data node)
| off | type | meaning |
|----:|------|---------|
| +0x04 | int | realized child count; **0 with flag 0x40 ⇒ children not yet provided** (triggers ProvideChildren) |
| +0x0a | u16 | **flags**: 0x1 inited · 0x20 expanded(laid-out) · 0x40 expandable(has provider) · 0x80 visible/matches-filter · 0x100 expand-target(pending) · 0x4000 busy-guard |
| +0x14 | int | node row height / extent |
| +0x18 | ptr | parent node (root sentinel = `tree+0x50c`) |
| +0x28 | ptr | next sibling |
| +0x30 | ptr | first child |
| +0x48 | ptr | **payload / data wrapper**: `payload->vtbl[0x80](payload,&out)` = caption; `*(payload+0x18)`=content item (`item+0x54` byte = file type); `*(payload+0x10)`=owning tree |

Tree-level node anchors: `tree+0x50c` = ROOT sentinel (iteration boundary); `tree+0x504` = the flattened
**display list** (`FUN_00940d00`=current idx, `FUN_0093d2d0(*(list+0x10),idx)`=node@idx); `tree+0x540`
(=`[0xa8]`) = **selected node**; `tree+0x54c` = active row index; `tree+0x5fc` visible count; `tree+0x60c`
expanded count; `tree+0x614` pending-reveal node; `tree+0x524` = update-lock (>0 suppress redraw); `tree+0x518`
indent (`FUN_0094a990`).

### 4c. Traverse / select / expand — the functions
| fn | addr | does |
|----|------|------|
| `FLui_Ctl_Tree_GetFirstNode(tree,flat)` | 0x961e50 | first node (root+0x30; or first visible in flat mode); fires InitNode/ProvideChildren as it walks |
| `FLui_Ctl_Tree_GetNextNode(tree,node,flat)` | 0x962f60 | DFS next **visible** node (child→sibling→uncle) |
| `FLui_Ctl_Tree_GetNextVisibleNode(tree,node,1)` | 0x9634b0 | next node skipping collapsed subtrees (display order) |
| `FLui_Ctl_Tree_GetNextSubtreeNode(tree,node,flat)` | 0x963180 | next sibling-subtree / step-out |
| `FLui_Ctl_Tree_SelectNode(tree,node)` | 0x94a7d0 | set selection (`tree+0x540` via vtbl[0x478]) + scroll-into-view (vtbl[0x468]) |
| `FLui_Ctl_Tree_SetNodeExpanded(tree,node,1/0)` | 0x94a680 | expand/collapse (no-op if already in state) → calls Impl |
| `FLui_Ctl_Tree_ExpandCollapseImpl(tree,node)` | 0x968b10 | the heavy expand/collapse worker (lazy-provides children via vtbl[0x6f0], animates, relayouts) |
| `FLui_Ctl_Tree_SetNodeExpandTarget(tree,node,1)` | 0x94aea0 | mark a node to be expanded (sets flag 0x100; used to expand the path to the selection) |
| `FLui_Ctl_Tree_CollapseOverflow(tree)` | 0x9602d0 | auto-collapse to fit the viewport |
| `FLui_Ctl_Tree_BaseInit(tree,alloc,owner)` | 0x9460a0 | tree ctor (creates display list +0x504, node-model +0x51c via vtbl[0x6a8], child node +0x770) |

**Population is provider-driven** (no public AddNode): when a walker hits a node with flag 0x40 and child
count 0, the tree calls **`tree->vtbl[0x6f0](tree,node)` = ProvideChildren**; first touch of any node calls
**`tree->vtbl[0x6f8](tree,node)` = InitNode**. So children appear lazily on expand. The browser supplies these
through the providers at `browser+0x2ec` (each `provider->vtbl[0x70](provider,tree,…)` populates) — see §5.

---

## 5. Tab system + content rendering
Tab APIs (most from re/14; consolidated here): `FLbrz_CreateConfiguredTab@0x9acc90`,
`FLbrz_AddTabCloneOfSource@0x9ac910` (clone a valid tab — simplest spawn), `FLbrz_TabContainerCreateTab@0x993fd0`,
`FLbrz_TabInitFields@0x98aff0`, `FLbrz_SelectTabById@0x9ac590`, `FLbrz_GetCurrentTab@0x994770`,
`FLbrz_GetRawTabCount@0x994790`, `FLbrz_GetVisibleTabCount@0x994b00`, `FLbrz_FindTabSlotById@0x994870`,
`FLbrz_BrowseToPathInTab@0xf8c2d0`, `FLweb_BrowserTabRelativeSelect@0x9b60c0` (prev/next tab: 0x2a=first,
0x2b=next).

**Tab slot fields** (re/14 + adds): +0x10 kind · +0x14 color · +0x1c baseId (new=+0xf400) · +0x20 caption UStr ·
+0x30 **contentType** (what ContentSwitch dispatches on) · +0x34 selNode · +0x44 dataFolders dynarray ·
+0x4c/+0x50 viewIndex · +0x64 root path · +0x6c name/filter · +0x74 filter ext · +0x8c visible(0=shown) ·
+0x100 path/content object (read by selection-move) · +0x10c subtype · +0xf4 tab id · +0x540 the tab's selected
node · +0x604/+0x60c path-provider list. Live: a real browser has **7 built-in tabs**, kinds {0,1,2,4,5,6,8},
content types {0,1,4,7} (Plugin/Packs/Projects/Samples/…), each a different renderer.

**How content renders** (the critical structural fact, re/14 confirmed): `FLbrz_SelectTabById` writes the new
index then calls `vtbl[0xd0]` = `FLbrz_ContentSwitch`, which **repopulates the single SHARED tree**
(`browser+0x190`) via **`FLbrz_PopulateTreeFromTab@0x9b8e10(browser, tree, selNode, dataFolders, kind, p3)`**.
PopulateTreeFromTab iterates the providers `*(browser+0x2ec)` calling each `provider->vtbl[0x70](provider,
tree,…)` to inject that provider's nodes, then locates + selects the tab's saved node
(`FLui_Browser_FindNodeByName@0x9b36f0` + `vtbl[0x108]` reveal). **There is no per-tab content host** — every
built-in tab paints the same tree (or a web view) with different data; a tab is pure config.

⇒ Hosting OUR OWN content in a real tab still requires the re/14 approach: clone a tab, install the
`vtbl[0xd0]` hook, and when our tab is active draw our own WP children on `browser+0x34c` instead of letting
PopulateTreeFromTab run (re/14 §"REAL TAB" — shipped). The alternative (native tree content) = register a
provider (§10).

---

## 6. Content panels per tab
There is exactly one shared content region: `browser+0x34c` (parent panel) → hosts the tree(s)
`browser+0x190`/`+0x198`, the splitter `+0x1a0`, the top tab/category strip `+0x180`/`+0x188`, the search bar
`+0x1a8`, and the footer `+0x1d0`. WP-canvas rect offsets on these controls: x=+0x90 y=+0x94 w=+0x98 h=+0x9c
(re/14). Web-backed tabs (News/Help/Gopher in the *separate* help browser `*0x15800d0`) use a different
instance, not this one — the MAIN browser has no web view (re/14 pass-6 correction).

---

## 7. Search / filter
- **Search edit** = `browser+0x1b0`. onChange (`edit+0x664`) = **`FLui_Browser_SearchTextChanged@0x9b1760`**:
  stamps `browser+0x33c = GetTickCount()` and schedules a **debounced async refresh**
  `FUN_00717830({code=FUN_009b1660, data=browser}, 0)` (the deferred `FUN_009b1660` rebuilds the result list).
- While a query/filter is active the node-model enters **flat mode** (`model+0x10 bit 0x40000`) so the tree
  shows a flat list of matches instead of the folder hierarchy (every traverse fn branches on this bit).
- Filter UI: TAGS toggle `+0x1e8` (search-by-tags), search-in-folder `+0x1f0`, clear/search glyph `+0x1c8`,
  tag-match "any" `+0x208` / "all" `+0x210` (`FUN_009b08c0`, tag 0/1), **TYPE** extension filter `+0x248`
  (dropdown popup `+0x250`).
- **Space-in-search fix** (matches re/14 C2): the search edit is marked keyboard-capturing via
  `FUN_00802820(edit,1)` → sets `edit+0x490 bit 0x8`. (Necessary but the browser form ALSO forwards space to
  the main form via `OnBrowserKeyDown` — see §2.)

---

## 8. Drag-SOURCE (browser item → project) — ties to ui-05
- **Internal OLE drag** starts at **`FLui_Ctl_Tree_BeginNodeDragDrop@0x9567d0`**: builds an `IDataObject`
  (**`FLui_Ctl_Tree_CreateNodeDataObject@0x9485d0`**) + an `IDropSource` (vtbl `&DAT_00956890`) and calls
  Win32 **`DoDragDrop`**. Triggered from the tree's mouse-drag (capture). The SAME `CreateNodeDataObject`
  factory is reused when the tree registers itself as a drop TARGET (§4a, `RegisterDragDrop`).
- **Drop targets** are the per-window DragOver/Drop handlers (ui-05 §7): channel rack `FUN_00f4a240`, event
  editor `TEventEditForm.CheckDragOver@0xd4c6c0`, main form `DragWAVDrop@0x10caa70`. The browser's own
  drag-hover handler **`FLui_Browser_AutoHideOnLeave@0xf8c0c0`** auto-expands/collapses the auto-hidden browser
  when you drag over it (and recenters the cursor) — it's both the leave-timer and the drag-hover handler.
- **Programmatic "drop selected item into the project"** (no real drag needed):
  `FLui_Browser_SelectMenuItem_worker@0xe14ff0` → `FLgl_GlobalCommandDispatch(0x5b|0x50, 1, 0x22, 0xf)`
  (0x5b = add as new channel/insert; 0x50 = replace current). **Preview/audition** =
  `FLui_Browser_PreviewMenuItem_worker@0xe151c0` → `browser->vtbl[0xf8](browser,1)` then `FUN_00b6a1d0(5,0)`.
  These are the clean, scriptable equivalents of dragging a sample/plugin/pattern into FL.

---

## 9. High-level browser API (clean, Python-backed — best reuse surface)
Operate on the focused browser `*0x157ffb8`. (The Python `FLpy_ui_*` wrappers just marshal onto these.)
| fn | addr | does |
|----|------|------|
| `FLui_Browser_NavigateSelection(mgr,dir,ctrl)` | 0xf8b8e0 | arrow-navigate selection (0x28 down/0x29 up; Enter = add/replace via GlobalCommandDispatch + preview) |
| `FLui_Browser_MoveSelection(browser,key,expand)` | 0x9b61f0 | move selection inside the tree/content (content-type aware: up/down 0x28/0x29, left/right 0x2a/0x2b → collapse/expand) |
| `FLui_Browser_ToggleSelectedNode(browser,mode)` | 0x9b67c0 | mode −1 toggle / 0 collapse / 1 expand the selected node (`vtbl[0xf8]` get-selected → `FLui_Ctl_Tree_SetNodeExpanded`/`ExpandCollapseImpl`) |
| `FLui_Browser_GetSelectedNodeName(browser,&out)` | 0x9b6730 | selected node caption (`node+0x48`→vtbl[0x80]) |
| `FLui_Browser_GetNodePathNames(tree,node,&name,&parent)` | 0x9b8d50 | node + parent captions |
| `FLui_Browser_FindNodeByName(browser,tree,name,…)` | 0x9b36f0 | locate a node by caption (linear walk via the traverse fns) |
| `FLui_Browser_NavigateTabs(mgr,dir)` | 0xf8b880 | switch browser tab |
| `FLui_Browser_*_worker` (toggle/select/preview) | 0xe14c00/0xe14ff0/0xe151c0 | the op-thread workers behind FLpy_ui_toggleBrowserNode / selectBrowserMenuItem / previewBrowserMenuItem |

Globals: `*0x157ffb8` (`PTR_DAT_014ab950`) = focused browser; `PTR_DAT_014abff8` = data-browser manager/form;
`PTR_DAT_014abca8` = project controller; `PTR_DAT_014a91a8` = current channel/target (add-vs-replace test);
`PTR_DAT_014a8750` = main app.

---

## 10. Reuse — add a custom browser tab + populate a tree/content
1. **Custom tab (shipped recipe, re/14):** `FLbrz_AddTabCloneOfSource@0x9ac910(browser=*0x157ffb8, srcTabId)`
   → rename slot `+0x20`/`+0x6c` → clear `+0x44`/`+0x64` → install the `vtbl[0xd0]` content-switch hook
   (`VirtualProtect`+swap `*(*browser+0xd0)`) gated on `browser==g_browser && currentTab==g_ourTab` → on our
   tab: host our WP widgets on `browser+0x34c` and **return without** calling the original (skip
   PopulateTreeFromTab); off our tab: tail-call original. Eject = restore the slot first, then destroy widgets.
   (Full spec + teardown ordering in re/14 §"REAL TAB vtbl-hook" / Stage B2.)
2. **Native tree CONTENT (provider model):** because the tree pulls children via `vtbl[0x6f0]` ProvideChildren,
   to fill FL's own tree natively you would register a provider object into `browser+0x2ec` (implementing the
   provider `vtbl[0x70]` populate contract) and/or seed the tab's `+0x44` dataFolders + `+0x64` root path, then
   let `FLbrz_PopulateTreeFromTab` drive it. The provider/vtbl[0x70] node-emit contract is the remaining
   unknown (gap below) — until mapped, host **our own** WP tree/list on the content panel (option 1) and drive
   it ourselves via the §4c node API rather than fighting the data-source.
3. **Drive the existing browser** (no new tab): the §9 high-level API + §8 GlobalCommandDispatch let us
   navigate, expand, select, preview, and drop items into the project programmatically — often enough without
   any custom UI.

---

## 11. Ghidra renames applied (all tagged `UI_win_browser`) — 28 funcs renamed + 5 already-named tagged
**TQuickTree node API (resolves ui-02 gap) — `FLui_Ctl_Tree*`:**
`FLui_Ctl_Tree_GetFirstNode@0x961e50`, `FLui_Ctl_Tree_GetNextNode@0x962f60`,
`FLui_Ctl_Tree_GetNextVisibleNode@0x9634b0`, `FLui_Ctl_Tree_GetNextSubtreeNode@0x963180`,
`FLui_Ctl_Tree_SelectNode@0x94a7d0`, `FLui_Ctl_Tree_SetNodeExpanded@0x94a680`,
`FLui_Ctl_Tree_ExpandCollapseImpl@0x968b10`, `FLui_Ctl_Tree_SetNodeExpandTarget@0x94aea0`,
`FLui_Ctl_Tree_CollapseOverflow@0x9602d0`, `FLui_Ctl_Tree_BaseInit@0x9460a0`,
`FLui_Ctl_Tree_CreateNodeDataObject@0x9485d0`, `FLui_Ctl_Tree_BeginNodeDragDrop@0x9567d0`,
`FLui_Ctl_TreeModel_SetStyleFlags@0x935d30`, `FLui_Ctl_TreeModel_SetBehaviorFlags@0x935da0`,
`FLui_Ctl_TreeModel_SetOptions@0x935f10`, `FLui_Ctl_TreeModel_SetDisplayFlags@0x935f60`.
(Already-named, tagged: `FLui_Ctl_Tree_GetNodeModel@0x973aa0`, `FLui_Ctl_TreeModule_Create@0x1058140`,
`FUN_0098a130` browser-tree ctor.)

**Browser — `FLui_Browser_*`:**
`FLui_Browser_NavigateSelection@0xf8b8e0`, `FLui_Browser_NavigateTabs@0xf8b880`,
`FLui_Browser_MoveSelection@0x9b61f0`, `FLui_Browser_ToggleSelectedNode@0x9b67c0`,
`FLui_Browser_GetSelectedNodeName@0x9b6730`, `FLui_Browser_GetNodePathNames@0x9b8d50`,
`FLui_Browser_FindNodeByName@0x9b36f0`, `FLui_Browser_AutoHideOnLeave@0xf8c0c0`,
`FLui_Browser_SearchTextChanged@0x9b1760`, `FLui_Browser_ToggleNode_worker@0xe14c00`,
`FLui_Browser_SelectMenuItem_worker@0xe14ff0`, `FLui_Browser_PreviewMenuItem_worker@0xe151c0`.
(Already-named, tagged: `FLbrz_BuildBrowserControls@0x9a5790`, `FLbrz_PopulateTreeFromTab@0x9b8e10`.)

---

## 12. Gaps / for next pass
- **Provider `vtbl[0x70]` node-emit contract** (how a provider object actually creates+links child nodes inside
  ProvideChildren) — NOT decompiled. This is the one piece needed to populate FL's own tree natively (vs.
  hosting our own list). The provider classes live in `browser+0x2ec`; trace one `vtbl[0x70]`.
- **The exact node ALLOCATOR** (function that mallocs a node struct and links +0x18/+0x28/+0x30/+0x48) — the
  node tree is built only inside ProvideChildren/InitNode, so it's behind the same provider gap.
- The debounced search worker `FUN_009b1660` (filter rebuild) and the TYPE/extension filter menu builder
  (`FUN_009ab2c0`) not fully decompiled (roles confirmed by wiring; renames deferred to avoid guessing).
- `TVirtualDataBrowser` vtbl 0x99b858 slot meanings beyond the ones in §1 (0xd0/0xf0/0xf8/0x100/0x108/0x260/
  0x338) — partial; the full content-control contract was mapped in re/14.
- Visual confirmation (pixels) of any custom tab — needs the user's eyes (re/14 already flagged).
