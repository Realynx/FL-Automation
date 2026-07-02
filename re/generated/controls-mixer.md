# controls-mixer.md — MIXER & FX control domain (FLEngine_x64.dll)

Static RE in Ghidra (image base 0x400000; addresses below are absolute Ghidra addrs).
Source: the engine's `mixer` PyMethodDef natives (`FLpy_mixer_*`, RVA cluster `0xE05xxx..0xE0Bxxx`)
plus their inner engine op-handlers (renamed `FLmx_*`). The injected DLL is native, so prefer the
**inner op / param-id / struct write** column over the Python wrapper.

## Core mechanisms

- **Param-id scheme** — `FL_MixerEffectParamBase(track, slot)` @ `0x11C23B0` = `((track*0x40 + slot) << 16)`.
  - `slot 0..9` selects an FX slot; the **track itself** uses the slot-0 namespace.
  - Track built-in controls and EQ live at `base(track,0) + 0x70001f00..0x70001fff`.
  - FX plugin params (per slot): `base(track,slot) + paramIndex + 0x70008000` (the `0x70000000` bit = "mixer" target).
  - `0x70000000` = mixer flag on the cmdId fed to the param event bus.
- **Param value bus** — three low-level primitives (shared/global, NOT mixer-specific, so left un-renamed):
  - `FUN_00F5F1A0(cmdId, value, flags, ...)` — set param event value (used by vol/pan/sep/slot-mix).
  - `FUN_00F5E6F0(cmdId, value, flags)` — set param (used by EQ, flags `0x11`).
  - `FUN_00F5E780(cmdId, 0x7fffffff, 1)` — **read** current param value.
  - `FL_DispatchCommand(cmdId, 0, 2)` @ `0xF53FE0` — also used as a param **getter** (see getPluginMuteState).
- **Op dispatch** — setter wrappers build a managed result-record (`FUN_00415610(buf,&DAT_00e28818)`),
  fetch the scripting context root (`FUN_00E0C4D0(self)` → `*ctx`), parse args, then call
  `FL_DispatchOpEvent(ctx, resultBuf, FLmx_*_op, flagArg, arg0, arg1, arg2, arg3, 0,0, 1)` @ `0xE2C350`.
  The op fn receives an op-struct: `+0x18`=arg0, `+0x20`=arg1(ptr for floats), `+0x28`=arg2, `+0x30`=arg3,
  `+0x60`=result/error slot, `+0x10`=extra (e.g. name handle / route index).
- **Value units** — float controls are fixed-point `value = floatVal * 2^30` (mask `9.313226e-10 = 2^-30`
  on read). Send level uses `int = levelFloat * 16000`.
- **Main-thread only.** All ops first check `(*0x14abca8 ...)+0xa0` (a main-thread/engine-ready gate).

## Operation map

