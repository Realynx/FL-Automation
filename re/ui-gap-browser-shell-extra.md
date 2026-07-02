# UI residual polish — G10 (browser VMT) + G11 (shell transport toggles)

Wave 4 residual-gap closure over `re/ui-gaps.md` **G10** (`TVirtualDataBrowser` vtbl `0x99b858` residual slot
meanings) and **G11** (shell transport/toolbar toggle **1:1** field→control→model mapping). Static Ghidra on
`FLEngine_x64.dll`, image base `0x400000` (all addrs absolute/Ghidra; runtime = `ghidra - 0x400000 +
flEngineBase`). No live FL. Renames in-lane (`FLui_Browser_*`, tag `UI_win_browser`); shell side is a
data/offset mapping (annotated via bookmark + this doc). Builds on `re/ui-win-browser.md`, `re/ui-win-shell.md`,
`re/14`. Program saved.

---

## G10 — TDataBrowser control VMT (`0x99b858`) residual slots

### Identity + a correction
- The class VMT the browser doc calls **`TVirtualDataBrowser`** carries the **RTTI name `TDataBrowser`**, unit
  **`DataBrowser`** (published-class strings immediately after the method table). It manages a
  **`TPair<System.string, VirtualTrees.PVirtualNode>`** field — i.e. the browser is a Delphi **VirtualTrees**
  (Virtual TreeView) host with a **name→PVirtualNode map** (this is the `browser+0x2e4` cache used by
  `FLui_Browser_CommitNodeSelection`, and confirms the "provider/virtual tree" model in browser §4/§10).
- **VMT extent:** the method-pointer array runs **slots `0x00`–`0x2d0` (91 entries)**; RTTI/field-init data
  begins at **`+0x2d8`**. Therefore **`vtbl[0x338]` cited in `re/ui-win-browser.md` §1/§12 is OUT OF RANGE for
  this VMT** (`0x99b858+0x338` lands inside the RTTI block, not a method slot). It is a doc error — the
  drag-hover "expand/contract" behaviour lives on `FLui_Browser_AutoHideOnLeave@0xf8c0c0` (browser §8) and/or a
  tree vtable, not a browser-VMT slot. Treat 0x338 as spurious. (Bookmarked at `0x99b858`.)
- Slots **`0x00`–`0xc8`** are the inherited **WP / `TQuickControl` base** virtuals (ctor/dtor, paint, mouse,
  bounds/align, showing, etc.) — not browser-specific; not enumerated here. The browser-specific overrides start
  at `0xd0`. Prior-known slots (re/14): `0xd0` ContentSwitch, `0xf0` get active/secondary tree, `0xf8`
  GetSelectedNode(mode), `0x100` is-web-content, `0x108` reveal-node, `0x260` activate/focus.

