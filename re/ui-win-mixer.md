# UI-WIN-MIXER — the Mixer window (`TFXForm`, `forms.mixer`)

AREA (overnight UI-RE wave 2): the **Mixer** — track strips (fader/pan/mute/solo/arm/sends), the per-track
**FX slot chain**, sends/routing, the parametric **EQ**, the master strip, track selection + scroll, and the
form construction + child widgets + key fields. Static Ghidra on `FLEngine_x64.dll`, image base `0x400000`
(addresses below are absolute/Ghidra; runtime = `ghidra - 0x400000 + flEngineBase`). Builds on Wave-1
`re/ui-03-forms.md` (TFXForm is a `TChildVectorForm`/`TCustomWPForm` so it inherits all the chrome/skin/content
plumbing), `re/ui-02-controls.md` (slider/wheel/quickbutton control classes), and cross-references the native
mixer data API in `re/12-controls-harvest.md` / fl-control-catalog (the `FLpy_mixer_*` accessors).
**All mixer code is clean Delphi VCL + the WP layer — NO VMProtect.** FL kept the published-method RTTI, so
~110 `TFXForm.*` methods were already symbol-named; this pass renamed the unnamed build/layout/handler helpers
and tagged the whole window `UI_win_mixer`.

## TL;DR / verdict (HIGH confidence)
- The Mixer window is **`TFXForm`** — VMT `0x117b6b0`, classRef `0x117b6c8`, descriptor `forms.mixer:wpform`,
  `FormCreate @0x1181870`, family **TChildVectorForm** (dockable vector child), singleton `g_MixerManagerPtr`
  (created at boot in `FUN_010b8240`, re/ui-03 §6). It is a large instance (param qword indices run past `0x1c3`
  plus a per-track dB cache at `form+0xe20+track*8` for 502 tracks ⇒ size ≳ `0x1de0`).
- **The track strips are NOT one custom-painted panel — each strip is a real container control with real WP child
  widgets, and the widgets live in the *track data struct*, not the form.** The data model is a fixed array
  **`g_MixerTrackArrayPtr`, 502 tracks (idx 0..0x1f5), stride `0x1474`**; track idx 0 = Master, idx 0x1f5 = the
  "Current/selected" pseudo-track. Strip child-control pointers sit in each track struct's **`+0x13d0..+0x1470`**
  region; the strip's container panel is **`track+0x1440`**.
- **Strip widget set** (skin descriptors `forms.mixer.track.controls.*`): `slider.volume:slider` (the fader,
  `track+0x13e8`), `wheel.pan:wheel` (`track+0x13f0`), `wheel.stereosep:wheel`, `button.sendbutton:quickbutton`
  (`track+0x1408`), `button.recbutton:quickbutton` (=arm, `track+0x1410`), `button.delaybutton`, `button.pluginsbutton`,
  `button.revstereo`, `button.flipy`, `button.mutebtn:quickbtn`, plus the per-route `wheel.sendwheel:wheel`
  (`track+0x1400`) and `track.meter.*` (the peak meter). The strip header tab (track number/name/colour, "Master"/
  "Current") is built by `FLui_Mixer_BuildStripHeaderTab @0x11ca730`.
- **The whole strip area is built AND laid out by one giant function: `FLui_Mixer_BuildLayoutStrips @0x01183c80`**
  (~107 KB pseudo-C). It lazily creates each strip child (guards `FUN_011839a0/_8c0/_ad0` = "control exists?"),
  assigns its skin descriptor + event-id + change/click handlers + bounds (`vtbl[0x188]`=SetBounds), and positions
  every strip by dock side. `FLui_Mixer_RelayoutShared @0x11a1c80` → `FLui_Mixer_BuildLayoutStrips` is the relayout
  entry (called from `TFXForm.FormResize @0x11a1cc0`).
- **The right-hand "selected-track" shared panel** (parented on `form+0xaa8` = param `[0x155]`) is built directly
  in `TFXForm.FormCreate`: a **10-slot FX chain** (array `PTR_DAT_014ab080`, stride `0x20`, 4 widgets/slot) plus
  the EQ "band visual" and the send/routing controls. When the selected track changes, the sends + EQ + FX params +
  the routing **cables** are rebuilt by **`FLui_Mixer_RebuildSelTrackSendsCables @0x0118fdc0`** (keyed off the
  selected-track global `*(int*)PTR_DAT_014a9d28`).
