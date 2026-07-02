# Plugin / VST parameter control — RE-COMPLETE (Serum 2 confirmed)

Goal: let the LLM read and set individual plugin parameters (native FL plugins and VST2/VST3 alike)
and design/recall whole sounds — e.g. set a Pro‑Q band's freq/gain, sculpt a Serum 2 patch.

FL exposes every plugin's parameters as a flat indexed list: **index → name → normalized value (0..1) →
display string** (e.g. "440 Hz", "‑6 dB"). This holds for native plugins and hosted VST2/VST3 (FL's
wrapper proxies the VST param list).

## STATUS (2026‑07‑02): ✅ LIVE‑VERIFIED ON SERUM 2 (read + write + display strings)
Verified against a running FL with Serum 2 in the channel rack, driving the real `FlInjectBridge` over
the pipe (harness `tools/SerumProbe`): channel 1 = Serum 2, **4240 params**; filtered list returns real
display strings — `205: Filter 1 Freq = 425 Hz`, `206: Filter 1 Res = 10 %`, `202: Filter 1 Level =
0.0 dB`, `204: Filter 1 Type = MG Low 12`, `203: Filter 1 On = Off`. Normalized SET is monotonic across
the VST3 range: `0.05→12 Hz`, `0.5→425 Hz`, `0.9→10025 Hz`. One bridge path covers native/VST2/VST3.

**THE BUG FOUND + FIXED (was blocking all VST param I/O):** `FlInjectBridge.ResolvePluginAsync` +
read helpers guarded every resolved function pointer with `EnsureInModule` (FLEngine‑only). But for a
hosted VST the plugin‑instance's param methods legitimately live in the **plugin's own DLL** (Serum's
`getParamName` sat at `0x5de4bcd0`, outside FLEngine `0x5e56xxxx`), so the guard aborted with
"resolution invalid". Fix: dropped the FLEngine‑only guard on the three instance‑vtable reads
(`ReadParamName/Value/ValueString`) — the native `callabs` is SEH‑guarded, so a genuinely bad pointer
returns `ok:0` (clean exception) instead of crashing FL. `getInstance` (an FLEngine method) keeps its
guard. Value note: Serum returns raw = **float bits** (`0x3f000000` = 0.5f) — the display‑string path
makes this moot for the LLM; the raw fallback now reinterprets to a 0..1 float.

## (superseded) prior status: RE done, implementation ~confirmed
Three parallel Ghidra passes (full detail in `re/generated/serum-A-read.md`, `serum-B-write.md`,
`serum-C-addressing.md`) decompiled FL's `plugins.*` scripting API down to callable engine paths and
proved the flavor model. **Headline: the existing `FlInjectBridge` implementation already matches the
RE** and one code path covers native/VST2/VST3.

### The decisive finding — VST3/Serum 2 shares the native wrapper vtable (hard‑proven)
The plugin instance is a `TBaseAudioPlugin`‑derived object: `TGenericPlugin` (native), `TVSTPlugin`
(VST2), `TVST3Plugin` (VST3). Raw vtable reads show **all three flavors' slot 4 (getParamName) point to
`TBaseAudioPlugin.vtbl_4 @0x84CBA0`, and slot 6 (getParamValue) to `TBaseAudioPlugin.vtbl_6 @0x84CDA0`**.
`TVST3Plugin` overrides neither. Both base methods forward to an inner flavor driver at `inst+0x78`; the
VST3 driver bottoms out in the plugin's COM controller (VST3 ptrs at `TVST3Plugin+0x814`, ready flag
`+0x970`). ⇒ **one bridge path drives Serum 2 exactly like a native plugin.**

## Engine map (Ghidra addrs, FLEngine_x64.dll image base 0x400000)
PyMethodDef `plugins.*` wrappers (map only; the bridge calls the engine paths directly, not Python):
`isValid @0xE25CD0 · getPluginName @0xE25E10 · getParamCount @0xE26200 · getParamName @0xE26360 ·
getParamValueString @0xE26810 · getParamValue @0xE26CE0 · setParamValue @0xE270A0 ·
nextPreset @0xE27560 · prevPreset @0xE27740 · getPresetCount @0xE27920 · getColor @0xE27A90 ·
getName @0xE27DB0 · getPadInfo @0xE28290`.