### Newly-mapped slots (ghidra addrs; renamed `FLui_Browser_*` where confident, else FUN_ + role)
| slot | addr | name / role |
|-----:|------|-------------|
| 0xd0  | 0x9abbc0 | *(known)* `FLbrz_ContentSwitch` — repopulate shared tree for the current tab |
| **0xd8**  | 0x9ad0f0 | **`FLui_Browser_ProvidersOnNodeExpand`** — iterate providers `browser+0x2ec`, each `provider->vtbl[0x18](provider, tree@+0x190, node, node)`; guarded `+0x108==0` & not-searching (`+0xb2==0`). Providers add/refresh a node's children on expand |
| **0xe0**  | 0x9ad200 | **`FLui_Browser_ToggleNodeExpand`** — `is-expanded(0x9486b0)` then `FLui_Ctl_Tree_SetNodeExpanded(!state)`; if node==selected → `FUN_009ba740(browser,0,0)` |
| **0xe8**  | 0x9b5fc0 | **`FLui_Browser_GetPrimaryTree`** — returns `*(browser+0x190)` (the shared main tree, `[0x32]`) |
| 0xf0  | 0x9b6640 | *(known)* get active/secondary tree (returns a tree w/ `+0xa9`,`+0x50c` root,`+0x540` sel) |
| 0xf8  | 0x9b6650 | *(known)* `GetSelectedNode(mode)` |
| 0x100 | 0x9b6840 | *(known)* is-web-content (bool) |
| 0x108 | 0x9b68f0 | *(known)* reveal/select node in tree |
| **0x110** | 0x9b4190 | *(FUN_)* node hover/prepare-activate: reads current tab flags (`+0x128`,`+0x10c/0x10d`,`+0x10`) + node payload `+0x48`; on match sets `browser+0x134 = node` (candidate open-node), clears `+0x2d0`; else `FUN_009b40e0` |
| **0x118** | 0x9b4140 | *(FUN_)* leaf-open helper: `payload->vtbl[0x68]` (is-container); if not → `this->vtbl[0xc0](payload[2])` then `FUN_0094b230(payload[2],node,0)`. Called from slot 0x128 |
| **0x120** | 0x9ac7c0 | **`FLui_Browser_SelectTabByIdOrCreate`** — find tab id in `+0x158`; if missing & `force`, create (`FUN_009afac0`); then `FLbrz_SelectTabById` if different |
| **0x128** | 0x9ba170 | **`FLui_Browser_CommitNodeSelection`** — scroll/select node (`FUN_009b9fa0`), restore expand + row/col indices via the name→index cache `+0x2e4` (`FUN_009917c0/00991880`), then `this->vtbl[0x118]` |
| **0x130** | 0x9ba480 | *(FUN_)* setter: `browser+0x2d0 = arg` (pending-activation node) |
| **0x138** | 0x9ba490 | *(FUN_)* setter: `browser+0x2c8 = arg` |
| **0x140** | 0x9ba4a0 | *(FUN_)* fire user callback: if `browser+0x268`(code)≠0 → `(*code)(browser+0x270 data, browser, UStr(arg))` — a browser `OnX(string)` event hook |
| **0x148** | 0x9aea80 | **`FLui_Browser_GetEffectiveNode`** — via `vtbl[0xf0]` active tree; returns `node+0x18` (parent) unless `tree+0xa9` set & node is a root child → returns `tree+0x540` (selected) |
| **0x150** | 0x9b2790 | **`FLui_Browser_RefreshTreeForContentType`**`(browser, contentType, viewIdx)` — if current tab `contentType(+0x30)==arg`: `FLbrz_PopulateTreeFromTab` + select-first-if-none. External "refresh views of type X" hook |
| **0x158** | 0x9b4040 | **`FLui_Browser_ActivateCurrentNode`**`(browser, mode)` — default-action commit (Enter/dbl-click): `mode==3`→`vtbl[0x140](0)`; if `+0x47` handler & activated node `+0x134` → `payload->vtbl[0x28](handler,mode)`; then current-tab provider `vtbl[0x68](mode)` |
| **0x160** | 0x9ae0c0 | **`FLui_Browser_PopulateTreeViaProviders`**`(browser, tree, p3, p4, kind)` — the provider-driven populate **workhorse**: clears, iterates `+0x2ec` calling `provider->vtbl[0x38]` (populate-as-tree, rich args incl. a dataFolder object) OR `provider->vtbl[0x30]` (flat) per `kind` bits (mask `0x90`), then finalizes the tree (`vtbl 0x850/0x8f0/0x888/0x298`). Companion to `FLbrz_PopulateTreeFromTab`; **direct lead for G2 provider contract** (the `vtbl[0x38]`/`[0x30]` provider-populate methods) |
| **0x168** | 0x9b0810 | *(FUN_)* insert-into-tree helper: tree defaults to `browser+0x160` (the secondary tree); `lock(0x97f1b0,0)` → `FUN_0097cf00(tree,p2,p3,0)` → unlock |
| **0x170** | 0x9b0870 | *(FUN_)* insert variant: tree via `FUN_009b01d0(browser)`, `FUN_0097cf00(...,1)`, `FUN_009a39e0` |
| **0x178** | 0x9bde80 | **`FLui_Browser_SetSecondaryHeader`**`(browser, show, caption)` — create/destroy a secondary header panel `browser+0x228` + label `browser+0x230` (font "Barlow-Medium"), parented into the content panel `+0x34c`. The captioned section-header strip |
| **0x180** | 0x9b90b0 | **`FLui_Browser_NodeItemExists`**`(browser, node)` — if `payload+0x74==2` (unresolved): resolve item (`FUN_0097d1c0`) + `item->vtbl[0xb0]` file-exists, cache result to `+0x74`; returns `(+0x74==1)` |
| **0x190** | 0x9b8820 | *(FUN_)* query aux object at `browser+0x150`: if present & `(+0x150)+1` flag → `(+0x150)->vtbl[0]`; else returns 1 |
| 0x260 | 0x9b9c20 | *(known)* activate/focus |
| **0x2c0** | 0x9a3ca0 | **`FLui_Browser_AllProvidersReady`** — for each visible tab (`+0x8c==0`) with provider `+0x110`: `provider->vtbl[0x48]()`; returns false if any not ready (indexing/loading complete check) |
| **0x2c8** | 0x9a3b20 | **`FLui_Browser_EnumProvidersUntilMatch`** — build a provider iterator (`FUN_009c03d0(&PTR_FUN_0099ece8,1, browser+0x2ec)`); call each `provider->vtbl[0x90](provider,a,b,c)` until one returns nonzero |
| **0x2d0** | 0x9ba9c0 | **`FLui_Browser_ResolveNodeTintColor`** — luminance-weighted tint of a node's RGB by select/hover/parity flags, via `this->vtbl[0x88]` |

