# Serum 2 / VST3 plugin-parameter addressing — the ONE shared chain

Program: `FLEngine_x64.dll`, image base `0x400000`. All addresses are Ghidra addresses.

Scope: the single addressing/vtable chain every plugin-parameter op shares, and hard proof that a
hosted VST3 (Serum 2) routes its params through the **same FL wrapper vtable** as native plugins.
This is the make-or-break for "let the LLM do sound design on Serum 2" via one C++ bridge path.

TL;DR: **YES — one bridge code path covers native, VST2 and VST3.** The FL-side plugin instance is a
`TBaseAudioPlugin`-derived object (`TGenericPlugin` = native, `TVSTPlugin` = VST2, `TVST3Plugin` =
VST3). `getParamName`/`getParamValue` are **inherited base virtuals at instance vtbl `+0x20` / `+0x30`**
— the VST3 class does NOT override them (verified from the raw vtables). Param count is a cached
struct field, and set/display-string go through FL's flavor-agnostic command bus. Nothing in the
param surface is flavor-specific above the wrapper.

---

## 1. Instance resolution — two recipes (verified against 0xE25E10 / 0xE26200 / 0xE26360 / 0xE26CE0 / 0xE26810 / 0xE270A0)

### Shared helpers
- `FLcr_ResolveChannelByIndex(idx, useGlobalList, &out)` @ `0x00F22AC0` -> bool.
  - `useGlobalList==0`: list = `*(void**)0x14A7968` (current/filtered rack view).
  - `useGlobalList!=0`: list = `*(void**)0x14A98D8` (global chanList).
  - bounds: `0 <= idx <= *(int*)(list+0x10) - 1`; item = `*(void**)(*(void**)(list+8) + idx*8)`
    (via `FLcr_ChannelListGetItem` @ `0x00F00F80`).
- `FLcr_ChannelHasPlugin(obj)` @ `0x012297E0`  ==  `*(int*)(obj+0x64) >= 0`.
- `FLplug_GetChannelPluginHost(obj, &host)` @ `0x0122B320`:
  `host = (*(void**)(obj+0x38) != 0) ? *(void**)(obj+0x38) + 0x48 : 0;`
  `if (host == 0) host = DAT_01832ba8;`  (global fallback host).
- Instance = `host->vtbl[+0x20](host)` (host vtbl slot index 4). May be **NULL** (no plugin / bridge
  plugin slot) — guard before use.

The channel-rack object and each mixer FX-slot object share the **same struct layout** (both have
`+0x38` host ptr, `+0x64` has-plugin flag, `+0x68` param count, `+0x9c` channel cmd base, `+0x58`
Delphi UTF-16 name). `FLplug_GetChannelPluginHost` works on both — this is the unification point.

### Recipe A — channel-rack generator (slotIndex == -1)
```c
// bounds/guards
if (!FLcr_ResolveChannelByIndex(channelIndex, useGlobalList, &chan)) return ERR;   // 0xF22AC0
if (!FLcr_ChannelHasPlugin(chan)) return NO_PLUGIN;          // *(int*)(chan+0x64) >= 0
void* host; FLplug_GetChannelPluginHost(chan, &host);        // 0x122B320
void* inst = (*(void*(**)(void*))(*(void**)host + 0x20))(host);   // host vtbl+0x20
if (!inst) return NO_PLUGIN;
int   count       = *(int*)(chan + 0x68);                    // param count (cached)
int   chanCmdBase = *(int*)(chan + 0x9c);                    // for command-bus ops
// param ops: see section 2. cmdId = chanCmdBase + paramIndex + 0x8000
```

### Recipe B — mixer FX slot (track, slot)
```c
if (!(track >= 0 && track <= *g_pMixerTrackCount - 1 && slot >= 0 && slot < 10)) return ERR;
void* slotObj = *(void**)(g_MixerTrackArrayPtr + track*0x1474 + 0x1324 + slot*8);   // 0x14A7EB0
if (!FLcr_ChannelHasPlugin(slotObj)) return NO_PLUGIN;       // *(int*)(slotObj+0x64) >= 0
void* host; FLplug_GetChannelPluginHost(slotObj, &host);
void* inst = (*(void*(**)(void*))(*(void**)host + 0x20))(host);
if (!inst) return NO_PLUGIN;
int   count = *(int*)(slotObj + 0x68);
// cmdId = ((track*0x40 + slot) << 16) + paramIndex + 0x70008000    (0x70000000 = mixer-param flag)
```