### Instance resolution (shared by every op)
- **Channel generator** (`slotIndex == -1`): resolve channel `obj` (`FLcr_ResolveChannelByIndex @0xF22AC0`,
  or `ChannelObjAsync`); has‑plugin gate `*(int*)(obj+0x64) >= 0`.
- **Mixer FX slot** `(track,slot)`: `obj = *(void**)(0x14A7EB0 + track*0x1474 + 0x1324 + slot*8)`; track
  count @`0x14A9850` (0‑125), slot 0‑9.
- **Then (both):** `host` via `FLplug_GetChannelPluginHost @0x122B320` (== `*(obj+0x38)+0x48` in practice);
  `inst = (*(*host+0x20))(host)` (host vtbl+0x20 = GetInstance; 0 ⇒ no plugin). Resolvers take **no lock**
  (pure pointer walks).

### The 5 ops
| op | mechanism | detail |
|---|---|---|
| getParamCount | struct field | `*(int*)(obj+0x68)` (no call) |
| getParamName(i) | inst vtbl **+0x20**, mode 0 | `inst[+0x20](inst, 0, i, 0, buf)` → NUL‑term C‑string |
| getParamValue(i) raw | inst vtbl **+0x30** | `inst[+0x30](inst, i, 0, 2)` → raw int in RAX |
| getParamValueString(i) | inst vtbl **+0x20**, mode 1 (or cmd‑bus `FLgl_cmd_FormatEventValue @0xF5A720`) | `inst[+0x20](inst, 1, i, raw, buf)` → display str w/ units |
| setParamValue(i, norm) | **command bus** | `FL_SetParamEventValue @0xE2C960` (self‑marshals to main thread) → terminal `FL_DispatchCommand @0xF53FE0`; value = `round(norm * 2^30)` (**Q30 fixed‑point, normalized 0..1**); flags **0x3fd** (bit 0x20 = "normalized → bus rescales to [min,max]") |

**cmdId:** channel = `*(int*)(obj+0x9c) + i + 0x8000`; mixer = `((track*0x40+slot)<<16) + i + 0x70008000`.

### Presets (secondary; not yet wired to tools)
`getPresetCount @0xE27920` → `FUN_011BAAD0(obj)` (builtin + .fst count; synchronous, directly callable).
`nextPreset/prevPreset` defer to main thread → nav core `FUN_011BADB0(obj, ±1, 0)` (`newIdx=(cur+dir+total)%total`,
store via `FUN_011BF590`, load via **obj vtbl+0xf0**). No `setPreset(idx)` export, but
`(*(*obj+0xf0))(obj, idx, 0, desc, 1, 0)` + `FUN_011BF590(obj, idx)` on the main thread sets an absolute
index (derived from nav core — **live‑verify before trusting**). This is FL bank nav (builtin + .fst),
not raw VST program‑change.

## Implementation status in `src/FruityLink.FlStudio/Inject/FlInjectBridge.cs`
- `ResolvePluginAsync` — ✅ matches RE (obj+0x68 count, obj+0x38→host+0x48→getInst, both cmdBases).
- `SetPluginParamAsync` — ✅ Q30 (`norm*2^30`) + `DispatchCommandAsync(cmdBase+i, fixedVal, 0x3fd)`.
- `ReadParamNameAsync` — ✅ inst vtbl+0x20 mode 0.
- `ReadParamValueAsync` — ✅ inst vtbl+0x30 (raw int).
- `ReadParamValueStringAsync` — ✅ **NEW 2026‑07‑02**: inst vtbl+0x20 mode 1 → display string; `ListPluginParams`
  now shows the display string ("Cutoff = 1.2 kHz"), raw int as fallback. (pending live‑verify on Serum)
- LLM tools: `native_list_channel_plugin_params`, `native_set_channel_plugin_param` (0..1),
  `native_list_mixer_plugin_params`, `native_set_mixer_plugin_param` (0..1). Value + index clamping added.

## Live‑verify — DONE ✅ (2026‑07‑02, via tools/SerumProbe against live Serum 2)
- List: 4240 params, names + display strings correct (ASCII read fine — no UTF‑16 garbling seen).
- Read: display strings carry units (Hz/%/dB/enum/on‑off).
- Write: normalized 0..1 maps monotonically across the VST3 range (12 Hz → 10025 Hz). Q30 + flags 0x3fd
  works for Serum's float params (no explicit `FLgl_cmd_GetValueRange` normalization needed for display).

