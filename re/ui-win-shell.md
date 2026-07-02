# UI-WIN-SHELL — the app SHELL: Toolbar (`TToolbarForm`) + Main form (`TFruityLoopsMainForm`) + transport

AREA (overnight UI-RE wave 2, "concrete windows"): the **application shell** — the top **Toolbar form** and every
widget on it (the menu bar [re/16], transport play/stop/rec, tempo, pattern selector, time/position display, snap,
shuffle, master vol/pitch, the meters/LEDs/CPU, the system min/max/close chrome, the customizable quick-edit
toolbar) **and** the **Main application form** as the workspace/window-host (toolbar dock, client area, docked
editor hosting, status/hint bar). Static Ghidra on `FLEngine_x64.dll`, image base `0x400000` (all addresses are
absolute/Ghidra; runtime = `ghidra - 0x400000 + flEngineBase`). Builds on re/16 (menu bar), re/23 (menu cats),
re/ui-03 (forms catalog), re/06 (layout/dock), re/22 (window host/manager), re/13/14 (WP widgets). **All shell
code is clean Delphi VCL + the WP layer — NO VMProtect.**

## TL;DR / verdict (HIGH confidence)
- The visible top strip is the **`TToolbarForm`** (VMT `0xcb3458`, classRef `0xcb3470`, descriptor
  `forms.toolbarform:wpform`, a `TVectorForm`). It is a **singleton at `*PTR_DAT_014aa4c8`**, created by the MAIN
  form during its `FormCreate` and **docked into the main form's top region** (`FUN_00cb5a20` = attach, renamed
  `FLui_Shell_AttachToolbarPanels`). **It carries the app's window chrome** (the real min/max/close buttons live on the
  toolbar, not the OS frame).
- The toolbar is a **densely-populated VCL form whose child widgets kept their Delphi published-method names** —
  so the entire control catalog is legible from the RTTI (TempoSelect*, PatSelect*, SongPosSlider*, TimeLabel*,
  Rec/Min/Max/CloseBtn, CPUBox, MixScope, MIDIInputLED, …). We did NOT have to guess widget identities.
