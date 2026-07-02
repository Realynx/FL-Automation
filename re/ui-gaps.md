# FL UI RE — COMPLETENESS CRITIC + gap map (Wave 3)

Critic pass over the Wave-1 framework docs (re/ui-01..06) + Wave-2 concrete-window docs (re/ui-win-*).
Goal: find FL UI code/behaviour **not yet RE'd/renamed/tagged** and drive the high-value gaps to closure so
the UI is comprehensively covered for **reuse / #80** (native FL UI + window embed/docking). Static Ghidra on
`FLEngine_x64.dll`, image base `0x400000` (all addrs absolute/Ghidra; runtime = `ghidra - 0x400000 +
flEngineBase`). RE only — no source edits.

## How the gaps were enumerated
1. Read every UI doc (ui-01..06, ui-win-{shell,channelrack,mixer,eventedit,browser,dialogs}) and harvested each
   doc's own "Gaps / open items" section + every inline "(gap)"/"to confirm"/"unconfirmed".
2. Checked Ghidra tag coverage (`list_function_tags`): 12 UI tags, ~825 functions tagged —
   `UI_wpcore` 27 · `UI_controls` 63 · `UI_forms` 86 · `UI_skin` 79 · `UI_input` 49 · `UI_layout` 46 ·
   `UI_win_browser` 33 · `UI_win_chanrack` 44 · `UI_win_dialogs` 21 · `UI_win_eventedit` 163 ·
   `UI_win_mixer` 128 · `UI_win_shell` 66.
3. Spot-probed the binary to pin concrete anchor addresses for the top gaps (results in the table below).

The framework + all six concrete windows are catalogued at HIGH confidence. The gaps below are the
**residual** un-RE'd surfaces — mostly DFM/resource-created control classes (no direct ctor xref) and a few
large dispatcher bodies mapped by call-graph but not line-by-line.

---

## Prioritized gap list

Legend — disposition: **FILL** = a Wave-3 gap-filler sub-agent is taking it (doc `re/ui-gap-<topic>.md`);
**REMAIN** = catalogued, lower value, left for a later pass.