| Operation | wrapper (RVA) | inner op / param-id / struct write | units / range | flags | notes |
|---|---|---|---|---|---|
| trackCount | `e05f40` | `*g_pMixerTrackCount` | int (=127: master+125+current) | — | also `getTrackCount e0ba00` |
| getTrackInfo(what) | `e05f60` | const: 0→0,1→1,2→500,3→0x1F5 | indices | — | max routable dest = 0x1F5(501) |
| getTrackName | `e05ff0` | `FLmx_GetTrackNameCore 11c2450` → struct+0x0C | string | — | default by type@+0x08 |
| setTrackName | `e061d0` | `FLmx_SetTrackName_op e06160` → struct+0x0C | string | — | name via op `+0x10` |
| getTrackColor | `e06330` | `FLmx_GetTrackColorCore 11c59f0` → struct+0x04 | 0xAARRGGBB | — | stored BGR, swapped on read |
| setTrackColor | `e064a0` | `FLmx_SetTrackColor_op e063f0` → `FLmx_SetTrackColorCore 11c58a0` struct+0x04 | 0xAARRGGBB | — | sets custom-color flag@+0x00 |
| getSlotColor | `e065c0` | `FUN_011c22d0(slotPlugin@+0x1324[slot])` | 0xAARRGGBB | — | color is a plugin-object prop, slot 0..9 |
| setSlotColor | `e067a0` | (plugin-object setter) | 0xAARRGGBB | — | slot 0..9 |
| isTrackArmed | `e068d0` | struct+0x145C | bool | — | |
| armTrack | `e06a70` | `FLmx_SetTrackArmed_op e06970` → `FLmx_SetTrackArmedCore 11c59d0` struct+0x145C | 0/1, -1=toggle | — | |
| isTrackSolo | `e06bb0` | struct+0x1A | bool | — | |
| soloTrack | `e06d20` | `FLmx_SetTrackSolo_op e06c50` → `FLmx_SetTrackSoloCore 117f9a0` struct+0x1A | mode 0..2 (def 2), val -1=toggle | — | solo-exclusive |
| isTrackEnabled | `e06e80` | struct+0x18 | bool | — | |
| enableTrack | `e06f90` | `FLmx_SetTrackEnabled_op e06f20` → `FLmx_SetTrackEnabledCore 117f050` struct+0x18 | 0/1, -1=toggle | — | |
| isTrackMuted | `e070d0` | `struct+0x18 == 0` | bool | — | mute = !enabled |
| muteTrack | `e07180` | `FLmx_SetTrackEnabled_op e06f20` (value inverted) struct+0x18 | 0/1, -1=toggle | — | shares enable core |
| isTrackMuteLock | `e072e0` | struct+0x1B | bool | — | |
| getTrackPluginId | `e07380` | `FL_MixerEffectParamBase(track,slot)` | int (param base) | — | =(track*64+slot)<<16 |
| isTrackPluginValid | `e07420` | `FUN_012297e0(slotPlugin@+0x1324[slot])` | bool | — | slot 0..9 |
| isTrackAutomationEnabled | `e074e0` | `*(slotPlugin@+0x1324[slot] + 0x178)` | bool | — | per-slot plugin flag |
| getTrackVolume | `e075a0` | read `base(track,0)+0x70001fc0` | float 0..1 (or dB if mode=1) | — | dB path uses log curve |
| setTrackVolume | `e07880` | `FLmx_SetTrackVolume_op e07730`: set `base(track,0)+0x70001fc0` | float 0..1 → *2^30 | `0x3f5` | 1.0=0dB unity≈0.8 |
| getTrackPan | `e079d0` | read `base(track,0)+0x70001fc1` | float -1..1 | — | `(v*2^-30-0.5)*2` |
| setTrackPan | `e07c40` | `FLmx_SetTrackPan_op e07ac0`: set `base(track,0)+0x70001fc1` | float -1..1 | `0x3f5` | |
| getTrackStereoSep | `e07d90` | read `base(track,0)+0x70001fc2` | float -1..1 | — | |
| setTrackStereoSep | `e08000` | `FLmx_SetTrackStereoSep_op e07e80`: set `base(track,0)+0x70001fc2` | float -1..1 | `0x3f5` | |
| getPluginMixLevel | `e08150` | read `base(track,slot)+0x70001f01` | float 0..1 | — | FX slot dry/wet, slot 0..9 |
| setPluginMixLevel | `e08640` | `FLmx_SetSlotMixLevel_op e08500`: set `base(track,slot)+0x70001f01` | float 0..1 → *2^30 | `0x3f5` | slot 0..9 |
| getPluginMuteState | `e08210` | `FL_DispatchCommand(base(track,slot)+0x70001f00, 0, 2)` | bool | `2` (read) | |
| setPluginMuteState | `e083b0` | `FLmx_SetSlotEnabled_op e082b0`: set `base(track,slot)+0x70001f00` | 0/1 (0=bypassed) | `0x3f5` | slot 0..9 |
| isTrackSelected | `e087a0` | struct+0x19 | bool | — | |
| selectTrack | `e08920` | `FLmx_SelectTrack_op e08840` struct+0x19 | 0/1, -1=toggle | — | |
| selectAll | `e08ad0` | (all struct+0x19=1) | — | — | |
| deselectAll | `e08ba0` | (all struct+0x19=0) | — | — | |
| setActiveTrack | `e08d00` | `FLmx_SetActiveTrack_op e08c70` | track idx | — | focus/active track |
| setRouteTo | `e08ea0` | `FLmx_SetRouteTo_op e08e20` → `FLmx_SetRouteActiveCore 11a67f0` toggles struct+0x2E8+dest*8 | enable 0/1, -1=toggle | — | args "iii\|i"=src,dest,val,mode; mode&2=no-refresh |
| setRouteToLevel | `e090d0` | `FLmx_SetRouteLevel_op e09010`: write struct+0x2E4+dest*8 = level*16000 | float (1.0=unity) | — | needs route valid (`FUN_0117fdf0`) |
| getRouteToLevel | `e09220` | read int@struct+0x2E4+dest*8 / 16000 | float | — | |
| getRouteSendActive | `e092c0` | read struct+0x2E8+dest*8 | bool | — | dest 0..0x1F5 |
| afterRoutingChanged | `e093b0` | refresh notify | — | — | |
| getTrackPeaks | `e09a00` | `*g_MixerPeakBufferPtr + track*0x10 + chan*4` | float (modes: 0=L,1=R,2=max,3..5=log) | — | separate meter buffer |
| getLastPeakVol | `e0a510` | global last-peak | float | — | |
| linkTrackToChannel | `e09dd0` | link op | — | — | |
| linkChannelToTrack | `e0a050` | link op | — | — | |
| getTrackDockSide | `e0a590` | struct+0x2D | 0/1/2 | — | left/none/right dock |
| isTrackRevPolarity | `e0a630` | struct+0x2C0 | bool | — | |
| revTrackPolarity | `e0a790` | (toggle struct+0x2C0) | 0/1, -1=toggle | — | |
| isTrackSwapChannels | `e0a8d0` | struct+0x2C1 | bool | — | |
| swapTrackChannels | `e0aa30` | (toggle struct+0x2C1) | 0/1, -1=toggle | — | |
| isTrackSlotsEnabled | `e0ab70` | struct+0x13CD | bool | — | FX rack on/off |
| enableTrackSlots | `e0acc0` | (set struct+0x13CD) | 0/1, -1=toggle | — | |
| getActiveEffectIndex | `e0afe0` | global | int | — | |
| getEqBandCount | `e0b0a0` | const 3 | — | — | 3-band mixer EQ |
| getEqGain(t,band) | `e0b0d0` | read `base(t,0)+band+0x70001fd0` | float 0..1 (dB=(v-0.5)*36) | — | band 0..2, ±18dB |
| setEqGain | `e0b2b0` | `FLmx_SetEqGain_op e0b1f0`: set `base(t,0)+band+0x70001fd0` | float | `0x11` | band 0..2 |
| getEqFrequency | `e0b3f0` | read `base(t,0)+band+0x70001fd8` | float 0..1 | — | band 0..2 |
| setEqFrequency | `e0b620` | `FLmx_SetEqFreq_op e0b560`: set `base(t,0)+band+0x70001fd8` | float | `0x11` | band 0..2 |
| getEqBandwidth | `e0b750` | read `base(t,0)+band+0x70001fe0` | float 0..1 | — | band 0..2 |
| setEqBandwidth | `e0b8d0` | `FLmx_SetEqBandwidth_op e0b810`: set `base(t,0)+band+0x70001fe0` | float | `0x11` | band 0..2 |
| getTrackRecordingFileName | `e09c20` | recording path | string | — | |
| getEventValue / automateEvent / remoteFindEventValue | `e094a0` / `e09890` / `e09540` | MIDI/automation event bridge | — | — | event-id layer |