- **Selected/active track global = `*(int*)PTR_DAT_014a9d28`.** Clicking any strip control selects its track:
  strip controls wire OnClick → `FLui_Mixer_Strip_OnClickSelectTrack @0x11a5070` → `FLui_Mixer_SelectTrackUI
  @0x01192fd0(form, trackIdx, 0x15)`.
- **Everything drives the command bus.** Mixer params are addressed by `FL_MixerEffectParamBase(track, slot)
  @0x011c23b0` (returns a base event-id); the per-control low bits select the parameter (vol `+0x70001fc0`, pan
  `+0x70001fc1`, stereo-sep `+0x70001fc2`, EQ `+0x70001fd0..`, FX-slot mute/mix `+0x70001f00/01`). Changes go out
  via `FL_DispatchCommand` (re/08 command bus). This is the SAME id space the native control map (re/12) pokes.

---

## 1. Form identity + creation

| item | value |
|---|---|
| class | **`TFXForm`** |
| VMT | `0x117b6b0` · classRef `0x117b6c8` (= VMT+0x18) |
| descriptor | `forms.mixer:wpform` (string @`0x01182a3c`) |
| `FormCreate` | **`0x1181870`** (`TFXForm.FormCreate`, tag `UI_forms`+`UI_win_mixer`) |
| family | **TChildVectorForm** (CVF) → TVectorForm → TUnpaintedWPForm → TCustomWPForm → TForm (re/ui-03 §1) |
| singleton | `g_MixerManagerPtr` (boot: `FLui_CreateFormFromClassRef(&0x117b6c8, g_MixerManagerPtr)` in `FUN_010b8240`) |
| FormDestroy / Show / Activate | `0x1182d80` / `0x1199ba0` / `0x11b0dd0` |
| Resize / Mouse-wheel / Key | `TFXForm.FormResize 0x11a1cc0` / `FormMouseWheel 0x11aeb20` / `FormKeyDown 0x119a100` `FormKeyUp 0x119a460` |

Inherited TCustomWPForm fields (re/ui-03 §2.2) still apply: caption `+0x110`, **content container `+0x11c`**,
HWND `+0x2b0`, skin/painter `+0x304`, visible flag `+0x6a9`, skin descriptor UStr `+0x6e8`.

### 1.1 `TFXForm.FormCreate @0x1181870` — what it builds (in order)
1. `UStrAsg(form+0x6e8, "forms.mixer:wpform"); FLui_WP_FreeSkinDescriptors(form,1)` — apply skin.
2. Wires 9 form controls (`[0x15d..0x15f]`, `[0x161..0x166]`) with backptr `ctl+0x2c4=form` + onChange
   `ctl+0x2bc = FLui_Mixer_Ctl_DispatchOnChange` (the master meters/scrollers — see §9), and records them into
   `PTR_DAT_014a8f38` as 3 groups of 3.
3. `FUN_005cf9a0(form,500)` (default width 500) etc.; computes meter scaling into `PTR_DAT_014abce0`.
4. **`FLui_Mixer_InitTrackDataArray @0x118a670`** — allocate + init the 502-track data model (§3).
5. **The 10-slot FX chain** (loop `local_68 != 10`): builds, per slot, 4 WP widgets parented on `form[0x155]`
   (the shared panel) — see §5. Stored in `PTR_DAT_014ab080[slot]` (stride 0x20).
6. Misc shared-panel wiring + `FLui_Mixer_WireSplittersScrollers @0x1182ca0` (splitter/scroller objects at
   `form+0xae8..0xb30`).
7. Sets `form+0x194 = TFXForm.FormResize` (resize handler), builds child-node list `form[0x1b1]`, lays out the
   master meter columns + scrollers (centered), then `FUN_01182ca0`/`FLui_Mixer_RelayoutShared`.

---

## 2. Form field map (`param_1` = form ptr; qword index N ⇒ byte offset N·8)
HIGH where it drives a named control; MED where inferred from layout math.