### Browser field-map additions (from the above)
`+0x134` currently-activated node · `+0x150` aux provider/query object · `+0x160` **secondary tree** (target of
insert helpers 0x168) · `+0x228`/`+0x230` secondary header panel + label (built by `SetSecondaryHeader`; note
this differs from the `+0x238` label panel in browser §1) · `+0x268`/`+0x270` user event callback (code/data) ·
`+0x2c8`/`+0x2d0` pending-selection/activation node slots · `+0x2e4` **name→node index cache**
(`TPair<string,PVirtualNode>` map; used by `CommitNodeSelection`).

### G10 reuse notes
- **Populate FL's own tree natively:** `FLui_Browser_PopulateTreeViaProviders@0x9ae0c0` is the concrete
  provider-iteration populate (calls `provider->vtbl[0x38]` tree / `vtbl[0x30]` flat) — pairs with
  `FLui_Browser_ProvidersOnNodeExpand@0x9ad0f0` (`provider->vtbl[0x18]` = lazy children). These three provider
  methods (`0x18/0x30/0x38`) are the exact surface G2 (`re/ui-gap-tree-provider.md`) needs to implement a custom
  provider.
- **Drive selection/expand programmatically:** `FLui_Browser_CommitNodeSelection@0x9ba170` (select+reveal+restore
  state), `FLui_Browser_ToggleNodeExpand@0x9ad200`, `FLui_Browser_ActivateCurrentNode@0x9b4040` (default action),
  `FLui_Browser_GetPrimaryTree@0x9b5fc0` — a clean C-callable layer over the tree.
- **Custom header text** without a full tab: `FLui_Browser_SetSecondaryHeader@0x9bde80(browser,1,L"caption")`.
- **State queries:** `FLui_Browser_AllProvidersReady@0x9a3ca0` (is indexing done), `FLui_Browser_NodeItemExists`.

---

## G11 — Shell transport/toolbar toggle 1:1 (RESOLVED)

Resolved definitively from the **`TToolbarForm` published-field table** (control name ↔ instance offset,
decoded at ghidra `0xcb3760`+; entry = `{u32 offset, u16 typeIdx, shortstring name}`, ordered ascending) cross-
referenced with the **`FUN_005d0a30(toolbar+off, mainForm+off)` bind calls in `TToolbarForm.FormCreate@0xcb7f90`**.
This pins every field the shell doc left as "(region A/B to confirm)"/"(gap)". (Bookmarked at `0xcb7f90`.)