### Track built-in param map (slot-0 namespace, `base(track,0) + offset`)
| offset | control |
|---|---|
| `+0x70001f00` | (slot) FX enable/bypass — at `base(track,slot)` |
| `+0x70001f01` | (slot) FX dry/wet mix — at `base(track,slot)` |
| `+0x70001fc0` | track volume |
| `+0x70001fc1` | track pan |
| `+0x70001fc2` | track stereo separation |
| `+0x70001fd0..d2` | EQ gain band 0..2 |
| `+0x70001fd8..da` | EQ frequency band 0..2 |
| `+0x70001fe0..e2` | EQ bandwidth band 0..2 |
| `+0x70008000 + idx` | FX plugin param idx — at `base(track,slot)` |

## FX slots (10 per track)
- Per track, **10 FX-slot plugin object pointers** at `trackstruct + 0x1324` (8 bytes each, indexed by slot 0..9).
  `slotPlugin = *(void**)(trackBase + slot*8 + 0x1324)` (0 = empty).
- **Bypass/enable** & **dry/wet** are param-events on `base(track,slot)+0x70001f00 / +0x70001f01` (above).
- **Validity:** `FUN_012297e0(slotPlugin)`. **Slot color:** `FUN_011c22d0(slotPlugin)` (plugin-object prop).
- **Per-slot automation-enabled:** byte at `slotPlugin + 0x178`.
- **Add/replace/remove/move** a plugin in a slot has no `mixer.*` native; it is driven by `TFXForm` menu
  handlers (UI command path), e.g. `TFXForm.CompPlugListMenuClick 0x1180840` (choose/add effect),
  `TFXForm.FXDetachMenuClick 0x119FF20`, `TFXForm.ResetFXTrackMenuClick 0x11A6E00` (clear track),
  `TFXForm.MoveLeftMenuClick 0x11AFF80` (reorder). The slot pointer array `+0x1324` is the model side.