| # | gap | where (anchors) | value-for-reuse | prio | disposition |
|---|-----|-----------------|-----------------|------|-------------|
| G1 | **Channel-rack back-grid (BackWP) class + step-cell paint + left-click step-toggle** | back-grid ctrl = `*(form+0x7a8)` on `TStepSeqForm` (VMT 0xf41a00). Handlers known: `BackWPMouseDown@0xF47A30`→`FLui_ChanRack_GridPanBegin@0xF47AE0` (right-drag pan only), `FLui_ChanRack_BackGridHintDispatch@0xF47990`. Metrics: cell-w `*0x14AB898`, row-h `*0x14A8C48`, content-w `*0x14AB940`, visible-steps `DAT_0141e584`, beat-group `*0x14A9368`. Class is DFM-created → no direct ctor xref. | **Render an FL step grid in our own UI**: the cell-blit + the left-click toggle are THE missing piece for a native channel-rack step sequencer (#80 reuse). | HIGH | **FILL** → `re/ui-gap-chanrack-backgrid.md` |
| G2 | **TQuickTree provider node-emit contract + node allocator** | providers @ `browser+0x2ec`; `FLbrz_PopulateTreeFromTab@0x9b8e10` calls each `provider->vtbl[0x70](provider,tree,…)`. ProvideChildren = tree `vtbl[0x6f0]`; InitNode = `vtbl[0x6f8]`. Node struct: `+0x18` parent · `+0x28` next-sib · `+0x30` first-child · `+0x48` payload (caption via payload->vtbl[0x80]). Allocator unknown. | **Populate FL's own tree natively** (browser tabs, any tree widget) instead of hosting a separate list — the one piece blocking native tree content (browser §10/§12). | HIGH | **FILL** → `re/ui-gap-tree-provider.md` |
| G3 | **Docking internals: `FLui_Dock_RepositionHostedWindow@0x114b810` + main-form L/R dock sub-sites `mainForm+0xb38/+0xb40`** | `FLui_Dock_WindowSetAttached@0x1228cd0` calls `FLui_Dock_RepositionHostedWindow@0x114b810` (now pinned). Dock host = `*(mainForm+0xb18)`; host vtbl[0xe0]=dock origin, vtbl[0xe8]=rect. `FormResize@0x10ca8f0` realigns `mainForm+0xb38/+0xb40` via `FLui_Layout_RealignChildRegion`. | **#80 window embed/docking** — the exact reposition math + the left/right dock sub-sites are the missing internals for true workspace-embed of our window. | HIGH | **FILL** → `re/ui-gap-dock-reposition.md` |
| G4 | **Popup/context-menu WINDOW rendering (`TQuickPopupMenuWindow`) + floating hint-bar rendering (`TFLHintBarForm`) + drag cursors** | menu window class methods: `TQuickPopupMenuWindow.FormGesture@0x7136a0`, `.ScrollBarChange@0x711640`, `.ScrollBarSetKnobWidth@0x7116c0` (find class VMT + FormCreate/paint/item-layout). Hint bar: `TFLHintBarForm` singleton `DAT_0157c980`, `FormPaint@0xb65730`, `FLui_Hint_BarRefresh@0xb654a0`; string grammar `|` / `^^` (ui-05 §6). Drag cursors: set during the per-window DragOver handlers (ui-05 §7). | The menu/hint **rendering** surfaces are explicitly un-RE'd (re/16/23 covered the menu MODEL, not the popup window paint). Needed to render our own skinned menus/hints + correct drag cursors. | MED-HIGH | **FILL** → `re/ui-gap-popup-hint-render.md` |
| G5 | Mixer per-strip control offsets in the `track+0x13d0..+0x143x` region (delay/plugins/revstereo/flipy/stereosep/mute exact offsets) + strip **drag-reorder / drag-to-route** gesture path | `FLui_Mixer_BuildLayoutStrips@0x1183c80` (~107 KB, only headline ctrls walked); `SplitterBoxR{MouseDown@0x11a3e90…}` + the strip gesture handler | Full strip offset table + the reorder gesture for a faithful mixer clone; reorder is niche | MED | REMAIN |
| G6 | Editor `PianoPanelMouseMove@0xd6b8d0`/`Up@0xd64bb0` (~68 KB unified gesture body) line-by-line + `AUTracksPanelPaint@0xd7cec0` (~11 KB) + per-slot toolbar child identities | eventedit §3b/§5/§8; toolbar laid out by `FLui_Editor_LayoutToolbar@0xd3d150` | Exact note/clip move/resize/slice gesture semantics + PL render reference; draw path already mapped (§3c) so this is refinement | MED | REMAIN |
| G7 | `TQuickMIDIKb` family ctors (on-screen keyboard) | chain VMTs 0xb47788..0xb48d40; instantiated dynamically by host forms, no direct ctor xref; host wires NoteOn/Off/LayerChange/Mouse* TMethods | On-screen keyboard widget reuse; the event surface (host TMethods) is already documented (ui-02 §7.2) | LOW-MED | REMAIN |
| G8 | `TQuickTabSelector` programmatic **tab-add** | ui-02 §7.4 — no standalone add-tab fn; pager auto-populates from a sheet/descriptor list | Adding a tab to a generic pager; browser tab-add already solved via `FLbrz_AddTabCloneOfSource@0x9ac910` | LOW | REMAIN |
| G9 | `TQuickPaintBox` OnPaint TMethod offset (exact slot) | ui-02 §7.1 "exact paint-TMethod offset to live-confirm"; paint slot likely `+0x49c/+0x4a4` (cf. toolbar paint pairs, shell §2.1) | Wiring a custom owner-draw surface; create path already known | LOW | REMAIN (note: candidate `+0x49c/+0x4a4` — confirm) |
| G10 | `TVirtualDataBrowser` vtbl 0x99b858 remaining slot meanings (beyond 0xd0/0xf0/0xf8/0x100/0x108/0x260/0x338) | browser §12 | Completeness of the browser-control contract; current slots cover the reuse paths | LOW | REMAIN |
| G11 | Shell transport-toggle cluster exact 1:1 (metronome/loop-mode/wait-for-input/blend/song-vs-pat at toolbar `+0xaa8/+0xab0/+0xa10/+0xa18/+0xb48`); Play/Stop vs Poly field | shell §3/§4.5 | Pinning each toolbar toggle to its model object; command-bus target ids already authoritative | LOW | REMAIN |

---

## Gaps being FILLED this wave (anchors for the sub-agents)

### G1 — Channel-rack back-grid paint + toggle  → `re/ui-gap-chanrack-backgrid.md`
- Resolve the **class of `*(form+0x7a8)`** (the BackWP step-grid control): find its VMT (via the form's
  published-method/field table, the DFM-stream site in `TStepSeqForm.FormCreate@0xF45870`, or by xref to the
  metrics globals it must read). Then RE its **WP paint** (msg `0xf` / `vtbl` paint slot — the cell blit that
  reads each channel's step data using `*0x14AB898`/`*0x14A8C48`/`*0x14AB940`/`DAT_0141e584`/`*0x14A9368`) and
  its **mouse handler** (the LEFT-click step toggle + drag-paint). Rename `FLui_ChanRack_BackGrid_*`,
  tag `UI_win_chanrack`. Document the cell↔step mapping + the toggle data-op so we can re-implement it.

### G2 — TQuickTree provider node-emit  → `re/ui-gap-tree-provider.md`
- Trace one provider's `vtbl[0x70]` (from the `browser+0x2ec` array, reached via
  `FLbrz_PopulateTreeFromTab@0x9b8e10`) into the **node allocator** (the fn that mallocs a node and links
  `+0x18/+0x28/+0x30/+0x48`) and the **ProvideChildren** path (tree `vtbl[0x6f0]`). Document the emit contract
  (how a provider creates+attaches a child node + sets caption/payload/expandable flag `+0xa` bit 0x40).
  Rename `FLui_Ctl_Tree_*` / `FLui_Browser_Provider_*`, tag `UI_win_browser`.

### G3 — Dock reposition internals  → `re/ui-gap-dock-reposition.md`
- Decompile + RE `FLui_Dock_RepositionHostedWindow@0x114b810` (the hosted-window reposition math), and the
  main-form **L/R dock sub-sites** `mainForm+0xb38/+0xb40` (how `FormResize@0x10ca8f0` /
  `FLui_Layout_RealignChildRegion` size+place them, and whether a window can be docked left/right not just into
  the central host `*(mainForm+0xb18)`). Also pin the dock-host vtbl slots `[0xe0]` (origin) / `[0xe8]` (rect).
  Rename/tag `FLui_Dock_*` / `FLui_Layout_*`, tag `UI_layout`. Output a #80 dock recipe with exact offsets.

### G4 — Popup-menu + hint-bar rendering  → `re/ui-gap-popup-hint-render.md`
- **Context-menu window:** resolve `TQuickPopupMenuWindow`'s class VMT + FormCreate + the paint/item-layout
  path (start from `FormGesture@0x7136a0` / `ScrollBarChange@0x711640`; find xrefs to the class). Document how
  a menu item row is measured + drawn (the skinned popup), and how `FLmenu_ShowPopup` (re/16) spawns the window.
- **Hint bar:** RE `TFLHintBarForm.FormPaint@0xb65730` + `FLui_Hint_BarRefresh@0xb654a0` (singleton
  `DAT_0157c980`) — how the hint UStr (`DAT_015817d0`) is rendered, and enumerate the **`|` / `^^` hint-string
  grammar** (sections / value-format / icon markup).
- **Drag cursors:** which cursor is set during drag (the per-window DragOver handlers, ui-05 §7) + the cursor-id
  field (`form+0xa5c` in the editor; the WP cursor setter).
- Rename `FLui_Menu_*` / `FLui_Hint_*`, tag `UI_skin` (rendering) / `UI_forms` as appropriate.

---

## Remaining gaps after this wave (catalogued, lower value)
G5 mixer strip offset table + drag-reorder · G6 editor PianoPanelMouseMove/Up line-by-line + AUTracksPanelPaint
+ toolbar slot identities · G7 MIDIKb ctors · G8 TQuickTabSelector tab-add · G9 TQuickPaintBox OnPaint slot
(candidate +0x49c/+0x4a4) · G10 TVirtualDataBrowser vtbl residual slots · G11 shell transport-toggle 1:1.

These are refinements/niche: none block the documented reuse recipes. They are left for a later pass if needed.

## Assessment
See the closing report. After G1-G4 are filled, the only residual gaps are MED/LOW refinements (G5-G11) that
do not block reuse — i.e. FL UI coverage is comprehensive for the #80 / native-UI goal.
