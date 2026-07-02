# FL UI reverse-engineering — master index (overnight loop, started 2026-06-30)

GOAL: RE essentially ALL of FL Studio's UI in Ghidra (`FLEngine_x64.dll`, base 0x400000) — every UI element, RENAMED/labelled/plate-commented in Ghidra with a consistent convention, and DOCUMENTED in `re/ui-NN-*.md`, focused on **reuse** (exact create/use call sequences so we can build our own native FL UI). Hard line: run until comprehensive coverage. Complements the window-embed work (#80).

CONVENTION (all agents): rename funcs `FLui_<Area>_<Name>`, classes `TFL*`/keep `T*WPForm`; add a Ghidra function TAG `UI_<area>`; plate-comment classes + key funcs. Only rename within your area; note (don't rename) cross-area finds. Ghidra is ONE shared flaky instance — retry + HTTP API 127.0.0.1:8089 + save docs incrementally. RE only: NO source edits. Agents may spawn 2-4 disjoint sub-agents.

MECHANISM: coordinator launches waves of disjoint-region agents; completions re-invoke the coordinator → it updates this index + dispatches the next wave; a heartbeat wakeup covers stalls. Continue until every area below is ✅ and a completeness-critic finds no major gaps.

## ✅ STATUS: COMPLETE (2026-06-30) — FL UI comprehensively reverse-engineered
All waves done: W1 framework 6/6, W2 concrete windows 6/6, W3 critic+cookbook, W4 residual polish 4/4. ~825+ functions renamed + tagged across 12 `UI_*` tags (UI_wpcore/controls/forms/skin/input/layout/win_chanrack/win_mixer/win_eventedit/win_browser/win_shell/win_dialogs) + gap docs; ~18 `re/ui-*.md` docs; synthesis = **re/ui-reuse-cookbook.md**. Critic verdict: comprehensive for reuse/#80. Ghidra program saved.
### Morning to-dos (CODING — needs the user; not done overnight, RE-only):
1. **#80 embed** — reinstall to test the current build (TTestForm + HandleNeeded realize fix). BETTER host per RE = **TScriptDialog** (classRef `0xcf3888`), or use FL's own `FLui_WP_EmbedForeignWindow@0x7e3a60` into the content WP control (@form+0x700). Recipe: ui-reuse-cookbook R2 + debug-dev-loop.
2. **#25 in-FL chat input UNBLOCKED** — the WP focus mechanism is `FLui_Focus_RequestFocus@0x5ddeb0`; host a TQuickEdit on a SEPARATE WP form (so main-form FormShortCut/FormKeyDown don't eat space). Recipe: ui-reuse-cookbook R6 + ui-05-input.
3. Also pending reinstall: tool-calls-checkbox default→checked; installer follow-ups (stale .flbak, launch FL de-elevated).

## Wave 1 — UI framework  [DONE ✅ 6/6]
| area | doc | status |
|---|---|---|
| WP widget core + lifecycle | re/ui-01-wp-core.md | ✅ (base TFLWPControl; Delphi VMT pos+neg slots, ctor@vtbl+0x78=0x717a90; lifecycle create/parent/bounds/show/render(Invalidate vtbl+0x178)/msg(0xf)/destroy; fields parent+0x78/rect+0x90/value+0xc4; 27 funcs UI_wpcore) |
| Concrete control types catalog | re/ui-02-controls.md | ✅ (~35 classes; FLwp_CreateControl@0x717A90 + classRef(VMT+0x18) + descriptor@+0x328 + value@+0xc4 + event-TMethod map; 59 funcs UI_controls. Gaps: TQuickTree node API, tab-add, MIDIKb ctors) |
| Forms/windows catalog + chrome | re/ui-03-forms.md | ✅ (TCustomWPForm host + full hierarchy; ~90 concrete forms catalogued by class/VMT/classRef(VMT+0x18)/descriptor(+0x6e8)/FormCreate; chrome=BuildTitleBar/TitleBarColor/PaintBorder + caption text@+0x110 via FLwp_SetFormCaption; all dialogs MODAL via vtbl[0x2e8]; PR/Playlist/EventEdit = one TEventEditForm (mode@+0x160); embed-host=TScriptDialog (empty shell); 86 funcs UI_forms) |
| Skinning / rendering / theming | re/ui-04-skin.md | ✅ (paint canvas@+0x304, WM_PAINT pump, draw prims, descriptor/role system, theme color+fonts, reuse recipe; ~79 funcs UI_skin) |
| Input / events / focus / hints | re/ui-05-input.md | ✅ (WP focus mechanism RESOLVED — FLui_Focus_RequestFocus; resolves the parked space-capture blocker; mouse/keyboard/hover/hints/drag-drop + FormShortCut relationship) |
| Layout / docking / scrolling / sizing | re/ui-06-layout.md | ✅ (VCL-style explicit SetBounds(vtbl+0x188=0x802ef0)+anchor@+0xb3; vtbl[0x138]=0x5d0850 FLui_WP_SetParent [re/13 mislabel fixed]; dock=reparent into *(mainForm+0xb18) / WindowSetAttached@0x1228cd0; scroll obj@+0xd0; reuse recipes; ~46 funcs UI_layout) |

## Wave 2 — concrete windows  [DONE ✅ 6/6]
| area | doc | status |
|---|---|---|
| Channel Rack (TStepSeqForm) | re/ui-win-channelrack.md | ✅ (TStepSeqForm VMT 0xf41a00 *0x14A8BF8; 2 regions [strip list *(form+0x7a0) / back step-grid form+0x7a8]; strip build FLwp_BuildChannelRackControls@0xF0E330 [ctrls on chan+0x7bc..], arrange FLui_ChanRack_ArrangeChannels@0xF50340 [col-X table 0x0157f738]; layout chain + metrics globals; 25 renamed + 44 UI_win_chanrack. Gap: back-grid step paint/toggle in DFM control) |
| Mixer | re/ui-win-mixer.md | ✅ (TFXForm VMT 0x117b6b0 forms.mixer:wpform; track model g_MixerTrackArrayPtr 502×stride0x1474 [mute+0x18/solo+0x1a/arm+0x145c/sends+0x2e8/EQ+0xb8/FXslots+0x1324]; strip widgets forms.mixer.track.controls.*; builder FUN_01183c80; sel-track *(PTR_DAT_014a9d28); cmd-bus map↔re/12; 128 tagged+12 renamed UI_win_mixer. Gaps: some per-strip offsets, drag-reorder) |
| Editor (TEventEditForm: PianoRoll/Playlist/EventEdit) | re/ui-win-eventedit.md | ✅ (TEventEditForm VMT 0xd280f8, mode@+0xB00 [NOT +0x160]; shared canvas TFLEditorCanvas→TFLNoteGrid→TFLPlaylistGrid; draw pipeline + hit/hover resolver + BindDataSource@0xd3dc90; 163 funcs UI_win_eventedit, 38 renames, ~50 plate cmts. Gaps: PianoPanelMouseMove/Up, AUTracksPanelPaint, per-slot toolbars) |
| Browser internals | re/ui-win-browser.md | ✅ (TVirtualDataBrowser vtbl 0x99b858 *0x157ffb8; tree node model RESOLVED [virtual/data-source, no AddNode, lazy providers]; node struct+traverse/select/expand; tab system FLbrz_*; search/filter; drag-source OLE; hi-level nav API; 33 funcs UI_win_browser. Gap: provider node-emit contract) |
| Shell: toolbar + main form + transport | re/ui-win-shell.md | ✅ (toolbar form + transport/tempo/pattern/time/snap controls + panel layout/toggles/chrome/master + main-form workspace/dock-host/status-bar; ~70 funcs UI_win_shell + 9 FLui_Shell_* renamed; builds on re/16 menu RE) |
| Dialogs + plugin-window host + settings | re/ui-win-dialogs.md | ✅ (FL foreign-GUI embed = FLui_WP_SetParentWindow@0x5d8e70 / FLui_WP_EmbedForeignWindow@0x7e3a60, plugin host TBridgedEditorForm, content WP @form+0x700; TScriptDialog clean shell; Settings=TMIDIForm sheets; modal contract vtbl[0x2e8]/result==1/Free; 21 funcs UI_win_dialogs) |

## Wave 3 — gaps + synthesis  [DONE ✅]
| item | doc | status |
|---|---|---|
| Completeness-critic + gap-fill | re/ui-gaps.md (+ gap sub-docs) | ✅ (11 gaps; G1-G4 HIGH filled: chanrack back-grid TSSGrid paint+toggle, tree provider node-emit→native populate, dock-reposition +0xB38/+0xB40=L/R sites [#80], popup-menu+hint render+drag cursors; verdict COMPREHENSIVE for reuse/#80; G5-G11=optional polish→Wave 4) |
| UI reuse cookbook (synthesis) | re/ui-reuse-cookbook.md | ✅ (§0 ABI + 8 recipes: host window / #80 embed / controls / layout / skin / #25 focus / hints+drag / menus; funcs+offsets cited per source; concrete-vs-live-confirm map) |

## Wave 4 — residual gap polish (G5-G11, optional)  [DONE ✅ 4/4]
| item | doc | status |
|---|---|---|
| G5 mixer per-strip offsets + drag-reorder | re/ui-gap-mixer-strips.md | ✅ (full strip offset table +0x13d0..+0x1440 [fader/pan/sep/send/arm/plugins/mute/delay/flipy/revstereo/panel]; gestures traced; reorder/route=menu/kbd not drag; 6 renames UI_win_mixer) |
| G6 editor PianoPanelMouseMove/Up + AUTracksPanelPaint + toolbar slots | re/ui-gap-editor-detail.md | ✅ (gesture enum @canvas+0x400; AUTracksPanelPaint=strips not clips, track rec stride 0x114; toolbar visibility/order table; 12 renames UI_win_eventedit) |
| G7/G8/G9 controls: MIDIKb ctors, tab-add, PaintBox OnPaint | re/ui-gap-controls-extra.md | ✅ (PaintBox OnPaint=+0x398; MIDIKb ctor VMT+0x90 + OnNoteOn+0x3c8/Off+0x3d8; tab-add via caption list @selector+0x58c→RebuildTabs; 24 renames UI_controls) |
| G10/G11 browser residual vtbl + shell transport-toggle 1:1 | re/ui-gap-browser-shell-extra.md | ✅ (TDataBrowser VMT 91 slots decoded [vtbl[0x338] was OOR doc error]; full TToolbarForm transport/toggle field table [Metronome+0x9f8/Play StartBtn+0x790/Stop+0x780/Vol+0x9e0/Pitch+0xb28...]; 14 renames UI_win_browser + shell mapping) |

## Prior UI RE to build on
re/13-wp-custom-ui.md (WP widgets), re/14-chat-ui.md (forms/HWND@+0x2b0/focus), re/16 + re/23 (menus/categories), re/22-window-host{,-manager,-plan}.md (TCustomWPForm host, show/hide/dock, form factory).