Globals: `g_MixerTrackArrayPtr` @ `0x14A7EB0` (stride `0x1474`/track, 10 FX-slot ptrs at
`+0x1324`), `g_pMixerTrackCount` @ `0x14A9850` (`int*`), `FL_MixerEffectParamBase(track,slot)` @
`0x011C23B0` = `(track*0x40 + slot) << 16`.

Param-index range check FL applies (both paths): let `n = count-1`; valid iff
`(n<1) ? (idx==0) : (0 <= idx <= n)`.

---

## 2. Param-op → addressing map (the 5 ops)

Only **2 of the 5** ops touch the plugin instance vtable; the rest are FL-uniform (struct field /
command bus). Every one is flavor-agnostic.

| op | mechanism | exact call |
|----|-----------|------------|
| **getParamCount** | struct field (no vtable call) | `*(int*)(obj + 0x68)`  (obj = chan/slot struct) |
| **getParamName(i)** | **instance vtbl `+0x20`** (slot 4) | `inst->vtbl[+0x20](inst, 0, i, 0, char outBuf[>=0x1000])` -> ANSI/UTF-8 C-string in outBuf |
| **getParamValue(i) (raw)** | **instance vtbl `+0x30`** (slot 6) | `raw = inst->vtbl[+0x30](inst, i, 0, 2)` -> int in the param's native range |
| **getParamValueString(i)** | command bus (no vtable) | `FUN_00E2C880(pluginMgr, &outStr, cmdId, 0, 2)`; if 0, `FLgl_cmd_FormatEventValue(&str, cmdId, raw)` @ `0xF5A720` |
| **setParamValue(i, norm)** | command bus (no vtable) | `FL_SetParamEventValue(pluginMgr, ..., cmdId, round(norm * 2^30 = 1073741824), 0x3fd, ...)` @ `0xE2C960` |

Notes:
- To normalize a raw value: `FLgl_cmd_GetValueRange(cmdId, &lo, &hi)` @ `0xF5E410`; if `!(ret&2)`
  then `norm = (raw-lo)/(hi-lo)` else raw is already float. (per serum-A/B agents; cross-check.)
- `pluginMgr` for the command-bus ops = `*(void**)0x14A95B0`-family object the Python funcs fetch via
  `FUN_00E28790(module)` -> `*ptr`; the bridge can hold FL's global plugin manager equivalently.
- getParamName/getParamValue arg shapes are taken verbatim from the FL Python natives at
  `0xE26360` and `0xE26CE0` (calls `inst->vtbl[+0x20](inst,0,i,0,buf)` and
  `inst->vtbl[+0x30](inst,i,0,2)`).

---

## 3. Wrapper type, vtable model & native-vs-VST2-vs-VST3 dispatch

### Class hierarchy (Ghidra RTTI-recovered)
- `TBaseAudioPlugin` — base wrapper. Full ~122-entry vtable (indices 3..121 named `vtbl_N`).
- `TGenericPlugin` — native FL plugin (derived).
- `TVSTPlugin` — VST2 host wrapper (derived).
- `TVST3Plugin` — VST3 host wrapper (derived).  ← Serum 2 lands here.
- (`TBaseAudioPluginEditor` = editor side, not param-relevant.)

All four share identical vtable slot numbering (each has `vtbl_3..vtbl_121`), i.e. they share the
base interface — polymorphic dispatch on one vtable shape.