| field (byte) | qidx | role | conf |
|---:|---:|---|---|
| `+0x760` | 0xec | skin/font source (`+0x49c`) | MED |
| `+0x768` | 0xed | control w/ callback `+0x154=FUN_010c9030`, src `PTR_DAT_014a8750` | MED |
| `+0x798` | 0xf3 | control, sets `+0x151 |= 0x40` | LOW |
| `+0x868` | 0x10d | send-button popup menu object | MED |
| `+0x908/0x930/0x938` | 0x121/0x126/0x127 | **master peak-meter columns** (group A; +0xd0 inset) | MED |
| `+0x918/0x920/0x928` | 0x123/0x124/0x125 | controls w/ `+0xd0` inset (master sub-meters) | MED |
| `+0x940` | 0x128 | **central master meter / big area** (`FUN_005ddf30`; +0x94/+0x9c sized) | MED |
| `+0x948` | 0x129 | sizing reference control (`+0x94`,`+0x9c`) | MED |
| `+0x960/0x968/0x970` | 300/0x12d/0x12e | **master peak-meter columns** (group B) | MED |
| `+0x980/0x990` | 0x130/0x132 | controls resized via `FUN_005cf940` | LOW |
| `+0x9c0/0x9c8` | 0x138/0x139 | tracks-panel zoom/scroll src (`+0x492` byte read in strip build) | MED |
| `+0xaa8` | **0x155** | **selected-track SHARED PANEL** (parent of FX-slot + EQ + send widgets) | HIGH |
| `+0xab0/0xab8/0xac0/0xac8` | 0x156-0x159 | shared-panel sub-areas (EQ / wave / scroll) | MED |
| `+0xad0` | 0x15a | EQ band-visual host (used by `BandVisualPaint`) | MED |
| `+0xae8..0xaf8` | 0x15d-0x15f | scroller/splitter trio (left) — also in `PTR_DAT_014a8f38+0x80..` | MED |
| `+0xb08..0xb30` | 0x161-0x166 | **scrollers + splitter boxes** (wired by `FLui_Mixer_WireSplittersScrollers`) | HIGH |
| `+0xd60` | 0x1ac | control w/ `+0xd0` inset | LOW |
| `+0xd7c` | — | **active/focused strip control ptr** (set on strip click) | HIGH |
| `+0xd84` | — | drag/tweak-mode flag (multi-track relative tweak) | MED |
| `+0xd88` | 0x1b1 | child-node TList (`FUN_00510130`, layout) | MED |
| `+0xd90` | 0x1b2 | a quickbutton (descriptor `&DAT_0118a65c`) | LOW |
| `+0xd98..0xdd0` | 0x1b3-0x1ba | the 3 peak-meter groups (aliases of [0x121/0x126/0x127]+[0x12d/300/0x12e]) | MED |
| `+0xe10/0xe18` | 0x1c2/0x1c3 | layout TLists | LOW |
| `+0xe20 + track·8` | — | **per-track dB readout cache (double)** | HIGH |

---

## 3. Data model — the mixer track array (cross-ref re/12 / fl-control-catalog)
`FLui_Mixer_InitTrackDataArray @0x118a670` allocates + inits **`g_MixerTrackArrayPtr`**: a flat array of **502
tracks** (`do{…}while(iVar3 != 0x1f6)`), **stride `0x1474`**. `track[i] = g_MixerTrackArrayPtr + i*0x1474`.
Track 0 = **Master**; 0x1f5 = the **"Current"/selected** pseudo-track. Count = `*g_pMixerTrackCount`.
`FLpy_mixer_getTrackInfo` maps logical ids → {0→master, 1→1, 2→500, 3→0x1f5}.

Track-struct offsets (verified from the `FLpy_mixer_*` accessors + the build/refresh funcs):

| off | type | field |
|---:|---|---|
| `+0x04` | u32 | magic `'clq'` (0x716c63) |
| `+0x08` | u32 | kind (0 normal; >0 for master/special) |
| `+0x18` | u8 | **enabled** (0 ⇒ muted) — `isTrackMuted` returns `==0` |
| `+0x19` | u8 | track in-use / valid flag (gates strip build + change) |
| `+0x1a` | u8 | **solo** |
| `+0x28` | i32 | track index (self) |
| `+0x2d` | u8 | **dock side / group id** (left/centre/right column group) |
| `+0x4c` | CRIT | `CRITICAL_SECTION` |
| `+0x250/+0x254` | i32 | routing flags |
| `+0x2b4` | u32 | default level `0x3200` |
| `+0x2e8 + dest·8` | u8 | **send-active matrix** (row = source track; `[src*0x1474 + dest*8 + 0x2e8]`) |
| `+0xb8 + band·0x88` | — | **EQ band** record (3 bands; read region `+0xc4+band*0x88`; per-band param id `+0xc4/+0x14c/+0x1d4` = 6/7/8). Band floats incl. gain & freq (10..19990 Hz) |
| `+0x1324 + slot·8` | ptr | **FX slot plugin object** (10 slots) |
| `+0x1374 + slot·8` | ptr | FX slot UI mirror (built in `BuildLayoutStrips`) |
| `+0x13d0 .. +0x1470` | ptr[] | **strip child-control pointers** (see §4) |
| `+0x145c` | u8 | **armed** (record) |
| color | — | BGR via `FLmx_GetTrackColorCore(track)`; name via `FLmx_GetTrackNameCore(&out, idx, 0)` |