## Remaining polish (optional)
- Deploy the fixed `FruityLink.FlStudio.dll` into `<FL>\FruityLink\` so the in‑FL LLM Agent gets it
  (verified via external harness; needs FL closed for the DLL swap).
- Preset tools (`native_next/prev_preset`, `getPresetCount`, absolute setPreset) — RE'd, not wired.
- System prompt: teach the query→reason→set→read‑back sound‑design loop; push the name filter (4240
  params on Serum 2 — never list unfiltered).
- Full‑state clone (save/restore serialized plugin state) — deferred.
- `tools/SerumProbe` — kept as a manual "verify a plugin's params without the LLM" harness.

## Preset control — RE done (3 agents) + core LIVE‑VERIFIED on Serum 2 (2026‑07‑02)
Full detail: `re/generated/preset-{A-index,B-names,C-files}.md`. Preset ops use the SAME channel/mixer
`obj` as params (NOT the plugin instance). Current preset index = `*(int*)(obj+0xf0)`. **Main‑thread only.**

**Bridge‑ready recipes (Ghidra addrs; obj = channel via FLcr_ResolveChannelByIndex or mixer slot obj):**
- `getPresetCount(obj)` = `FUN_011baad0(obj)` @0x11BAAD0. ✅ verified (Serum → 128).
- `setPresetIndex(obj, N)`: `loader = (*(void***)obj)[0x1e]` (obj vtbl **+0xf0**); `loader(obj, N, 0, 0, 1, 0)`
  = `FUN_011BB770(obj, index, wpath, wname, applyFlag=1, noUndo=0)`. ✅ verified (Serum idx→5→0). The
  loader stores obj+0xf0 itself. index in [0,builtinCount) = builtin/program (opcode 9 host dispatch);
  index<0 (FL uses −2) = load `wpath` as `.fst`.
- `nextPreset/prevPreset(obj)` = `FUN_011badb0(obj, ±1, 0)` @0x11BADB0. ✅ verified (wraps −1↔0↔127).
- `setPresetFile(obj, wDelphiPath)`: `loader(obj, -2, wpath, 0, 1, 0)` (path = Delphi UnicodeString). RE'd,
  not yet live‑tested. Lower‑level alt (Agent C): `FUN_0122ACF0(obj, 0x12, 0, utf8Path)` @0x122ACF0 —
  "load state from file", **UTF‑8** path, loads into the live slot without swapping the plugin.
- **Preset NAMES:** `GetName(inst, section, i, 0, buf)` = inst vtbl **+0x20** (same method as param names).
  section **6**=FPN_Preset, **3**=FPN_Patch. ✅ verified: Serum → section 6 = 128 generic **"Prog 1..128"**,
  section 3 = empty. Native FL synths return REAL preset names via section 6 (RE‑backed; none loaded in
  the test project to demo). FL‑bank names (incl. `.fst`) via `FUN_011bab30(obj,&uStr,i)` (Delphi UString).

**THE LIMITATION (decisive):** getPresetCount + section‑6 names expose FL's **wrapper bank** (builtin/
generic programs + `.fst` files), NOT a VST's real internal library. Serum 2 shows only 128 generic
"Prog N"; its 897 `*.SerumPreset` factory files (`~\Documents\Xfer\Serum 2 Presets\Presets\`) are opaque
to FL (FL ingests only `.fst`/`.fxp`/`.vstpreset`). ⇒ **Preset recall by name works great for native FL
plugins; for Serum/VSTs use file‑load of a `.fst` (convert‑once: load a Serum sound → FL "Save preset
as… .fst" embeds the full VST3 chunk → recall via the file‑load path).**

**On‑disk libraries:** FL factory `.fst` = `C:\Program Files\Image-Line\FL Studio 2025\Data\Patches\
Plugin presets\{Generators,Effects,VST}\<Plugin>\...` (7631 files); user = `~\Documents\Image-Line\FL
Studio\Presets\Plugin presets\`. Serum 2 = `~\Documents\Xfer\Serum 2 Presets\Presets\` (`.SerumPreset`).

**Proposed tools (not yet built):** `native_list_plugin_presets` (count+names), `native_set_plugin_preset`
(index), `native_load_plugin_preset_file` (path → `.fst`/state), optional `native_list_preset_files`
(browse the on‑disk `.fst` library). Remaining live‑verify: the file‑load path (`0x12` / index=−2) on a
native plugin with a real `.fst`, and section‑6 real names on a native synth.