### Toolbar control field → name (transport / toggles / master / chrome)
| toolbar off | control name | notes |
|------------:|--------------|-------|
| +0x760 | `TopToolbar` | top toolbar section (menu+transport row) |
| +0x778 | **`TempoSelect`** | tempo (paint=TempoSelectPaint) |
| +0x780 | **`StopBtn`** | **transport Stop** (value-bound ← main+0x1920) |
| +0x788 | **`RecBtn`** | record (value-bound ← main+0x13d0) |
| +0x790 | **`StartBtn`** | **transport Play** (action-driven; NOT value-bound) |
| +0x7a0 | `PatMenuBtn` | pattern menu |
| +0x7a8 | **`PatSelect`** | pattern selector |
| +0x7b0 | `PatNextEmptyBtn` | + next-empty-pattern |
| +0x7e8 | **`SongPosSlider`** | song position |
| +0x818 | **`PolyDigiLabel`** | **Poly** digit (resolves shell §3 "Poly field") |
| +0x820 | **`PolyLabel`** | Poly label |
| +0x838 | **`TimeLabel`** | time/position digits (value-bound ← main+0x1c50) |
| +0x888 / +0x890 / +0x898 | **`MinBtn` / `MaxBtn` / `CloseBtn`** | window chrome — **resolves the min/max/close ordering gap** |
| +0x9e0 | **`MainVolSlider`** | master VOLUME (← main+0xc78; cmd target `0x40000000`) |
| +0x9f8 | **`MetronomeBtn`** | metronome (← main+0xcf0) |
| +0xa00 | **`LoopRecordBtn`** | loop recording (action: `Options…`; not value-bound) |
| +0xa08 | **`BlendRecordBtn`** | blend recorded notes (action) |
| +0xa10 | **`PrecountBtn`** | countdown before recording (← main+0xd10) |
| +0xa18 | **`StartOnInputBtn`** | wait for input to start playing (← main+0xd28) |
| +0xaa8 | **`KbToMIDIBtn`** | typing-keyboard→MIDI-out (← main+0xc00) |
| +0xab0 | **`AutoScrollBtn`** | auto-scroll during playback (← main+0x2140) |
| +0xab8 | **`StepEditBtn`** | step edit (action) |
| +0xac0 | **`EnableGroupsBtn`** | enable channel groups (action) |
| +0xaf8 | `AddStuffBtn` | add-channel popup |
| +0xb00 | `ParamCtrlBigBtn` | quick-tweak multilink (shell §4.5) |
| +0xb08 / +0xb10 / +0xb18 | `SnapPanel` / `SnapLabel` / **`SnapSelect`** | snap (matches shell §3) |
| +0xb28 | **`MainPitchSlider`** | master PITCH (← main+0xa78; cmd target `0x40000002`) |
| +0xb40 | `DownloaderBtn` | — |
| +0xb48 | **`MultiLinkBtn`** | multilink to controllers (← main+0x1348) |
| +0x878 | `NewMainMenu` | menu bar (re/16) |

### The FormCreate bind table (widget → main-form value object) — 1:1, authoritative
`FUN_005d0a30(toolbar+OFF, *(mainForm+VAL))` in `TToolbarForm.FormCreate@0xcb7f90`, in call order:

