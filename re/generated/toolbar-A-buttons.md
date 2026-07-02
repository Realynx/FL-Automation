# Toolbar-A — the BIG SQUARE TOGGLE BUTTONS (metronome / typing-kb / wait / countdown / loop-rec / blend / step-edit)

Static Ghidra on `FLEngine_x64.dll`, image base `0x400000` (all addresses absolute/Ghidra; runtime =
`ghidra - 0x400000 + flEngineBase`). Builds on `re/16-toolbar-plugins-menu.md` and `re/ui-win-shell.md` (§4.5
transport-toggle cluster gap). **RESEARCH ONLY** — no runtime poked. All shell/button code is clean Delphi VCL +
the FL WP widget layer — NO VMProtect. This note RESOLVES the ui-win-shell "exact 1:1 assignment" gap and pins
the widget class, the toggle-state field, the render, and the full click→command chain.

## TL;DR / verdict (HIGH confidence)
- **Every square toolbar button is ONE class: `TQuickEditToolbarItem`** (VMT **`0xb50880`**; class-data record
  `0xb507b8`; skin descriptor **`forms.toolbar.button:quickbutton`**, keys `color1/color2/textcolor`). The SAME
  class backs the toggles (metronome…), the View buttons, and the momentary buttons (Cut/Copy/Paste/Undo/AddStuff).
- They are **DFM-streamed as published fields of `TToolbarForm`** (they are NOT created in code). The published
  field table (@`0xcb37e0`, classtab @`0xcb4aa3`, all these fields = class type-index **2** = `TQuickEditToolbarItem`)
  gives the exact field offset of each button (§1). `TToolbarForm.FormCreate@0xcb7f90` then **binds** the value-linked
  ones to a main-form value object via `FUN_005d0a30(btn, valueObj)` (sets `btn+0xdc`).
- **Toggle/"lit" state = a single byte at `btn+0x492`** (down/checked/mode). The paint reads `+0x492` and picks the
  glyph COLOR from a state-indexed table, then draws the glyph char with the button's skin font (`btn+0x340`). It is
  a **color/frame swap by state, not a bitmap swap** (glyph char is constant; color = lit vs dim). Confirmed by
  `RecBtnPaintCaption` (§3).
- **Programmatic toggle = `FLbtn_SetToggleStateAndClick(btn, newState, fire)` @0x717e10** (was FUN_00717e10): if
  `btn+0x492 != newState`, it repaints (`vtbl[0x200]`) and, if `fire`, invokes the button's dynamic **click** handler
  (DMT index `0xffeb`, resolved via `FUN_0040ffe0(btn,0xffeb)`).
- **The option toggles' source-of-truth is a GLOBAL byte, not the button.** A dedicated setter (metronome
  `FLtoggle_SetMetronome@0x100efa0`, etc.) writes the global then fans out: pushes the button (`vtbl[0x200]` refresh)
  → checks the popup-menu mirror (`FLmenu_SetItemChecked@0x81dbb0`) → checks the `TShortcutsModule` action
  (`action->vtbl[0xe0]`) → sets the engine dirty flag (`FUN_00ef9690`: `DAT_0157f4f8 |= 0x100`).
- **Three equivalent ways to flip a toggle** (all converge on the setter/global): the toolbar button click, the
  `Options|…` menu item / keyboard shortcut (`TShortcutsModule.Options*ActionExecute`), and the global command bus
  `FLgl_GlobalCommandDispatch@0xef7b20` (op ids in §4).

---

## 1. The buttons — class, field offsets, bindings

`TToolbarForm` singleton = `*PTR_DAT_014aa4c8` (VMT 0xcb3458; desc `forms.toolbarform:wpform`). Its published-field
table (`vmtFieldTable` @ `0xcb37e0`: Word count=139, classtab ptr `0xcb4aa3`) declares every child as
`{Cardinal offset; Word classIndex; ShortString name}`. Class-index **2** → classtab[1] → **`TQuickEditToolbarItem`**
(VMT `0xb50880`). Parsed entries (the square buttons):