### The base wrapper is a thin forwarder onto an inner flavor "param driver" at `this+0x78`
Decompiled base slots (each forwards to `drv = *(void**)(this+0x78)`):
```c
TBaseAudioPlugin.vtbl_3 @0x084CB70 : drv->vtbl[+0x08]()      // slot idx 1
TBaseAudioPlugin.vtbl_4 @0x084CBA0 : drv->vtbl[+0x00]()      // getParamName   (inst slot 4 = byte 0x20)
TBaseAudioPlugin.vtbl_5 @0x084CCA0 : drv->vtbl[+0x90]()      // slot idx 18
TBaseAudioPlugin.vtbl_6 @0x084CDA0 : drv->vtbl[+0x10]()      // getParamValue  (inst slot 6 = byte 0x30)
TBaseAudioPlugin.vtbl_7 @0x084CDC0 : drv->vtbl[+0x78](byte)  // slot idx 15
TBaseAudioPlugin.vtbl_8 @0x084CDF0 : drv->vtbl[+0x20](int)   // slot idx 4
```
So `getParamName`/`getParamValue` are one virtual hop on the **instance** (vtbl `+0x20`/`+0x30`) that
lands in the flavor-specific driver at `instance+0x78`. The bridge only needs the instance hop.

### Instance vtable offset table (the 5 ops)
| logical op | instance vtbl byte off | slot idx | target (native/VST2/VST3 all) |
|---|---|---|---|
| getPluginInstance (on **host**, not instance) | +0x20 | 4 | host-specific: returns the instance |
| getParamName | **+0x20** | 4 | `TBaseAudioPlugin.vtbl_4` @0x084CBA0 |
| getParamValue (raw) | **+0x30** | 6 | `TBaseAudioPlugin.vtbl_6` @0x084CDA0 |
| (param count) | n/a | — | struct field `obj+0x68` |
| (set / value-string) | n/a | — | command bus (cmdId) |
| plugin/DLL name (read) | field | — | `*(*(inst+0x10)+4)` (Delphi UTF-16); fallback `*(obj+0x58)` |

Inner driver (`instance+0x78`) mini-vtable, for reference only (bridge does not call it directly):
`[+0x00]=getParamName, [+0x08]=?, [+0x10]=getParamValue, [+0x20]=? (int), [+0x78]=? (bool),
[+0x90]=?`.

### PROOF that VST3 shares the exact slots (raw vtable read)
Flavor vtable bases (derived from method xrefs):
- `TBaseAudioPlugin` vtbl @ ~`0x084B138` (slot6 ptr @ `0x084B168`).
- `TVST3Plugin`    vtbl @ ~`0x00ADA188` (slot4 @ `0x00ADA1A8`, slot6 @ `0x00ADA1B8`).
- `TVSTPlugin`     vtbl @ ~`0x00AC91A0` (slot4 @ `0x00AC91C0`, slot6 @ `0x00AC91D0`).
- `TGenericPlugin` vtbl @ ~`0x00AFADE0` (slot4 @ `0x00AFAE00`, slot6 @ `0x00AFAE10`).

Read of each slot pointer:
- slot 4 (getParamName): VST3 `0x00ADA1A8`, VST2 `0x00AC91C0`, native `0x00AFAE00` **all = `0x0084CBA0`**
  (`TBaseAudioPlugin.vtbl_4`).
- slot 6 (getParamValue): VST3 `0x00ADA1B8`, VST2 `0x00AC91D0`, native `0x00AFAE10` **all = `0x0084CDA0`**
  (`TBaseAudioPlugin.vtbl_6`). Corroborated: `get_xrefs_to 0x0084CDA0` lists `0x0084B168`(base),
  `0x00AC91D0`(VST2), `0x00AFAE10`(native), `0x00ADA1B8`(VST3) + others (mixer-fx / additional
  wrapper classes) — one shared getParamValue across the whole family.

`TVST3Plugin` defines NO `vtbl_4`/`vtbl_6` override (confirmed by function search) — it inherits
them. Therefore a hosted VST3 (Serum 2) calls the **identical** wrapper code path as a native FL
plugin. 

