# UI-03 — Forms / windows catalog + window chrome (TCustomWPForm family)

AREA (overnight UI-RE wave 1): the **forms/windows catalog + window chrome** — `TCustomWPForm` and EVERY
concrete form/window class, plus the shared chrome (titlebar / caption / min-close-dock buttons / border).
Static Ghidra on `FLEngine_x64.dll`, image base `0x400000` (all addresses below are absolute/Ghidra; runtime =
`ghidra - 0x400000 + flEngineBase`). Builds on re/22-window-host.md + re/22-window-manager.md (host object +
show/hide/dock) and re/13/14/16. **All form/chrome code is clean Delphi VCL + the WP layer — NO VMProtect.**

## TL;DR / verdict (HIGH confidence)
- FL windows are **Delphi VCL forms sub-classed into a skinned "WP" frame**. The runtime **classRef = VMT + 0x18**.
  A class's virtual constructor is **vtbl[0x78] = `*(VMT+0x90)`** and **resolves to the nearest base that
  overrides it** — so the concrete leaf forms do NOT have their own ctor; they share a base `*.Create`. The
  **per-class identity/setup lives in each form's Delphi `T<Class>.FormCreate`** (the `OnCreate` handler), which
  is where the **skin descriptor UString `+0x6e8` = `"forms.X:wpform"`** is assigned. FL kept the published-method
  RTTI, so ~55 of these `FormCreate`s were already symbol-named; I renamed the ~5 that weren't and tagged them all.
- The shared skinned host is **`TCustomWPForm` (VMT 0x7de900, classRef 0x7de918, size 0x708)**. Above it `TForm`
  /`TCustomForm` are stock VCL (size 0x678, no skin). Below it a two-pronged tree:
  `TUnpaintedWPForm → { TWPForm→TChildWPForm ; TVectorForm→{ TChildVectorForm , TFLBaseVectorForm , … } }`.
- **Window chrome** (titlebar / caption / buttons / border) is painted by FL, not the OS: `FLui_Form_BuildTitleBar`
  (loads skin part `P_TitleBar` into skin obj `+0x49c`), `FLui_Form_TitleBarColor`, `FLui_Form_PaintBorder`
  (FrameRect 3D edge on a frame HWND `+0x45c`), and the **caption control `TNewCaption` (`tnewcaption`, VMT
  0x7dc078)** which carries the close/min/dock/menu buttons (skin-config driven via `forms.X.newcaption.*btn`).
- **Caption TEXT lives at `form+0x110`** (the VCL `FCaption` UString) — set via **`FLwp_SetFormCaption@0x841690`**
  which also calls `SetWindowTextW(HWND@+0x2b0, text)`. **This resolves the one "needs-live" open item from re/22.**
- **Best embed-host for #80 = `TScriptDialog` (forms.pianorollscriptform, VMT 0xcf3870)** — the ONLY concrete form
  whose `FormCreate` bakes in **zero child widgets**; it is literally FL's "host dynamically-built UI" shell. See §7.
- **Piano Roll, Playlist and the Event Editor are the SAME class — `TEventEditForm`** (VMT 0xd280f8, desc
  `forms.eventeditform`), distinguished by a mode field `+0x160` (1=PR, 2=PL, 0=EventEdit); factory
  `FLui_Form_CreateEventEditor@0xd2d0d0`. (Resolves why no `forms.pianoroll`/`forms.playlist` descriptor exists.)
- **Every tool/utility dialog (§4.2/4.3/4.4) is MODAL via one contract:** `Create(&classRef,1,owner)` →
  `vtbl[0x2e8]` (ShowModal, blocks) → `if result==1` read edited fields → Free. Bases: `TFLBaseVectorForm.Create`
  @0x722040 (generic+PR) / `TVectorForm.Create`@0x7e77c0 (Edison). See §7 for the copy-paste recipe.
- **Canonical window creation** = `FLui_CreateFormFromClassRef(&classRef, &globalSingleton)`. The whole main-window
  set is created at startup in **`FUN_010b8240` (FL "BeforeRunning" boot)** — that function is the create recipe
  and the classRef→singleton map (§6).

---

## 1. Class hierarchy (VMT = Ghidra addr; classRef = VMT+0x18; size = instance size)

