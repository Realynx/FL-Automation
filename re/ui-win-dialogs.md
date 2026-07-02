# UI-WIN-DIALOGS — plugin-GUI host (foreign-window embed for #80), Settings/Project windows, common modal dialogs, TScriptDialog

AREA (overnight UI-RE wave 2): the **plugin-window host + the foreign-GUI embed pattern**, the **Settings/Options
window**, the **common modal dialogs** (nameedit / render-export / msg / plugin picker), and a deep map of
**`TScriptDialog`** (the recommended empty embed shell). Static Ghidra on `FLEngine_x64.dll`, image base
`0x400000` (all addresses Ghidra/absolute; runtime = `ghidra - 0x400000 + flEngineBase`). No live FL.

Builds on **re/ui-03-forms.md** (form hierarchy, `TCustomWPForm` host, `vtbl[0x2e8]` modal contract, the catalog)
and re/22-window-host.md / re/22-window-manager.md. **All form / dialog / WP-embed code is clean Delphi VCL + the
WP layer — NO VMProtect.** The cross-process plugin *bridge IPC* (FLui_PluginBridge_*) is also clean here.

## TL;DR / verdict (HIGH confidence)
- **THE foreign-GUI embed answer for #80:** FL embeds a foreign window by (1) giving an FL **WP content control a
  real Win32 child HWND** (`HandleNeeded` realizes it via the control's `vtbl[0x1d0]` into `ctl+0x45c`), then
  (2) calling Win32 **`SetParent`** to nest that HWND under a target window. The reusable primitive is
  **`FLui_WP_SetParentWindow@0x5d8e70`** (stores target host HWND @ `ctl+0x354`, calls `SetParent(ctl.ownHwnd@+0x45c,
  target)`, re-realizes), wrapped by **`FLui_WP_EmbedForeignWindow@0x7e3a60`** (the dispatcher: clear if target==0,
  else realize the foreign object's HWND with `FLui_WP_GetHandle` and parent into it). **For #80 we mirror this:
  realize OUR content's HWND and `SetParent` it under the FL host form's HWND (`form+0x2b0`), or use these exact
  primitives.** (§2.3)
- **The plugin foreign-GUI HOST window = `TBridgedEditorForm`** (VMT `0xbd3f20`, classRef **`0xbd3f38`**, size 0x738,
  desc-less TCustomWPForm child). It is created **on demand by the plugin-editor wrapper** `TBaseAudioPluginEditor`
  via **`FLui_PluginEditor_SetupHost@0xbdd530`**: when the editor is "bridged" (flag `obj+0x60`), it does
  `TCustomWPForm.Create(&0xbd3f38,1,0)`, wires a show-callback (`form+0x720`=fn, `form+0x728`=editor backptr), then
  embeds the plugin content into the form's content WP control `*(form+0x700)` via `FLui_WP_EmbedForeignWindow`. (§2.1-2.2)
- **The in-FL native plugin host (Channel Settings + native generator/effect UI) = `TPluginForm`** (VMT 0xe7a5f0,
  desc `forms.pluginform`, FormCreate 0xe87b60). It is a heavyweight TChildVectorForm with ~150 baked child
  controls (wheels/scopes/keyboard/env-editor) — **NOT** a blank embed host; do not repurpose it. (§2.4)
- **Show/raise any plugin editor = `FLui_ShowFocusPluginEditor@0x122acb0`**: `(*(*obj+0xE0))(obj,1)` [ShowEditor:
  1=show/0=hide/-1=toggle] then bring the host form (`obj+0xB8`) to front via `vtbl[0x260]`. (§2.5)
- **Settings/Options window = `TMIDIForm`** (VMT 0x124ab10, desc `forms.options`, FormCreate 0x1255200, FBV family,
  **startup singleton `*0x14A8B00`** → modeless, shown via the window manager). It is **one window with many "Sheet"
  pages** (General / MIDI / Audio / Project-info / File / Debug / About / GDPR), switched by a SheetSelect control
  at **`form+0x7c8`** through **`TMIDIForm_SheetSelectChange`**; most controls write straight to global config
  pointers. (§3)
- **Common modal dialogs share the re/ui-03 §7 contract** (`Create(&leafClassRef,1,owner)` → `vtbl[0x2e8]`
  ShowModal blocks → `result==1`=OK read fields → Free via `vtbl[-0x20]`). This pass pins the concrete
  create/show/read recipes for **NameEdit** (full out-param offsets), **Render/Export**, **Msg box**, **Picker**. (§4)
- **`TScriptDialog` (VMT 0xcf3870, classRef 0xcf3888, desc `forms.pianorollscriptform`) is confirmed the cleanest
  embed shell** — its `FormCreate@0xcf42c0` bakes **zero** child widgets; content container `+0x11c` is a clean
  canvas; full skinned WP window (titlebar/border/buttons + HWND@+0x2b0). (§5)

---

## 1. Win32 SetParent call sites (the only two in the binary)
`SetParent` import thunk = **`0x425330`**. Exactly two callers:
| caller | role |
|---|---|
| **`FLui_WP_SetParentWindow@0x5d8e70`** | the WP-control "set host window" primitive (§2.3) — the embed workhorse |
| **`FLui_PluginBridge_WrapMsgWindow@0xbc0470`** | wraps a `TPUtilWindow` HWND and reparents it to **`HWND_MESSAGE`** (-3) — bridge teardown/detach (§2.2) |

So **all FL window-embedding flows through `FLui_WP_SetParentWindow`**; everything else is WP-layer plumbing on top.

---

## 2. Plugin-window host + the foreign-GUI embed (PRIMARY #80 deliverable)

### 2.1 The editor wrapper object — `TBaseAudioPluginEditor`
The per-plugin editor controller object (one per channel/mixer-slot plugin instance). Decompiled fields (offsets
from the editor obj; from `FLui_PluginEditor_SetupHost` + the vtbl accessors @0x84d7b0..0x84d9d0):
| off | field | conf |
|----:|-------|------|
| `+0x08` | plugin instance backptr (`obj[1]`; its `+0x7c0` = the plugin processor/window mgr) | HIGH |
| `+0x28`/`+0x2c` | editor X / Y (set by `vtbl_6`/`vtbl_7`) | HIGH |
| `+0x30`/`+0x34` | editor W / H (set by `vtbl_7`) | HIGH |
| `+0x3c` | foreign/native content handle source (`vtbl_3` stores arg here) | MED |
| `+0x46` | a u8 flag (`vtbl_5`) | LOW |
| `+0x58` | **host form ptr** (`obj[0xb]`) — the `TBridgedEditorForm` when bridged | HIGH |
| `+0x60` | **"bridged?" flag** (`obj[0xc]`; param_6 of SetupHost) — selects the TBridgedEditorForm path | HIGH |
| `+0xB8` | **editor host TForm** (read by `FLui_ShowFocusPluginEditor` as `obj[0x17]`) — bring-to-front target | HIGH |
| `+0xE0`(vtbl) | **ShowEditor** method (1=show / 0=hide / -1=toggle) | HIGH |

`vtbl[3]@0x84d860` = base init (`TBaseAudioPluginEditor.vtbl_3`): stores content source @+0x3c, bounds @+0x28/+0x2c.

### 2.2 `FLui_PluginEditor_SetupHost@0xbdd530` — creates the host form + embeds (the canonical pattern)
```c
TBaseAudioPluginEditor.vtbl_3(editor, ...);            // base init (bounds + content src)
editor[0xa] = pluginContentDesc;  editor[0xc] = bridged;   // +0x50, +0x60
... (FUN_00bd57a0 wires the plugin processor @ editor[1]+0x7c0) ...
if (bridged) {
    if (editor[0xb] == 0) {                            // editor+0x58 host form not yet made
        form = TCustomWPForm.Create(&PTR_FUN_00bd3f38, 1, 0);   // create TBridgedEditorForm
        editor[0xb] = form;
        *(form + 0x728) = editor;                      // form.backptr = editor
        *(form + 0x720) = TBridgedEditorForm_ShowEditorCb@0xbdd430;  // form.showCb
    }
    FLui_WP_EmbedForeignWindow(*(form + 0x700), pluginContent, ...);   // EMBED into form's content WP ctl
}
```
- **`TBridgedEditorForm` host-form fields:** `+0x700` = ptr to the **embed content WP control** (the canvas the
  foreign GUI is parented into); `+0x720` = show-editor callback fn; `+0x728` = editor backptr.
- **`TBridgedEditorForm.ShowEditorBtnClick@0xbd43b0`** = `if(form+0x720) (*(form+0x720))(form+0x728, form)` — the
  "Show editor" button fires the callback. **`TBridgedEditorForm.FormCreate@0xbd43a0`** is a thin shell
  (`FUN_007fe210` only — the shared post-create helper many FormCreates call); all wiring is done by SetupHost.
- **`TBridgedEditorForm_ShowEditorCb@0xbdd430`**: if `editor+0x60` (bridged) → `FUN_00bd5dd0(editor[1]+0x7c0, 2)`
  (ask the plugin processor to open its editor in the bridge).

**Cross-process bridge IPC** (for separately-bridged plugins; not strictly UI but adjacent):
- **`FLui_PluginBridge_HostCtor@0xbce060`** sets up the bridge host: `DuplicateHandle` of current thread,
  `GetCurrentThreadId`, allocates message/dispatch objects, and creates a hidden **`TPUtilWindow`** message window
  via `FLui_PluginBridge_WrapMsgWindow@0xbc0470` with WndProc callback `FUN_00bcf3a0` (stored @ host+0xac).
- **`FLui_PluginBridge_WrapMsgWindow@0xbc0470`** → `FUN_005288d0` creates the actual Win32 window: registers class
  **`"TPUtilWindow"`** (DefWindowProc-based, `WS_EX_TOOLWINDOW`, `WS_POPUP`), `CreateWindowExW`, subclasses it via
  `SetWindowLongPtrW(hwnd, GWLP_WNDPROC/-4, thunk)`; then `SetParent(hwnd, HWND_MESSAGE)`. (Useful template for a
  hidden message-only host window if we ever need one.)

### 2.3 The WP ↔ Win32 embed primitives (THE reuse core for #80)
| function | addr | what it does |
|---|---|---|
| **`FLui_WP_EmbedForeignWindow`** | **0x7e3a60** | dispatcher. `(target==0)`→`FLui_WP_SetParentWindowEx(ctl,_,1)` (clear); else detach (`ctl.vtbl[0x138](ctl,0)`), `hwnd = FLui_WP_GetHandle(targetObj)`, `FLui_WP_SetParentWindow(ctl, hwnd)`. |
| **`FLui_WP_SetParentWindow`** | **0x5d8e70** | **the SetParent workhorse.** Stores host HWND @ `ctl+0x354`; if `ctl` already owns a Win32 HWND (`ctl+0x45c`) → Win32 **`SetParent(ctl+0x45c, host)`** + re-realize (`FUN_005d8d30`); else slow path re-creates the handle under the new parent. Guards on `ctl+0xf` (parent link) and dup-parent. |
| `FLui_WP_SetParentWindowEx` | 0x7e3a20 | `if(detach) ctl.vtbl[0x138](ctl,0)` then `FLui_WP_SetParentWindow(ctl,host)`. |
| `FLui_WP_GetHandle` *(FUN_005ddf70)* | 0x5ddf70 | `HandleNeeded(ctl); return *(ctl+0x45c)` — realize + return the WP control's own Win32 HWND. |
| `FLui_WP_HandleNeeded` *(FUN_005ddf30)* | 0x5ddf30 | if `ctl+0x45c==0`: realize parent first (`ctl+0xf`) then `ctl.vtbl[0x1d0](ctl)` (CreateWnd). |
| `FLui_WP_UpdateParentWindow` *(FUN_005d8d30)* | 0x5d8d30 | post-reparent re-realize / focus-message refresh. |

**Key field:** every WP control stores its **own Win32 child HWND at `+0x45c`** and its **host/parent HWND at
`+0x354`**. (FUN_005ddf70/5ddf30 left as `FUN_*` in Ghidra — they are generic ui-02 WP-core; documented here by
address to avoid clobbering the controls lane. 0x5d8e70/0x7e3a60/0x7e3a20 renamed — embed-specific, in-lane.)

### 2.4 `TPluginForm` (in-FL native plugin host = Channel Settings)
VMT 0xe7a5f0, desc `forms.pluginform`, FormCreate **0xe87b60**, TChildVectorForm (dockable), multi-instance
(one per channel). Its FormCreate wires ~150 baked WP controls (pitch/stretch wheels @+0x850/+0x858, scope @+0x818,
keyboard, the env-editor host @+0x10d8 "EnvEditorForm", mixer-track combo @+0x968, sheet selector @+0x7a8). This is
the **native generator/effect + sample/misc settings** surface, **not** a blank embed host. Skin via
`Delphi_UStrAsg(form+0x6e8, L"forms.pluginform:wpform")`. The chrome key `forms.pluginform.newcaption.*` (re/22) is
this window's titlebar.

### 2.5 Show / focus a plugin editor — `FLui_ShowFocusPluginEditor@0x122acb0`
```c
(*(*obj + 0xE0))(obj, 1);              // ShowEditor(1=show,0=hide,-1=toggle)
host = obj[0x17];                      // editor host TForm @ obj+0xB8
if (host) (*(*host + 0x260))(host);    // bring-to-front
```
`obj` = channel obj (`FLcr_ChannelListGetItem(*(*0x14A98D8), idx)`) or mixer-slot obj
(`*(*0x14A7EB0 + track*0x1474 + slot*8 + 0x1324)`). Workers: `FLui_ChannelShowEditorWorker@0xe03b80`,
`FLui_ChannelFocusEditorWorker@0xe03d80`, `FLui_MixerFocusEditorWorker@0xe0ae00`. Python shells:
`FLpy_channels_showEditor@0xe03c20`, `FLpy_channels_focusEditor@0xe03e10`, `FLpy_mixer_focusEditor@0xe0aec0`.
Main-thread only.

### 2.6 #80 reuse recipe (own FL-native window hosting OUR foreign content)
1. **Create the host:** repurpose `TScriptDialog` (§5) via the factory (`FLui_CreateFormFromClassRef` /
   `TVectorForm.Create`) → skinned titlebar/border/buttons + HWND@`form+0x2b0` + clean content container @`form+0x11c`.
2. **Embed our HWND (simplest):** Win32 `SetParent(ourChildHwnd, form+0x2b0)` then position it; loses WP skinning
   on the child but is robust. (FL's own path proves cross-window `SetParent` is the mechanism.)
3. **Embed via the WP layer (native-feel, dockable):** create a WP container control, parent it into `form+0x11c`
   (re/13/14 Stage-B1: `FUN_0074c400`→`vtbl[0x138]`SetParent→`vtbl[0x188]`SetBounds→`FUN_005ceef0`realize), realize
   its HWND with **`FLui_WP_GetHandle@0x5ddf70`**, then `SetParent(ourChildHwnd, thatHwnd)` — or feed our content
   object straight into **`FLui_WP_EmbedForeignWindow@0x7e3a60`**.
4. **Show modeless:** `FormShow@0x7EA5D0(form)` (not `vtbl[0x2e8]`). Dockable: `form+0x6d0 |= 2`. Caption:
   `FLwp_SetFormCaption@0x841690`.
5. **Teardown (main thread, before eject):** unparent our child (`SetParent(child, NULL)` / `HWND_MESSAGE`), clear
   any callback fn-ptrs, `FormClose`/destroy the form (removes it from `formMgr+0x20`). A dangling registry entry or
   live fn-ptr = AV on next focus/menu. (FL itself parks detached plugin windows under `HWND_MESSAGE` — see 0xbc0470.)

---

## 3. Settings / Options window — `TMIDIForm`
VMT **0x124ab10**, classRef 0x124ab28, desc `forms.options`, **FormCreate 0x1255200**, **FormShow 0x124ff60**,
family FBV (TFLBaseVectorForm). **Startup singleton `*0x14A8B00`** (created in `FUN_010b8240`, re/ui-03 §6) → it is
**modeless**, opened/closed/raised through the window manager (re/22), not via ShowModal.

**One window, many "Sheet" pages.** `FormShow` calls **`TMIDIForm_SheetSelectChange(form, *(form+0x7c8))`** — the
**SheetSelect control @ `form+0x7c8`** is the page/tab switcher; the active sheet's pointer is read from
`(form+0x7c8)+0x588`. Pages (inferred from the ~120 published handlers):
| Sheet | representative controls (handlers) |
|---|---|
| **General** | Language, HighVisibility, FancyAnim, AnimRefreshRate, AutoCheckUpdates, HoldClick, InvertPencilButtons |
| **MIDI** | InPortSel, InputDeviceType, MIDIInEnable, ControllerTypeMenu, ExtClockSync, ILRemote, MIDIInCtrlHelp |
| **Audio** | AudioOutSelect, ASIOClock/ASIOMixInBS, BufL(Ofs)Slider, AsioPanel, FastDeclick, CloseDS, TruePPISlider |
| **Project info** | GenreEdit/GenresBtn, InfoEdit, (title), Tempo/Den(ominator)Select |
| **File** | BackupFolder, AskProjFolders, CopySamples, AutoZip, ExtToolList, AutoSave(Max), DeleteUndoToRecycleBin |
| **Debug / FLR** | DebugMemo, DebugSheet, FLREnableDebug, ForceRefreshes |
| **About** | AboutSheet, AboutTitleLabel, AboutEdit, Unlock button (`form+0xef8`/`form+0xf48` text varies by license) |
| **GDPR** | GDPREnableLogStatistics, GDPRMoreInfo |
Most handlers write directly to global config pointers (`PTR_DAT_014a*`). No separate "Project settings" window —
project info lives on a TMIDIForm sheet; the standalone modal `TProjectRenameForm` (0x12499b0) only renames.
`NewProject`/template picker is the modal `TNewProjForm` (0xdbb300, FormCreate 0xdbe190).

---

## 4. Common modal dialogs — concrete create/show/read recipes
All are TFLBaseVectorForm leaves; **modal contract** (re/ui-03 §7): `form=TFLBaseVectorForm.Create(&leafClassRef,1,
owner)@0x722040` → populate → `result=(*form->vtbl[0x2e8])(form)` (ShowModal, blocks) → `if(result==1) read` →
`Free` via `(*form->vtbl[-0x20])(form,1)` (or `FUN_0040faa0(form)`).

### 4.1 Name / rename — `FLui_Form_ShowNameEdit@0x798d30` (TNameEditForm, classRef 0x796258)
The canonical full-marshalling template. Child-control map + **read-back offsets** (after `result==1`):
| out | source | meaning |
|---|---|---|
| `*param_5` (name) | `Delphi_UStrAsg(name, *(form[0xf2]+0x624))` | edited text; `form[0xf2]` = the edit control |
| `*param_6` (color) | `*(form[0xf3]+0xc4)` | color menu control |
| `*param_7` (color2) | `*(form[0xfa]+0xc4)` | second color control |
| `*param_8` (icon) | `*(form[0xf7]+0x18)` | icon menu control |
| `*param_9` (valuetype) | `*(uint*)(form+0x102*8)` (`form[0x102]`) | value-as type |
Set input text via `FLui_Ctl_Edit_SetText(form[0xf2], name)`. Style bits in `param_12` (0x01 unit, 0x04 icon col,
0x10 color2, 0x20 multiline, 0x80 numeric). Positions at cursor (`GetCursorPos`) when `param_1==0xffffffff`. The
`param_12 & 8` branch instead pops the `TPaletteEditorForm` color picker.

### 4.2 Message box — `FLui_ShowMessageBox@0xa88770` → `FLui_ShowMessageBoxCore@…`
`FLui_ShowMessageBox(delphiStr "Title|Body", flags, param3)`. `'|'` splits title/body; **flags 0x10=error,
0x30=warning**. Blocks until dismissed. (Plain reuse: build a Delphi UString, call it.)

### 4.3 Plugin picker — `FLui_Form_ShowPluginPicker@0xfe7670` (TPluginListForm, classRef 0xfe34a0)
`FUN_00fe8250(form)` populates from the plugin DB ("Installed\\"); `iVar=(*form->vtbl[0x2e8])(form)`;
**returns `iVar==1`** (a selection was made). Multi-instance, freed after. Effect/generator filter is set on the
form before show. (Note: the cursor-positioned **add-channel** picker is the *glass* `TPlugListForm` 0xdae350 via
`FLcr_OpenAddChannelPicker@0xdae920`, modeless — re/ui-03 §5; different class from `TPluginListForm`.)

### 4.4 Render / Export — `FLui_Dlg_ShowRenderExport@0xdcc720` (TRenderForm, classRef 0xdc5f08, singleton `DAT_0157eca8`)
```c
if (recording) FLui_ShowMessageBox("Can't master while recording|…", 0x30, 0);     // guard
else if (FUN_00dcc520() /*precheck*/) {
    DAT_0157eca8 = TFLBaseVectorForm.Create(&PTR_FUN_00dc5f08, 1, 0);   // create TRenderForm
    FUN_00dcce20(form);                                                 // configure/populate
    (*form->vtbl[0x2e8])(form);                                         // ShowModal (blocks)
    DAT_0157eca8 = 0; FUN_0040faa0(form);                               // Free
}
```
The dialog drives the actual render itself: **`TRenderForm.StartBtnClick@0xdd25c0`** kicks off
`FLrender_*` (MixDownWorker 0xde0f80 / RealtimeMaster 0xde26b0; validate 0xdd59d0; length 0xdd0d80). Output format
controls: `OutputFormatWAVBoxClick`/`MP3BitrateSlider`/`WFBox`, `ExportModeCombo`, plus Mastering/Loudness/Audition/
SoundCloud-upload panels. Progress drawn by `ProgressPaintBoxPaint`. WAV/DWP progress have their own forms
(`TWAVRenderForm` 0xc4de78, `TDWPRenderForm` 0xb87598). (Render trigger itself: see overnight-loop notes.)

---

## 5. `TScriptDialog` — the empty embed shell (confirmed for #80)
VMT **0xcf3870**, classRef **0xcf3888**, desc `forms.pianorollscriptform`, size 0x7f8, TVectorForm-derived.
**`FormCreate@0xcf42c0` (full body):**
```c
FUN_007e7a70(form, 3);                                      // vector-form mode/scale init
*(byte*)(form+0x708) &= 0xfe;                               // clear no-auto-paint bit (TUnpaintedWPForm flag)
Delphi_UStrAsg(form+0x6e8, L"forms.pianorollscriptform:wpform");   // skin descriptor
FLui_WP_FreeSkinDescriptors(form, 1);                      // re-apply skin
return;                                                     // <-- NO child widgets created
```
→ It is literally a skinned WP window with an **empty content container `+0x11c`** — FL's purpose-built host for
*dynamically-built* UI (the PR-scripting feature fills it at runtime). It is a full skinned form: titlebar/caption/
buttons/border (re/ui-03 §3), real HWND @ `form+0x2b0`, content @ `form+0x11c`.
**Current usage = modal** via `FLui_Dlg_ShowScriptDialog@0xcf3ff0`:
```c
form = FUN_00cf4180(&PTR_FUN_00cf3888, 1, 0);   // == TVectorForm.Create(classRef,…) ; the on-demand ctor
FUN_00cf4140(form); Delphi_UStrAsg(form+0xfc, msg); FUN_00cf4a10(form, mode);
if (mode<=1) FLui_Ctl_Edit_SetText(*(form[0xf7]+0x658), text);   // form[0xf7] = its one edit control
FUN_00786ad0(form, owner, 3);
r = (*form->vtbl[0x2e8])(form);                 // ShowModal; r==6 => "yes" (opens notepad via ShellExecuteW)
FUN_0040faa0(form);                             // Free
```
**To use as a modeless embed panel for #80:** create the same way (`FUN_00cf4180(&PTR_FUN_00cf3888,1,0)`), set a
caption (`FLwp_SetFormCaption@0x841690`), optionally override the descriptor, parent our content into `form+0x11c`
(or SetParent our HWND under `form+0x2b0`), and call `FormShow@0x7EA5D0(form)` instead of `vtbl[0x2e8]`.

---

## 6. Ghidra annotations this pass (tag `UI_win_dialogs`)
Created tag **`UI_win_dialogs`** (with description) and attached to **21** functions (plugin host/embed set +
dialog/settings show-wrappers + TScriptDialog).
**Renamed:**
- Plugin host / bridge: `FLui_PluginEditor_SetupHost@0xbdd530`, `TBridgedEditorForm_ShowEditorCb@0xbdd430`,
  `FLui_PluginBridge_HostCtor@0xbce060`, `FLui_PluginBridge_WrapMsgWindow@0xbc0470`.
- WP↔Win32 embed primitives (in-lane, embed-specific): `FLui_WP_EmbedForeignWindow@0x7e3a60`,
  `FLui_WP_SetParentWindowEx@0x7e3a20`, `FLui_WP_SetParentWindow@0x5d8e70`.
- Dialog show-wrappers: `FLui_Dlg_ShowRenderExport@0xdcc720`, `FLui_Dlg_ShowScriptDialog@0xcf3ff0`.
- Tagged-only (kept their existing/symbol names): `TPluginForm.FormCreate@0xe87b60`,
  `TBridgedEditorForm.FormCreate@0xbd43a0`, `TBridgedEditorForm.ShowEditorBtnClick@0xbd43b0`,
  `FLui_ShowFocusPluginEditor@0x122acb0`, `TScriptDialog.FormCreate@0xcf42c0`, `TMIDIForm.FormCreate@0x1255200`,
  `TMIDIForm.FormShow@0x124ff60`, `TRenderForm.FormCreate@0xddba20`, `TRenderForm.StartBtnClick@0xdd25c0`,
  `FLui_Form_ShowNameEdit@0x798d30`, `FLui_Form_ShowPluginPicker@0xfe7670`, `FLui_ShowMessageBox@0xa88770`.
- **Left as `FUN_*` on purpose** (generic ui-02 WP-core, documented by address to avoid clobbering that lane):
  `FUN_005ddf70` (WP GetHandle), `FUN_005ddf30` (WP HandleNeeded), `FUN_005d8d30` (WP UpdateParentWindow),
  `FUN_005288d0` (TPUtilWindow create).
- No source edits, no git. Program **saved**. (No generic plate-comment tool exposed by this MCP build — used
  renames + the `UI_win_dialogs` tag; prior passes' plate comments on shared funcs remain intact.)

---

## 7. Confidence + gaps
- **HIGH:** the embed mechanism (`SetParent` only via `FLui_WP_SetParentWindow`; `EmbedForeignWindow` dispatcher;
  WP control HWND@+0x45c / host HWND@+0x354); `TBridgedEditorForm` = the foreign-GUI host form created by
  `FLui_PluginEditor_SetupHost` (content WP ctl @form+0x700, callback @+0x720/+0x728); `FLui_ShowFocusPluginEditor`
  contract; `TPluginForm` = the native (non-embed) plugin/channel-settings host; TScriptDialog = empty shell with
  full FormCreate; the modal contract + NameEdit read-back offsets + Render/Msg/Picker show recipes; TMIDIForm =
  multi-Sheet Options singleton with SheetSelect @form+0x7c8.
- **MED:** exact arg threading inside `FLui_PluginEditor_SetupHost` (which param is content vs plugin-path); the
  `TBridgedEditorForm` instance size/extra fields beyond +0x700/+0x720/+0x728; the precise TMIDIForm sheet→control
  grouping (named by handler prefix, not by reading each sheet's child list).
- **Open / not chased:** the **in-process (non-bridged) VST editor** window — when `editor+0x60==0`,
  `SetupHost` makes no TBridgedEditorForm; that window is owned by the plugin processor (`editor[1]+0x7c0`) and its
  host TForm lands at `editor+0xB8` — the deeper generator/effect-wrapper window creation lives in the FLEngine
  plugin-host (partly outside this UI lane) and was not decompiled. For #80 it is unneeded: the embed primitives in
  §2.3 are identical regardless of in/out-of-process, and `TBridgedEditorForm` is the cleaner host template.
- **No VMProtect** on any form / dialog / WP-embed / bridge-IPC path examined.