Data-side API (do NOT rename — re/12 lane): `FL_MixerEffectParamBase(track,slot) @0x11c23b0`,
`FLpy_mixer_*` (getTrackVolume/Pan/Name/Color/Peaks, isTrackMuted/Solo/Armed/Selected, setRouteTo,
setRouteToLevel, getRouteSendActive, getEqGain/Frequency/Bandwidth, set…, selectTrack, setActiveTrack via
op-event `FLmx_SetActiveTrack_op`, etc.). These read/write the same struct + command-bus ids the UI uses.

---

## 4. Track strips — build + per-strip widgets (`FLui_Mixer_BuildLayoutStrips @0x01183c80`)
One function builds **and** positions every strip (`~107 KB`; readable dump cached at
`scratchpad/FUN_01183c80.c` during this pass). Structure: outer iterate over tracks (filtered/ordered by dock
side `track+0x2d`); for each strip it (a) ensures the container, (b) **lazily creates each child control** if
missing — guard helpers `FUN_011839a0` / `FUN_011838c0` / `FUN_01183ad0` return "exists?" and on first build the
code sets descriptor (`ctl+0x328` = the WP control skin-descriptor UStr, == `plVarN[0x65]`), event-id
(`ctl[3]=ctl+0x18`), change/click handlers, then (c) `SetBounds` via `vtbl[0x188]`.

Per-strip controls (descriptor, storage offset in track struct, event id / handler):

| strip widget | skin descriptor | track-struct slot | id / handler |
|---|---|---|---|
| **Volume fader** | `forms.mixer.track.controls.slider.volume:slider` | `track+0x13e8` | id = `paramBase+0x70001fc0`; OnChange `FLui_Mixer_Strip_OnVolPanChange @0x11a4970` |
| **Pan wheel** | `forms.mixer.track.controls.wheel.pan:wheel` | `track+0x13f0` | id = `paramBase+0x70001fc1`; OnChange `…_OnVolPanChange`; click `…_OnClickSelectTrack @0x11a5070`; hint/r-click `FUN_011a50f0/…5110/…5130` |
| **Stereo-sep wheel** | `forms.mixer.track.controls.wheel.stereosep:wheel` | `+0x13d0` region | id = `paramBase+0x70001fc2` |
| **Send button** | `forms.mixer.track.controls.button.sendbutton:quickbutton` | `track+0x1408` | OnChange `FUN_011ad510`; sets caption `SendBtn%d` |
| **Rec/Arm button** | `forms.mixer.track.controls.button.recbutton:quickbutton` | `track+0x1410` | OnChange `FUN_011ad340`; click `FUN_011ad460` (descriptor at `ctl+0x328`) |
| **Delay button** | `forms.mixer.track.controls.button.delaybutton:quickbutton` | `+0x13d0` region | (delay/PDC popup) |
| **Plugins button** | `forms.mixer.track.controls.button.pluginsbutton:quickbutton` | `+0x13d0` region | opens FX chain on this track |
| **Rev-stereo / polarity** | `forms.mixer.track.controls.button.revstereo:quickbutton` | `+0x13d0` region | |
| **Flip-Y** | `forms.mixer.track.controls.button.flipy:quickbutton` | `+0x13d0` region | |
| **Mute button** | `forms.mixer.controls.button.mutebtn:quickbtn` | `+0x13d0` region | strip mute |
| **Send-level wheel** | `forms.mixer.track.wheel.sendwheel:wheel` | `track+0x1400` | per-destination; built in `RebuildSelTrackSendsCables` (§6) |
| **Peak meter** | `forms.mixer.track.meter.*` (background/gradient/color1..6/selected/border/text) | drawn on strip panel | |
| **Strip container panel** | `forms.mixer.track.*` (background/textcolor/header/selectedcolor/separator) | `track+0x1440` (+0x90 x, +0x98 w, +0x3b8 h-scroll-ofs) | |
| **Strip header tab** | (built by `FLui_Mixer_BuildStripHeaderTab @0x11ca730`) | `DAT_01582f9c + track·0x1474` | name=track#/"Master"/"Current", colour = track BGR |

Key handlers (renamed/identified this pass):
- **`FLui_Mixer_Strip_OnVolPanChange @0x11a4970`** — OnChange for the fader & pan wheel: dispatches
  `FL_DispatchCommand(ctl+0x18, value, 0x3dd)`, refreshes the dB cache `form+0xe20+track*8`, and when grouped/linked
  applies the relative tweak to the other selected tracks (`+0x70001fc0`, flag at `PTR_DAT_014a8a68`).