## Routing / sends
- Each track has a **send table at `trackstruct + 0x2E4`**, 8 bytes per destination, up to 0x1F6 (502) dests:
  - `+0` (`+0x2E4 + dest*8`): int32 **send level** = `levelFloat * 16000` (unity 1.0 = 16000).
  - `+4` (`+0x2E8 + dest*8`): byte **send active** flag.
- **Enable a route:** `mixer.setRouteTo(src, dest, value[, mode])` → `FLmx_SetRouteActiveCore 0x11A67F0`
  `(g_MixerManagerPtr, src, dest, value, snap)` — `value<0` toggles, else `value&1`; `mode&2` skips the
  `FUN_011a5d20` refresh. Shows a "Disable routing?" confirm if `dest` is used as a plugin sidechain.
- **Set send level:** `mixer.setRouteToLevel(src, dest, level)` writes the int directly + refresh.
- Master is track 0; route-to-master = `setRouteTo(track, 0, 1)`.
- **UI path (also valid):** `TFXForm.MixSend1WheelChange 0x11A4680` and `TFXForm.LinkMenuClick 0x11A01C0`
  route through the generic `FL_DispatchCommand(ctrl[+0x18] /*cmdId*/, ctrl[+0x3C0] /*value*/, 0x3DD)`.

## Data structures

### Mixer track array
- **`g_MixerTrackArrayPtr` @ `0x14A7EB0` is a POINTER** (not inline). Array base = `*(void**)0x14A7EB0`.
  Track N struct = `*(void**)0x14A7EB0 + N*0x1474`. **Stride `0x1474`** (= `0x51D*4`, confirmed in asm).
  *(Corrects prior note that called 0x14A7EB0 the inline base.)*
- **Track count:** `*(int*)( *(void**)0x14A9850 )` (`g_pMixerTrackCount`; =127 at rest: master+125+current).
- **Peak meter buffer:** `*(float*)( *(void**)0x14AB688 + track*0x10 + chan*4 )` (`g_MixerPeakBufferPtr`); 0x10/track, L@+0, R@+4.
- **Mixer manager object:** `*(void**)0x14A99A0` (`g_MixerManagerPtr`) — passed to route core.