```
TObject
 └ … TControl … TWinControl … TScrollingWinControl
    └ TCustomForm        VMT 0x826f20  cr 0x826f38  size 0x678   (stock VCL: window, HWND@+0x2b0, FCaption@+0x110)
       └ TForm           VMT 0x828d08  cr 0x828d20  size 0x678   (stock VCL TForm; the form-factory "is-a" gate)
          ├ TCustomWPForm        VMT 0x7de900 cr 0x7de918 size 0x708   ***the shared SKINNED host*** (chrome+content+skin)
          │  ├ TUnpaintedWPForm  VMT 0x7df8d8 cr 0x7df8f0 size 0x718   (+0x708=1 no-auto-paint)
          │  │  ├ TWPForm        VMT 0x7dfdb0 cr 0x7dfdc8 size 0x748   (adds sub-obj@+0x710)
          │  │  │  ├ TChildWPForm        VMT 0x7e0298 cr 0x7e02b0 size 0x748  (dockable child: +0x6d0|=2)
          │  │  │  └ TDragDockObjectForm VMT 0x90f570 cr 0x90f588 size 0x748
          │  │  └ TVectorForm    VMT 0x7e0768 cr 0x7e0780 size 0x768   (vector-UI form: scale1.0@+0x758,type4@+0x75c)
          │  │     ├ TChildVectorForm   VMT 0x7e0ce0 cr 0x7e0cf8 size 0x768  (dockable vector child; base for DOCKED editors)
          │  │     │  ├ TBaseGlassForm  VMT 0x6d4eb0 cr 0x6d4ec8 size 0x770  (frameless GLASS popup base; → picker)
          │  │     │  ├ TStepSeqForm / TEventEditForm / TPluginForm / TFXForm / TGraphEditorForm / TNotificationForm …
          │  │     ├ TFLBaseVectorForm  VMT 0x721b88 cr 0x721ba0 size 0x798  ***base for FL modal DIALOGS***
          │  │     │  └ TPRBaseToolForm VMT 0xcbef00 cr 0xcbef18 size 0x7c8  (base for piano-roll tool dialogs)
          │  │     ├ TMEBaseModalForm   VMT 0xba5bd0 cr 0xba5be8 size 0x788  (base for Edison/audio-editor tool dialogs)
          │  │     ├ TBrowserForm / TSampleListForm / TToolbarForm / TPluginListForm / TInfoForm / TTouchKeybForm /
          │  │     │  TQuickEditToolbarContainer / TScriptDialog / TDeverbForm / TFruityLoopsMainForm
          │  ├ TLayeredForm      VMT 0x7e1180 cr 0x7e1198 size 0x780   (→ TFLHintBarForm)
          │  ├ TQuickDockForm    VMT 0x90e518 cr 0x90e530 size 0x708
          │  ├ TBridgedEditorForm VMT 0xbd3f20 cr 0xbd3f38 size 0x738  (bridged/external plugin editor host)
          │  └ TWPControlForm    VMT 0x7de430 cr 0x7de448 size 0x588   (WP-control-as-form variant)
          └ (plain VCL, NOT skinned — OS/VCL frame, no FL chrome):
             TMessageForm 0x64a388 · TInputQueryForm 0x64c4e0 · TZipDialogBox 0x8d4f60 · TAboutForm 0x1265440
```
Parent lookup rule for `re/generated/fl-classes.txt`: a row's `ParentVMT` column = **(parent VMT − 0xb0)**.

---

## 2. TCustomWPForm deep map — fields + the construction chain

### 2.1 The ctor chain (each level chains down to TCustomForm.Create which creates the HWND)
| ctor (renamed) | addr | adds on top of its base |
|---|---|---|
| `TCustomWPForm.Create` | 0x7e3d60 | WP defaults: `+0x6b4/+0x6b8`=0x2000 (def w/h), `+0x6bc/+0x6c0`=1, `+0x6d0`=0x801 flags, `+0x6d2`=0x200, `+0x6a9`=1 (visible), `+0x690..+0x69c`=0 (saved rect), `+0x6fc`=0xf, `+0x640`=8. Calls `TCustomForm.Create@0x8323d0` (VCL — registers form, **creates HWND@+0x2b0**), creates **skin/painter helper@+0x304** (`FUN_007fde80`, class `PTR…007fc980`), content container `+0x11c` padding `+0x20/+0x21`=0x10. |
| `TUnpaintedWPForm.Create` | 0x7e7130 | `+0x708`=1 (no auto-paint). |
| `TWPForm.Create` | 0x7e7440 | inits sub-object @`+0x710` (self-backptr@`+0x734`, `+0x73c`=1) with a rect. |
| `TChildWPForm.Create` | 0x7ea230 | **dockable child flag `+0x6d0 |= 2`**. |
| `TVectorForm.Create` | 0x7e77c0 | scale `1.0f`@`+0x758`, type `4`@`+0x75c`, `+0x730`=-2, content padding=0 (manages own layout). |
| `TChildVectorForm.Create` | 0x7ea430 | **dockable child flag `+0x6d0 |= 2`** (base for docked editors). |
| `TFLBaseVectorForm.Create` | 0x722040 | `FUN_007e46b0` → computes a default content size into `+0x6ac/+0x6b4`. (base for modal dialogs) |

