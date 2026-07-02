# Loading preset FILES into an existing plugin slot (native FL + hosted VST)

RE target: FLEngine_x64.dll, image base 0x400000. All addresses are Ghidra addresses.

## TL;DR — the one primitive that matters

To load a preset FILE into the plugin ALREADY in a slot (no re-instantiation), call the
plugin instance dispatcher with **opcode 0x12 (18) = "load state/preset from file"**, passing the
file path as a **UTF-8 `char*` (PAnsiChar)**:

```c
// pluginObj = channel obj (FLcr_ResolveChannelByIndex, slot==-1)
//           OR mixer plugin obj *(0x14A7EB0 + track*0x1474 + 0x1324 + slot*8)
// path must be UTF-8 (codepage CP_UTF8 / 0xFDE9), NOT UTF-16, NOT Delphi UString.
void* host = NULL;
FLplug_GetChannelPluginHost(pluginObj, &host);          // @0x122B320 (verified)
void* inst = (*(void*(**)(void*))(*(void**)host + 0x20))(host);   // inst=(*(*host+0x20))(host)
if (inst)
    (*(intptr_t(**)(void*,int,int,const char*))(*(void**)inst + 8))(inst, 0x12, 0, utf8Path);
```

FL wraps this exact 3-line sequence in a helper: **`FUN_0122acf0(pluginObj, opcode, index, value)`**
(a generic "channel plugin dispatch" = GetChannelPluginHost -> inst -> `(*(*inst+8))(inst,op,idx,val)`).
So the bridge can just call `FUN_0122acf0(pluginObj, 0x12, 0, utf8Path)`.

This is the SAME dispatcher already used by the bridge's "load plugin by path". Opcode 0x12 with a
`.fst` = load state into the current instance; it does NOT swap the generator when the `.fst` is a
preset for the plugin already loaded. (Full plugin REPLACEMENT is a different, higher-level path that
first re-instantiates the correct plugin from the `.fst` header, then calls 0x12 — see below.)

## 1. Native FL `.fst` preset -> existing slot

### The high-level "open file into channel" path (what FL's UI/drag uses)
- **`FLcr_OpenFileIntoChannel` @0xF1C5F0** = channel **vtbl+0x150**.
  Signature: `(channelObj, DelphiUStr path, char mode, ushort flags, ptr)`.
  Dispatches by extension (.CHENV, .404/.FPR/.FSC, .MID/.MIDI, .FLEXPACK, else -> sample/plugin).
  For non-special extensions it falls through to `FLcr_LoadChannelContent`.
- **`FLcr_LoadChannelContent` @0xF06750** `(channelObj, DelphiUStr path, char mode, ushort flags, ptr)`.
  In its `.FST` branch it:
  1. looks up the plugin descriptor from the `.fst` header (registry `*PTR_014ac360` vtbl+0xB0),
     and ensures the correct plugin is present (`FUN_00f063a0`) — this is the REPLACE-if-needed step;
  2. gets the live instance via `FLplug_GetChannelPluginHost` + `(*(*host+0x20))(host)`;
  3. expands the path (`FUN_0107edc0`), converts UString->**UTF-8 AnsiString** (`FUN_0041c420`,
     which stamps codepage 0xFDE9=CP_UTF8), takes the `char*` (`FUN_00414000`), and calls
     **`(*(*inst+8))(inst, 0x12, 0, utf8Path)`** — the state load.

