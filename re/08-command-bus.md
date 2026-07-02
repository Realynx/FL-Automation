# 08 — FL Command Bus (the master control entry point) ⭐

## `FL_DispatchCommand(uint cmdId, ULONG_PTR value, uint flags)` @ `0xF53FE0`
FL's central command/parameter bus. **LIVE-VERIFIED** via the injected bridge: set tempo
130 → 150 BPM (`call f53fe0 40000005 249f0 3dd`, `ok:1`, FL alive, memory reflected it).

- **`cmdId`** encodes the target:
  - **Global params:** `0x4000xxxx` — e.g. `0x40000005` = **tempo** (value = milli-BPM,
    valid 10000..0x7F710). `0x40000000/1/2` = other master/global params (TBD).
  - **Channel plugin params:** `channelParamBase + paramIndex + 0x8000`.
  - **Mixer FX params:** `FL_MixerEffectParamBase(track,slot) + paramIndex + 0x70008000`.
- **`value`** — new value (param-specific units; tempo = BPM×1000; many params fixed-point ×2^30).
- **`flags`** — source/context: `0x3DD` (wheel/param/script path), `0x185` (slider), etc.
- Pulls target objects from engine **globals** (no context arg). **Must run on the main thread.**

## Why this is the master key
~100+ callers, and (thanks to RTTI recovery) they're self-documenting UI handlers — **every**
wheel/slider/menu/button routes here. The UI control object stores its `cmdId` at `+0x18` and
value at `+0x3C0`:
- `TToolbarForm.MainVolSliderChange` → `FL_DispatchCommand(ctrl[+0x18], ctrl[+0x3C0], 0x185)`
- `TPluginForm.VolWheelChange` → `FL_DispatchCommand(chanBase[+0x9C] + ctrl[+0x18], value, 0x3DD)`
- `TPluginForm.PitchWheelChange` / `ChannelMuteBtnClick` / `TrackPanWheelChange`
- `TFXForm.MixSend1WheelChange` / `LinkMenuClick`  (mixer)
- `TFruityLoopsMainForm.*MenuClick`  (menu commands)
- `TStepSeqForm.ShuffleSliderChange`, `TToolbarForm.MainPitchSliderChange`, …

So **the command catalog = decompiling these named callers to read each `cmdId`/`flags`.**

## Verified commands
| cmdId | operation | value | flags | status |
|---|---|---|---|---|
| `0x40000005` | set tempo | milli-BPM | `0x3DD` | ✅ live-tested (130→150), UI-confirmed |
| `0x40000001` | global param (TBD: master pitch?) | — | `0x11` | seen (const) |
| `0x40000002` | global param (TBD: master vol/shuffle?) | — | `0x2`/`0x11` | seen (const) |

## Catalog enumeration status (`re/generated/fl-commands.txt`)
A script walked all **160** `FL_DispatchCommand` call sites. A simple "MOV ECX,imm before the call"
scan found only the **constant global** cmdIds above (`0x40000001/2/5`); the rest are **computed**
(the per-control param-id scheme — channel/mixer `base+idx`). So the control space =
{ small set of global `0x4000xxxx` commands } + { the full param-id space }. To pin every global
cmdId + its meaning, the refinement is decompiler-level arg extraction over the named callers
(`TFruityLoopsMainForm.*MenuClick`, slider/wheel handlers) — each names its own operation.

## Drive it from the harness
```
flprobe inject
flprobe bridge call f53fe0 <cmdId> <value> <flags>   # main-thread, SEH-guarded
# e.g.  call f53fe0 40000005 249f0 3dd   -> 150 BPM
```

## Next — build the full command catalog (path to "every option")
1. **Script:** for each caller of `FL_DispatchCommand`, extract the constant `cmdId`/`flags`
   (and map the named caller → operation). Emit `re/generated/fl-commands.txt`.
2. Decompile the `*MenuClick` / slider handlers to enumerate the **global-param ids** (`0x4000xxxx`).
3. Combine with the channel/mixer **param-id scheme** (`re/06`) for full per-parameter control.
4. **Wire into C#:** `NamedPipeRpcClient : IFlRpc` → bridge `call`; expose typed ops
   (`setTempo`, `setChannelVolume`, `setMixerParam`, menu commands) behind the existing `IFlBridge`
   so the LLM agent drives them as tool calls (additive; Agent plugins unchanged).
