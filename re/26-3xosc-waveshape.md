# 3xOSC wave-shape sound design — LIVE-VERIFIED (2026-07-03)

Goal: from a plain "sine bass" / "saw lead" / "square pluck" request, the AI adds FL's native
**3x Osc** generator and sets its oscillator shape(s). This documents whether the standard plugin-param
path works for a NATIVE FL generator (vs the VST3/Serum proof in `re/10-plugin-param-control.md`), the
osc-shape param map, and the value→shape mapping — all confirmed against a running FL via `tools/SerumProbe`
(DEBUG bridge swapped in, restored after).

## Add name
`native_add_channel "3x Osc"` — exact FL registered name; `.fst` at
`…\Presets\Plugin database\Generators\Synth classic\3x Osc.fst`. Verified: adds a real generator channel
(osctest → "channel 17 = '3x Osc'", "has a generator plugin"). The plugin DLL is
`…\Plugins\Fruity\Generators\3x Osc\3x Osc_x64.dll` (a separate native module, hosted through FL's
`TGenericPlugin` wrapper).

## Does the standard param path work for a NATIVE generator? YES.
`native_list_channel_plugin_params` enumerates **114 params** for 3xOSC, and `native_set_channel_plugin_params`
(the normalized command-bus path, `cmdId = *(obj+0x9c)+0x8000+i`, value `round(norm*2^30)`, flags `0x3fd`)
DOES change them. Native FL generators go through the SAME `TBaseAudioPlugin` base as VST2/VST3 — one code
path. Continuous params proven live (coarse pitch: norm 0.75 → +12 semitone, 0.6 → +5). No new bridge
method needed.

## Oscillator SHAPE params (the target)
| control | param index |
|---|---|
| OSC 1 shape | **1** |
| OSC 2 shape | **8** |
| OSC 3 shape | **15** |
(Full per-osc block: panning, shape, coarse pitch, fine pitch, stereo phase, stereo detune; OSC2/3 also mix
level. Params 21/22/23/40/57/74/91/108/109/112 are unnamed/reserved. Envelopes+LFOs for pan/vol/cutoff/res/
pitch fill 24-107; filter freq/bw/type at 110/111/113.)

## value → shape mapping (7 shapes, indices 0-6)
The osc-shape is a stepped enum with **7 slots**. FL's normalized rescale maps `index = round(norm*6)`
(measured: norm 0.50→3, 0.333→2, 0.833→5, 0.95-1.0→6). Names per the Image-Line 3x Osc manual (exact order):

| shape | index | normalized value = index/6 |
|---|---|---|
| **sine** | 0 | 0.0 |
| **triangle** | 1 | 0.167 |
| **square** | 2 | 0.333 |
| **saw** | 3 | 0.5 |
| **rounded saw** | 4 | 0.667 |
| **noise** | 5 | 0.833 |
| **custom** (channel sample) | 6 | 1.0 |

So to set a shape: `native_set_channel_plugin_params(channel, '[{"index":1,"value":0.5}]')` = OSC1 saw.
`value = index/6` lands exactly (round((k/6)*6)=k). Each osc can be a different shape (layering).

### DISPLAY-STRING CAVEAT (important, cost me the first read)
3xOSC's osc-shape param reports its **display string as a bare number and it is STUCK/broken** —
`native_list_channel_plugin_params` shows "Osc 1 shape = 0" no matter the real shape (and the mode-1
formatter sometimes returns empty). This made the first sweep look like "set does nothing." It's cosmetic:
the SET applies fine. The reliable read is the instance `getParamValue` (inst vtbl+0x30) — used only for
verification here, not needed by the AI. The AI must NOT try to read back / confirm the osc shape from the
param listing; the set is deterministic. (Contrast: the LFO-shape params, e.g. index 36, DO display names
like "sine".) FL's hint/status bar is NOT populated by a programmatic command-bus tweak, so it can't name
the shape either.

### Why the normalized set "looked" broken at first
Flags `0x3fd` include the `0x20` "value-is-normalized" bit → the bus rescales `Q30(norm)` into the param's
`[min,max]`. Sending a small raw integer with `0x3fd` collapses to ~0 (raw 3 / 2^30 ≈ 0). The normalized
value (index/6) is the correct input for the shipping tool. (For reference, a raw-exact path also exists:
dispatch the integer with flags `0x3dd` = `0x3fd` minus the `0x20` bit → writes the index verbatim; but the
shipping `SetPluginParamAsync`/tool only exposes the normalized path, which is sufficient.)

## Implementation — PROMPT-ONLY (no new tool, no bridge/interface change)
The existing tools already work, so per the brief this is prompt-only:
- `native_add_channel "3x Osc"` + `native_set_channel_plugin_params` with `value = shapeIndex/6` on param
  index 1/8/15.
- Guidance added to `SystemPrompts.Default` (Authoring notes): the add name, the 3 shape param indices, the
  value→shape map, "layer with different shapes per osc", and the "display is cosmetic, no read-back" note.
- No `native_set_osc_shape` tool was added: the advertised-tool ratchet (`ToolDescriptionBudgetTests`,
  `MaxAdvertisedToolCount = 86`) is exactly at the cap and that test file is out of scope; a tool would also
  duplicate what the normalized param path already does reliably.

## LIVE-VERIFY (DEBUG bridge, restored after) — PASS
Set via the SHIPPING normalized path then read back the real index (instance getParamValue):
```
oscset 17 1 0.5 8 0.3333 15 0.8333     # OSC1 saw, OSC2 square, OSC3 noise
read-back: OSC1(param1)=3  OSC2(param8)=2  OSC3(param15)=5   # saw / square / noise — STUCK. FL alive.
```
(The `oscset` display read-back showed "= 0" for all three — the broken display string, NOT the real value;
the instance read confirms 3/2/5.) Also verified: raw sweep raw N→index N; normalized sweep norm→round(norm*6);
add-channel yields a live generator.

## Bridge / harness footprint
- `FlInjectBridge.cs`: NO net change (temp diagnostics were added for the sweep, then removed — file pristine).
- `tools/SerumProbe/Program.cs`: added durable shipping-path modes `osctest` / `oscsweep` / `oscset` (all use
  only the public `AddChannelAsync` / `ListPluginParamsAsync` / `SetPluginParamAsync`).
- `SystemPrompts.cs`: wave-shape guidance bullet.
- Deploy artifact: **`FruityLink.Agent.dll`** only (SystemPrompts change; prompt-only). No native/bridge redeploy.
- Release `FlBridge.dll` RESTORED (hash-identical to `tools/bridge/build/Release/FlBridge.dll`), FL relaunched.