### 2.2 Key instance fields (offsets from form ptr)
| off | type | field | conf |
|----:|------|-------|------|
| `+0x110` | UStr | **CAPTION TEXT (VCL FCaption)** — set via `FLwp_SetFormCaption@0x841690` | HIGH |
| `+0x11c` | ptr | **content/layout container** (parent your child controls here; padding `+0x20/+0x21`) | HIGH |
| `+0x2b0` | HWND | **Win32 window handle** (VCL TWinControl; shared by ALL forms) | HIGH |
| `+0x304` | ptr | WP skin/painter helper object | MED |
| `+0x45c` | HWND | **frame/border HWND** used by `FLui_Form_PaintBorder` | MED |
| `+0x49c` | ptr | **skin / font object** (`P_TitleBar` loaded here) | HIGH |
| `+0x500/+0x504` | u32 | caption gradient colours (active) | MED |
| `+0x508/+0x50c` | u32 | caption gradient colours (inactive) | MED |
| `+0x512` | u8 | titlebar mode/state (`==2` = custom colour path) | MED |
| `+0x530` | f32 | current caption colour intensity | MED |
| `+0x6a9` | u8 | **visible / open flag** (the View ✓ source, re/22) | HIGH |
| `+0x6d0` | u16 | flags (`0x801` default; **bit1 = dockable child**) | HIGH |
| `+0x6e8` | UStr | **WP skin DESCRIPTOR `"forms.X:wpform"`** (set in each FormCreate) | HIGH |
| `+0x700` | ptr | TWPForm sub-object (border colour source @+0x500) | MED |
| `+0x708` | u8 | no-auto-paint flag (TUnpaintedWPForm) | MED |
| `+0x758/+0x75c` | f32/u32 | vector form scale (1.0) / type (4) | MED |

The descriptor idiom inside every `FormCreate`:
```c
Delphi_UStrAsg(form + 0x6e8, L"forms.<name>:wpform");
FLui_WP_FreeSkinDescriptors(form, 1);     // re-applies the skin from the new descriptor (skin area)
```

---

## 3. The chrome map (titlebar / caption / buttons / border)

| function (renamed) | addr | role |
|---|---|---|
| `FLui_Form_BuildTitleBar` | 0x7dd6a0 | build/refresh skinned title bar: `FLui_Form_TitleBarBuildBase` → load skin part **`P_TitleBar`** into skin obj `+0x49c` → `FLui_Form_TitleBarColor` → content container `+0x11c` top-pad `+0x20`=10. |
| `FLui_Form_TitleBarColor` | 0x7dd1a0 | titlebar colour: if `+0x512==2` custom → `FUN_00653c30(skin,+0x508)`; else `FLui_Skin_ComputeCaptionColor`. |
| `FLui_Form_TitleBarBuildBase` | 0x802650 | base titlebar build; `+0xab` = has-custom-colour flag, `+0xb4` = default font colour. |
| `FLui_Form_PaintBorder` | 0x7e2e70 | paints the resizable **border / 3D edge** (`FrameRect`) on frame HWND `+0x45c`; colour from form colour `+0xc4` or TWPForm sub-obj `+0x700+0x500`. |
| `FLwp_SetFormCaption` | 0x841690 | set caption: store UStr@`+0x110` + `SetWindowTextW(HWND@+0x2b0)`. |
| (skin) `FLui_Skin_ComputeCaptionColor` | 0x7dd0a0 | computes caption colour from `+0x500/+0x504/+0x508/+0x50c`, state `+0x512`, into `+0x530` (skin area — not renamed by this pass). |

Chrome widgets (control-catalog cross-refs, see re/ui-02): WP control-type names live in the string table @0x761d00:
`titlebar`@0x761d24, `tnewcaption`@0x761d44, `tnewmenu`@0x761d68, `toolbutton`@0x761d84. The **caption control class
is `TNewCaption` (VMT 0x7dc078, size 0x540)** — it renders the window title and **hosts the close / minimize /
dock / menu buttons**, which are skin-config-driven per form via `forms.<name>.newcaption.<btn>.*` keys (e.g.
`forms.pluginform.newcaption.keyboardfocusbtn.color2`). The menu bar (`tnewmenu` → `NewMainMenu`) sits on
`TToolbarForm` (re/16). **What you inherit by hosting in a TCustomWPForm:** skinned border, `titlebar`+`tnewcaption`
caption, `toolbutton` close/min/dock buttons, drag-move/resize, dock behaviour, and the FL skin — all automatic.

---

## 4. The full concrete-form catalog

`classRef = VMT + 0x18`. `FormCreate` = the per-class `OnCreate` (sets the skin descriptor). All are tagged
`UI_forms` in Ghidra; all `FormCreate`s have plate comments. Skin descriptor shown without the `forms.`/`:wpform`
wrapper. Family: **FBV**=TFLBaseVectorForm (modal dialog), **CVF**=TChildVectorForm (docked editor), **VF**=TVectorForm,
**PRB**=TPRBaseToolForm (PR tool), **MEB**=TMEBaseModalForm (Edison tool), **GLS**=TBaseGlassForm.