### Where VST3 bottoms out (evidence, not on the bridge path)
`TVST3Plugin` holds VST3 COM interface pointers as members: e.g. `TVST3Plugin.vtbl_33` @ `0x00AE25C0`
calls `(*(*(this+0x814))->vtbl[+0x40])(this+0x814, 0/1)` gated by a ready flag at `this+0x970`. The
`instance+0x78` param driver for a VST3 plugin ultimately calls into this VST3 controller
(IComponent/IEditController — `setParamNormalized`/`getParamNormalized`/`getParamStringByValue`),
which FL invokes on the main thread. (No VST3/"IEditController"/"setParamNormalized" strings exist in
the binary — VST3 is pure COM vtable dispatch; only `Steinberg\VST2`, `Steinberg\VstPlugins` path
strings @ `0x00AD643C`/`0x00AD6468` are present, in the VST2 wrapper.) This inner detail is NOT
needed by the bridge; it confirms the flavor divergence happens strictly **below** the shared
wrapper vtable.

---

## 4. cmdId formulas (command-bus ops)
- channel: `cmdId = *(int*)(chan + 0x9c) + paramIndex + 0x8000`
- mixer:   `cmdId = ((track*0x40 + slot) << 16) + paramIndex + 0x70008000`

Used by getParamValueString (`FUN_00E2C880` + `FLgl_cmd_FormatEventValue` @0xF5A720) and
setParamValue (`FL_SetParamEventValue` @0xE2C960, value = `round(norm * 2^30)`, flags `0x3fd`).

---

## 5. Readiness / thread notes
Resolve only after the existing `fl_ready` gate is satisfied (all non-null):
`mainForm 0x14A8750`, `toolbarForm 0x14AA4C8`, `song 0x14A9F40`, `chanList 0x14A98D8`.

Per-path requirements:
- **Channel path** needs `chanList 0x14A98D8` (and current-view list `0x14A7968` when
  useGlobalList=0) non-null; reads `list+0x8` (items), `list+0x10` (count).
- **Mixer path** needs `g_MixerTrackArrayPtr 0x14A7EB0` non-null and `g_pMixerTrackCount 0x14A9850`;
  track `0..count-1` (FL 0..125), slot `0..9`.
- Guard order every time: has-plugin (`*(int*)(obj+0x64) >= 0`) -> resolve host
  (`FLplug_GetChannelPluginHost`, may fall back to `DAT_01832ba8`) -> `inst = host->vtbl[+0x20]`
  **check inst != NULL** -> vtable/command ops.

Locks / threading:
- The resolvers (`FLcr_ResolveChannelByIndex`, `FLplug_GetChannelPluginHost`,
  `FLcr_ChannelHasPlugin`) take **no lock** — plain pointer walks. Safe to read on FL's main/UI
  thread once fl_ready.
- Do **writes** through the command bus (`FL_SetParamEventValue`) — FL's own thread-safe
  param/automation path — never by poking the plugin directly.
- Reads via the instance vtable (`getParamName`/`getParamValue`) should run on the FL main thread:
  the VST3 controller these forward into is main-thread-only per the VST3 spec, and FL's forwarding
  assumes the UI/main thread. Prefer marshalling bridge calls onto FL's main thread.

---

## 6. Uncertainties for live verification
- Exact most-derived class of the object returned by `host->vtbl[+0x20]` (TVST3Plugin vs a
  TBaseAudioPlugin holding a TVST3Plugin at +0x78). Does NOT change the bridge — slots 4/6 are proven
  shared either way — but worth confirming with a live `getPluginName` on a Serum 2 slot + a pointer
  read at `inst` / `inst+0x78` / `inst+0x814`.
- Confirm live that `obj+0x68` param count is populated for a freshly-loaded VST3 (FL fills it on
  load; verify it is non-zero for Serum 2 before iterating).
- getParamValue raw range/normalization: confirm `FLgl_cmd_GetValueRange` (@0xF5E410) returns sane
  lo/hi for a VST3 param so the bridge can present 0..1 to the LLM (Serum 2 has many params).
- Arg 1 of `getParamName` (passed as 0) and arg2/arg3 of `getParamValue` (0,2) are copied verbatim
  from FL's Python natives; verify no per-flavor meaning when driven from the injected bridge.