### The exact string-passing recipe (copied from the `.FLEXPACK` branch @0xF1C5F0, unambiguous)
```
FUN_01082cf0(&s, L"FLEX.fst");
(*(*chan+0x150))(chan, s, 0, 0x27, 0);        // (optional) ensure plugin present
FUN_0107edc0(&u, path);                        // expand path tokens, Delphi UString
FUN_0041c420(&a, u);                            // UString -> AnsiString, codepage CP_UTF8 (0xFDE9)
p = FUN_00414000(a);                            // AnsiString -> char*  (NULL -> "")
FUN_0122acf0(chan, 0x12, 0, p);                 // == (*(*inst+8))(inst,0x12,0,p)  -> LOAD STATE
```
Bridge shortcut: skip the Delphi helpers — build your own UTF-8 `char*` in scratch memory and call
`FUN_0122acf0(pluginObj, 0x12, 0, utf8Ptr)` (or the inlined instance-dispatch). Path may contain
FL tokens like `%FLPluginDBPath%` / `%FLPath%` (expanded by the loader), but a plain absolute
Windows path in UTF-8 works.

### Loads STATE (params), not a generator swap
Opcode 0x12 on an existing instance restores the plugin's parameters/state (for VST it also does
setChunk). It is state-only for the SAME plugin. Only the higher-level `FLcr_LoadChannelContent`
adds the "instantiate correct plugin first" wrapper — for "recall a preset for the plugin already in
the slot", the raw 0x12 call is exactly what you want.

### Thread affinity
All of the above touch FL channel/plugin objects and the audio engine -> **FL main thread only**.
Use the bridge's SEH-guarded main-thread callabs (same as every other FL object call).

## 2. VST / VST3 preset files

FL DOES understand native VST preset formats (it has dedicated loaders), but they hang off the VST
wrapper classes (Ghidra recovered the RTTI names):

- **VST2 `.fxp` / `.fxb` (Cubase/FXB format)**: `TVSTPlugin.vtbl_79` @0xAD1C40
  (log string "LoadCubasePreset"). Parses `FxBk`/`FxCk`/`FPCh`/`FBCh` chunks and applies via
  setParameter (wrapper vtbl+0x1F0) and setChunk (wrapper+0x9b4 dispatcher, sub-op 0x18). This is a
  wrapper method taking a parsed stream, not a clean "path in" call — reachable but fiddlier than 0x12.