| DFM field name | `TToolbarForm+off` | bound value obj (FormCreate `FUN_005d0a30`) | role |
|---|---|---|---|
| **MetronomeBtn**    | **+0x9f8** | `mainForm+0xcf0` | metronome |
| **LoopRecordBtn**   | **+0xa00** | (not FUN_005d0a30-bound) | loop record |
| **BlendRecordBtn**  | **+0xa08** | (not bound) | blend recorded notes |
| **PrecountBtn**     | **+0xa10** | `mainForm+0xd10` | countdown / recording precount |
| **StartOnInputBtn** | **+0xa18** | `mainForm+0xd28` | wait for input to start |
| ViewPLBtn/ViewSSBtn/ViewPRBtn/ViewSBBtn/ViewFXBtn | +0xa20/+0xa28/+0xa30/+0xa38/+0xa40 | — | show/hide Playlist/StepSeq/PianoRoll/Browser/Mixer (toggles) |
| ViewProjPickerBtn/ViewPlugPickerBtn/ViewTTBtn/ViewKbBtn | +0xa48/+0xa50/+0xa58/+0xa60 | — | pickers / typing-test / kb |
| SaveAsBtn/RenderBtn/EditAudioBtn/RecAudioBtn/SongSetBtn/HelpBtn/ArrangeBtn/UndoBtn | +0xa68…+0xaa0 | — | momentary |
| **KbToMIDIBtn**  | **+0xaa8** | `mainForm+0xc00` | **typing keyboard → piano** |
| **AutoScrollBtn**| **+0xab0** | `mainForm+0x2140` | auto-scrolling |
| **StepEditBtn**  | **+0xab8** | (not bound) | step edit |
| EnableGroupsBtn  | +0xac0 | (not bound) | enable channel groups |
| **CtrlKeyBtn / AltKeyBtn / ShiftKeyBtn** | +0xac8 / +0xad0 / +0xad8 | — | focus-passthrough modifier buttons (re/ui-win-shell §4.5 CtrlKeyBtn) |
| CutBtn/CopyBtn/PasteBtn/AddStuffBtn | +0xae0/+0xae8/+0xaf0/+0xaf8 | — | momentary |
| (ParamCtrl…) | +0xb00… | — | quick-tweak |

Note: `FUN_005d0a30(btn, valueObj)` = `btn+0xdc = valueObj` + register hover/hint (re/ui-win-shell §2.2). The
value-linked toggles (Metronome/Precount/StartOnInput/KbToMIDI/AutoScroll, + Rec `+0x788`, Play/Stop `+0x780`) read
their bound value object for display; the self-toggle buttons (LoopRecord/Blend/StepEdit) rely on `btn+0x492` only.

## 2. Widget class VMT `TQuickEditToolbarItem` @ `0xb50880` (WP control base)
`class-data record = 0xb507b8` (holds vmtClassName "TQuickEditToolbarItem"@0xb5074e, instanceSize etc.); the actual
**vtable of code pointers begins at `0xb50880`** (vmt[0]=`0x40f9b0`, the shared FL-WP base — same first slots as
`TToolbarForm`). Slot map (byte offset from `0xb50880`), cross-checked vs re/16:
| vtbl off | fn | meaning |
|---|---|---|
| 0x138 | `0x5e1550` | **SetParent** (focus-preserving override) — matches re/16 SetParent=vtbl[0x138] |
| 0x188 | `0x802b80` | SetBounds (re/16 vtbl[0x188]) |
| 0x1f0 | `0x5d88c0` | fires the value-change callback TMethod at `btn+0x420`/`+0x428` |
| 0x200 | `0x5d89e0` | **RecreateWnd + repaint** (recurses children, EnumChildWindows, calls vtbl[0x210]); takes NO state arg — used as a generic "refresh" |
| 0x208 | `0x5d86c0` | DestroyWindow (DestroyHandle) |
| 0x210 | (repaint) | invalidate/paint |

The setters call `btn->vtbl[0x200](btn, state)`; **the `state` arg is ignored by `0x5d89e0`** — the call only
forces a refresh so the button re-reads its state and repaints (`get_function_variables` shows 0 formal params).