### 4.1 Major editor / host windows
| Class | VMT | descriptor | FormCreate | what it is | family · singleton |
|---|---|---|---|---|---|
| TFruityLoopsMainForm | 0x1060840 | main | 0x10bcf70 | **MAIN application window** | VF · `DAT_01581200` |
| TToolbarForm | 0xcb3458 | toolbarform | 0xcb7f90 | top toolbar + `NewMainMenu` strip (docked into main) | VF · `*PTR_DAT_014aa4c8` |
| TStepSeqForm | 0xf41a00 | channelrack | 0xf45870 | **Channel Rack / Step Sequencer** | CVF · `*0x14A8BF8` |
| TFXForm | 0x117b6b0 | mixer | 0x1181870 | **MIXER window** | CVF · `g_MixerManagerPtr` |
| TPluginForm | 0xe7a5f0 | pluginform | 0xe87b60 | **plugin GUI host / Channel Settings** (multi-instance, per channel) | CVF |
| **TEventEditForm** | 0xd280f8 | eventeditform | 0xd40fb0 | **= PIANO ROLL (mode1) + PLAYLIST (mode2) + Event Editor (mode0)**; factory `FLui_Form_CreateEventEditor@0xd2d0d0`, mode @+0x160 | CVF · PR `*0x14A9B20`, PL `*0x14AAB88` |
| TGraphEditorForm | 0x1207640 | grapheditorform | 0x1207ff0 | envelope / graph (automation) editor | CVF · `DAT_01832a18` |
| TSampleListForm | 0xf8a4f0 | browser | 0xf8f200 → `FLbrz_MainBrowserCtor@0xf8d580` | **the Browser** | VF · `*0x14ABFF8` |
| TBrowserForm | 0x9e0320 | — | 0x9e0920 (FormShow 0x9e0850) | secondary/embeddable browser (small/clean) | VF |
| TPluginListForm | 0xfe3488 | pluginlistform | 0xfe68d0 | plugin database picker — **MODAL**, show `FLui_Form_ShowPluginPicker@0xfe7670` (multi-instance) | VF |
| TNotificationForm | 0xfc57a0 | notification | 0xfc6af0 | news/notifications/downloads right panel (browser-tab based) | CVF |
| TTouchKeybForm | 0xe377b0 | touchkeybform | 0xe38400 | touch / typing keyboard | VF |
| TScriptDialog | 0xcf3870 | pianorollscriptform | 0xcf42c0 | **piano-roll script dialog (empty shell — see §7)** | VF |
| TQuickEditToolbarContainer | 0xb4fdb0 | quickedittoolbarcontainer | 0xb58840 | quick-edit toolbar container | VF |
| TInfoForm | 0xb29df0 | — | — | info popup | VF |
| TBaseGlassForm | 0x6d4eb0 | — | 0x6d5290 (ctor) | frameless glass-popup base | CVF |
| TFLHintBarForm | 0xb645c0 | — | (Layered) | the hint/help bar | TLayeredForm · `*0x14A7580` |
| TTestForm | 0xe35570 | testform | 0xe36a60 (FormShow) | FL internal test/scratch form (startup singleton) | FBV · `*0x14AC300` |
| TPythonForm | 0xe30ec0 | pythonform | 0xe32c80 | Python/script output console | FBV · `*0x14A9A10` |
| TMIDIForm | 0x124ab10 | options | 0x1255200 | **SETTINGS / OPTIONS window** (MIDI/Audio/General/File) | FBV · `*0x14A8B00` |

> **Piano Roll, Playlist AND the Event Editor are the SAME class — `TEventEditForm`** (VMT 0xd280f8, classRef
> 0xd28110, descriptor `forms.eventeditform:wpform`). One time-grid editor class, selected by **mode field
> `instance+0x160`: 0 = Event Editor strip ("EventEditForm"), 1 = PIANO ROLL ("PRForm"), 2 = PLAYLIST ("PLForm")**.
> That's why no `forms.pianoroll`/`forms.playlist` `:wpform` descriptor exists (only skin sub-keys like
> `forms.pianoroll.grid.background`). Built by the shared factory **`FLui_Form_CreateEventEditor@0xd2d0d0`** →
> `FLui_CreateFormFromClassRef(&PTR_FUN_00d28110, …)` with the mode arg; the active PR instance is registered in
> `*0x14A9B20` (slot `DAT_0157e998`), the Playlist in `*0x14AAB88` (slot `DAT_0157e9a0`). The View menu drives
> them via the window-manager `wm.vtbl[0x70]` ids 2 (PR) / 0 (PL) (re/22-window-manager).