- **`FLui_Mixer_Strip_OnClickSelectTrack @0x11a5070`** — sets `form+0xd7c = clicked ctl` and selects the track via
  `FLui_Mixer_SelectTrackUI @0x01192fd0(form, track, 0x15)`.
- **`FLui_Mixer_Ctl_DispatchOnChange @0x11a46a0`** — generic: `FL_DispatchCommand(ctl+0x18, ctl+0x3c0, 4)` (used by
  the master meters/scrollers wired in FormCreate).
- Mute/solo via the strip button + context `TFXForm.SoloMenuClick @0x11af1d0`; arm via
  `TFXForm.ArmSelectedTracksMenuClick @0x11a5e30`.

---

## 5. FX slot chain UI (the selected-track effect slots) — built in `TFXForm.FormCreate`
The right panel shows **10 effect slots** for the selected track. Built in the FormCreate loop (10 iterations),
parented on `form[0x155]` (shared panel). Slot records live in **`PTR_DAT_014ab080`, stride `0x20`** (4 qwords),
one record per slot; each record holds 4 WP control pointers:

| record qword | control | descriptor | wiring |
|---:|---|---|---|
| `[2]` | **plugin-name button** | `forms.mixer.controls.button.plugin:quickbutton` | the slot's plugin name / open-editor button |
| `[0]` | **slot options button** | (quickbutton) | caption `"FX slot %d options"`; OnClick `FLui_Mixer_FXSlot_OnOptionsClick @0x01199fb0` |
| `[3]` | **mix-level wheel** | `forms.mixer.controls.wheel.pluginmixwheel:wheel` | tooltip `Mix level`; id = `paramBase+0x70001f01`; OnChange `TFXForm.MixSend1WheelChange @0x11a4680` |
| `[1]` | **slot mute button** | `forms.mixer.controls.button.pluginmutebtn:quickbtn` | tooltip `(ctrl+)0..9 Mute/solo`; id = `paramBase+0x70001f00`; OnClick `FLui_Mixer_FXSlot_OnMuteClick @0x0119a020` |

Mute on/off skin colours: `forms.mixer.controls.button.pluginmutebutton.on/offcolor`. Mouse-down on a mix wheel
= `TFXForm.MixWheelMouseDown @0x11a5190` → decode id (`FUN_011c2390`) → select that slot's track.
Plugin-list / "compact plugin list" menu: `TFXForm.CompPlugListMenuClick @0x1180840`,
`EffectsMenuPopup @0x11a0a10`, `EffectsMenuOpenWindow @0x11b13a0`, `BrowsePresetsMenuClick @0x11aea00`.

---

## 6. Sends / routing — `FLui_Mixer_RebuildSelTrackSendsCables @0x0118fdc0`
Called whenever the selected track changes. Keyed on `*(int*)PTR_DAT_014a9d28` (selected track). It:
1. Pushes the selected track's 10 FX-slot params + EQ params to the command bus (`FL_DispatchCommand(…,0x1a)`),
   using `FL_MixerEffectParamBase(sel, slot)`.
2. Iterates **all** tracks and, for each track the selected one can send to, **lazily creates a send-level wheel**
   (`forms.mixer.track.wheel.sendwheel:wheel`) stored at `dest_track+0x1400`; parents it to the dest strip
   (`dest+0x1408` parent), sets its id `paramBase+0x70002000+dest`, OnChange
   `TFruityLoopsMainForm.FrontVolWheelChange`, hint `TFruityLoopsMainForm.FrontVolWheelHint`. The send **button**
   is `dest+0x13f0`-style; tooltips: `Enable/Disable send from %s to %s`, `Cannot send from %s to %s`.
3. **Send-active matrix:** `g_MixerTrackArrayPtr[sel*0x1474 + dest*8 + 0x2e8]` (byte). Send wheel hidden when
   `dest == sel`.
4. Computes + draws the routing **cables** overlay (the `DAT_01581a48 + 0xd98 + n*0x18` cable buffers, 3 of them;
   painted by `TFXForm.CablesPanelMPaint @0x1183040`; toggled by `ViewCablesMenuClick @0x11aff50`).

Routing UI / menus:
- Output device / route select: `TFXForm.MixOutSelectChange @0x119a4f0`, `MixOutSelectBeforePopup @0x11ae9b0`,
  `MixOutSelectGetItemFlags @0x11a5cc0`, `OutMenuPopup @0x11af6e0`.