- **VST3 `.vstpreset`**: `TVST3Plugin.vtbl_77` @0xAE8B30 (references `.vstpreset`, `VST3 Presets\`,
  `.VSTPRESET`). Loads a VST3 preset (IComponent setState). There is also a `TVSTPresetFile` /
  `TVSTPresetFakeComponent` helper class in the binary.
- **Simplest, uniform path**: because a FL `.fst` for a VST embeds the full wrapper state INCLUDING
  the VST chunk, calling opcode **0x12 with a `.fst`** restores the entire VST state (Serum included)
  — one code path for native + VST. Prefer this over the per-format vtable methods.

FL does NOT read a plugin's *proprietary* preset format (e.g. Serum's `.SerumPreset`). It only
ingests `.fst`, `.fxp/.fxb`, `.vstpreset`.

## 3. On-disk preset locations (this machine)

### FL native presets (all are `.fst`)
- User: `C:\Users\poofi\Documents\Image-Line\FL Studio\Presets\Plugin presets\{Generators,Effects}\<Plugin>\...\*.fst`
  (mostly empty here — 7 files).
- **Factory (bulk): `C:\Program Files\Image-Line\FL Studio 2025\Data\Patches\Plugin presets\{Generators,Effects,VST}\<Plugin>\[Factory\]*.fst`** — **7631 `.fst`** files.
  e.g. `...\Generators\BassDrum\Factory\Bassdrum 01.fst`, `...\Generators\3x Osc\Default.fst`.
- Plugin registry (drag-to-add stubs, NOT sound presets):
  `...\FL Studio\Presets\Plugin database\{Generators,Effects,Installed}\...\<Plugin>.fst`
  e.g. `Plugin database\Generators\Serum 2.fst`, `Plugin database\Installed\Generators\VST3\Serum 2.fst`.
- Channel/Mixer/etc. presets also live under `Presets\` and `Data\Patches\` (Channel presets,
  Mixer presets, Scores, Speech, Envelopes, Chord progressions...).
- Path constants in binary: `Presets\Plugin presets`, `Data\Patches\Plugin presets\`,
  `Presets\Plugin database\`, `VST3 Presets\`.

### Serum 2 (Xfer) — `C:\Users\poofi\Documents\Xfer\Serum 2 Presets\`
Organized by content type; sound presets under `Presets\{Factory,User,Splice,S1 Presets}\<Category>\...`:
- **`*.SerumPreset` (897 files)** — Serum's OWN internal format. **FL cannot read these.**
  e.g. `...\Presets\Factory\Bass\Acid\...SerumPreset`, categories: Bass, Lead, Pad, Bell, Drum...
- `*.fxp` (110) — VST2 programs, mostly under `Presets\User\...`; FL CAN load via `TVSTPlugin.vtbl_79`.
- `*.SerumFX` (163), `*.SerumFXRack` (57), `*.XferShape` (169), `*.XferClip` (137), plus
  Multisamples (`.flac`/`.wav`/`.sfz`), Wavetables, LFO shapes, etc. — all Serum-internal.
- Serum 2 installed in FL as **VST3** (`Plugin database\Installed\Generators\VST3\Serum 2.fst`).
- No `.vstpreset` files present anywhere on disk (checked Documents + Common Files\VST3 Presets).

## 4. End-to-end recipes

### (a) Native FL plugin (FLEX, Sytrus, Harmor, 3x Osc, BassDrum, GMS, ...)
FULLY SOLVED. Recall preset X into the plugin at (channel | track,slot):
1. Resolve pluginObj (channel: `FLcr_ResolveChannelByIndex(idx,-1,&obj)`; mixer: the +0x1474 formula).
2. Verify `*(obj+0x64) >= 0` (has plugin).
3. Build UTF-8 path to the `.fst` (e.g. `...\Data\Patches\Plugin presets\Generators\FLEX\Factory\<name>.fst`).
4. Call `FUN_0122acf0(obj, 0x12, 0, utf8Path)` on FL main thread.
The LLM browses the `.fst` tree on disk, picks a file, and this loads it. No plugin swap.

### (b) Serum 2 specifically — VERDICT: factory `.SerumPreset` library is NOT directly recallable
- Serum's 897 factory sounds are `.SerumPreset` (Serum-internal). FL has no code path that reads them.
- FL's plugin "preset next/prev/count" (`FLpy_plugins_nextPreset/prevPreset/getPresetCount`
  @0xE27560/0xE27740/0xE27920 -> core `FUN_011badb0` / `FUN_011baad0`) navigates the FL WRAPPER's
  preset list, which is built from `.fst`/plugin-DB files — **it does NOT reach Serum's internal
  `.SerumPreset` browser.** So you cannot step through the factory library that way.
- Serum's own preset browser is not exposed to the host (no VST param / standard call selects an
  arbitrary internal preset by name).

Realistic Serum approaches, best first:
1. **Convert-once to `.fst` (recommended).** Load each wanted Serum sound once (via Serum's UI /
   one-time automation), then FL "Save preset as..." writes a `.fst` that embeds Serum's full VST3
   state. Store these under `...\Presets\Plugin presets\Generators\Serum 2\...`. Thereafter recall
   any of them instantly via the opcode-0x12 file-load primitive — identical to native plugins, and
   fully LLM-addressable by filename. Round-trips exactly (FL stores getChunk, 0x12 does setChunk).
2. **VST3 `.vstpreset`.** FL loads these (`TVST3Plugin.vtbl_77`). Batch-export Serum's factory library
   to `.vstpreset` once (Serum can export), then load by path. More setup than (1); none exist yet.
3. **`.fxp` (only if the VST2 build is used).** 110 exist in the User library; loadable via
   `TVSTPlugin.vtbl_79`. Poor coverage vs the full library, and Serum 2 here is installed as VST3.

Bottom line: for Serum, do a one-time capture into FL `.fst` (or `.vstpreset`), then recall through
the same 0x12 mechanism. The direct "point FL at a factory `.SerumPreset`" is blocked by design.

## 5. Key addresses (quick reference)
| what | addr / offset |
|---|---|
| FLplug_GetChannelPluginHost | 0x122B320 |
| instance = `(*(*host+0x20))(host)` | host vtbl+0x20 |
| **plugin dispatcher** `(*(*inst+8))(inst,op,idx,val)` | inst vtbl+8 |
| **opcode: load state/preset from file** | **0x12 (18)**, arg = UTF-8 char* |
| FUN_0122acf0 = channel-plugin dispatch helper | 0x122ACF0 |
| FLcr_OpenFileIntoChannel (channel vtbl+0x150) | 0xF1C5F0 |
| FLcr_LoadChannelContent (.fst branch = 0x12 load) | 0xF06750 |
| UString -> UTF-8 AnsiString (cp 0xFDE9) | 0x41C420 |
| AnsiString -> char* (NULL->"") | 0x414000 |
| path token expand (%FLPath% etc.) | 0x107EDC0 |
| VST2 .fxp/.fxb loader (TVSTPlugin.vtbl_79 "LoadCubasePreset") | 0xAD1C40 |
| VST3 .vstpreset loader (TVST3Plugin.vtbl_77) | 0xAE8B30 |
| wrapper preset next/prev core (FL's own .fst list) | 0x11BADB0 |
| wrapper preset count | 0x11BAAD0 |
| FLpy plugins nextPreset/prevPreset/getPresetCount | 0xE27560 / 0xE27740 / 0xE27920 |

## 6. Live-verify plan (read-only where possible, one guarded call to test)
1. Sanity: pick channel with a native plugin (e.g. add FLEX). Read `*(obj+0x64)>=0`.
2. Dump instance vtbl: read `*inst`, confirm slots +8 (dispatcher) and +0x20 path resolve.
3. Non-destructive probe: call `FUN_011BAAD0(obj)` (preset count) — pure read, confirms wrapper is live.
4. State load test: build UTF-8 path to a known factory `.fst`
   (`C:\Program Files\Image-Line\FL Studio 2025\Data\Patches\Plugin presets\Generators\FLEX\...\*.fst`
   or `3x Osc\Default.fst`), call `FUN_0122acf0(obj, 0x12, 0, utf8Path)` on the main thread, and
   confirm the plugin's params/sound changed (and no plugin swap: instance pointer unchanged).
5. Serum test: on a channel already holding Serum 2, first verify a `.SerumPreset` path FAILS/ignored
   (confirms the block); then save one Serum sound as `.fst` from FL, and confirm 0x12 recall of that
   `.fst` restores it exactly (validates the convert-once path).

## 7. Uncertainties / flags
- Opcode 0x12 semantics confirmed by the `.fst` and `.FLEXPACK` branches (state load into existing
  instance). Not exhaustively decompiled per-wrapper; behavior when the `.fst` targets a DIFFERENT
  plugin than the one loaded is untested via the raw call (the safe high-level replace path is
  `FLcr_LoadChannelContent`). For same-plugin recall (our case) 0x12 is correct.
- Mixer-slot object supports the identical GetChannelPluginHost/instance interface per the project's
  VERIFIED CONTEXT; `FUN_0122acf0` calls `FLplug_GetChannelPluginHost` on whatever obj you pass —
  verify once on a real mixer effect slot.
- `.fxp` loader (`TVSTPlugin.vtbl_79`) takes a parsed stream, not a file path; wiring a raw path to
  it would need locating its caller/entry — deferred (the `.fst`/0x12 route makes it unnecessary).
- Serum could theoretically expose preset selection via a plugin parameter; not found in FL's side
  (would be Serum-internal). Treat factory `.SerumPreset` as opaque to FL.
