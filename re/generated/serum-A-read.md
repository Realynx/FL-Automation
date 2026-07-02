# Serum-A: READ hosted-plugin parameters (FLEngine_x64.dll)

RE of FL's Python `plugins.*` READ funcs to let the injected C++ bridge read VST/native
plugin params directly (bypassing Python). Image base 0x400000; all addresses are Ghidra
addresses. These are `PyObject* fn(PyObject* self, PyObject* args)` C functions that parse
args with `PyArg_ParseTuple` (via `PTR_DAT_014abac8`) then call engine code.

---

## 0. Shared arg-parse + target resolution (identical across all funcs)

`PTR_DAT_014abac8` = `PyArg_ParseTuple`. Two arg orderings are used (see per-func).

### Instance-pointer resolution — the crux for the bridge

Two paths, selected by whether `slotIndex == -1`:

**Channel-rack generator path (`slotIndex == -1`):**
```
char ok = FLcr_ResolveChannelByIndex(index, useGlobalIndex>0, &chanObj);   // chanObj = channel plugin object
if (!ok) -> error -4
if (!FLcr_ChannelHasPlugin(chanObj)) -> error -8      // = (*(int*)(chanObj+0x64) >= 0)
FLplug_GetChannelPluginHost(chanObj, &host);          // @0x122B320
instance = (*(*host + 0x20))(host);                    // host vtbl[4]; ==0 -> no plugin
```

**Mixer FX-slot path (`slotIndex != -1`, 0..9):**
```
// bounds: 0 <= index <= *g_pMixerTrackCount-1  AND  0 <= slotIndex < 10
slotObj = *(void**)(g_MixerTrackArrayPtr + slotIndex*8 + index*0x1474 + 0x1324);
// then identical: FLcr_ChannelHasPlugin(slotObj) -> FLplug_GetChannelPluginHost(slotObj,&host) -> instance
```

`FLplug_GetChannelPluginHost(obj,&out)` @0x122B320:
`host = *(obj+0x38) ? *(obj+0x38)+0x48 : DAT_01832ba8`. Returns host slot ptr in `*out`.
`instance = (*(*host+0x20))(host)` (host vtable+0x20 = GetPluginInstance, returns 0 if empty).

So for BOTH paths the bridge ends with a **plugin-instance pointer** `inst` and calls the
param methods on `inst`'s vtable (offsets below). `obj` (channel or mixer-slot object) also
carries the **param count** at `+0x68` and the **channel cmdId base** at `+0x9c`.

`FLcr_ResolveChannelByIndex` / `FLcr_ChannelHasPlugin` / `FLplug_GetChannelPluginHost` are
owned by the addressing-chain agent; treated as known here.

---

## 1. plugins.getParamCount @ 0xE26200  (`FL_py_plugins_getParamCount`)

- **Args:** `PyArg_ParseTuple(args,"i|ii", index, &slotIndex(=-1), &useGlobalIndex(=0))`.
  Order: `index`, optional `slotIndex`, optional `useGlobalIndex`.
- **Core:** NO engine/vtable call. Count is a plain field read:
  ```
  count = *(int*)(obj + 0x68);      // obj = chanObj or mixer slotObj
  ```
  Returned via `PyLong_FromLong` (`PTR_DAT_014ab7d8`).