### 4.2 Generic modal dialogs — base **TFLBaseVectorForm** (0x721b88, FormCreate 0x722040 is the shared ctor)
| Class | VMT | descriptor | FormCreate | what it is |
|---|---|---|---|---|
| TNameEditForm | 0x796240 | nameeditform | 0x798090 | inline name / rename edit box |
| TMsgForm | 0xa86530 | msgform | 0xa89ba0 | FL message box |
| TPaletteEditorForm | 0x788478 | paletteeditorform | 0x78ae50 | colour palette editor |
| TRenderForm | 0xdc5ef0 | renderform | 0xddba20 | **Export/Render dialog** |
| TWAVRenderForm | 0xc4de78 | wavrenderform | 0xc59d10 | WAV/audio render progress |
| TDWPRenderForm | 0xb87598 | dwprenderform | 0xb8cea0 | DirectWave/zip render progress |
| TNewProjForm | 0xdbb300 | newprojform | 0xdbe190 | new project / template picker |
| TTapTempoForm | 0xe3e5e0 | taptempoform | 0xe3fb70 | tap-tempo dialog |
| TProjectRenameForm | 0x12499b0 | projectrenameform | 0x124a490 | project rename |
| TMIDIImportForm | 0xcd33a0 | midiimportform | 0xcd3980 | MIDI import options |
| TMIDIInputForm | 0xe9e858 | midiinputform | 0xeaba80 | MIDI controller input / link |
| TMIDIInputForm_Generic | 0xe97960 | midiinputform_generic | 0xe9ce20 | generic MIDI controller mapping |
| TPLMergeArrangementForm | 0xce64d0 | plmergearrangementform | 0xce69e0 | Playlist merge-arrangement |
| TShortcutForm | 0xe33e60 | shortcutform | 0xe34ba0 | keyboard-shortcut capture |
| TEnvMapForm | 0xefa468 | envmapform | 0xefaf60 | articulation/env map |
| TSpeechForm | 0xefb320 | speechform | 0xefb9e0 | text-to-speech |
| TExportScoreForm | 0xfe1c90 | exportscoreform | 0xfe2700 | export score / MIDI |
| TPRTimeSigForm | 0xfe2a90 | prtimesigform | 0xfe2f80 | PR time signature |
| TPRFillForm | 0xf3bae0 | prfillform | 0xf3f8a0 | PR Fill tool |
| TCloudAccountsForm | 0xb7fcc0 | cloudaccountsform | 0xb80550 | cloud / account login |
| TInAppShopForm | 0xb72860 | (msgform skin) | 0xb72de0 | in-app shop |
| TPluginMonitorForm | 0xc27808 | pluginmonitorform | 0xc2b490 | plugin performance monitor |
| TUnlockForm | 0xde8890 | unlockform | 0xdeb270 | unlock / registration |
| TThemeEditorForm | 0x102f918 | themeeditorform | 0x1030900 | theme editor |
| TDemucsForm | 0x103d0e0 | stemseparationform | 0x103ec00 | stem separation (Demucs) |
| TWelcomeUnlockForm | 0x103fb20 | welcomeunlock | 0x1040360 | first-run unlock |
| TWelcomeGDPRForm | 0x1042020 | welcomegdpr | 0x1042850 | first-run GDPR consent |
| TWelcomeWizard | 0x10438c0 | welcomewizard | 0x104ce70 | first-run welcome wizard (singleton `*0x14A7C50`) |
| TRedeemCodeForm | 0xde6950 | — | — | redeem-code dialog |
| TSharewareForm | 0xfdf6a8 | — | — | shareware/trial nag |
| TMissingPluginsForm | 0xc91080 | — | — | missing-plugins report |
| TLargePluginDataForm | 0x1006510 | — | — | large plugin-data warning |
| TParamCtrlForm | 0xb689c0 | — | — | parameter control / link |
| TDeverbForm | 0xbed550 | `1` (`1:wpform`) | 0xbedc80 | Edison Deverb tool (VF) |

### 4.3 Piano-roll tool dialogs — base **TPRBaseToolForm** (0xcbef00, prbasetoolform, FormCreate 0xcbf6b0)
All **modal** PR tools (Tools menu / piano-roll right-click), shown via the modal pattern (§7). Base ctor
**`TPRBaseToolForm.Create@0xcbf380`** stores PR context (`form+0xf2` active PR data, `form+0x79c` source ctx)
then chains `TFLBaseVectorForm.Create`. Several are singletons (e.g. Quantize guards `DAT_0157c9c0`, shown by
`FUN_00b69fa0`).
| Class | VMT | descriptor | FormCreate | tool |
|---|---|---|---|---|
| TPRQuantizeForm | 0xb66830 | prquantizeform | 0xb67840 | Quantize |
| TPRScoreCreatorForm | 0xcbd540 | prscorecreatorform | 0xcbe0b0 | Riff/Score creator |
| TPRLegatoForm | 0xcbf9e0 | prlegatoform | 0xcc03a0 | Legato |
| TPRNotePropForm | 0xcd0ca0 | prnotepropform | 0xcd1480 | Note properties |
| TPRLFOForm | 0xcd3b40 | prlfoform | 0xcd4680 | LFO |
| TPRSliceForm | 0xcd5740 | prsliceform | (resolve) | Slice |
| TPRFlamForm | 0xcd7560 | prflamform | 0xcd7c80 | Flam |
| TPRKeyLimitForm | 0xcd8690 | prkeylimitform | 0xcd9040 | Key/limit range |
| TPRFlipForm | 0xcda0d0 | prflipform | 0xcda8e0 | Flip |
| TPRTripletForm | 0xcdaea0 | prtripletform | 0xcdb640 | Triplet |
| TPRLevelScaleForm | 0xcdbf80 | prlevelscaleform | 0xcdc630 | Level scale |
| TPRArpForm | 0xcdd450 | prarpform | 0xcde2f0 | Arpeggiate |
| TPRStrumForm | 0xcdfc60 | prstrumform | 0xce0730 | Strum |
| TPRRandomForm | 0xce1960 | prrandomform | 0xce27d0 | Randomize |
| TPRChordToolForm | 0xd09350 | prchordtoolform | 0xd13580 | Chord/scale |
| TPLClipPropForm | 0xd22cc0 | plclippropform | 0xd236d0 | Playlist clip properties |