- Input / external input: `TFXForm.MixInSelectChange @0x119a840`, `MixInSelectClick @0x119fef0`,
  `MixInBtnPaintCaption @0x119a610`; ext-input monitor `ExtInputMonitorOffMenuClick @0x11a1300`,
  `ExtInputLevelWhenNotMonitoringMenuClick @0x11a12c0`.
- Route-to helpers: `RouteSelToThis1MenuClick @0x11af8f0`, `RouteThisToSel1MenuClick @0x11afd20`,
  `RouteToOnlyMenuClick @0x11afe40`, `FXTrackResetRoutingMenuClick @0x11aee80`,
  `FXTrackUnrouteRoutedTracksMenuClick @0x11aef20`, `ResetFXTrackMenuClick @0x11a6e00`.
- Data-side: `FLpy_mixer_setRouteTo`, `setRouteToLevel`, `getRouteToLevel`, `getRouteSendActive`,
  `afterRoutingChanged`.

---

## 7. Parametric EQ ("band visual")
The selected track has a 3-band parametric EQ rendered as an interactive curve:
- **`TFXForm.BandVisualPaint @0x11a2b70`** draws it. Reads the 3 bands from
  `g_MixerTrackArrayPtr + sel*0x1474 + band*0x88 + 0xc4` (sel = `*(int*)PTR_DAT_014a9d28`) into a 0x78-byte copy
  per band; per band uses gain (`band[1]`) and frequency (`band[2]`, 10..19990 Hz mapped log). Highlight = band ==
  `host+0x18` (hovered band).
- Interaction: `BandVisualMouseDown @0x11a3bf0`, `BandVisualMouseMove @0x11a46c0`, `BandVisualMouseWheel @0x11a48f0`;
  level slider mouse-up `MixEQLevel1SliderMouseUp @0x11a4300`.
- Skin keys: `forms.mixer.sharedpanel.bandvisual.{background,bordercolor,textcolor,texthighlightcolor,linecolor,
  linehighlightcolor}`.
- Command-bus ids (per band b, on `paramBase=FL_MixerEffectParamBase(track,0)`): gain `+0x70001fd0+b`,
  frequency `+0x70001fd8+b`, bandwidth `+0x70001fe0+b` (from `RebuildSelTrackSendsCables`).
- Data-side: `FLpy_mixer_getEqBandCount/getEqGain/getEqFrequency/getEqBandwidth` (+ setters).

---

## 8. Master strip + peak meters + scrolling
- **Master** = track index 0; its strip + the big master meters live in the form's `[0x121]/[0x126]/[0x127]` and
  `[0x12d]/[300]/[0x12e]` columns (recorded into `PTR_DAT_014a8f38` as 3×3 and into `form[0x1b3..0x1ba]`), with the
  central area `[0x128]`. Master vol/pan/sep ids = `FL_MixerEffectParamBase(0,0)+0x70001fc0/01/02`.
- **Peak meter** mouse: `TFXForm.PeakMeter_BigMouseMove @0x11a4570`; meter colours `forms.mixer.track.meter.color1..6`.
- **Scrolling / splitter:** the tracks panel is a scrolled container; `TFXForm.TracksPanelMAdjustScroller @0x11af360`
  sets the scroller step (per-strip width × zoom from `form[0x138]+0x492`), `TracksPanelMMouseWheel @0x11af620`,
  `TracksPanelMGesture @0x11af400`, `FormMouseWheel @0x11aeb20`, FX-offset scroller `FXOfsScrollerSetKnobWidth
  @0x11aee70`. The splitter between the strips and the shared panel: `SplitterBoxR{MouseDown @0x11a3e90, MouseMove
  @0x11a3fb0, MouseUp @0x11a40e0, Paint @0x11a41b0}` (skin `forms.mixer.splitter.*`). Scroller/splitter objects are
  bound in `FLui_Mixer_WireSplittersScrollers @0x1182ca0` (form `+0xae8/+0xb08/+0xb18` = scrollers, `+0xb10/+0xb20/
  +0xb28/+0xb30` = scrolled panels).

---

## 9. Track selection + add/insert/delete + grouping (menu/handler index)
- **Select track (UI)** = `FLui_Mixer_SelectTrackUI @0x01192fd0(form, track, 0x15)`; active-track global
  `*(int*)PTR_DAT_014a9d28`. Selection menus: `Select1 @0x1192420`, `Select2 @0x11924e0`, `SelectAll @0x11a5d90`,
  `SelectGroup @0x1192b90`, `SelectLinked @0x11a06e0`.
