# controls-global.md — TRANSPORT / PROJECT / GLOBAL command catalog ⭐

Authoritative map of FL Studio 2025's **global command space** for the injected-DLL bridge.
Domain: transport, project/global params, main-menu/toolbar. Static RE only (Ghidra,
`FLEngine_x64.dll`, image base 0x400000). Companion to `re/08-command-bus.md`.

---

## 1. The GLOBAL COMMAND TABLE — `FL_DispatchCommand` 0x4000xxxx ids

`FL_DispatchCommand(uint cmdId, ULONG_PTR value, uint flags)` @ **0xF53FE0** is the central bus.
The `0x4000xxxx` ids are the **master/global param group**. They are the *only* constant
(non-computed) cmdIds — everything else is the per-control param-id scheme
(channel `base+idx+0x8000`, mixer-FX `base+idx+0x70008000`).

| cmdId | operation | value units / range | flags (typical) | source handler | verified? |
|---|---|---|---|---|---|
| `0x40000000` | **Master Volume** | 0..12800 (display %, scale 0x3200). Normalized form: 0..2^30 | `0x185` slider / `0x3dd` wheel; set `0x1`/get `0x2` | `TToolbarForm.MainVolSliderChange` 0xCB9930 (cmdId via ctrl[+0x18]); bus arm @0xf548xx | ⬤ format-confirmed (value@`PTR_014aac98`) |
| `0x40000001` | **Master Shuffle / Swing** | 0..128 (`0x80`) | `0x3cd` wheel; `0x11` set | `TToolbarForm.TSDFeedWheelChange` 0xCB8A70 + `TStepSeqForm.ShuffleSliderChange` 0xf47980 (cmdId via ctrl[+0x18]); jog in `FLgl_GlobalCommandDispatch` 0xef7b20 (idx 0x7a) | ◐ strong inference (contiguous master group; backend `FLgl_SetMasterShuffleSwing` writes swing multiplier; value@`PTR_014aa878`) |
| `0x40000002` | **Master Pitch** | cents, -1200..+1200 (`-0x4b0..0x4b0`) | `0x3cd` wheel / `0x11` set / `0x2` get | `TToolbarForm.MainPitchSliderChange` 0xCB9910; dialog `FLgl_dlg_MasterPitchHz` 0xf3b0c0 | ⬤ confirmed ("Master pitch (Hz)" dialog; value@`PTR_014aa258`) |
| `0x40000005` | **Tempo** | milli-BPM, 10000..0x7F710 (10..522 BPM) | `0x3dd` wheel/script | `FL_cmdhandler_SetTempo` 0xe0a230 | ⬤ LIVE-VERIFIED (130→150 BPM) (value@`PTR_014a9f00`) |

Notes / non-commands:
- `0x40000003` and `0x40000004` are **not** commands. The lone `0x40000003` reference is the
  *exclusive loop bound* of a `for(cmdId=0x40000000; cmdId<0x40000003; …)` over the 3-element
  master group (vol/shuffle/pitch) in `FUN_00e6ac00`.
- A `value` of `0x40000000` together with `flags 0x20` is the **"query MAX"** sentinel used by
  `FLgl_cmd_GetValueRange` (`min = call(cmdId,0,0x20)`, `max = call(cmdId,0x40000000,0x20)`),
  not a cmdId.
- No `0x40000006+` global cmdIds exist (verified by full-binary immediate scan of
  0x40000000..0x4000003F: histogram = {…1:13, 2:7, 3:1(loop bound), 5:23}).

### flags bitfield (param_3 of FL_DispatchCommand)
| bit | meaning |
|---|---|
| `0x1` | **SET** the value |
| `0x2` | **GET** — return current value (does not write) |
| `0x20` | `value` is **NORMALIZED 0..2^30**; engine maps it into `[min,max]` (`min + value/2^30*(max-min)`) |
| `0x10` | post-set UI/hint refresh |
| composite | `0x185` slider drag · `0x3cd`/`0x3dd` wheel/script · `0x11` set+refresh · `0x3d5/0x3f5` param-wheel |

### Master/song root object
`PTR_014aa4c8` = master/song control panel. Control sub-objects:
`+0x778` tempo ctrl · `+0x7e8` song-position obj · `+0x9e0` master-volume ctrl · `+0xb28` master-pitch ctrl.
(Shuffle ctrl name lives on a different root: `PTR_014a8bf8 + 0x808`.)
Each control stores min @`[0x77]`/`+0x3b8` and max @`+0x3bc`; live value mirrors at the `PTR_*` globals above.

