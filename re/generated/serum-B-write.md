# Serum-B: WRITE hosted-plugin params + change presets (FLEngine_x64.dll)

RE of FL's Python `plugins.*` WRITE / preset funcs so the injected C++ bridge can set
VST/native plugin parameters and navigate presets (the "let the LLM do sound design on
Serum 2" write half). Image base 0x400000; all addresses are Ghidra addresses. These are
`PyObject* fn(PyObject* self, PyObject* args, PyObject* kwargs)` C funcs that parse args with
**`PyArg_ParseTupleAndKeywords`** (`PTR_DAT_014abac8`) then drive engine code.

Target resolution (channel `slotIndex==-1` vs mixer FX-slot 0..9), `FLcr_ResolveChannelByIndex`,
`FLcr_ChannelHasPlugin`, `FLplug_GetChannelPluginHost @0x122B320`, `g_MixerTrackArrayPtr`
(stride 0x1474, slot ptrs at +0x1324), `g_pMixerTrackCount` — all identical to serum-A-read.md
and owned by the addressing agent. Not re-derived here; only the **setter/preset cores** are.

> **KEY FINDING:** the WRITE path is NOT an instance-vtable call. FL's `setParamValue` routes
> through the **central command bus** keyed by an **event/cmd id** (the "event id indirection"),
> marshalled to the **main GUI thread**. There is no `(*(*inst+0xNN))(inst,paramIdx,value)` write
> in this path (that pattern is READ-only, serum-A). The bridge should replicate the bus path.

---

## 1. plugins.setParamValue @ 0xE270A0  (`FL_py_plugins_setParamValue`) — PRIMARY write path

### 1a. Python arg signature
`PyMethodDef` @0x140DDC8, `METH_VARARGS|METH_KEYWORDS`. Parsed as:
```
PyArg_ParseTupleAndKeywords(args, kw, "fii|iii", kwnames,
    &paramValue /*float*/, &paramIndex /*int*/, &index /*int*/,
    &slotIndex /*int, default -1*/, &touchFlag /*int, default 0*/, &useGlobalIndex /*int, default 0*/)
```
Format `"fii|iii"` = 6 targets, first 3 required, last 3 optional. **Arg order (positional):**

| # | name | C local | type | default | meaning |
|---|------|---------|------|---------|---------|
| 1 | `paramValue`   | local_30 | float  | —  | **normalized 0..1** value to write |
| 2 | `paramIndex`   | local_24 | int    | —  | plugin param index |
| 3 | `index`        | local_1c | int    | —  | channel index (slot==-1) OR mixer track (slot!=-1) |
| 4 | `slotIndex`    | local_20 | int    | -1 | -1 = channel-rack generator; 0..9 = mixer FX slot |
| 5 | `touchFlag`    | local_28 | int(byte used) | 0 | automation touch: 1=begin, 0=end, 2=conditional (see 1c) |
| 6 | `useGlobalIndex`| local_2c | int   | 0  | passed to `FLcr_ResolveChannelByIndex(index, useGlobalIndex>0, …)` |

(`kwnames` table @0x140DBE0 begins `"paramValue"`.)

### 1b. cmdId (event-id) computation — the indirection
```
// channel (slotIndex == -1), chanObj resolved via FLcr_ResolveChannelByIndex:
cmdId = *(int*)(chanObj + 0x9c) + paramIndex + 0x8000;      // chanObj+0x9c = channel event base
// mixer FX slot (slotIndex 0..9), bounds-checked (index in [0,trackCount-1], slot in [0,10)):
cmdId = FL_MixerEffectParamBase(index, slotIndex) + paramIndex + 0x70008000;
      = ((index*0x40 + slotIndex) << 16) + paramIndex + 0x70008000;   // 0x70000000 = mixer-param flag
FL_MixerEffectParamBase @0x11C23B0 : return (track*0x40 + slot) << 16;
```
Same cmdId formula the READ side uses (serum-A §3). Mixer occupancy is pre-checked by
`FLcr_ChannelHasPlugin(*(void**)(g_MixerTrackArrayPtr + slot*8 + track*0x1474 + 0x1324))`.

### 1c. Value encoding (fixed-point Q30, normalized) + XMM note
```
valueFixed = (int) round( paramValue * 1.0737418e+09 );   // 1.0737418e9 == 2^30 == 0x40000000
           // FUN_0040C660 = ROUND(double)->longlong
```
- Input is **float, normalized 0..1**; scaled to a **30-bit fixed-point int** (0..2^30).
- The float→double multiply happens in **XMM** in the *Python wrapper only*; the **engine terminal
  takes an integer** fixed-point value (see 1d) — the value is **NOT** passed in XMM0 to the core.
- The 0x20 ("value is normalized") flag (part of the 0x3fd flag set) tells the bus to rescale the
  Q30 normalized value into the param's real `[min,max]`. So: pass **normalized**, not raw.

### 1d. Full call chain to the engine core
```
FL_py_plugins_setParamValue
  -> FUN_00EA2560(cmdId, touchFlag)          // automation "touch" begin/end (side effect, 1e)
  -> valueFixed = FUN_0040C660(paramValue * 2^30)
  -> FL_SetParamEventValue(engine, &resultBuf, cmdId, valueFixed, 0x3fd, 0, 0, 0)   @0xE2C960
       -> FL_DispatchOpEvent(0, &resultBuf, FUN_00E2C8F0 /*handler*/, 0,
                             cmdId, valueFixed, 0x3fd, 0, 0, &param8, 1)             @0xE2C350
            // allocates event struct, stores args at +0x18(cmdId) +0x20(valueFixed)
            //   +0x28(0x3fd) +0x30(0) +0x38(0) +0x40(&0), handler ptr at +0x58,
            // PostMessageW(flMainHwnd, 0x562, 0, 0) + Delphi Synchronize (FUN_00524270)
            //   ==> handler runs on FL MAIN/GUI THREAD.
       (handler) FUN_00E2C8F0(evt)                                                   @0xE2C8F0
            // readiness gate: (*(*(engine+8)+0xa0))(engine); if false, no-op (returns -2 sentinel)
            -> FUN_00F5F1A0(cmdId, valueFixed, 0x3fd, 0, 0, 0, 0)                    @0xF5F1A0
                 // param handler; for a normal set (arg4==0, arg5<1):
                 -> enc = FUN_00EA2D30(0, valueFixed)     // ==identity for plugin params (0->passthru)
                 -> enc = FUN_00F5F110(desc, enc)         // step-quantize; no-op unless (desc.cmdClass&0xffff)==4
                 -> FL_DispatchCommand(cmdId, enc, 0x3fd)                            @0xF53FE0  ***TERMINAL***
                 -> if(recording-armed) FUN_00EA34A0(...) // record/register automation (1e)
```

**Final callable core:** `FL_DispatchCommand(uint cmdId, ULONG_PTR value, uint flags)` @0xF53FE0.
```c
// giant switch over cmdId; targets pulled from engine globals (no ctx arg). MAIN-THREAD ONLY.
// flags 0x3fd = SET(0x1) | 0x4 | 0x8 | 0x10 | NORMALIZED(0x20) | 0x40 | 0x80 | 0x100 | 0x200.
uint FL_DispatchCommand(uint cmdId, ULONG_PTR value /*Q30 normalized when flag 0x20*/, uint flags);
```
`value` is an **integer (ULONG_PTR/int fixed-point)** — no XMM. Returns current value (used by GET).

**Recommended bridge write** (from any thread — it self-marshals to main thread + gives scaling,
automation, and UI update, exactly like FL):
```c
struct { void* status; void* a; void* b; } out;              // >=0x18 result buffer
int cmdId = channelBase + paramIndex + 0x8000;               // or mixer: (trk*0x40+slot)<<16 + i + 0x70008000
int v = (int)lround((double)norm * 1073741824.0);            // Q30
FL_SetParamEventValue(engine, &out, cmdId, v, 0x3fd, 0, 0, 0);   // @0xE2C960
// `engine` = engine module singleton (the *(PTR_DAT_014abca8) chain) — same ptr the getters use.
```
If the bridge is *already on FL's main thread*, it may skip the marshalling and call
`FL_DispatchCommand(cmdId, v, 0x3fd)` directly (or `FUN_00F5F1A0(cmdId, v, 0x3fd, 0,0,0,0)` to also
get the automation-record + last-tweaked side effects).

### 1e. Side effects (NOT a clean value poke)
- **Automation touch:** `FUN_00EA2560(cmdId, touchFlag)` (arg5). `touchFlag==1` registers cmdId in the
  "currently tweaking" set (DAT_0157F350) = begin touch; `==0` removes = end touch; `==2` conditional
  on the automation-record flag `*(...+0x492)`. This is the smoothing/latch grouping for automation.
- **Automation record:** inside `FUN_00F5F1A0`, if FL is arm-recording (flag `*(*(PTR_014a8b00)+0xf30)+0x492`
  or the event-record counter `*(PTR_014a7860+0x10) > 0`) AND flag 0x20 set, the write is routed through
  `FUN_00F5EBA0` (resolve automation target) and only committed if it resolves; when NOT recording it
  dispatches directly and calls `FUN_00EA34A0` (updates last-tweaked registry / controller map).
  **=> setting a param while FL's automation-record is armed WILL write automation**, not just a live value.
- **UI + dirty:** `FL_DispatchCommand` moves the on-screen control and marks the project modified
  (standard FL param-tweak behavior).
- **Return codes** (boxed to Python): -4 = channel/track not resolvable, -8 = slot has no plugin,
  0/1 = OK.

---

## 2. plugins.getPresetCount @ 0xE27920  (`FLpy_plugins_getPresetCount`)

- **Args:** `PyArg_ParseTupleAndKeywords(args,kw,"i|ii", kwnames, &index, &slotIndex(=-1), &useGlobalIndex(=0))`.
- **Core (synchronous, directly callable):**
  ```
  count = FUN_011BAAD0(chanObj);          // @0x11BAAD0, chanObj = resolved channel OR mixer-slot object
  ```
  `FUN_011BAAD0(obj)` returns **builtin-preset count + .fst-file count**:
  ```
  builtin = (*(*(obj+0x94) + 0x28))(*(obj+0x94));   // obj+0x94 = preset provider; vtbl+0x28 = Count
  node    = FUN_00510130(&PTR_..._RejectIncompatible_004cd480, 1);  FUN_011BB1B0(obj, node);
  fst     = (*(*node + 0x28))(node);                // count of .fst preset files for this plugin
  return builtin + fst;
  ```
- **Semantics:** signed int total selectable presets. This is **FL preset-bank** counting (built-in
  wrapper presets + on-disk .fst), NOT a raw VST `numPrograms`.
- **Bridge:** resolve `obj`, call `FUN_011BAAD0(obj)`. Read-ish but walks FL structures — prefer main thread.

---

## 3. plugins.nextPreset @ 0xE27560 / plugins.prevPreset @ 0xE27740

- **Args (both):** `"i|ii"` = `&index, &slotIndex(=-1), &useGlobalIndex(=0)` via kwargs.
- **Both are deferred to the main thread** (do NOT act inline). They build a small heap job struct
  and post it:
  ```
  job = FUN_0040F9E0(&DAT_00E25C48, 1);
  job[+8]  = index;
  job[+0xc]= slotIndex;
  job[+0x10]= direction;         // nextPreset = +1 ; prevPreset = -1 (0xFFFFFFFF)
  job[+0x14]= useGlobalIndex;
  FUN_0088A570(mainQueue /*(*(PTR_DAT_014abca8)+100)*/, FUN_00E27400 /*handler*/, job);
  ```
- **Deferred handler** `FUN_00E27400(job, mask)` @0xE27400 (runs on main thread): re-resolves channel/
  slot, then calls the preset-nav core:
  ```
  FUN_011BADB0(chanObj_or_slotObj, direction, 0);     // @0x11BADB0  ***preset nav core***
  ```
- **Preset-nav core** `FUN_011BADB0(obj, dir, flag)` @0x11BADB0:
  ```
  total   = builtinCount + fstCount;                              // same two sources as getPresetCount
  newIdx  = ((int)obj[0x1e] + dir + total) % total;              // obj[0x1e] == *(int*)(obj+0xf0) = current preset idx
  FUN_011BF590(obj, newIdx);                                     // @0x11BF590 : *(int*)(obj+0xf0) = newIdx  (store index)
  if (newIdx < builtinCount)
      (*(*obj + 0xf0))(obj, newIdx, 0, descStr, 1, flag);        // load builtin preset by absolute index
  else
      (*(*obj + 0xf0))(obj, 0xFFFFFFFE, fstPath, descStr, 1, flag); // load .fst file preset
  ```
- **Preset change kind:** FL **bank navigation** through the plugin wrapper (builtin presets + .fst),
  wrapping mod `total`. For a VST like Serum this drives the wrapper's program navigation — it is NOT a
  raw VST program-change opcode; it is FL's own preset load.
- **Return codes:** -4 not resolvable, -8 no plugin, 0 = job posted OK.

### Directly-callable ABSOLUTE "set preset by index" (bonus for the bridge)
There is no `plugins.setPreset`, but the mechanism underneath is directly usable **on the main thread**:
```c
// obj = resolved chanObj (or mixer slotObj); idx in [0, builtinCount)
(*(void(**)(void*,int,void*,void*,int,int))((*(void***)obj)[0x1e]))
    (obj, idx, 0, descStr, 1, 0);       // vtable +0xf0 (0x1e * 8) = LoadPresetByIndex
// and update the stored current index:
FUN_011BF590(obj, idx);                 // *(int*)(obj+0xf0) = idx
```
Or simply `FUN_011BADB0(obj, dir, 0)` for relative moves, or loop it. `(*(*obj+0xf0))` with a `.fst`
path + arg `0xFFFFFFFE` loads a preset file. **Verify vtable slot 0xf0 + arg shape live** before relying
on absolute-index (derived from the nav core, not a named export).

---

## 4. plugins.getColor @ 0xE27A90  (`FLpy_plugins_getColor`)

- **Args:** `"i|iiii"` = `&index, &slotIndex(=-1), &arg3, &arg4, &useGlobalIndex(=0)`
  (kwnames @0x140DC78; positional: index, slotIndex, then two selector ints, then useGlobalIndex).
- **Core (INSTANCE vtable, generic dispatch +8):**
  ```
  inst = (*(*host+0x20))(host);                          // host from FLplug_GetChannelPluginHost
  bgr  = (*(*inst + 8))(inst, 0x21, arg3, arg4);          // dispatch id 0x21 = GetColor
  rgb  = (bgr & 0xff00ff00) + (bgr & 0xff00ff)*0x10000 + ((bgr & 0xff00ff) >> 0x10);  // BGR<->RGB swap
  ```
- **Semantics:** returns an int color; wrapper swaps FL's BGR to 0x00RRGGBB. Instance vtable **+8**
  = the plugin-wrapper generic dispatch `int dispatch(void* self, int id, intptr a, intptr b)`.
  READ-only. Same main-thread caveat as serum-A instance calls.

---

## 5. plugins.getName @ 0xE27DB0  (`FLpy_plugins_getName`) — semantic-element name

- **Args:** `"i|iiii"` = `&index, &slotIndex(=-1), &kind, &elemIndex, &useGlobalIndex(=0)`
  (kwnames @0x140DCA8; positional: index, slotIndex, kind, elemIndex, useGlobalIndex).
- **Core:**
  ```
  inst = (*(*host+0x20))(host);
  if (kind == 6)  FUN_011BAB30(obj, &str, elemIndex);        // kind 6 = preset name (from bank/list)
  else {
      if (elemIndex < 0) elemIndex = 0;
      (*(*inst + 0x20))(inst, kind, elemIndex, 0, buf /*char[0x1000]*/);   // GetParamText-style, mode=kind
  }
  // -> PyUnicode
  ```
- **Semantics:** `kind` selects what to name (param/pad/preset/etc.); **kind 6 = preset name** via
  `FUN_011BAB30(obj, &out, presetIndex)` — handy to label presets when driving next/prev. Instance
  vtable **+0x20** is the same multiplexed GetParamText the READ agent documented (mode arg = `kind`).
  NUL-terminated ANSI/UTF-8 into a >=0x1000 buffer. READ-only.

---

## 6. plugins.getPadInfo @ 0xE28290  (`FLpy_plugins_getPadInfo`)

- **Args:** `"i|iiii"` = `&index, &slotIndex(=-1), &arg3, &arg4, &useGlobalIndex(=0)` (kwnames @0x140DCD8).
- **Core (INSTANCE vtable +8, dispatch id 0x35 = GetPadInfo/drumpad):**
  ```
  inst = (*(*host+0x20))(host);
  r = (*(*inst + 8))(inst, 0x35, sel_a, sel_b);
  // if the pad-info selector requests a color (sub-selector == 2), same BGR->RGB swap as getColor
  ```
- **Semantics:** drum-pad metadata for pad-based plugins (e.g. FPC). READ-only. Returns int; color
  sub-queries get the BGR→RGB swap. Main-thread caveat.

---

## Thread / locking summary
- **Write (`setParamValue`)**: the setter runs on **FL's main GUI thread**. `FL_SetParamEventValue`
  →`FL_DispatchOpEvent` marshals via `PostMessageW(hwnd,0x562)` + Delphi `Synchronize`, and the handler
  first checks a **readiness gate** `(*(*(engine+8)+0xa0))(engine)` (returns -2 / no-op if not ready).
  `FL_DispatchCommand` is documented main-thread-only. Two write-path bracket critical sections
  (`PTR_014ab2d8`, `PTR_014a96c8`) are taken only on the relative-adjust / value!=0 subpaths, not the
  normal absolute-set path. **Bridge rule:** call `FL_SetParamEventValue` (self-marshals) from any
  thread, OR call `FL_DispatchCommand`/`FUN_00F5F1A0` directly ONLY when already on FL's main thread.
- **Presets (`next/prevPreset`)**: explicitly **queued to main thread** (`FUN_0088A570`→`FUN_00E27400`).
  The direct `FUN_011BADB0` / vtable+0xf0 preset-load must also run on the main thread.
- **getPresetCount / getColor / getName / getPadInfo**: synchronous reads that walk FL/plugin
  structures — no internal lock; call on main thread to avoid tearing during (re)load.

## Function name map (Ghidra)
| addr | name | role |
|------|------|------|
| 0xE270A0 | FL_py_plugins_setParamValue | build cmdId + Q30 value -> FL_SetParamEventValue |
| 0xE27920 | FLpy_plugins_getPresetCount | FUN_011BAAD0(obj) = builtin + .fst count |
| 0xE27560 | FLpy_plugins_nextPreset | queue job dir=+1 -> FUN_00E27400 -> FUN_011BADB0 |
| 0xE27740 | FLpy_plugins_prevPreset | queue job dir=-1 -> FUN_00E27400 -> FUN_011BADB0 |
| 0xE27A90 | FLpy_plugins_getColor | inst->vtbl[8](inst,0x21,a,b) + BGR->RGB |
| 0xE27DB0 | FLpy_plugins_getName | kind6=FUN_011BAB30 preset name; else inst->vtbl[0x20](inst,kind,i,0,buf) |
| 0xE28290 | FLpy_plugins_getPadInfo | inst->vtbl[8](inst,0x35,a,b) |
| 0xE2C960 | FL_SetParamEventValue | (engine,&out,cmdId,valFixed,0x3fd,0,0,0) — self-marshaling write entry |
| 0xE2C350 | FL_DispatchOpEvent | PostMessage + Synchronize -> main-thread handler |
| 0xE2C8F0 | (handler) param-event body | readiness gate -> FUN_00F5F1A0 |
| 0xF5F1A0 | (param handler) | encode + FL_DispatchCommand + automation record |
| 0xF53FE0 | FL_DispatchCommand | central command bus TERMINAL (cmdId,value,flags); main-thread only |
| 0xE27400 | (deferred preset handler) | re-resolve -> FUN_011BADB0(obj,dir,0) |
| 0x11BADB0 | (preset nav core) | (cur+dir+total)%total -> obj->vtbl[0xf0] load |
| 0x11BAAD0 | (preset count core) | builtin(obj+0x94 vtbl+0x28) + .fst count |
| 0x11BF590 | (set preset index) | *(int*)(obj+0xf0) = idx |
| 0x11C23B0 | FL_MixerEffectParamBase | (track*0x40+slot)<<16 |
| 0x0040C660 | ROUND(double)->longlong | float*2^30 -> Q30 int |

## Open items to verify live (Serum 2)
- **Normalized write range:** confirm `FL_DispatchCommand` with flag 0x20 rescales Q30 (0..2^30) into
  Serum's param range so `norm=0.5` lands mid-range. (serum-A notes some params are `&2`-float; a
  float param may want the raw float bits rather than Q30 — verify per-param via getParamValue round-trip.)
- **Automation-record coupling:** confirm that with FL NOT in record, `setParamValue` writes a live value
  without creating automation, and that `touchFlag` (arg5) isn't required for a one-shot set (default 0).
- **Absolute set-preset:** confirm vtable slot **+0xf0** signature `(obj, idx, 0, desc, 1, 0)` works for
  absolute index on a VST wrapper, and that `+0xf0` field = current preset index. (Derived from nav core.)
- **Direct-instance write shortcut (optional):** the READ side uses `inst->vtbl[0x30](inst,i,0,2)`; a
  direct write may be `inst->vtbl[0x30](inst,i,value,1)` — UNCONFIRMED and would bypass scaling/
  automation/UI. Prefer the command-bus path; only probe the direct write if the bus proves too heavy.
- **Main-thread requirement:** verify the readiness gate + Synchronize actually block/no-op when the
  bridge calls off-thread, so the bridge always goes through `FL_SetParamEventValue`.