- **Add/Insert/Delete tracks:** `AddOneMixerTrackMenuClick @0x11abfc0`, `AddTrackAfterSelectedBtnClick @0x11ac6a0`,
  `InsertMixerTrackMenuItemClick @0x119ff60`, `DeleteMixerTrackMenuClick @0x119ff50`,
  `AddMixerTracksMenuBeforePopup @0x11abbe0`, `TrimUnusedMenuItemClick @0x11af6d0`.
- **Group / colour / icon / name:** `CreateGroupMenuClick @0x11ad600`, `SelectGroupMenuClick`, `AutoColorGroupMenuClick
  @0x11ae680`, `SetColorMenuClick @0x1192fa0`, `SetRandomColorMenuClick @0x11a62b0`, `SetGradientColorMenuClick
  @0x11a6540`, `SetIconMenuClick @0x1198b30`, `SetNameMenuClick @0x11a1d50` (`NewCaptionMouseDown @0x11a1d80`).
- **View toggles:** `ViewPPanelBtnClick @0x11a5bd0` (properties panel), `ViewSSBtnClick @0x11a5c20`,
  `ViewCtrlsMenuClick @0x11a5b10`, `ViewWaveMenuClick @0x11a5c70`, `ViewCablesMenuClick @0x11aff50`,
  `StripLinesMenuClick @0x118fd90`, `AlternativeMixerHighlightingMenuClick @0x11a5de0`, `MultiTouchBtnClick @0x11acf70`.
- **PDC / latency:** `AutoPDCMenuClick @0x11a2630`, `SetupPDCMenuClick @0x11a6c70`, `ResetManualPDCMenuClick @0x11a7120`,
  `CompensateMasterLatencyMenuClick @0x11a2580`, `CompensateAutomationsMenuClick @0x11a2520`, `AutoIOMenuClick @0x11a1ef0`.
- **Recording:** `RecRenderMenuClick @0x11aae90`, `RecSelectedMenuClick @0x11ab190`, `RecAUMenuClick @0x11ab3c0`,
  `AudioRecHwInputMenuClick @0x11ae170`, `AudioRecorderRoutingMenuPopup @0x11ae550`, `LogAudioMenuClick @0x11a56f0`,
  `PostFXRecApplyLevelMenuClick @0x119a0c0`.
- **Mix state / dock / delay:** `SaveMixStateMenuClick @0x11a51f0`, `OpenMixStateMenuClick @0x11a5870`,
  `DockToLMenuClick @0x1192db0`, `DockToMenuClick @0x1192f60`, `Delay*` family (`DelayMenuPopup @0x11b1460`, etc.).

---

## 10. Reuse recipe (build our own mixer-style strip / drive FL's)
- **Render a strip ourselves:** mirror `FLui_Mixer_BuildLayoutStrips` — for each track create a WP container
  (`FLui_WP_CreateControl`, re/ui-02), then children: a `slider`-class fader (descriptor
  `forms.mixer.track.controls.slider.volume:slider`), `wheel`-class pan/stereo-sep, `quickbutton`-class
  send/rec/mute, and a `track.meter`-painted peak meter; set each child's descriptor at `ctl+0x328`, event-id at
  `ctl[3]`, OnChange/OnClick TMethods, then `SetBounds (vtbl[0x188])`, realize + render (re/ui-02 §content-drop).
- **Drive FL's mixer from the bridge (no UI):** use the command-bus id space directly —
  `id = FL_MixerEffectParamBase(track, slot)`; set volume `FL_DispatchCommand(id+0x70001fc0, value0..0x3200, flags)`,
  pan `+0x70001fc1`, stereo-sep `+0x70001fc2`, FX-slot mute/mix `+0x70001f00/01`, EQ gain/freq/bw
  `+0x70001fd0/d8/e0 +band`, send-level `+0x70002000+dest`. Toggle send-active by poking
  `g_MixerTrackArrayPtr[src*0x1474 + dest*8 + 0x2e8]`. Select a track = `FLui_Mixer_SelectTrackUI(form, track, 0x15)`
  or data-side op-event `FLmx_SetActiveTrack_op`. Read state from the track struct (§3) or the `FLpy_mixer_*`
  accessors. (Matches the native control map in re/12 / fl-control-catalog.)
- After any direct struct poke that the UI caches (e.g. mute `+0x18`), the strip will refresh on the next
  selection/relayout; force it with `FLui_Mixer_RelayoutShared(form)` or `FLui_Mixer_RebuildSelTrackSendsCables`.