- **Bridge:** after resolving `obj` (don't even need `inst`), read `*(int32*)(obj+0x68)`.
- **Semantics:** signed int, number of params exposed by the wrapper. No lock/thread guard.

---

## 2. plugins.getParamName @ 0xE26360  (`FLpy_plugins_getParamName`)

- **Args:** `PyArg_ParseTuple(args,"ii|ii", &paramIndex, &index, &slotIndex(=-1), &useGlobalIndex(=0))`.
  Order: **`paramIndex` FIRST, then `index`**, optional `slotIndex`, optional `useGlobalIndex`.
  (Note the arg order differs from getParamCount — paramIndex leads.)
- **Range check:** valid range is derived from `count = *(int*)(obj+0x68)`; `paramIndex` must be
  within `[0, count-1]` (special-cased when count-1 < 1).
- **Core call (on the INSTANCE vtable):**
  ```
  char buf[0x1000];                         // caller supplies >=4096-byte buffer
  (*(*inst + 0x20))(inst, 0, paramIndex, 0, buf);   // mode 0 = NAME
  ```
  Then wrapped into a std/Delphi string and returned as `PyUnicode_FromString` (`PTR_DAT_014ab2e0`).
- **Instance vtable offset:** **+0x20**, mode arg = 0.
  `void GetParamText(void* self, int mode/*0=name*/, int paramIdx, int value/*0*/, char* outBuf)`.
- **String semantics:** writes a **NUL-terminated ANSI/UTF-8 C-string** into `outBuf` (the
  wrapper then does `FUN_00414910(&s, buf, 0x1000)` = build string from char* with cap 0x1000).
  Length is by NUL terminator. Give it a >=0x1000 buffer to be safe.

---

## 3. plugins.getParamValue @ 0xE26CE0  (`FLpy_plugins_getParamValue`)  — returns normalized value

- **Args:** `PyArg_ParseTuple(args,"ii|ii", &paramIndex, &index, &slotIndex(=-1), &useGlobalIndex(=0))`.
  Same order as getParamName: `paramIndex`, `index`, opt `slotIndex`, opt `useGlobalIndex`.
- **Core call (on the INSTANCE vtable):**
  ```
  int32 raw = (*(*inst + 0x30))(inst, paramIndex, 0, 2);   // vtable+0x30, flag 2 = "get value"
  ```
- **Instance vtable offset:** **+0x30**.
  `int GetParamValue(void* self, int paramIdx, int /*0*/, int flag/*2*/)` -> raw 32-bit value.
- **Normalization** (helper `FUN_00e26c80(_, cmdId, raw)` @0xE26C80):
  ```
  flags = FLgl_cmd_GetValueRange(cmdId, &lo, &hi);   // @0xF5E410
  if (flags & 2) norm = *(float*)&raw;               // continuous param: raw IS an IEEE float
  else            norm = (hi==lo) ? 0.0 : (double)((int)raw - lo) / (double)(hi - lo);  // stepped
  ```
  cmdId (channel) = `*(int*)(obj+0x9c) + paramIndex + 0x8000`.
  cmdId (mixer)   = `FL_MixerEffectParamBase(index,slotIndex) + paramIndex + 0x70008000`
                     (= `((index*0x40+slotIndex)<<16) + paramIndex + 0x70008000`).
  Result boxed via `PyFloat_FromDouble` (`PTR_DAT_014a95b0`).
- **Value semantics (IMPORTANT):** the Python API returns a **double**, USUALLY normalized 0..1
  for stepped/integer params. BUT for params whose flags byte has bit 1 set (`&2`), the raw
  32-bit is reinterpreted as a **float and returned AS-IS (not 0..1)**. The bridge must
  replicate the `FLgl_cmd_GetValueRange` step to know which. `FLgl_cmd_GetValueRange` is cached
  and takes a critical section (`PTR_DAT_014aa540`); on a cache miss it issues
  `FL_DispatchCommand(cmdId,0,0x20)`/`(cmdId,0x40000000,0x20)` to probe min/max.
- **Shortcut:** if the bridge only wants the plugin's own raw param value, `inst->vtbl[0x30]`
  is enough; normalization is optional post-processing.

---

## 4. plugins.getParamValueString @ 0xE26810  (`FLpy_plugins_getParamValueString`) — display text

- **Args:** `PyArg_ParseTuple(args,"ii|ii", &paramIndex, &index, &slotIndex(=-1), &useGlobalIndex(=0))`.
  Same order as getParamValue.
- **Core, two steps:**
  1. Read current raw value through the command/event system:
     `FUN_00e2c880(pyGlobal, &out{status,rawVal}, cmdId, 0, 2)` @0xE2C880 which wraps
     `FL_DispatchOpEvent(..., FUN_00e2c820, ..., cmdId, 0, 2, ...)`. `FUN_00e2c820` @0xE2C820
     runs on the engine thread: it checks a **readiness gate** `(*(*g+0xa0))(g)` (g via
     `PTR_DAT_014abca8`); if not ready it returns `-2` (0xFFFFFFFFFFFFFFFE) and does nothing,
     else `rawVal = FL_DispatchCommand(cmdId, 0, 2)`. Status 0 = OK.
  2. If status==0: `FLgl_cmd_FormatEventValue(&s, cmdId, rawVal)` @0xF5A720 formats the raw
     value into a human display string (units per cmdId: %, cents, BPM, dB, Hz, etc.), boxed
     via `PyUnicode_FromString`.
- **cmdId:** same formula as getParamValue (channel `obj+0x9c + i + 0x8000`; mixer
  `base + i + 0x70008000`).
- **`FLgl_cmd_FormatEventValue` plugin fallback:** if the built-in formatter produces nothing
  (`*out==0`), it resolves the plugin itself and calls the SAME instance method used for names:
  ```
  (*(*inst + 0x20))(inst, 1, paramIdx, rawVal, outBuf);   // mode 1 = VALUE string
  ```
  So instance vtable **+0x20 is multiplexed by its first arg**: `0` = param NAME (sec.2),
  `1` = param VALUE display string. `void GetParamText(self, int mode, int paramIdx, int value, char* buf)`.
- **String semantics:** NUL-terminated ANSI/UTF-8 C-string into a >=0x1000 buffer; wrapped to
  UTF-16 for Python. Length by NUL terminator.
- **Bridge choice:** cheapest correct display string = call `inst->vtbl[0x20](inst, 1,
  paramIdx, raw, buf)` directly with the raw value from `inst->vtbl[0x30]`. That skips the
  command-dispatch/readiness machinery entirely. Use `FLgl_cmd_FormatEventValue` only if you
  want FL's built-in unit formatting for engine (non-plugin) params.

---

## 5. plugins.isValid @ 0xE25CD0  (`FLpy_plugins_isValid`) — the validity gate / resolution template

- **Args:** `PyArg_ParseTuple(args,"i|ii", index, &slotIndex(=-1), &useGlobalIndex(=0))`
  (same shape as getParamCount).
- **Logic:** resolves `obj` via the exact channel/mixer paths above, then returns
  `FLcr_ChannelHasPlugin(obj)` (bool) via `PyBool_FromLong` (`PTR_DAT_014ab7d8`).
  `FLcr_ChannelHasPlugin(obj) == (*(int*)(obj+0x64) >= 0)`.
- **Bridge:** this is the canonical "does this channel/mixer-slot hold a plugin" check =
  resolve `obj`, test `*(int32*)(obj+0x64) >= 0`. Do this before calling any param method.

---

## Instance (plugin-wrapper) vtable — bridge cheat-sheet

Given `inst` (from `(*(*host+0x20))(host)` after `FLplug_GetChannelPluginHost`):

| offset | method | prototype | returns |
|--------|--------|-----------|---------|
| +0x20  | GetParamText | `void (*)(void* self, int mode, int paramIdx, int value, char* outBuf)` | fills `outBuf`; mode 0 = param name, mode 1 = value display string |
| +0x30  | GetParamValue | `int (*)(void* self, int paramIdx, int /*0*/, int flag/*2*/)` | raw 32-bit value (int for stepped, float-bits for `&2` params) |

Object (`obj` = chanObj or mixer slotObj) plain fields:
- `+0x64` : int, slot-occupied / has-plugin sentinel (`>=0` means present).
- `+0x68` : int, **param count**.
- `+0x9c` : int, **channel event/cmd base** (for cmdId = base + paramIdx + 0x8000).

Host vtable: `+0x20` = GetPluginInstance (returns `inst`, 0 if empty).

---

## Locking / thread-affinity notes for the bridge

- **Direct instance calls** (`inst->vtbl[0x20]`, `inst->vtbl[0x30]`) have **no internal lock**
  in these wrappers. The plugin wrapper is GUI/engine-owned and NOT thread-safe — the bridge
  MUST call these on **FL's main/GUI thread** (the thread that runs the plugin), not from an
  arbitrary bridge/IPC thread.
- **`FLgl_cmd_GetValueRange`** (@0xF5E410) takes `EnterCriticalSection(PTR_DAT_014aa540)` around
  its cache and may issue `FL_DispatchCommand` on miss — safe to call, but still respects the
  engine command model.
- **`FUN_00e2c820`** (the getParamValueString primary read) has an explicit **readiness gate**
  `(*(*g+0xa0))(g)` via `PTR_DAT_014abca8`; if the engine isn't ready it returns -2. This
  confirms the value-string path assumes engine-ready + main-thread. The bridge's direct
  `inst->vtbl[0x20](...,1,...)` path avoids the dispatch but still needs main-thread affinity.
- `count`/`+0x64`/`+0x9c` are plain field reads; still read under main-thread to avoid tearing
  during plugin (re)load.

---

## Function name map (Ghidra)

| addr | current name | role |
|------|--------------|------|
| 0xE26200 | FL_py_plugins_getParamCount | count = *(int*)(obj+0x68) |
| 0xE26360 | FLpy_plugins_getParamName | inst->vtbl[0x20](inst,0,i,0,buf) |
| 0xE26CE0 | FLpy_plugins_getParamValue | inst->vtbl[0x30](inst,i,0,2) + normalize |
| 0xE26810 | FLpy_plugins_getParamValueString | dispatch read + FormatEventValue / vtbl[0x20] mode1 |
| 0xE25CD0 | FLpy_plugins_isValid | FLcr_ChannelHasPlugin(obj) == (*(int*)(obj+0x64)>=0) |
| 0xE25E10 | FLpy_plugins_getPluginName | (reference) host->inst->name |
| 0xE26C80 | FUN_00e26c80 | normalize raw via GetValueRange |
| 0xE2C880 | FUN_00e2c880 | dispatch-op wrapper (read raw value) |
| 0xE2C820 | FUN_00e2c820 | op body: readiness gate + FL_DispatchCommand |
| 0xF5E410 | FLgl_cmd_GetValueRange | (cmdId,&lo,&hi)->flags; flag&2 = float param |
| 0xF5A720 | FLgl_cmd_FormatEventValue | (out,cmdId,rawVal)->display; plugin fallback vtbl[0x20] mode1 |
| 0x122B320 | FLplug_GetChannelPluginHost | obj -> host slot |

---

## Open items to verify live

- **`inst->vtbl[0x30]` return interpretation**: confirm on Serum 2 whether params report as
  stepped (int, needs `(raw-lo)/(hi-lo)`) or `&2`-float (raw is IEEE float, already 0..1-ish).
  VST params are typically already normalized 0..1 — verify whether FL's wrapper exposes them
  as `&2`-float (then getParamValue returns the plugin's native 0..1 directly) or rescales.
- Confirm the exact meaning of the `mode`/`flag` args (0/1 for vtbl+0x20; the trailing `2` for
  vtbl+0x30) hold for a 3rd-party VST wrapper vs FL native plugins (both go through the same
  wrapper vtable, so expected identical — verify with a breakpoint).
- Confirm `outBuf` encoding is single-byte (ANSI/UTF-8) not UTF-16 for VST names with unicode.
