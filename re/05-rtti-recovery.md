# 05 — Delphi RTTI Recovery (applied)

With the Ghidra script engine enabled, a VMT-walking pass made the engine
human-readable in bulk.

## VMT layout (Delphi Win64) — confirmed empirically
Anchored on `TFruityPlugHost` (the 11 consecutive `TObject` method pointers at
`V-88..V-8` are the unmistakable signature). Offsets **from the class reference `V`**
(where user virtual methods begin):

| Offset | Field |
|---|---|
| `V-144` | vmtTypeInfo |
| `V-128` | **vmtMethodTable** (published methods) |
| `V-112` | **vmtClassName** (PShortString) |
| `V-104` | **vmtInstanceSize** |
| `V-96`  | **vmtParent** |
| `V-88 … V-8` | the 11 standard `TObject` virtuals (Equals…Destroy) |
| `V+0 …` | user virtual methods |

Published-method entry: `[Size:u16][Addr:u64][NameLen:u8][Name]`, step by `Size`.

## Detector heuristic
A location `V` is a class VMT when `*(V-112)` points to a valid Delphi class-name
ShortString (len 2–63, leading letter, chars in `[A-Za-z0-9_.<>,$ ]`), `*(V-104)`
(instance size) is a sane `0 < n ≤ 0x200000`, and `*(V-96)` (parent) is 0/in-image.

## Results (applied + saved to the Ghidra project)
- **2,578 class VMTs** labeled `vmt_<ClassName>` at their `V`.
- **2,697 published methods** renamed `Class.Method` (skipping anything already named).
- Reference lists generated (also grep-able outside Ghidra):
  - `re/generated/fl-classes.txt` — `VMT_RVA  ClassName  InstanceSize  ParentVMT  #PublishedMethods`
  - `re/generated/fl-published-methods.txt` — `FuncRVA  Class.Method`

Most published methods are VCL **form/UI event handlers** (great for UI automation).
The **core engine model classes have no `published` section** (`m=0`): their methods
live in VMT virtual slots + detailed RTTI — that's the next recovery pass.

## Control-surface class map (high-value targets)
From `fl-classes.txt`:
- **Plugins/VST (param control):** `TBaseAudioPlugin` (`0x84B138`, size `0x7A8`) →
  `TVSTPlugin` (`0xAC91A0`), `TVST3Plugin` (`0xADA188`), `TGenericPlugin` (`0xAFADE0`);
  editors `TBaseAudioPluginEditor`/`TVSTPluginEditor`. Mgmt: `TPluginLocator`,
  `TPluginFormatVST2/VST3/FL`, `TPluginFileInfo`, interfaces `IAudioPluginHost`/`IPluginBase`.
- **Plugin SDK host:** `TFruityPlugHost` (`0x795EF8`) — the host dispatcher (Task #4).
- **Mixer:** `TMixerIntf` (`0x88C1A0`) + the param-path mixer-track array `@0x14A7EB0`
  (stride `0x1474`, 10 FX slots `@+0x1324`) already mapped via `setParamValue`.
- **Channels:** `TChannelSample` (`0xAA4C00`), `TChannelEvent` (`0x8C8920`),
  `TChannelSamplePool*`, `TFlpChannel` (FLP load model).
- **Patterns:** `TPatternManager` (`0x59E098`).
- Still to locate by name: playlist/arrangement + piano-roll/score classes (search the
  generated list; some may be named by unit, e.g. `Arrang*`, `Score*`, `PR*`).

## Next recovery pass (deeper)
For the `m=0` core classes: name **virtual-method slots** (`Class.vmt_N`) and parse
**detailed RTTI** (`vmtTypeInfo` → published properties + field RTTI) to label fields and
methods. Combine with: (a) the PyMethodDef native map (`04-engine-survey.md`) and
(b) string-xref naming, to pin the exact channel/pattern/playlist/mixer operations.