### How to drive (bridge)
```
call f53fe0 40000005 <milliBPM>  3dd      # tempo (e.g. 249f0 = 150 BPM)  -- LIVE-VERIFIED
call f53fe0 40000000 <0..12800>  11       # master volume (set)
call f53fe0 40000002 <-1200..1200> 11     # master pitch (cents, set)  (value is signed int)
call f53fe0 40000001 <0..128>    11       # master shuffle/swing (set)
call f53fe0 <cmdId>  0           2        # GET current value of any of the above (returns value)
call f53fe0 <cmdId>  <0..2^30>   21       # SET via normalized 0..2^30 (flags 0x20|0x1)
```

---

## 2. TRANSPORT ops — `FL_DispatchTransportOp` / `FL_DispatchOpEvent` (separate channel)

Play/stop/record/loop do **NOT** use the 0x4000xxxx bus. They route through
`FL_DispatchTransportOp` @ **0xE2C790** → `FL_DispatchOpEvent` @ **0xE2C350** (handler `FUN_00e2c730`).

| python native | RVA | op | args | meaning |
|---|---|---|---|---|
| `transport.start` | 0xE0E850 | **10** | (op=10, argc=2, mode, flag 8) | start / play |
| `transport.stop` | 0xE0E910 | **11** | (op=11, argc=1, mode, flag 8) | stop |
| `transport.record` | 0xE0E9D0 | **12** | (op=12, argc=1, mode, flag 0xf) | toggle record |
| `transport.setLoopMode` | 0xE0EAE0 | **15** | (op=15, argc=1, mode, flag 0xf) | toggle pattern↔song loop mode |
| `transport.getLoopMode` | 0xE0EAC0 | — | read | current loop mode |
| `transport.isRecording` | 0xE0EA90 | — | read | recording state |
| `transport.isPlaying` | 0xE0F3C0 | — | read | playing state |
| `transport.globalTransport` | 0xE0E6D0 | — | generic | generic transport command entry |
| `transport.getSongPos` | 0xE0EBA0 | — | read | song pos (abs/bars/steps via `PTR_014aa4c8+0x7e8`) |
| `transport.setSongPos` | 0xE0EE00 | — | `FL_DispatchOpEvent`(handler `FUN_00e0ed20`) | set song pos |
| `transport.getSongPosHint` | 0xE0EF40 | — | read | formatted pos string |
| `transport.rewind` | 0xE0F010 | — | — | rewind |
| `transport.fastForward` | 0xE0F2C0 | — | — | fast-forward |
| `transport.setPlaybackSpeed` | 0xE0F1A0 | — | — | playback speed |
| `transport.markerJumpJog` / `markerSelJog` | 0xE0F3F0 / 0xE0F500 | — | — | marker jog (also via `FLgl_GlobalCommandDispatch`) |
| `transport.getSongLength` | 0xE0F910 | — | read | song length |
| `transport.getHWBeatLEDState` | 0xE0F610 | — | read | beat LED |
| `transport.continuousMove(Pos)` | 0xE0F680 / 0xE0F7F0 | — | — | scrub |

---

## 3. PROJECT / GLOBAL (general module) — time sig, PPQ, undo, metronome, project info

These use `FL_DispatchOpEvent` with per-op handlers (not the 0x4000xxxx bus), or read engine globals.

| python native | RVA | op handler / note |
|---|---|---|
| `general.setNumerator` | 0xE104E0 | `FL_DispatchOpEvent`(handler `FUN_00e10470`) — time-sig numerator |
| `general.setDenominator` | 0xE10670 | `FL_DispatchOpEvent`(handler `FUN_00e10600`) — time-sig denominator |
| `general.getRecPPB` / `getRecPPQ` | 0xE102A0 / 0xE102C0 | read PPB / PPQ |
| `general.setRecPPQ` | 0xE10350 | set PPQ |
| `general.getUseMetronome` | 0xE107B0 | metronome on/off (toggle via `FLgl_GlobalCommandDispatch`) |
| `general.getPrecount` | 0xE107D0 | count-in state |
| `general.undo` / `undoUp` / `undoDown` | 0xE0FD50 / 0xE0FE10 / 0xE0FED0 | undo history nav |
| `general.saveUndo` | 0xE10890 | push undo point |
| `general.getUndoHistoryPos/Count/Last` (+set) | 0xE10A20.. / 0xE10B10.. | undo history cursor |
| `general.getChangedFlag` | 0xE10790 | dirty flag |
| `general.getProjectTitle/Author/Genre` | 0xE11280 / 0xE11330 / 0xE113E0 | project metadata |
| `general.getVersion` | 0xE10E70 | scripting API version |