- **Widgets are bound to main-form value objects** (the FL model) by **`FUN_005d0a30(ctrl, valueObj)`** which sets
  `ctrl+0xdc = valueObj` and registers mouse-enter. Reading/writing a widget = reading/writing its bound value
  object (or calling the widget's `vtbl[0x1f0](value,0)` setter). The whole bind table is in `TToolbarForm.FormCreate`
  (§2.2) — this is our map from toolbar field offset → FL model object.
- **Transport/tempo/pattern/songpos are settable from our bridge via the toolbar field offsets** (e.g. tempo =
  `(*(toolbar+0x778))->vtbl[0x1f0](bpm*1000, 0)`, exactly what `FL_PushTransportTempoToToolbar@0x1263ac0` does),
  but the *model-first* path (set the bound value object / dispatch the FL command) is cleaner — see §3/§4.
- **Reuse for adding OUR controls** (beyond the re/16 menu insertion): the toolbar lays out its children via
  **`FLui_Ctl_ControlBar_LayoutToolbars@0x910830`** (enumerates children, calls each child's layout callback
  `child+0x584(child+0x58c, bar)`); a child becomes part of the bar by being parented into the toolbar content
  panel (`toolbar+0x760`). See §4 for the panel layout + the customizable-toolbar (design-mode/presets) path,
  and §5 for how the **main form hosts/docks editor windows** (ties to #80 embed + docking).
- Confidence HIGH on structure/offsets/identities (all decompiled, names from RTTI). Runtime gap: obtaining the
  live toolbar/main-form pointers — both reachable from globals (`*PTR_DAT_014aa4c8`, `DAT_01581200`).

---

## 1. Shell identities + globals (quick reference, ghidra / base 0x400000)
| what | value |
|------|-------|
| Main form (TFruityLoopsMainForm) singleton | `DAT_01581200` (== `*PTR_DAT_014a8750`); VMT 0x1060840, cr 0x1060858, desc `forms.main` |
| Toolbar form (TToolbarForm) singleton | `*PTR_DAT_014aa4c8`; VMT 0xcb3458, cr 0xcb3470, desc `forms.toolbarform` |
| Toolbar.FormCreate | `0xcb7f90` (`TToolbarForm.FormCreate`) |
| Toolbar.FormDestroy | `0xcb8970` (frees news-timer global `DAT_0157e870`; child widgets freed by VCL) |
| Main.FormCreate | `0x10bcf70` (`TFruityLoopsMainForm.FormCreate`) |
| toolbar attach (dock into main) | `FLui_Shell_AttachToolbarPanels@0xcb5a20` |
| bind widget↔value object | `FUN_005d0a30` (ctrl+0xdc = value; cross-area, NOT renamed) |
| generic FL command dispatch (widget→cmd) | `FUN_00cb9950` = `FL_DispatchCommand(ctrl+0x18, ctrl+0x3c0, 4)` |
| toolbar children layout | `FLui_Ctl_ControlBar_LayoutToolbars@0x910830` |
| menu bar control "NewMainMenu" | `*(toolbar+0x878)` (re/16) |
| push tempo to toolbar widget | `FL_PushTransportTempoToToolbar@0x1263ac0` |

---

## 2. `TToolbarForm.FormCreate@0xcb7f90` — the construction recipe

The toolbar's child widgets are streamed from the DFM by the VCL constructor (`TVectorForm.Create`); `FormCreate`
then **wires** them: sets event/paint callbacks, configures fonts, builds the menu bar, and **binds each widget to
its FL model value object**. (Created/docked by the MAIN form — see §5.1.)

### 2.1 Widget event/paint callback model (toolbar WP widgets)
FL sets two TMethod-style callback pairs directly on the child widget object:
- **event (OnChange/OnClick)**: code `widget+0x2bc`, data(owner) `widget+0x2c4`.
- **paint (OnPaint)**: code `widget+0x49c`, data(owner) `widget+0x4a4`.
- value setter: `widget->vtbl[0x1f0](value, flag)`; current value typically `widget+0x3c0` (int) / `widget+0x3c4`.

FormCreate wires: `+0x7e8`→event `FUN_00cbbfd0`; `+0x9e0`→event `FUN_00cb9950` (cmd dispatch); `+0xb28`→event
`FUN_00cb9950`; `+0x778`→paint `TToolbarForm.TempoSelectPaint`. (The published `*Change/*Paint` handlers in the
RTTI are wired separately by DFM streaming.)

### 2.2 The widget→model BIND TABLE (`FUN_005d0a30(toolbar+OFF, *(mainForm+VAL))`)
This is the authoritative map from a toolbar field offset to the FL model object it displays/controls:

| toolbar field | bound to (mainForm + …) | identity (see §3/§4) |
|--------------:|-------------------------|----------------------|
| `+0x778` | `+0xa78` | **TempoSelect** (tempo; also paint=TempoSelectPaint) |
| `+0xb28` | `+0xa78` | second tempo-linked widget (cmd-dispatch event) |
| `+0x838` | `+0x1c50` | digit display (`P_DigitWheel` font) |
| `+0xaa8` | `+0xc00` | (region B/A to confirm) |
| `+0xab0` | `+0x2140` | (region B/A to confirm) |
| `+0xb48` | `+0x1348` | (region B/A to confirm) |
| `+0x9e0` | `+0xc78` | master slider (cmd-dispatch; main sets -1.0) |
| `+0x9f8` | `+0xcf0` | (region B/A to confirm) |
| `+0xa18` | `+0xd28` | (region B/A to confirm) |
| `+0xa10` | `+0xd10` | (region B/A to confirm) |
| `+0x788` | `+0x13d0` | (region B/A to confirm) |
| `+0x780` | `+0x1920` | (region B/A to confirm) |

Confirmed by handler decompiles: **`+0x778`=TempoSelect**, **`+0x7a8`=PatSelect (pattern)**, **`+0x7e8`=SongPosSlider**.

### 2.3 Other FormCreate work
- Fonts: loads `P_DigitWheel` into the `+0x838` display's skin font (`+0x340`), clones fonts across `+0x808/
  +0x810/+0x818`, scales per UI-scale `DAT_00cb8800`.
- Menu bar: `FLmenu_BuildBarFromActionList(toolbar+0x878, *(mainForm+0x760))` (re/16).
- **InfoLabel** (the toolbar hint/info text): `FLui_Ctl_Label_Create(&PTR…00cb3178,1,form)` → stored at
  `toolbar+0xbd0`, aligned, parented into `toolbar+0x848`, named `L"InfoLabel"`, positioned relative to
  `toolbar+0x850`'s bounds, font `P_Normal`/`P_NormalBold` by UI scale.

---

## 3. Transport / Tempo / Pattern / Time / Snap controls

All are `TToolbarForm` fields; handlers kept Delphi RTTI names (tagged `UI_win_shell`). WP value slot = `+0x3c0`,
set-value = `vtbl[0x1f0]`, font/skin = `+0x340`, paint canvas = `*(ctrl+0x304)+0xa0`.

| control | field | model / global | key handlers | read/set |
|---|---|---|---|---|
| **TempoSelect** | `+0x778` | val `mainForm+0xa78`; BPM source `*(PTR_DAT_014a9f40+0x10)`; `ctrl+0x3c0` = **BPM×1000** | `TempoSelectPaint@0xcb8b80` (draws `val/1000` . `val%1000`), `TempoSelectGetValue@0xcb8b30` (clamp ×1000), `TempoSelectRelease@0xcb8ef0` (offers "Restretch all channels") | push BPM: `(*ctrl[0x1f0])(ctrl, bpm×1000, 0)` = exactly `FL_PushTransportTempoToToolbar@0x1263ac0` |
| **PatSelect** (pattern) | `+0x7a8` | current pattern `PTR_DAT_014ab580`; `ctrl+0x3c0` = pattern idx | `PatSelectChange@0xcbb610` → `FLpat_SetCurrentPattern(ctrl+0x3c0)` (+ refresh CR/PR/playlist, set status hint), `PatSelectGetText@0xcbb950`, `PatSelectMouseDown/Up/DblClick/Release`, `PatBtnPaintCaption@0xcbb020`, `PatternPanelResize@0xcbbaf0` | set: `FLpat_SetCurrentPattern(idx)` (control repaints from the global — model-first) |
| **SongPosSlider** | `+0x7e8` | `ctrl+0x3c0` = song position; `ctrl+0x3bc` = drag-active; mode `PTR_DAT_014a8670` | `SongPosSliderChange@0xcbbfa0` → if dragging `FLui_Shell_StatusHintSongPos(mainForm,1)`, `SongPosSliderAdjustParams@0xcbbe50`, `SongPosSliderRelease@0xcbbff0` | read pos = `*(toolbar+0x7e8)+0x3c0` |
| **Time/position digits** | `+0x838` | val `mainForm+0x1c50`; font `P_DigitWheel` | `TimeLabelPaint@0xcb9060`, `TimeLabelMouseDown@0xcb8fe0`, `TimePanelVisibleChanged@0xcb9820`, `TimeSelectGetIncrement@0xcb8a90` | draws bar:beat + min:sec:frames; globals: bar `PTR_DAT_014a8130`, REC-IN countdown `PTR_DAT_014a7c78` ("REC IN n"), beat `PTR_DAT_014a9e98`; format toggles `PTR_DAT_014a9520`/`…80c8` ("B S T") and `PTR_DAT_014abbc8` ("M S FR") |
| **RecBtn** | `+0x788` | val `mainForm+0x13d0`; `ctrl+0x492` = rec mode | `RecBtnPaintCaption@0xcbbb70` | rec-arm blink `PTR_DAT_014a8630`; colour `&DAT_013f2a24[mode]` |
| **Play/Stop** | `+0x780` (probable) | val `mainForm+0x1920` (transport-playing state) | value/command-bound (no custom-paint method) | toggled via the transport command path / bound value object |
| **SnapSelect** | `+0xb18` | snap global `PTR_DAT_014ab758` | `SnapSelectChange@0xcbbd80` (writes global, applies to channels), `SnapLabelMouseDown@0xcbbd60` | label/icon `+0xb10`; value `3` = special ("(none)") |
| **Poly** | (poly ctrl) | per-pattern polyphony | `PolyDigiLabelMouseDown@0xcbbac0`, `PolyLabelDblClick@0xcbbb20` | — |
| **Shuffle/Swing** (TSDFeedWheel) | (master) | `FL_DispatchCommand(.., 0x40000001)` (0..128) | `TSDFeedWheelChange@0xcb8a70` | master swing wheel |

**Play/Stop & the transport-toggle cluster:** REC = `+0x788`; Play/Stop most likely `+0x780` (← `mainForm+0x1920`).
The remaining `FUN_005d0a30`-bound state controls `+0xaa8/+0xab0/+0xa10/+0xa18/+0xb48` (← `mainForm+0xc00/+0x2140/
+0xd10/+0xd28/+0x1348`) are the song/pattern-mode, loop-mode, wait-for-input, blend and **metronome** toggles —
each follows the identical *bound value object + `FL_DispatchCommand`* pattern; the exact 1:1 assignment is the one
remaining gap (resolved by fork A / by naming each value object). **Model-first reuse:** for tempo set the BPM value
object or mirror `FL_PushTransportTempoToToolbar`; for pattern call `FLpat_SetCurrentPattern`; for master vol/pitch/
shuffle call `FL_DispatchCommand` with `0x40000000`/`0x40000002`/`0x40000001`.

---

## 4. Toolbar panels / layout / toggles / chrome / master

All offsets are fields of the **TToolbarForm** instance (singleton `*PTR_DAT_014aa4c8`). The toolbar form carries
the app's window chrome and most non-menu widgets. `TToolbarForm.*` methods kept Delphi RTTI names (tagged, not
renamed).

### 4.1 Panel inventory
| panel | field / control | role |
|---|---|---|
| MenuPanel | `+0x870` container, `+0x9c0` cust. ctrl | hosts NewMainMenu strip (`+0x878`); `MenuPanelVisibleChanged`→`FUN_00b5d800` |
| MonitorPanel | master scope | `MonitorPanelResize`→`FUN_011cb1f0`; `VisibleChanged`→`TFruityLoopsMainForm.SwitchScopeTimer` |
| CPUPanel | `+0x800` chart, `+0x828`/`+0x7f8` box | CPU bar+chart; `CPUPanelDblClick`→system menu; `VisibleChanged`→`FLui_Shell_CPUPanelApplyVisible@0xcb7c30` |
| OnlineToolBar | `+0x9c8` panel, `+0x9d0` child | news/online buttons; visible flag `+0xa9` |
| SysBtnsPanel | `+0x888/+0x890/+0x898` btns | window chrome (min/max/close) + add/menu; `SysBtnsPanelMouseDown` guards design mode `(+0x9c0)+0x79` |
| NewsPanel | — | `NewsPanelTimerTimer` cycles news items |
| TimePanel / PatternPanel | `+0x7a8` patsel, time digits | region A (see §3) |

### 4.2 Meters / LEDs / boxes
- **CPUBoxPaint** `+0x828`/`+0x7f8`/`+0x808`; **CPUChartBoxPaint** `+0x800` (history buf `PTR_DAT_014a77d0`).
- **FpsMeasurePaintBoxPaint** → FPS to `*PTR_DAT_014ab980`.
- **MixScopeBoxPaint** + **MixVolumeterMouseMove** = master scope / "^Peak meter (master)" (master monitoring).
- **InfoBoxPaint** + **InfoLabel** `+0xbd0` (created in FormCreate, parented to `+0x848`, bounds from `+0x850`).
- **MIDIInputLEDPaint** (`+0x9e8` font, `+0x850` color) + **MIDISyncLEDPaint** (beat/sync, song-vs-pat via `PTR_DAT_014a8670`).
- **FlagBtnPaint** `+0xba0` (marker/flag); **OnlineBtnPaint** `+0xbbc` / `OnlineBtnClick`→`FUN_00fc6070(1)`.

### 4.3 Master section
- The master sliders dispatch via `FL_DispatchCommand(targetId = ctrl+0x18, value = ctrl+0x3c0, flags)`. **The
  real command is the target id (`ctrl+0x18`): `0x40000000` = master VOLUME, `0x40000001` = SHUFFLE/SWING,
  `0x40000002` = master PITCH.** The literal `flags` arg differs only cosmetically (`0x185` for vol, `0x3cd` for
  pitch & shuffle) — it is NOT the command id. Handlers: `MainVolSliderChange@0xcb9930`,
  `MainPitchSliderChange@0xcb9910`, `TSDFeedWheelChange@0xcb8a70` (shuffle; see §3). (All three carry plate comments.)
- Command-bound `+0x9e0` (←main+0xc78) and `+0xb28` (←main+0xa78) inited to **-1.0** in main FormCreate.
  NOTE: `+0xb28` shares the tempo value object (main+0xa78, same as TempoSelect `+0x778`); the precise toolbar
  field for vol vs pitch is not pinned 1:1 (gap) — but the **target ids above are authoritative** for the reuse path.

### 4.4 Window chrome buttons (toolbar = the borderless main window's title-bar controls)
- **MinBtnClick**→`SysMinMenuClick`; **MaxBtnClick**→`SysRestMenuClick` (mid-click MouseDown → `FUN_007fa370(-1)`+restore);
  **CloseBtnClick**→`SysCloseMenuClick`; **CloseBtnMouseDown** right-click → `FLmenu_ShowPopup(mainForm+0x1748)` (system menu).
- **SysBtnMouseEnter/Leave** toggle hover flag `+0xbd8` and invalidate `+0x888/+0x890/+0x898`.

### 4.5 Other buttons / toggles
- **ParamCtrlBtn / ParamCtrlBigBtn** = "Quick tweak" multilink: MouseDown→`FUN_00b69fa0(rect, last-event-id PTR_DAT_014aa7b8, leftClick)`; MouseEnter→hint `"Quick tweak %s"` via `FLgl_cmd_GetEventIDName`.
- **PasteBtnMouseDown** → `EEDupMenuClick`; **ViewSBBtnGesture** → `FLcr_OpenAddChannelPicker`.
- **CtrlKeyBtnMouseActivate** → returns activate-result `3` (no-activate) when WM `(PTR_DAT_014aa6e8)+0x139` set =
  focus-passthrough (typing-keyboard / Ctrl button so typed keys don't steal focus).
- Metronome / typing-kb / wait-for-input / countdown / loop-rec live in the command-bound value cluster
  `+0xaa8 +0xab0 +0xa10 +0xa18 +0xb48 +0x780 +0x788` (←main+0xc00/+0x2140/+0xd10/+0xd28/+0x1348/+0x1920/+0x13d0).
  Exact 1:1 = gap (resolve each value object).

### 4.6 Layout-callback mechanism — `FLui_Ctl_ControlBar_LayoutToolbars@0x910830`
`FUN_005dc9f0(bar)` pre-pass; if `!(bar+0x34 & 8)`, enumerate children (`FUN_005db950`=count, `FUN_005db970`=child);
per child: `FLui_WP_SetShowing(child, bar+0xa9)`; **if child is class `&PTR_FUN_0090e008` (toolbar-section) and
`child+0x584 != 0`, call its self-layout TMethod `(*child+0x584)(child+0x58c, bar)`.** So a toolbar SECTION carries
a layout callback at **`+0x584`(code)/`+0x58c`(data)** — the reuse hook for a self-arranging custom section.

### 4.7 Customizable ("quick-edit") toolbar
- Control = **`form+0x9c0`**; design-mode flag = `(+0x9c0)+0x79`. `EditToolbarsActionExecute` toggles it
  (`FUN_00b5fc10` enters design); `DesignModeChanged`/`CurrentPresetChanged`/`PresetDataChanged` manage **`.tpr`
  presets** ("Auto saved"), preset menu list at **`mainForm+0x2680`**. Global skin toggles via config
  `*(PTR_DAT_014abca8+0x7c)`: separators vtbl[0x58], flat-buttons vtbl[0x60], hint-bar vtbl[0x68]. Whole-toolbar
  hide/show = **`FLui_Shell_SetToolbarVisible@0x114a050`**.

### 4.8 Toolbar popups (item-insertion reuse, ties to re/16)
All via `FLmenu_ShowPopup(engine=mainForm+0x760, x, y, ctrl, root)`: AddStuffBtn root=`mainForm+0x1f00`;
ArrangeBtn=`+0x9a0`; toolbar right-click (`ToolbarMouseUp`)=`+0x1470`; CloseBtn right-click=`+0x1748`.

### 4.9 Implications for inserting OUR controls
1. **Two zones:** (a) FIXED widgets = TToolbarForm fields built once in FormCreate (persist for the form's life);
   (b) CUSTOMIZABLE `+0x9c0` area driven by user `.tpr` presets + design mode.
2. **Do NOT inject into `+0x9c0`** — its contents serialize to/from the user's preset and rebuild on preset-load /
   design-mode, so our control would be dropped (or pollute their preset). Prefer the re/16 routes: a top-level
   "Plugins" menu, or a `TQuickBtn` on a FIXED panel.
3. Toolbar can be fully hidden (`@0x114a050`) and the form is recreated on EditToolbars/skin change →
   **re-assert our control on toolbar (re)create** (same caveat as re/16 §6).
4. For a "native" self-laying-out section, mimic class `&PTR_FUN_0090e008` with a layout TMethod at `+0x584/+0x58c`;
   otherwise nest inside an existing fixed panel and ride its resize handler.

---

## 5. Main form workspace / window host / status bar

**TFruityLoopsMainForm** (VMT 0x1060840, cr 0x1060858, `forms.main:wpform`, TVectorForm, singleton
`DAT_01581200 == *PTR_DAT_014a8750`). FormCreate@0x10bcf70, **FormResize@0x10ca8f0**. The toolbar singleton
`*PTR_DAT_014aa4c8` is created *inside* main FormCreate. (`param_1[N]` in the longlong*-indexed decompile = byte
offset `N*8`.)

### 5.1 Main-form region/layout field map — a top-band / center-fill / bottom-band stack
| field | byte off | role |
|---|---|---|
| `[0x162]` | **+0xB10** | **TOP toolbar band** — hosts the toolbar's panels; gets P_ILGlyphs font (`+0x49c`); cached @+0x3380 |
| `[0x163]` | **+0xB18** | **DOCK HOST** — workspace area editor windows attach into (`FUN_007ffa50(.,1)` flags 0x11); `FLui_Dock_WindowSetAttached` uses `*(mainForm+0xb18)` |
| `[0x164]` | **+0xB20** | **BOTTOM dock band** — hosts a bottom toolbar panel (`BottomDockSiteResize`→`FUN_00b5c810(*(toolbar+0x9c0), *(mainForm+0xb20))`); cached @+0x3388 |
| `[0x166]` | **+0xB30** | **central WORKSPACE FILL region** (sized to client): `FormResize` → `FUN_007d0e20([0x166], clientW, clientH−y)` |
| `[0x167]/[0x168]` | **+0xB38/+0xB40** | child regions realigned each resize (likely left/right dock sub-sites) |
| `+0x4c2` | u8 | toolbar display mode: 1=hidden/minimal, 2=compact, else full (gates FormResize & compact caption) |
| `+0x6b4/+0x6b8` | i32 | default client size (560·scale × 400) — `FLui_Shell_SetDefaultClientSize` |
| `+0x304` / `+0x2b0` | ptr/HWND | WP skin-paint ctx (scale @`*(+0x304)+0xb4`→`+0xc`) / window handle (shared, re/ui-03) |

**Band init** `FLui_Shell_InitDockBand@0x10bce10` (top+bottom bands): WP flag 0x20 (`vtbl[0x270]`), auto-flag 0x8
(`FUN_00802820(.,1)`), min-extent `region+0x530 = child+0x5fc + 0x29` (≈41px band height). **FormResize@0x10ca8f0:**
(1) `FLui_Shell_UpdateCompactCaption`; (2) client rect (`vtbl[0x310]`); (3) `FUN_007d0e20(fill[0x166], clientW,
clientH−y)` fills center; (4) inset both bands by border width; (5) `FLui_Layout_RealignChildRegion` on form +
`[0x167]` + `[0x168]`.

### 5.2 Toolbar → main-form attachment (host glue) — `FLui_Shell_AttachToolbarPanels@0xcb5a20`
```
SetParent(toolbar+0x760, mainForm+0xb10); SetBoundsY(toolbar+0x760, 0)      // menu+transport row @ y=0
SetParent(toolbar+0x9b8, mainForm+0xb10); SetBoundsY(toolbar+0x9b8, ~41px)  // 2nd toolbar row below it
// bottom band gets toolbar+0x9c0 via BottomDockSiteResize
```
`SetBoundsY` = `FUN_005cf8e0(ctrl,y)` = `ctrl.vtbl[0x188]` (re/06 SetBounds) + mark-aligned. So the toolbar form
supplies **three** docked panels: `+0x760` & `+0x9b8` (top band), `+0x9c0` (bottom band).

### 5.3 Docked-window hosting / window manager
- **Dock host = `*(mainForm+0xb18)`**. `FLui_Dock_WindowSetAttached@0x1228cd0` (re/06) docks/floats ONE window:
  attached-style (`window+0xb8==0`): toggles bit 0x4 @`window+0xd4`, offsets pos `+0xc0/+0xc4` by host dock origin
  (`host.vtbl[0xe0]`); hosted-style (`window+0xb8!=0`): if `host+0x78` parent disagrees → FormHide +
  `FLui_Dock_RepositionHostedWindow` + `FormShow@0x7ea5d0`.
- **Window manager = `*PTR_DAT_014aa6e8`**; FormCreate wires `wm+0x228=mainForm`, `wm+0x220=FLui_Shell_HintBarDefaultText`.
  Drives show/hide/dock/View-menu (re/22-window-manager). Docked editors are dockable singletons (Channel Rack
  `*0x14A8BF8`, Mixer `g_MixerManagerPtr`, PR `*0x14A9B20`, Playlist `*0x14AAB88`, Browser `*0x14ABFF8` — re/ui-03 §6;
  TChildVectorForm/TChildWPForm, flag `form+0x6d0|=2`).

### 5.4 Status / hint bar wiring (two surfaces, one core)
- **Primary = toolbar InfoLabel `*(toolbar+0xbd0)`** — `FLui_SetStatusHintCore@0x10ec570` sets caption
  (`FUN_005d0ae0`), stores `DAT_015817d0`, auto-clear timer `DAT_015817e0=0x5dc` (1500 ticks).
- **Secondary = TFLHintBarForm `*0x14A7580`** (floating hint bar, re/ui-03) — `FLui_SetStatusHintAndRefresh@0x10ec870`
  repaints both InfoLabel + hint-bar form (`vtbl[0x190]`).
- Idle text: `FLui_Shell_HintBarDefaultText@0x10ee240` (wm cb) → `FLui_SetStatusHintCore(mainForm, *(wm+0xc4), 0)`.
- Transport status: `FLui_Shell_StatusHintSongPos@0x10ee350` builds "POS: bar:beat — pattern" from
  `*(toolbar+0x7e8)+0x3c0` (SongPos value) via `FLui_Shell_FormatSongPosHint@0x10ee300`. Fired by the SongPos slider.
- **Reuse:** surface an AI message as a transient FL toast via `FLui_SetStatusHintAndRefresh(*PTR_DAT_014a8750, delphiUStr)`.

### 5.5 #80 docking reuse recipe (main-form side)
1. Build a dockable form (re/ui-03 §7 — `TScriptDialog`+`form+0x6d0|=2`, or any TChildVectorForm) via `FLui_CreateFormFromClassRef`.
2. Attached-style: leave `ourForm+0xb8=0`, then `FLui_Dock_WindowSetAttached(ourForm,1)` (positions vs dock origin of `*(mainForm+0xb18)`).
3. Hosted-style (true embed in workspace stack): set `ourForm+0xb8 = hostParent`, `FLui_Dock_WindowSetAttached(ourForm,1)` → `FLui_Dock_RepositionHostedWindow` + FormShow.
4. Register with wm `*PTR_DAT_014aa6e8` + drive a View ✓ from `form+0x6a9` (re/22) for first-class show/hide/dock.
   Alternatively, to add OUR panel to the toolbar bands, parent a WP control into `*(mainForm+0xb10)` (top) /
   `*(mainForm+0xb20)` (bottom) exactly like `FLui_Shell_AttachToolbarPanels` (SetParent + `FUN_005cf8e0` SetBoundsY).
5. **Teardown (before FreeLibrary, main thread):** `FLui_Dock_WindowSetAttached(ourForm,0)` (float), FormHide/destroy,
   remove from wm registry, unparent. Dangling dock/registry entry = AV on next resize/View.

---

## 6. Ghidra annotations made this pass (area `UI_win_shell`)
- **Tag `UI_win_shell`** created (with the area description) and attached to **~70 functions**: all `TToolbarForm.*`
  published widget handlers (transport/tempo/pattern/time/snap + panels/meters/LEDs/chrome/master/param/customizable),
  the 7 `TShortcutsModule.*` toolbar-customization actions, `FLui_Ctl_ControlBar_LayoutToolbars` (tag only,
  cross-area), the `TFruityLoopsMainForm.*` host/resize/status functions, and the renamed `FLui_Shell_*` helpers.
- **Renamed `FUN_… → FLui_Shell_*` (9, the unnamed shell helpers):** `AttachToolbarPanels@0xcb5a20` (toolbar→main
  attach), `CPUPanelApplyVisible@0xcb7c30`, `InitDockBand@0x10bce10` (top/bottom band init), `SetDefaultClientSize
  @0x10bc9e0`, `UpdateCompactCaption@0x10ca860`, `SetToolbarVisible@0x114a050` (whole-toolbar hide/show),
  `HintBarDefaultText@0x10ee240` (wm idle-hint cb), `FormatSongPosHint@0x10ee300`, `StatusHintSongPos@0x10ee350`.
- **Plate comments** added on the master/transport handlers (`MainVolSliderChange`/`MainPitchSliderChange`/
  `TSDFeedWheelChange` — document the master target ids) and on the `FLui_Shell_*` helpers. Program saved.
- **Deliberately NOT renamed** (kept Delphi RTTI names — tagged + referenced only): every `TToolbarForm.*` and
  `TFruityLoopsMainForm.*` published method (already perfectly named); and cross-area helpers referenced but owned
  elsewhere: `FUN_005d0a30` (bind widget↔value), `FUN_00cb9950` (`FL_DispatchCommand` wrapper), `FUN_005db950/70`
  /`FUN_005dc9f0` (control-list), `FLui_Dock_WindowSetAttached@0x1228cd0` / `FLui_Dock_RepositionHostedWindow`
  (re/06), `FLui_Layout_RealignChildRegion` (re/06), `FLui_SetStatusHintCore@0x10ec570` /
  `FLui_SetStatusHintAndRefresh@0x10ec870`, the `FLmenu_*` (re/16) and `FLui_Form_*` (re/ui-03) families.

## 7. Confidence + open items
- **HIGH:** toolbar identity (`TToolbarForm` VMT 0xcb3458, singleton `*PTR_DAT_014aa4c8`) + creation/attach by the
  main form; the `FormCreate` widget→model **bind table** (§2.2); the widget event(`+0x2bc/+0x2c4`)/paint
  (`+0x49c/+0x4a4`)/value(`vtbl[0x1f0]`,`+0x3c0`) model; the transport offsets **+0x778 Tempo / +0x7a8 Pattern /
  +0x7e8 SongPos / +0x838 time-digits / +0x788 Rec**; master **target ids 0x40000000/1/2**; the **window chrome
  lives on the toolbar** (min/max/close → Sys*MenuClick); the main-form **3-band layout +0xb10 (top) / +0xb18
  (central dock host) / +0xb20 (bottom)** + `FormResize@0x10ca8f0`; the dual status surface (toolbar InfoLabel
  `+0xbd0` + `TFLHintBarForm`); the customizable-toolbar `.tpr` preset caveat (don't inject into `+0x9c0`).
- **MED / gaps:** exact 1:1 of the command-bound transport-toggle cluster (metronome / loop-mode / wait-for-input /
  blend / song-vs-pattern mode at toolbar `+0xaa8/+0xab0/+0xa10/+0xa18/+0xb48` ← main `+0xc00/+0x2140/+0xd10/
  +0xd28/+0x1348`); the precise **Play/Stop** field (`+0x780`←main+0x1920 probable, no custom-paint handler) and
  **Poly** field; master vol-vs-pitch toolbar field (`+0x9e0`/`+0xb28` overlap, though target ids are pinned);
  min/max/close button ordering among `+0x888/+0x890/+0x898`; internals of toolbar-section class `&PTR_FUN_0090e008`
  (the self-layout `+0x584` hook) and the toolbar-config object `*(PTR_DAT_014abca8+0x7c)`; left/right dock
  sub-sites `mainForm+0xb38/+0xb40`.
- **Runtime gap (low risk, all reachable from globals):** live `*PTR_DAT_014aa4c8` (toolbar) / `DAT_01581200`
  (main) pointers; that re-asserting our inserted control survives a toolbar (re)create (EditToolbars / skin change
  recreates the toolbar form — re-insert on (re)create, same caveat as re/16 §6).
