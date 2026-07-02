# Controls — CHANNEL RACK (FLEngine_x64.dll, image base 0x400000)

Static RE of the Channel Rack control domain: channel list, per-channel params
(vol/pan/pitch/mute/solo/name/color/route), the step-sequencer grid + per-step note
params, and the channel object struct. All addresses are RVAs against base 0x400000
(i.e. Ghidra absolute = base + RVA; here shown as the absolute Ghidra address).

## Core mechanism (how a channel op is addressed)

Every channel has a **rec-event id base** = `recTag << 16`, where `recTag` is the channel's
persistent global index. Both fields live on the `TChannel`:
- `TChannel + 0x7a4` = `recTag` (int, the global channel number). Returned by `channels.getChannelIndex`.
- `TChannel + 0x9c`  = `recEventId` = `recTag << 16`. Returned by `channels.getRecEventId`.

A channel parameter is addressed by **cmdId = recEventId + paramIndex**:

| paramIndex | parameter | notes |
|---|---|---|
| `0x0` | volume | |
| `0x1` | pan | |
| `0x4` | pitch | |
| `0x7` | mute / enabled | used by `TPluginForm.ChannelMuteBtnClick` |
| `0x8` | target mixer FX track | used by `FLcr_ApplyChannelFxRoute` |
| `+ 0x4000` | step-sequencer step notes | grid note events (`FLcr_SetStepNote`) |
| `+ 0x8000 + n` | generator/plugin param n | plugin wheels (`+0x8000` distinguishes plugin params from built-ins) |
| `+ trackN*0x10` | per-track (layered channel) offset | e.g. TrackPan = recEventId + trackN*0x10 + 1 |

Two interchangeable engine entry points operate on these ids:
- **`FL_DispatchCommand(cmdId, value, flags)` @ 0xF53FE0** — the command bus (06/08). Used by all
  UI wheels/buttons, and by the getters with `flags=2` to **read** the current value.
- **`FUN_00f5f1a0(eventId, value30, 0x3FD, 0,0,0,0)`** — the REC-event value poster used by the
  script natives' inner setters (preceded by `FUN_00ea2560(eventId, flagByte)`). Here `value30`
  is the **normalized value × 2^30** (0..0x40000000 = 0.0..1.0).