| toolbar (control) | mainForm value | meaning |
|-------------------|---------------:|---------|
| +0x838 `TimeLabel`       | +0x1c50 | time/position display value |
| +0x778 `TempoSelect`     | +0xa78  | tempo (BPM×1000) value (shared) |
| +0xaa8 `KbToMIDIBtn`     | +0xc00  | typing-kbd→MIDI toggle model |
| +0xab0 `AutoScrollBtn`   | +0x2140 | auto-scroll toggle model |
| +0xb48 `MultiLinkBtn`    | +0x1348 | multilink-to-controllers toggle model |
| +0x9e0 `MainVolSlider`   | +0xc78  | master volume value (init −1.0) |
| +0xb28 `MainPitchSlider` | +0xa78  | master pitch value (init −1.0; shares main+0xa78 object) |
| +0x9f8 `MetronomeBtn`    | +0xcf0  | metronome toggle model |
| +0xa18 `StartOnInputBtn` | +0xd28  | wait-for-input toggle model |
| +0xa10 `PrecountBtn`     | +0xd10  | countdown-before-record toggle model |
| +0x788 `RecBtn`          | +0x13d0 | record-arm value |
| +0x780 `StopBtn`         | +0x1920 | transport playing/stop value |

**Value-bound toggles** read/write their `mainForm+VAL` model object (write via the widget's
`vtbl[0x1f0]` setter, or set the model then repaint). **Action-bound toggles** (`StartBtn`/Play, `LoopRecordBtn`,
`BlendRecordBtn`, `StepEditBtn`, `EnableGroupsBtn`, the `View*Btn`s) are driven by VCL actions
(`OptionsMetronomeActionExecute`, `OptionsBlendRecordedNotesActionExecute`, `OptionsToggleMultiLinkActionExecute`,
… on `TShortcutsModule`/main form) rather than by a bound value.

### Corrections to prior docs (shell §3 / §4.5 guesses)
- The command-bound cluster is **not** "metronome/loop-mode/wait-for-input/blend/song-vs-pat". Actual value-bound
  set = **KbToMIDI, AutoScroll, MultiLink, Metronome, StartOnInput(wait), Precount(countdown), Rec, Stop** plus
  the two master sliders. **Loop/Blend/StepEdit are action-bound**, not in this bind list.
- **Play vs Stop pinned:** `StopBtn@+0x780` (value-bound ← main+0x1920) and `StartBtn@+0x790` (Play, action-bound)
  are **separate** buttons — the earlier "Play/Stop most likely +0x780" is now exact.
- **Vol vs Pitch pinned:** `MainVolSlider@+0x9e0` (←main+0xc78, target `0x40000000`), `MainPitchSlider@+0xb28`
  (←main+0xa78, target `0x40000002`). The `+0x9e0`/`+0xb28` overlap ambiguity is resolved.
- **Poly field** = `PolyDigiLabel@+0x818` / `PolyLabel@+0x820`. **Min/Max/Close** = `+0x888`/`+0x890`/`+0x898`.

### G11 reuse notes (model-first, unchanged philosophy)
- **Toggle a value-bound switch:** set the `mainForm+VAL` model (e.g. metronome = main+0xcf0) then repaint the
  widget, or call the widget's `vtbl[0x1f0]` setter. For **action-bound** toggles, execute the corresponding VCL
  action (`Options*ActionExecute`) — that is the canonical FL path and also updates the menu-item checkmark.
- **Read a toggle state** = read `mainForm+VAL` (or the widget's `+0x3c0`). Master vol/pitch/shuffle remain best
  driven by `FL_DispatchCommand` with target ids `0x40000000`/`0x40000002`/`0x40000001` (shell §4.3).
- No source edits; no functions renamed on the shell side (the `TToolbarForm.*` handlers already carry RTTI names
  and are tagged `UI_win_shell`). The mapping is captured here + in the `0xcb7f90` bookmark.

---

## Status
G10 + G11 closed to HIGH confidence. Ghidra: 14 browser VMT-slot functions renamed `FLui_Browser_*`, 22 tagged
`UI_win_browser`; 2 bookmarks (`0x99b858` browser VMT, `0xcb7f90` toolbar binds); program saved. Residual (not
pursued, LOW value): the ~6 browser slots left as `FUN_` (roles noted above, semantics uncertain); base slots
0x00–0xc8 (inherited WP virtuals). Notable lead surfaced: `FLui_Browser_PopulateTreeViaProviders@0x9ae0c0`
exposes the provider `vtbl[0x18/0x30/0x38]` populate contract for G2.