### 4.4 Edison / audio-editor tool dialogs — base **TMEBaseModalForm** (0xba5bd0; FormCreate 0xba6190, FormShow 0xba6260)
**Modal** audio-clip processing dialogs (Edison). This base derives `TVectorForm` *directly* (ctor =
`TVectorForm.Create@0x7e77c0`, no override). Shown via the §7 modal pattern (`vtbl[0x2e8]`); each leaf's classRef
= its VMT+0x18 (e.g. TMEWAVPropForm VMT 0xba66e0 → cr 0xba66f8, shown by `FUN_00c22cf0`). Members: TMEWAVPropForm 0xba66e0 · TMELoopToolForm 0xba8bf0 ·
TMEBlurToolForm 0xbaabc0 · TMEAmpToolForm 0xbabec0 · TMEArpToolForm 0xbad040 · TMEScaleToolForm 0xbafe30 ·
TMEDecimateToolForm 0xbb0d40 · TMESmoothToolForm 0xbb1740 · TMEEnvFilterToolForm 0xbb26f0 · TMEStretchToolForm
0xbb4c70 · TMEClawToolForm 0xbb74e0 · TMEDrumStretchToolForm 0xbb8e70 · TMEReverbToolForm 0xbbb040 · TMEEQToolForm
0xbbc3b0 · TMEDenoiseToolForm 0xbde408 · TMEOggOptionsForm 0xbe8850 · TMEMP3OptionsForm 0xbe9810 ·
TMEVocalDenoiseForm 0xbebf20.

### 4.5 Plain VCL dialogs (NOT skinned — OS/VCL frame, no FL chrome)
`TMessageForm` 0x64a388 · `TInputQueryForm` 0x64c4e0 · `TZipDialogBox` 0x8d4f60 · `TAboutForm` 0x1265440
(derive directly from `TForm`; ctor = `TCustomForm.Create@0x8323d0`). Avoid these as embed hosts (no FL look).

---

## 5. The picker / glass popup (re/13's repurpose candidate)
`TPlugListForm` (VMT 0xdae350, classRef **0xdae368**, TBaseGlassForm-derived) = the **Add-channel / plugin picker**.
Opened lazily as a singleton: `FLcr_OpenAddChannelPicker@0xdae920` → `FLui_CreateFormFromClassRef(&PTR_FUN_00dae368,
&DAT_0157ec58)`, type filter @picker+0x7e4, then shown at the cursor via `FUN_00db4770` (modeless). **Glass forms
are frameless** (no titlebar chrome) — good for dropdown/picker overlays, NOT for a chrome'd window.

---

## 6. Window singletons + the startup create recipe (`FUN_010b8240`, FL "BeforeRunning")
The canonical create call is `FLui_CreateFormFromClassRef(&classRef, &globalSingleton)` (re/13/14/22 — the form's
`vtbl[0x78]` init registers it in the form-manager TList `formMgr+0x20` and grabs HWND@+0x2b0). `FUN_010b8240`
creates the whole main set:
```
FLui_CreateFormFromClassRef(&TFruityLoopsMainForm_cr 0x1060858, &DAT_01581200);  // main window
FLui_CreateFormFromClassRef(&TStepSeqForm_cr   0xf41a18,  *0x14A8BF8);           // Channel Rack
FLui_CreateFormFromClassRef(&TFXForm_cr        0x117b6c8, g_MixerManagerPtr);    // Mixer
FLui_CreateFormFromClassRef(&TMIDIForm_cr      0x124ab28, *0x14A8B00);           // Settings/Options
FLui_CreateFormFromClassRef(&TSampleListForm_cr 0xf8a508, *0x14ABFF8);           // Browser
FLui_CreateFormFromClassRef(&TFLHintBarForm_cr 0xb645d8,  *0x14A7580);           // Hint bar
FLui_CreateFormFromClassRef(&TGraphEditorForm_cr 0x1207658, *0x14AB2C8);         // Graph editor
FLui_CreateFormFromClassRef(&TPythonForm_cr    0xe30ed8,  *0x14A9A10);           // Python console
FLui_CreateFormFromClassRef(&TTestForm_cr      0xe35588,  *0x14AC300);           // Test/scratch form
...  // then FormShow(*0x14AAB88) = show Playlist
```
(See re/22-window-manager.md for show/hide/dock/close + the View-menu wiring on top of these singletons.)