Snap / new-pattern / record-settings and most menu commands are dispatched as small integer
command indices through **`FLgl_GlobalCommandDispatch` 0xef7b20** (keyboard/MIDI hotkey handler,
`cmdIndex` 0..0x7b: transport, markers, loop, snap, metronome, undo, pattern↔song, etc.) and the
`TFruityLoopsMainForm.*MenuClick` handlers (which mostly call internal model setters, not the bus).

---

## 4. Toolbar / menu handlers that hit the bus (cmdId via widget ctrl[+0x18])

Wheel/slider widgets store their cmdId @`+0x18` and current value @`+0x3C0`:
`FL_DispatchCommand(ctrl[+0x18], ctrl[+0x3C0], <flags>)`.

| handler | RVA | flags | resolved cmdId |
|---|---|---|---|
| `TToolbarForm.MainVolSliderChange` | 0xCB9930 | 0x185 | 0x40000000 (master volume) |
| `TToolbarForm.MainPitchSliderChange` | 0xCB9910 | 0x3cd | 0x40000002 (master pitch) |
| `TToolbarForm.TSDFeedWheelChange` | 0xCB8A70 | 0x3cd | 0x40000001 (shuffle/swing) |
| `TStepSeqForm.ShuffleSliderChange` | 0xf47980 | 0x3cd | 0x40000001 (shuffle/swing) |

(All other named callers — `TPluginForm.*WheelChange`, `TFXForm.*`, `TParamCtrlForm.ParamWheelChange`,
`TFruityLoopsMainForm.Front*` — carry *computed* per-control param ids, not global 0x4000xxxx;
see `re/generated/fl-commands.txt`.)

---

## 5. Ghidra symbols renamed / commented (this pass, prefix `FLgl_`)

| address | new name | role |
|---|---|---|
| 0xf3b0c0 | `FLgl_dlg_MasterPitchHz` | "Master pitch (Hz)" dialog → cmdId 0x40000002 |
| 0xf5e7b0 | `FLgl_cmd_RelAdjustNormalized` | wheel/jog delta → normalized value for bus (flags|0x20) |
| 0xf5e410 | `FLgl_cmd_GetValueRange` | cmdId → (min,max,flags); uses 0x20 query path |
| 0xf5ca00 | `FLgl_cmd_GetEventIDName` | cmdId/param-id → display name |
| 0xf5a720 | `FLgl_cmd_FormatEventValue` | value → display string per cmdId/units |
| 0x1212290 | `FLgl_SetMasterShuffleSwing` | backend for cmdId 0x40000001 (writes swing multiplier) |
| 0xef7b20 | `FLgl_GlobalCommandDispatch` | keyboard/MIDI global command handler (cmdIndex 0..0x7b) |

Plate comments added: 0xF53FE0 (FL_DispatchCommand — full catalog), 0xe0a230 (SetTempo),
0xCB9930/0xCB9910/0xCB8A70 (toolbar vol/pitch/shuffle), 0xE2C790 (FL_DispatchTransportOp opcodes),
plus the 7 renamed funcs above.

Pre-existing in-domain symbols (kept): `FL_DispatchCommand` 0xF53FE0, `FL_cmdhandler_SetTempo`
0xe0a230, `FL_DispatchTransportOp` 0xE2C790, `FL_DispatchOpEvent` 0xE2C350,
`FLpy_transport_*`, `FLpy_general_*`.

---

## 6. Method / source addresses (quick ref)
- Bus: `FL_DispatchCommand` 0xF53FE0 · range `FLgl_cmd_GetValueRange` 0xf5e410 · rel-adjust 0xf5e7b0
- Tempo: handler `FL_cmdhandler_SetTempo` 0xe0a230 · apply FUN_01212710/01212730
- Volume: dispatch @ FL_DispatchCommand `param_1==0x40000000`; value `PTR_014aac98`
- Pitch: dialog 0xf3b0c0; value `PTR_014aa258`
- Shuffle: backend 0x1212290; value `PTR_014aa878`
- Transport: `FL_DispatchTransportOp` 0xE2C790 (op 10/11/12/15) · `FL_DispatchOpEvent` 0xE2C350
- Time sig: setNumerator 0xE104E0 (h:FUN_00e10470) · setDenominator 0xE10670 (h:FUN_00e10600)