Value domains differ by path:
- **Command-bus (FL_DispatchCommand) domain** (authoritative from the getters' `flags=2` reads):
  volume `0..12800` (default **10000** ≈ 78.1%; norm = raw/12800), pan `0..12800` (center **6400**;
  norm = raw/12800*2−1), pitch in **cents** (range = `TChannel+0x324` semitones × 100).
- **Native poster (FUN_00f5f1a0) domain**: normalized ×2^30 (vol = `vol*2^30`; pan = `(pan*0.5+0.5)*2^30`).

## Operation table

cmdId formulas assume `B = recEventId = *(int*)(TChannel+0x9c) = (*(int*)(TChannel+0x7a4)) << 16`.

| Operation | cmdId / fn(args) | units / range | flags | notes |
|---|---|---|---|---|
| **Channel count (current view)** | `*(int*)((*0x14A7968)+0x10)` | int | — | filtered/group list |
| **Channel count (all)** | `*(int*)((*0x14A98D8)+0x10)` | int | — | `channels.channelCount(1)` |
| **Resolve channel by index** | `FLcr_ResolveChannelByIndex(idx, useGlobalList, &out)`@0xF22AC0 | →bool, out=TChannel* | — | global list `*0x14A98D8`, filtered `*0x14A7968` |
| **List item by index** | `FLcr_ChannelListGetItem(listObj, idx)`@0xF00F80 | →TChannel* | — | |
| **Selected channel #** | `FLpy_channels_selectedChannel`@0xE007F0 → `FUN_010E3C90` | int | — | |
| **Set volume** | `FL_DispatchCommand(B+0, v, 0x3DD)` | 0..12800 (10000=default) | `0x3DD` | UI: `TPluginForm.VolWheelChange`@0xE86DA0. Native poster: `vol*2^30` |
| **Get volume** | `FL_DispatchCommand(B+0, 0, 2)` | →0..12800 | `0x2` | `channels.getChannelVolume`@0xE01520 (norm=ret/12800) |
| **Set pan** | `FL_DispatchCommand(B+1, v, 0x3CD)` | 0..12800 (6400=center) | `0x3CD` | UI: `TPluginForm.TrackPanWheelChange`@0xE942A0 (B + trackN*0x10 + 1). Native: `(pan*0.5+0.5)*2^30` |
| **Get pan** | `FL_DispatchCommand(B+1, 0, 2)` | →0..12800 | `0x2` | `channels.getChannelPan`@0xE01990 (norm=ret/12800*2−1) |
| **Set pitch** | `FL_DispatchCommand(B+4, cents, 0x3CD)` | cents, ±(range*100) | `0x3CD` | UI: `TPluginForm.PitchWheelChange`@0xE86DD0. Range field `TChannel+0x324` |
| **Get pitch** | `FL_DispatchCommand(B+4, 0, 2)` | →cents | `0x2` | `channels.getChannelPitch`@0xE01DB0 |
| **Set pitch range (semitones)** | `FLcr_ApplyChannelPitch`@0xE01ED0 mode 2 → `TChannel+0x324 = clamp(v,1,0x30)` | 1..48 semis | — | also PostMessage 0x550 |
| **Toggle/set mute** | `FL_DispatchCommand(B+7, enabledByte, 0x3DD)` | 0/1 | `0x3DD` | UI: `ChannelMuteBtnClick`@0xE7F510. Native `FLcr_ApplyChannelMute`@0xE00FE0 toggles `*(TChannel+0x7cc)+0x492` |
| **Get mute** | `*(byte*)(*(TChannel+0x7cc)+0x492) == 0` | bool | — | `channels.isChannelMuted`@0xE00F40 (byte=1 means *un*muted) |
| **Toggle/set solo** | `FLcr_ApplyChannelSolo`@0xE012F0 → `FUN_00F12940(TChannel,0)` | toggle | — | `channels.soloChannel`@0xE013A0 |
| **Get solo** | `FUN_00F12820(TChannel,0)` (+ byte `+0x35e`, `+0x8a2`) | bool | — | `channels.isChannelSolo`@0xE01230 |
| **Set name** | `FLcr_ApplyChannelName`@0xE00A40 → vtable slot `+0x70` | string | — | `channels.setChannelName`@0xE00AD0 |
| **Get name** | vtable slot `+0x68` | string | — | `channels.getChannelName`@0xE00920 |
| **Set color** | `FLcr_ApplyChannelColor`@0xE00D20 → vtable slot `+0x78` | 0xFFRRGGBB (stored BGR) | — | `channels.setChannelColor`@0xE00DE0; field `TChannel+0x78` |
| **Get color** | `TChannel+0x78` (R/B swapped, alpha 0xFF) | 0xFFRRGGBB | — | `channels.getChannelColor`@0xE00C70 |
| **Set mixer route (FX track)** | `FLcr_ApplyChannelFxRoute`@0xE03640 → `FL_DispatchCommand(B+8, track, 0x3DD)` | 0..0x1F4 | `0x3DD` | layered uses `0x83DD`; field `TChannel+0x288` |
| **Get mixer route** | `TChannel+0x288` | int | — | `channels.getTargetFxTrack`@0xE035B0 |
| **Get channel type** | `TChannel+0x190` | int | — | `channels.getChannelType`@0xE02370 |
| **Get MIDI-in port** | `TChannel+0x328` | int | — | `channels.getChannelMidiInPort`@0xE03480 |
| **Get activity level** | `*(float*)(TChannel+0x7b4)` | float | — | `channels.getActivityLevel`@0xE04910 |
| **Select channel** | `FLcr_ApplyChannelSelect`@0xE024B0 → `FUN_00F10A70(TChannel,state)` | -1=toggle/0/1 | — | `channels.selectChannel`@0xE02580; sel byte `TChannel+0x35c` |
| **Select exclusively** | `FLcr_ApplyChannelSelectOne`@0xE026E0 | | — | `channels.selectOneChannel`@0xE02770 |
| **Is selected** | `*(byte*)(TChannel+0x35c)` | bool | — | `channels.isChannelSelected`@0xE02420 |
| **Select all / deselect all** | handlers `FUN_00E028B0` / `FUN_00E029D0` | — | — | `channels.selectAll`@0xE02900 / `deselectAll`@0xE02A20 |
| **Add channel** | `FLcr_OpenAddChannelPicker(0,0,typeFilter)`@0xDAE920 | opens picker | — | UI only (no one-shot native); type filter at picker+0x7e4. Right-click rack menu via `TFruityLoopsMainForm.ChannelMenuPopup`@0x10EF450 |
| **Delete / clone / move / reorder** | — | — | — | UI only: `TFruityLoopsMainForm.ChannelMenuPopup`@0x10EF450 menu commands + channel-rack obj `*(*0x14A8750+0x1F98)` (ctx-menu vtable slot `+0xD0`) |
| **MIDI note on** | `channels.midiNoteOn`@0xE04040 → handler `FUN_00E03F90` | (note,vel,...) | — | live preview trigger |

### Step sequencer (grid)

| Operation | cmdId / fn(args) | range | notes |
|---|---|---|---|
| **Set grid bit** | `channels.setGridBit`@0xE02D30 → `FLcr_ApplyGridBit`@0xE02C80 → `FLcr_SetStepNote(grid, step, on, 1, vel=100)`@0x11D6920 | step≥0, on=0/1, vel 0..0x7F | grid obj `TChannel+0x7bc`; step note event id `= B + 0x4000`; step scaled by `*0x14A9C90` (ticks/step) |
| **Get grid bit** | `channels.getGridBit`@0xE02BD0 → `FLcr_GetStepNote(grid, step)`@0x11D6640 | →bool | cached bitmap `grid+0x22fc`, else scans notes |
| **Grid bit assigned?** | `channels.isGridBitAssigned`@0xE02B10 | →bool | true if `TChannel+0x7bc != 0` |
| **Get grid bit (w/ loop)** | `channels.getGridBitWithLoop`@0xE033C0 | →bool | |
| **Fill every N steps** | `channels.fillEachNSteps`@0xE02F60 → `FUN_00E02E90` | (n,...,vel=100) | |
| **Get step param** | `channels.getStepParam`@0xE031B0 → `FLcr_GetStepNoteParam(recTag,x,step,paramId)`@0xE030E0 | per-step note prop | paramId: 0=note/key, 1, 2=velocity, 3, 4, 5/6/7=color/mod, 8 |
| **Get current step param** | `channels.getCurrentStepParam`@0xE032E0 | | playback-position variant |
| **Set step param by index** | `channels.setStepParameterByIndex`@0xE04770 → `FLcr_ApplyStepParam`@0xE046B0 → `FUN_012109B0(recTag,a,b,c,d)` | | the per-step "graph editor" props writer |

### Graph editor (per-step params)
- `channels.isGraphEditorVisible`@0xE041A0, `updateGraphEditor`@0xE04230,
  `closeGraphEditor`@0xE04360, `showGraphEditor`@0xE04540.
- Per-step note properties are read/written via `FLcr_GetStepNoteParam`/`FLcr_ApplyStepParam`
  (paramId selects which step property; values are the step-note record fields decoded in
  `FUN_00E030E0`).

### Misc channel natives (same module)
- `channels.processRECEvent`@0xE005D0, `incEventValue`@0xE006C0 — generic event read/modify on any
  channel param event id (`B + paramIndex`).
- `channels.showCSForm`@0xE03A20, `showEditor`@0xE03C20, `focusEditor`@0xE03E10,
  `quickQuantize`@0xE04A80, `rerollLoopStarter*`@0xE04D40/0xE050C0/0xE05360.
- Mixer-side routing helpers (mixer module): `mixer.linkChannelToTrack`@0xE0A050,
  `linkTrackToChannel`@0xE09DD0.

## Data structures

### Globals (engine roots)
| Global | Meaning |
|---|---|
| `*0x14A98D8` | **all-channels** list; count at `+0x10`; item via `FLcr_ChannelListGetItem(list, idx)` |
| `*0x14A7968` | **current/filtered** channel list (group view); count at `+0x10` |
| `*0x14A8750` | main app/host object; `+0x1F98` = channel-rack module (ctx-menu vtable `+0xD0`); `+0x760`/`+0x828` menus |
| `*0x14A9C90` | step timebase (ticks per step) used to scale grid step positions |
| `DAT_0157EC58` | add-channel picker form (lazily created by `FLcr_OpenAddChannelPicker`) |
| `PTR_DAT_014ABAC8` | `PyArg_ParseTupleAndKeywords` ptr (native arg parsing) |
| `FUN_00f5f1a0` | REC-event value poster `(eventId, value×2^30, 0x3FD,...)` |
| `FUN_00ea2560` | event meta/setup called before the poster |

### TChannel (VMT 0xEFD190, instance size 0x7A8, parent VMT 0x11B4668)
| Offset | Type | Field |
|---|---|---|
| `+0x78`  | u32 | color (BGR; R/B swapped on get, alpha forced 0xFF) |
| `+0x9c`  | int | **recEventId** = `recTag << 16` (channel param event base) |
| `+0x190` | int | channel type (sampler / native plugin / VST / layer / automation) |
| `+0x288` | int | target mixer FX track (route) |
| `+0x324` | int | pitch range, semitones (clamp 1..0x30; default 2) |
| `+0x328` | int | MIDI-in port |
| `+0x35c` | u8  | selected flag |
| `+0x35e` | u8  | solo-related flag |
| `+0x7a4` | int | **recTag** (global channel index) |
| `+0x7b4` | float | activity level (meter) |
| `+0x7bc` | ptr | **step-sequencer grid / pattern note data** object |
| `+0x7cc` | ptr | channel module/state sub-object; `+0x492` = enabled byte (1=on, 0=muted) |
| `+0x8a2` | u8  | solo-related flag |
- Name via vtable slot `+0x68` (get) / `+0x70` (set); color via vtable slot `+0x78` (set).

Step-seq grid object (`*(TChannel+0x7bc)`):
| Offset | Field |
|---|---|
| `+0x3b4` | back-ref to owning `TChannel` |
| `+0x22fc` | cached step bitmap (byte/step), length at `[-8]` |
- Step note event id written by `FLcr_SetStepNote` = `(*(TChannel+0x9c)) + 0x4000`.

### Op dispatch plumbing
- Script natives package args and call **`FL_DispatchOpEvent(ctx, resultBuf, handlerFn, strArg,
  arg0..arg5, syncFlag)` @ 0xE2C350**. The handler receives a record where `+0x18`=arg0,
  `+0x20`=arg1, `+0x28`=arg2, `+0x30`=arg3, `+0x38`=arg4, `+0x40`=arg5, `+0x48`=strArg,
  `+0x58`=handler, `+0x60`=result. `ctx` comes from `FL_ResolveChannel`@0xE05CB0 (`(*0x14A7DC0)()`,
  null until Python init — the injected DLL should use the engine singleton reachable from host
  context `0x14A7E40`, per re/06).
- Channels resolved inside handlers by `FLcr_ResolveChannelByIndex(idx, useGlobal, &out)`@0xF22AC0.

## Ghidra symbols renamed (prefix FLcr_)
| Address | New name |
|---|---|
| 0xE016B0 | FLcr_ApplyChannelVolume |
| 0xE01A80 | FLcr_ApplyChannelPan |
| 0xE01ED0 | FLcr_ApplyChannelPitch |
| 0xE00FE0 | FLcr_ApplyChannelMute |
| 0xE012F0 | FLcr_ApplyChannelSolo |
| 0xE024B0 | FLcr_ApplyChannelSelect |
| 0xE026E0 | FLcr_ApplyChannelSelectOne |
| 0xE00A40 | FLcr_ApplyChannelName |
| 0xE00D20 | FLcr_ApplyChannelColor |
| 0xE03640 | FLcr_ApplyChannelFxRoute |
| 0xE02C80 | FLcr_ApplyGridBit |
| 0x11D6920 | FLcr_SetStepNote |
| 0x11D6640 | FLcr_GetStepNote |
| 0xE030E0 | FLcr_GetStepNoteParam |
| 0xF22AC0 | FLcr_ResolveChannelByIndex |
| 0xF00F80 | FLcr_ChannelListGetItem |
| 0xE046B0 | FLcr_ApplyStepParam |
| 0xDAE920 | FLcr_OpenAddChannelPicker |

(The `FLpy_channels_*` natives and `FL_DispatchCommand`/`FL_DispatchOpEvent`/`FL_ResolveChannel`
were already named in prior passes; each got a plate comment where decompiled here.)