---

## 7. Embed-host for #80 — recommendation + reuse recipes

**Goal:** a native-looking FL window we own (skinned titlebar + border + buttons), whose HWND@+0x2b0 and content
container @+0x11c we fill with our content (the FL Agent chat).

**Primary recommendation: repurpose `TScriptDialog` (VMT 0xcf3870, classRef 0xcf3888, descriptor
forms.pianorollscriptform, size 0x7f8).** Rationale: it is the **only concrete form whose `FormCreate`
(0xcf42c0) adds NO child widgets** — it is FL's purpose-built host for *dynamically-built* UI (the piano-roll
scripting feature populates it at runtime). So its content container `+0x11c` is a clean canvas, and it is a full
**TVectorForm-derived skinned WP window** (real titlebar/caption/buttons/border via §3, real HWND@+0x2b0).
- Behaviour today: created on demand + freed; `FUN_00cf3ff0` → `FUN_00cf4180(&PTR_FUN_00cf3888,1,0)` (create),
  configure, then **`(*vtbl[0x2e8])(form)` = ShowModal** (returns a result code; 6 = "yes").
- To use it as a **modeless panel**: create it the same way, set your own caption (`FLwp_SetFormCaption@0x841690`),
  optionally override the descriptor (`UStrAsg(form+0x6e8, …); FLui_WP_FreeSkinDescriptors(form,1)`), parent your
  content into `+0x11c`, and call **`FormShow@0x7EA5D0(form)`** instead of `vtbl[0x2e8]`. For dockability set
  `form+0x6d0 |= 2` (the TChildVectorForm flag).

**Secondary options (ranked, by FormCreate simplicity / no required args / no singleton):**
- **`TInfoForm`** (0xb29df0, FC `TInfoForm.FormCreate@0xb2a5a0`) — tiny FC (font only), no args/singleton; but it
  auto-closes on deactivate/keypress (would need that behaviour stripped).
- **`TBrowserForm`** (0x9e0320, FC 0x9e0920) — small; instantiates a single child browser control and nothing else.
- **`TQuickEditToolbarContainer`** (0xb4fdb0, FC 0xb58840) — small (font + descriptor), no args/singleton/self-show;
  its name literally is "container".
- **`TBaseGlassForm`** (0x6d4eb0, ctor 0x6d5290) — minimal `TChildVectorForm` alpha-blended overlay base
  (`+0x760`=0xFF alpha, `+0x764`=1.0f); ideal to subclass for an **overlay-style** embed (frameless, §5).
- **`TNameEditForm`** (0x796240) — has a baked edit control + decorations; only if you want an edit-style popup
  (shown via `FLui_Form_ShowNameEdit@0x798d30`).
- A `TChildVectorForm`-derived form gives native docking out-of-the-box, but all FL's are heavy; `TScriptDialog` +
  `form+0x6d0|=2` is the cleaner dockable route.
- The re/14 shipped chat tab hosts WP controls **inside the existing browser content panel** (not a registered
  window); that remains the lowest-risk route. This §7 is the "own skinned window" alternative.

**Modal-dialog reuse contract (HIGH conf — all §4.2/4.3/4.4 dialogs share it):** to *show* an FL dialog yourself:
```c
form = TFLBaseVectorForm.Create(&LeafClassRef, 1 /*alloc*/, owner);  // 0x722040; Edison uses TVectorForm.Create@0x7e77c0
//  ...set the value(s) to edit into the form's fields / call its configure helper...
result = (*(int(**)(void*))(*(void**)form + 0x2e8))(form);           // vtbl[0x2e8] = ShowModal — BLOCKS
if (result == 1) { /* mrOk: read edited values back out of the form's fields */ }
(*(void(**)(void*,int))(*(void**)form - 0x20))(form, 1);             // Free  (or FUN_0040faa0(form))
```
`&LeafClassRef` = the per-class VMT pointer (e.g. NameEdit `&PTR_FUN_00796258`, Msg `&PTR_FUN_00a86548`, Edison
WAVProp `&PTR_FUN_00ba66f8`). **Best copy-templates:** `FLui_Form_ShowNameEdit@0x798d30` (NameEdit, full
out-param marshalling: name/color/color2/icon/valuetype), `FLui_ShowMessageBox@0xa88770`→`FUN_00a899f0` (message
box), `FUN_00c22cf0` (Edison), `FUN_00b69fa0` + `TPRBaseToolForm.Create@0xcbf380` (PR tool). For a **modeless**
window (the chat panel) use `FormShow@0x7EA5D0` instead of `vtbl[0x2e8]`.

**Content-drop recipe** (re/13/14 Stage-B1): create WP control (`FUN_0074c400`) → SetParent into `form+0x11c`
(`vtbl[0x138]`) → SetBounds (`vtbl[0x188]`) → realize (`FUN_005ceef0`) → render (`FUN_0077adb0`). OR drop standard
Win32 children on HWND@+0x2b0 (loses skin). **First-class window** integration (form-mgr registry + View ✓ +
dock): follow re/22-window-manager.md §6 (create through the factory; drive a View item ✓ from `form+0x6a9`).