---

## 11. Ghidra annotations made this pass (area: Mixer)
- **Tag `UI_win_mixer` created** (with description) and attached to **128 functions** — all `TFXForm.*` published
  methods (FormCreate/Destroy/Show/Resize + every menu/handler: routing, EQ, meters, splitter/scroll, add/insert/
  delete, group/colour/icon/name, PDC, recording, view toggles, delay) plus the renamed build/layout/handler helpers.
- **Renamed** (previously unnamed `FUN_*`):
  - `FLui_Mixer_BuildLayoutStrips @0x01183c80` (the master strip build+layout)
  - `FLui_Mixer_RebuildSelTrackSendsCables @0x0118fdc0` (selected-track sends/EQ/FX/cables refresh)
  - `FLui_Mixer_RelayoutShared @0x011a1c80` (relayout entry, from FormResize)
  - `FLui_Mixer_WireSplittersScrollers @0x01182ca0`
  - `FLui_Mixer_InitTrackDataArray @0x0118a670` (502-track data model init)
  - `FLui_Mixer_Ctl_DispatchOnChange @0x011a46a0` (generic ctl→command-bus)
  - `FLui_Mixer_BuildStripHeaderTab @0x011ca730` (strip header: number/name/colour)
  - `FLui_Mixer_Strip_OnVolPanChange @0x011a4970`
  - `FLui_Mixer_Strip_OnClickSelectTrack @0x011a5070`
  - `FLui_Mixer_SelectTrackUI @0x01192fd0`
  - `FLui_Mixer_FXSlot_OnOptionsClick @0x01199fb0`
  - `FLui_Mixer_FXSlot_OnMuteClick @0x0119a020`
- Program saved. (No plate/decompiler-comment MCP endpoint is exposed on this Ghidra instance — semantics are
  captured here + in the tag comment + the `TFXForm.*` RTTI names; the tag groups the whole window.)
- **Cross-area finds (noted, NOT renamed — other lanes):** `FL_MixerEffectParamBase @0x011c23b0` (re/12 data),
  all `FLpy_mixer_* @0x00e05d30..0x00e0ba00` (Python/data API, re/12), `FLmx_GetTrackNameCore`/`GetTrackColorCore`/
  `SetActiveTrack_op` (data core), `FLui_Ctl_DigiWheel_CreateMixerTrack @0x00f0de00` (channel-rack "Target mixer
  track" wheel — channel-rack lane), `FL_DispatchCommand` (re/08 command bus), the send-wheel OnChange
  `TFruityLoopsMainForm.FrontVolWheelChange`/`…Hint` (shell lane).

---

## 12. Confidence + gaps
- **HIGH:** form identity (TFXForm/VMT/descriptor/FormCreate/family/singleton); the track data model
  (`g_MixerTrackArrayPtr`, 502×0x1474) + flag offsets (enabled `+0x18`, solo `+0x1a`, armed `+0x145c`, dock `+0x2d`,
  send matrix `+0x2e8`); strips are real WP children stored in the track struct (fader `+0x13e8`, pan `+0x13f0`,
  send wheel `+0x1400`, send btn `+0x1408`, arm `+0x1410`, panel `+0x1440`); the full strip descriptor set; the
  10-slot FX chain (`PTR_DAT_014ab080`, stride 0x20) + its 4 widgets; selected-track global `PTR_DAT_014a9d28`;
  the command-bus id map (vol/pan/sep/EQ/FX/send); EQ band layout; the build/relayout/refresh entry functions.
- **MED:** exact track-struct offsets for the less-used strip buttons (delay/plugins/revstereo/flipy/stereosep —
  confirmed present in the `+0x13d0..+0x143x` region & built in order, individual offsets not each pinned); the
  identity of each master-meter form field `[0x121..0x12e]` (grouped as 3+3 columns by layout math); the cable
  buffer struct (`DAT_01581a48+0xd98`).
- **GAPS / open items:** (1) `FLui_Mixer_BuildLayoutStrips` is ~107 KB — only the headline controls + guards were
  walked; a full offset table for every strip child would need another focused pass over the cached dump. (2) The
  exact size of the `TFXForm` instance (≳0x1de0, not pinned). (3) Strip drag-reorder / drag-to-route gesture path
  (`SplitterBoxR*`, gesture handler) only catalogued, not traced. (4) The `track+0x1374` FX-slot UI-mirror vs
  `+0x1324` plugin-ptr relationship (both arrays of 10) is inferred. None block the reuse recipe in §10.