## 3. Toggle STATE + how "lit" RENDERS
- **State byte = `btn+0x492`** (down / checked / mode). Read by the command bus for Rec/LoopRec/Blend/StepEdit
  (`*(btn+0x492)`), by `FLbtn_SetToggleStateAndClick`, and by the paint. (re/ui-win-shell already noted "RecBtn
  ctrl+0x492 = rec mode" — it is the general toggle-state field of `TQuickEditToolbarItem`.)
- **Render (concrete example `TToolbarForm.RecBtnPaintCaption@0xcbbb70`):**
  ```
  state = *(btn+0x492);
  color = (state==0 && recArmBlink) ? 0xE4E0D8 : DAT_013f2a24[state*4];   // state indexes a color table
  glyphText = FUN_005d0a70(btn);                                          // the button's glyph/caption char
  FUN_006b3600(canvas=*(btn+0x304)+0xa0, skinFont=btn+0x340, glyphText, rect, 0x10006A, color|0xFF000000, 0x110);
  ```
  So the **glyph is a character drawn with the skin glyph FONT at `btn+0x340`**, and the **lit look = a COLOR chosen
  from a state-indexed table** (dim when off, bright when on). Not a bitmap swap.
- **Glyph/skin source:** these controls are skinned as **`forms.toolbar.button:quickbutton`** (`color1`,`color2`,
  `textcolor` @0xb73b24/0xcb4da4). The Start button uses `forms.toolbar.toptoolbar.settingspanel.startbtn.*`. The
  toggle glyph char itself comes from the skin glyph font (P_ILGlyphs-family) loaded into `btn+0x340`; the button's
  caption/glyph is set the re/13/14 way (`FUN_005d0ae0(btn, …)`). The generic class paint mirrors RecBtn: read
  `+0x492` → pick lit/dim color → draw the glyph.
- **Source of truth for the option toggles is a GLOBAL byte** (not `+0x492`). The button's visual is kept in sync by
  the setter (§4) calling `vtbl[0x200]` (refresh) so the button re-reads and repaints.

## 4. Click / command binding — three converging paths

### 4a. State globals + dedicated setters (renamed this pass)
| toggle | state global (`*PTR_…`) | setter (config `*(0x14abca8)+0x7c` vtbl slot) | pushes to button |
|---|---|---|---|
| Metronome            | `*0x14a8be8` (char)      | **FLtoggle_SetMetronome@0x100efa0** (vtbl[0x40]) | toolbar+0x9f8 |
| Loop record          | `*0x14a83f8` (char)      | **FLtoggle_SetLoopRecord@0x100f510** | toolbar+0xa00 |
| Recording precount   | `*0x14a83b0` (char)      | **FLtoggle_SetPrecount@0x100f580** (vtbl[0x38]) | toolbar+0xa10 |
| Start on input (wait)| `*0x14ac368` (char)     | **FLtoggle_SetStartOnInput@0x100f610** (vtbl[0x30]) | toolbar+0xa18 |
| Blend recorded notes | `*0x14a9818` (char)     | **FLtoggle_SetBlendRecordedNotes@0x100f4a0** | toolbar+0xa08 |
| Typing kb → piano    | `*0x14abad0` (uint&1)   | **FLtoggle_SetTypingKbToPiano@0x100f770** (vtbl[0x48]) | toolbar+0xaa8 |

Each setter (canonical shape = `FLtoggle_SetMetronome`):
```
*stateGlobal = newState;
(*(toolbar+BTN))->vtbl[0x200]((toolbar+BTN), newState);          // refresh button (btn re-reads → repaint lit)
FLmenu_SetItemChecked(*(toolbar+MENUITEM), newState);           // tick the transport right-click popup mirror (Win32 CheckMenuItem)
(*(shortcutsMod+ACT))->vtbl[0xe0]((shortcutsMod+ACT), newState);// tick the Options-menu action (checkmark)
FUN_00ef9690();                                                 // DAT_0157f4f8 |= 0x100  (engine "apply transport opts" dirty flag)
```
`shortcutsMod = *0x14a83e0`; action offsets: Metronome `+0x290`, TypingKb `+0x288`, StartOnInput `+0x2a0`, Precount
`+0x298`, Blend `+0x2a8`, LoopRecord `+0x2b8`. `config = *0x14abca8`, sub-object `*(config+0x7c)` carries all the
option setters as vtbl methods (also skin toggles vtbl[0x58]/0x60/0x68 per re/ui-win-shell §4.7).

### 4b. Menu item / keyboard shortcut → action (`TShortcutsModule`)
`Options|Metronome` etc. run the published action Execute, which just calls the config setter with the flipped value:
- `OptionsMetronomeActionExecute@0xe45df0` → `config7c->vtbl[0x40](*0x14a8be8 == 0)`  (= FLtoggle_SetMetronome)
- `OptionsTypingKbToPianoActionExecute@0xe46230` → `config7c->vtbl[0x48](*0x14abad0 ^ 1)`
- `OptionsRecordingPrecountActionExecute@0xe45f40`, `OptionsStartOnInputActionExecute@0xe461c0`,
  `OptionsLoopRecordActionExecute@0xe45d90`, `OptionsBlendRecordedNotesActionExecute@0xe45be0`,
  `OptionsStepEditActionExecute@0xe461f0`, `OptionsAutoScrollingActionExecute@0xe45b70`,
  `OptionsEnableGroupsActionExecute@0xe45c10`.
- The matching `…ActionUpdate@0xe47d20..0xe47ed0` set the action's checkmark = the state global (`action+OFF ->
  vtbl[0xe0](*stateGlobal)`), i.e. the menu checkmark mirrors the same global. Python readers confirm the globals:
  `FLpy_ui_isMetronomeEnabled@0xe14660`, `FLpy_ui_isPrecountEnabled`, `FLpy_ui_isStartOnInputEnabled`, etc.

### 4c. Global command bus `FLgl_GlobalCommandDispatch@0xef7b20(op, value, mode, flags)`
The transport/global op dispatcher (safe scalar-only entry). Toggle ops:
| op | action | how |
|---|---|---|
| 0x6e | Metronome | `config7c->vtbl[0x40](*0x14a8be8 == 0)` |
| 0x6f | Start-on-input (wait) | `config7c->vtbl[0x30](*0x14ac368 == 0)` |
| 0x73 | Recording precount (countdown) | `config7c->vtbl[0x38](*0x14a83b0 == 0)` |
| 0x70 | Blend recorded notes | `FLbtn_SetToggleStateAndClick(*(toolbar+0xa08), !*(btn+0x492), 1)` |
| 0x71 | Loop record | `FLbtn_SetToggleStateAndClick(*(toolbar+0xa00), …)` |
| 0x72 | Step edit | `FLbtn_SetToggleStateAndClick(*(toolbar+0xab8), …)` |
| 0x0c / 0x11 | Record | `FLbtn_SetToggleStateAndClick(*(toolbar+0x788), !recmode, 1)` |
| 0x0b | Play/Stop | via `*(toolbar+0x780)` / `FUN_010c3c30` |
| 0x0f | (transport stop btn) | `*(toolbar+0x770)` |
So the "click a toggle" primitive = `FLbtn_SetToggleStateAndClick(btn, desired, fire)`; the option toggles can also
be driven "model-first" straight through the setter/config vtbl.

### 4d. What a real button CLICK does
The class routes a click through its **dynamic "click" method (DMT index `0xffeb`)** (see
`FLbtn_SetToggleStateAndClick`, which invokes exactly `FUN_0040ffe0(btn,0xffeb)`), and/or the onClick TMethod at
**`btn+0x1e4`(code)/`+0x1ec`(data)** (re/16). For the option toggles the effect is the action Execute (§4b) →
config setter → global + fan-out; the button's `+0x492`/bound value then re-syncs and repaints lit.

## 5. `TQuickEditToolbarItem` field map (for replicating a square toggle button)
| off | type | meaning |
|----:|------|---------|
| +0x18  | i32 | command/target id (FL_DispatchCommand target; used by value-dispatch controls) |
| +0x34  | u16 | WP control flags (bit1=has-parent, bit4, etc.) |
| +0xa4  | u32 | WP state flags (0x200 recreate-guard, 0x2000 no-focus, 0x40000000) |
| +0xa0  | u16 | auto control id |
| +0xdc  | ptr | **bound value object** (set by `FUN_005d0a30`; drives display + hover-hint for value-linked toggles) |
| +0x304 | ptr | WP skin/paint ctx (canvas = `*(+0x304)+0xa0`; UI scale `*(+0x304)+0xb4`) |
| +0x340 | ptr | **skin glyph FONT** (glyph char rendered from here; color chosen by state) |
| +0x420 / +0x428 | ptr | value-change callback TMethod (fired by vtbl[0x1f0]) |
| +0x45c | HWND | the control's window handle |
| +0x478 | i32 | (paint metric; used in Rec press-depth calc) |
| **+0x492** | **u8** | **TOGGLE / down / checked / mode state — the "lit" flag the paint reads** |
| +0x1e4 / +0x1ec | ptr | **onClick TMethod** code/data (re/16) |
| +0x2bc / +0x2c4 | ptr | event (OnChange/OnClick) callback TMethod (re/ui-win-shell §2.1) |
| +0x49c / +0x4a4 | ptr | custom OnPaint callback code/data (0 ⇒ class default paint, which reads +0x492) |
| +0x584 / +0x58c | ptr | (toolbar-SECTION self-layout TMethod — only on section class `&PTR_FUN_0090e008`, re/ui-win-shell §4.6) |
| vtbl[0x138] | | SetParent · vtbl[0x188] SetBounds · vtbl[0x200] Recreate/refresh · vtbl[0x210] invalidate |

**Recipe to add our own square toggle** (reuse the proven re/13/14/16 toolkit; all on FL UI thread):
1. `btn = FLwp_CreateButtonControl@0xF0DDB0()` (creates the `quickbutton`-class WP control = this family).
2. Caption/glyph char: `FUN_005d0ae0(btn, L"<glyph>")`; give it the toolbar skin font on `btn+0x340` (P_ILGlyphs).
3. Set hint: caption-hint string with the `|` FL hint format (§6).
4. Parent into a FIXED toolbar panel (NOT the customizable `+0x9c0` area — re/ui-win-shell §4.9): `SetParent`
   vtbl[0x138] into `toolbar+0x760` (or `mainForm+0xb10` top band); `SetBounds` vtbl[0x188]; realize `FUN_005ceef0`;
   render/invalidate.
5. Initial lit state: set `btn+0x492 = 0/1` and repaint (or call `FLbtn_SetToggleStateAndClick(btn, state, 0)`).
6. onClick: point `btn+0x1e4`(code)/`+0x1ec`(data) at our RWX thunk; in the thunk flip `btn+0x492`, repaint, and do
   our action. (To *reflect* an FL global instead, mirror the §4a setter shape: on state change set `+0x492` +
   repaint.) The class default paint already renders lit-by-color from `+0x492`, so no custom paint is required.
7. Teardown before FreeLibrary (main thread): clear `btn+0x1e4/+0x1ec`, `SetParent(0)`, destroy the control.

## 6. Tooltip / hint mechanism
Hover hints are FL `|`-prefixed hint strings shown via the WP hint system → toolbar **InfoLabel** (`*(toolbar+0xbd0)`,
`FLui_SetStatusHintCore@0x10ec570`) and the floating **TFLHintBarForm** (`FLui_SetStatusHintAndRefresh@0x10ec870`),
per re/ui-win-shell §5.4. Confirmed strings (format `|[^d][^^Shortcut] ^Caption`):
- `|^d^^Ctrl+M ^Metronome` @`0x137b33f`
- `|^d^^Ctrl+I ^Wait for input to start playing` @`0x137b312`
- `|^d^^Ctrl+P ^Countdown before recording` @`0x137b356`
- `|^d^^Ctrl+T ^Typing keyboard to piano keyboard` @`0x137b3a2`
- `|Loop recording` @`0x136d95a`
(`^d` = the "disabled/greyed" formatting flag, `^^…` = shortcut display, `^…` = the caption text.) Set the control's
hint to a like-formatted string; FL renders + auto-clears it (1500-tick timer, re/ui-win-shell §5.4).

## 7. Confidence + open items
- **HIGH:** button class `TQuickEditToolbarItem` (VMT 0xb50880) + it being the universal toolbar-item class; the
  per-button field offsets (from the DFM field table + cross-checked against each setter's `toolbar+OFF`); the state
  byte `+0x492` + color-by-state render + skin `quickbutton`; the state globals + setters (renamed) + fan-out shape;
  the 3 click paths + command-bus op ids; the hint strings/format. All decompiled + cross-checked.
- **MED / needs a live confirm:** (a) exact per-glyph skin char / image for each toggle (glyph char comes from the
  skin font `btn+0x340`; individual glyph codepoints not dumped — read live or from the skin). (b) Whether the
  value-linked toggles' `+0x492` is kept identical to the global on every path, or the paint reads the bound value
  object (`+0xdc`) instead for those specific buttons — both end at the same lit/dim result; confirm on the live
  MetronomeBtn if pixel-exact mirroring is needed. (c) The DMT `0xffeb` click handler body (the class's click
  message) was not decompiled — only its invocation is pinned.
- **Runtime gap (low risk, all reachable from globals):** live `*PTR_DAT_014aa4c8` (toolbar), `*0x14a83e0`
  (TShortcutsModule), `*0x14abca8` (config). A sibling verifies a toggle via the raw bridge (per
  minimax-LLM-flaky-toolcalls note), e.g. call `FLgl_GlobalCommandDispatch(0x6e, 1, 0, 8)` for metronome, or set the
  global + `FLtoggle_SetMetronome`.

## 8. Ghidra annotations made this pass (static, non-destructive)
Renamed: `FUN_0100efa0→FLtoggle_SetMetronome`, `FUN_0100f510→FLtoggle_SetLoopRecord`,
`FUN_0100f580→FLtoggle_SetPrecount`, `FUN_0100f610→FLtoggle_SetStartOnInput`,
`FUN_0100f4a0→FLtoggle_SetBlendRecordedNotes`, `FUN_0100f770→FLtoggle_SetTypingKbToPiano`,
`FUN_00717e10→FLbtn_SetToggleStateAndClick`, `FUN_0081dbb0→FLmenu_SetItemChecked`. (No prototypes/data changed.)

## 9. Key addresses (quick reference, ghidra / base 0x400000)
| what | addr |
|------|------|
| Toolbar form singleton | `*PTR_DAT_014aa4c8` (VMT 0xcb3458) |
| Toolbar.FormCreate (binds buttons) | `0xcb7f90` |
| **Button class `TQuickEditToolbarItem` VMT** | **`0xb50880`** (class-data `0xb507b8`, name @0xb5074e) |
| Toolbar published-field table / classtab | `0xcb37e0` / `0xcb4aa3` |
| bind widget↔value | `FUN_005d0a30` (btn+0xdc = value) |
| **toggle state byte** | **`btn+0x492`** |
| programmatic toggle+click | `FLbtn_SetToggleStateAndClick@0x717e10` |
| metronome setter / global | `FLtoggle_SetMetronome@0x100efa0` / `*0x14a8be8` |
| loop-rec setter / global | `FLtoggle_SetLoopRecord@0x100f510` / `*0x14a83f8` |
| precount setter / global | `FLtoggle_SetPrecount@0x100f580` / `*0x14a83b0` |
| start-on-input setter / global | `FLtoggle_SetStartOnInput@0x100f610` / `*0x14ac368` |
| blend setter / global | `FLtoggle_SetBlendRecordedNotes@0x100f4a0` / `*0x14a9818` |
| typing-kb setter / global | `FLtoggle_SetTypingKbToPiano@0x100f770` / `*0x14abad0` |
| menu-item checkmark helper | `FLmenu_SetItemChecked@0x81dbb0` |
| engine "apply opts" dirty flag | `FUN_00ef9690` (`DAT_0157f4f8 |= 0x100`) |
| global command bus | `FLgl_GlobalCommandDispatch@0xef7b20` (ops: 0x6e metro,0x6f wait,0x70 blend,0x71 looprec,0x72 stepedit,0x73 precount) |
| Options actions module (TShortcutsModule) | `*0x14a83e0`; Execute @0xe45b70..0xe46230, Update @0xe47d20..0xe47ed0 |
| config object (option setters vtbl) | `*(0x14abca8)+0x7c` |
| example toggle paint | `TToolbarForm.RecBtnPaintCaption@0xcbbb70` (reads +0x492 → color table `DAT_013f2a24`) |
| button toolkit create (fallback) | `FLwp_CreateButtonControl@0xF0DDB0` (re/16) |
| skin descriptor | `forms.toolbar.button:quickbutton` (color1/color2/textcolor) |