### Mixer track struct (offsets within the 0x1474 stride)
| offset | type | field |
|---|---|---|
| `+0x00` | byte | custom-color flag (0 = use default color) |
| `+0x04` | int32 | track color (stored BGR; `0xAARRGGBB` after R<->B swap, A forced 0xFF) |
| `+0x08` | int32 | track type: 0=Master, 1=Insert (name "Insert %d"), 2=Current |
| `+0x0C` | ptr | custom track name (Delphi UnicodeString; empty → default by type) |
| `+0x18` | byte | **enabled** (0 = muted) |
| `+0x19` | byte | **selected** |
| `+0x1A` | byte | **solo** (solo-exclusive: muting clears others) |
| `+0x1B` | byte | mute-lock |
| `+0x28` | int32 | scratch: track index (written by enable core) |
| `+0x2D` | byte | dock side (0/1/2) |
| `+0x2C0` | byte | reverse polarity |
| `+0x2C1` | byte | swap L/R channels |
| `+0x2E4` | int32[502]×8B | **send table**: per-dest {+0 level(int=float*16000), +4 active(byte)} (stride 8) |
| `+0x12A4` | ptr | (sidechain/route index table, used by route-active confirm) |
| `+0x1324` | ptr[10] | **FX-slot plugin object pointers** (8B each, slot 0..9; 0=empty) |
| `+0x13CD` | byte | FX slots (rack) enabled |
| `+0x13D0` | — | (per-track sub-object touched on enable) |
| `+0x145C` | byte | **armed** for recording |

### FX-slot plugin object (referenced fields)
| offset | field |
|---|---|
| `+0x158` | route/param index sub-table (used for sidechain enumeration) |
| `+0x178` | byte: automation enabled |

## Ghidra symbols renamed (this session, prefix FLmx_ + plate comments)

Inner op-handlers (called by `FL_DispatchOpEvent`):
`FLmx_SetTrackVolume_op 0xE07730`, `FLmx_SetTrackPan_op 0xE07AC0`, `FLmx_SetTrackStereoSep_op 0xE07E80`,
`FLmx_SetTrackEnabled_op 0xE06F20` (mute+enable), `FLmx_SetTrackSolo_op 0xE06C50`, `FLmx_SetTrackArmed_op 0xE06970`,
`FLmx_SetRouteTo_op 0xE08E20`, `FLmx_SetRouteLevel_op 0xE09010`, `FLmx_SetSlotMixLevel_op 0xE08500`,
`FLmx_SetSlotEnabled_op 0xE082B0`, `FLmx_SetEqGain_op 0xE0B1F0`, `FLmx_SetEqFreq_op 0xE0B560`,
`FLmx_SetEqBandwidth_op 0xE0B810`, `FLmx_SetTrackColor_op 0xE063F0`, `FLmx_SelectTrack_op 0xE08840`,
`FLmx_SetActiveTrack_op 0xE08C70`, `FLmx_SetTrackName_op 0xE06160`.

Engine cores / accessors:
`FLmx_SetTrackEnabledCore 0x117F050`, `FLmx_SetTrackSoloCore 0x117F9A0`, `FLmx_SetTrackArmedCore 0x11C59D0`,
`FLmx_SetRouteActiveCore 0x11A67F0`, `FLmx_GetTrackColorCore 0x11C59F0`, `FLmx_SetTrackColorCore 0x11C58A0`,
`FLmx_GetTrackNameCore 0x11C2450`, `FL_MixerEffectParamBase 0x11C23B0`.

Global labels: `g_MixerTrackArrayPtr 0x14A7EB0`, `g_pMixerTrackCount 0x14A9850`,
`g_MixerPeakBufferPtr 0x14AB688`, `g_MixerManagerPtr 0x14A99A0`.

Shared (NOT renamed — used across domains): param-set `FUN_00F5F1A0`, EQ param-set `FUN_00F5E6F0`,
param-read `FUN_00F5E780`, op dispatch `FL_DispatchOpEvent 0xE2C350`, command bus `FL_DispatchCommand 0xF53FE0`,
slot validity `FUN_012297E0`, slot color `FUN_011C22D0`.