**Teardown (eject, main thread, before FreeLibrary):** clear any onClick/popup hooks first, `FormClose`/destroy our
form (remove from `formMgr+0x20`), unparent our content. A dangling registry entry or hook = AV on next focus/menu.

---

## 8. Ghidra annotations made this pass (area: forms + chrome)
- **Tag `UI_forms`** created (with a description) and attached to **86** functions: the base Create chain + all
  ~60 `T<Class>.FormCreate` setups + the chrome funcs + the dialog-family ctors/show-wrappers + the major-window
  factories.
- **Renamed** (base ctors, Delphi style): `TCustomWPForm.Create`@0x7e3d60, `TUnpaintedWPForm.Create`@0x7e7130,
  `TWPForm.Create`@0x7e7440, `TChildWPForm.Create`@0x7ea230, `TVectorForm.Create`@0x7e77c0,
  `TChildVectorForm.Create`@0x7ea430, `TFLBaseVectorForm.Create`@0x722040, `TLayeredForm.Create`@0x7ea680,
  `TBaseGlassForm.Create`@0x6d5290, `TQuickDockForm.Create`@0x910770, `TWPControlForm.Create`@0x7e3970.
- **Renamed** (chrome funcs): `FLui_Form_BuildTitleBar`@0x7dd6a0, `FLui_Form_TitleBarColor`@0x7dd1a0,
  `FLui_Form_TitleBarBuildBase`@0x802650, `FLui_Form_PaintBorder`@0x7e2e70, `FLwp_SetFormCaption`@0x841690.
- **Renamed** (previously-unnamed FormCreates): `TPaletteEditorForm.FormCreate`@0x78ae50,
  `TTouchKeybForm.FormCreate`@0xe38400, `TNotificationForm.FormCreate`@0xfc6af0,
  `TPluginListForm.FormCreate`@0xfe68d0, `TGraphEditorForm.FormCreate`@0x1207ff0.
- **Renamed** (dialog-family ctors / show-wrappers): `TPRBaseToolForm.Create`@0xcbf380,
  `TMEBaseModalForm.FormCreate`@0xba6190, `TMEBaseModalForm.FormShow`@0xba6260,
  `FLui_Form_ShowNameEdit`@0x798d30 (+ tagged `UI_forms` + plate comments).
- **Renamed** (major windows / factories, from sub-agent A): `FLui_Form_CreateEventEditor`@0xd2d0d0 (PR/PL/EventEdit
  factory), `TToolbarForm.FormCreate`@0xcb7f90, `TSampleListForm.FormCreate`@0xf8f200, `TBrowserForm.FormCreate`@0x9e0920,
  `TBrowserForm.FormShow`@0x9e0850, `TInfoForm.FormCreate`@0xb2a5a0, `FLui_Form_ShowPluginPicker`@0xfe7670,
  `FLui_Form_ToggleTouchKeyb`@0xe38400, `TTouchKeybForm.FormCreate`@0xe388f0 (+ `UI_forms` + plate comments).
- **Plate comments** on the base ctors + all chrome funcs + all ~58 concrete FormCreates (class · window role ·
  descriptor · family). Program saved.
- Cross-area finds (noted, NOT renamed): skin `FLui_Skin_ComputeCaptionColor`@0x7dd0a0,
  `FLui_WP_FreeSkinDescriptors`; caption control class `TNewCaption`@0x7dc078 (re/ui-02 controls);
  startup `FUN_010b8240` (boot/init area).

## 9. Confidence + open items
- **HIGH:** class hierarchy + classRef=VMT+0x18 + vtbl[0x78]=*(VMT+0x90) resolving to nearest-base ctor; the
  FormCreate-sets-descriptor model; the full catalog (class/VMT/descriptor/FormCreate); chrome funcs + fields;
  **caption text @+0x110** (resolves re/22 open item); content @+0x11c, HWND @+0x2b0, descriptor @+0x6e8;
  TScriptDialog = the empty embed-host; **all §4.2/4.3/4.4 dialogs are MODAL via `vtbl[0x2e8]` (result==1=OK)**;
  the startup create recipe + classRef→singleton map; **Piano Roll/Playlist/Event-Editor = the one class
  `TEventEditForm` selected by mode `+0x160`** (factory `FLui_Form_CreateEventEditor@0xd2d0d0`).
- **MED / minor open:** `TPRSliceForm.FormCreate` (named in `re/generated/fl-classes.txt`, not separately pinned);
  exact wrapper addresses for a handful of less-common dialogs (the family modal contract covers them). The few
  forms without a `:wpform` descriptor in §4 (TRedeemCode/TShareware/TMissingPlugins/TLargePluginData/TParamCtrl)
  set their skin via the base or a non-literal path.
