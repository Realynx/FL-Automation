# Preset control — Part A: set preset by index / next / prev

Program: FLEngine_x64.dll, image base 0x400000. All addresses are Ghidra addresses.
Goal: let the injected bridge SET a hosted plugin's preset by index (+ next/prev) for native FL
plugins and hosted VSTs (Serum 2). Verified statically end-to-end; live-verify plan at bottom.

## TL;DR

- The preset ops operate on the **TChannel generator / mixer-FX-slot object** (the SAME object as
  param control), NOT the plugin instance. Current preset index lives at **`*(int*)(obj+0xf0)`**.
- The absolute "load preset" callable is the object's **vtable+0xf0** method =
  **`FUN_011bb770(obj, int index, wchar* fstPath, wchar* displayName, char applyFlag, char noUndo)`**
  (generators call it directly; mixer slots go through thunk `FUN_011c3820` -> `FUN_011bb770`).
- getPresetCount = **builtin/wrapper preset count + on-disk `.fst` file count** = the FL BANK count,
  NOT the VST's internal numPrograms/preset browser.
- MUST run on FL's **main/GUI thread** (next/prev explicitly marshal to it). The bridge's
  main-thread callabs is required.
- Native vs VST3 use the **same** vtable+0xf0 path; the per-plugin difference is handled inside via
  the generic host message interface (`inst-vtable+0x8`, opcode 9).

## Object model (obj = resolved TChannel generator OR mixer FX slot)

Resolve the object (same as param control):
- Channel generator: `FLcr_ResolveChannelByIndex(chanIdx, useGlobal, &obj)` @0xF22AC0 (slotIndex==-1).
- Mixer FX slot: `obj = *(void**)(g_MixerTrackArrayPtr + track*0x1474 + slot*8 + 0x1324)`
  (g_MixerTrackArrayPtr = 0x14A7EB0).
- has-plugin gate: `FLcr_ChannelHasPlugin(obj)` = `*(int*)(obj+0x64) >= 0`.

Object fields used by preset code:
- `obj+0`     = VMT pointer (Delphi class). Generator VMTs: **0xEFD1A8 / 0xEFD4F8 / 0xEFD7C8**
                (three generator subclasses). Mixer-FX-slot VMT: **0x11B4960**.
- `obj+0x38`  = plugin container; host = `*(obj+0x38)+0x48`; instance = `(*(*host+0x20))(host)`.
- `obj+0x64`  = plugin kind (>=0 has plugin; **==2** = hosted plugin/wrapper — native + VST both).
- `obj+0x68`  = param count.
- `obj+0x94`  = **preset-provider object** (builtin preset name list). `provider vtable+0x28` = count;
                `provider vtable+0x18` = get-name(idx).
- `obj+0xf0`  = **current preset index (int)**. Setter `FUN_011bf590(obj, idx)` writes `*(int*)(obj+0xf0)`.
- `obj+0xb8`  = editor/WP window struct (UI refresh side-effects).

VMT slot layout (confirmed by reading VMT @0xEFD1A8):
- +0x28 = 0x11C1740 (open/replace plugin from descriptor — NOT preset)
- +0x30 = 0x11BEA20 (clear plugin)
- +0xC0 = **0x122A000** (apply-preset-by-index sub-method)
- +0xF0 = **0x11BB770** (preset/file loader — the one we want)

## 1) getPresetCount  (@0xE27920 -> FUN_011BAAD0)

`FLpy_plugins_getPresetCount` resolves obj (channel or mixer slot) exactly like param control,
gates on `FLcr_ChannelHasPlugin`, then returns `FUN_011baad0(obj)`.

```c
int FUN_011baad0(void* obj) {                          // 0x11BAAD0
    void* fstList = new_ChildNodeList();               // FUN_00510130(&PTR_...004cd480, 1)
    FUN_011bb1b0(obj, fstList);                         // enumerate <plugin>*.fst preset files
    int builtin = (*(int(**)(void*))(*(void**)(obj+0x94) + 0x28))(*(void**)(obj+0x94)); // provider count
    int fstCount = (*(int(**)(void*))(*(void**)fstList + 0x28))(fstList);                // .fst file count
    list_release(fstList);                              // (*(fstList + -0x20))(fstList, 1)
    return builtin + fstCount;                          // FL BANK count
}
```

Semantics: total = (wrapper/builtin preset names exposed at obj+0x94) + (number of `.fst` files FL
found for this plugin). This is the **FL preset bank**, NOT the plugin's internal library.
For a native FL plugin (Sytrus, FL Keys) `builtin` is the plugin's factory bank (often large).
For a VST3 like Serum 2 the wrapper typically exposes 0 builtin programs (VST3 uses preset units,
not the old numPrograms), so the count degrades to just the `.fst` files present on disk — a small
number that does NOT reflect Serum's own preset browser. (See preset-B-names / preset-C-files.)

## 2) The loader = obj vtable+0xf0 = FUN_011BB770

Full prototype (recovered):
```c
// return: undefined (void in practice — callers ignore it)
void FUN_011bb770(void*  obj,          // this (TChannel generator OR mixer FX slot)
                  int    index,        // builtin preset index; or <0 to load from file (FL uses -2)
                  wchar* fstPath,      // Delphi UnicodeString .fst path; used only when index<0 || index>=builtin
                  wchar* displayName,  // Delphi UnicodeString; ONLY used to build the status-hint text (safe to pass NULL)
                  char   applyFlag,    // 1 = actually select/apply the builtin preset
                  char   noUndo);      // passed to FLproj_ReadFlpFile for the .fst branch (0 in FL's own calls)
```

Behavior:
- `local_114 = index`. If `0 <= index < builtinCount` (builtinCount via provider vtable+0x28):
  - if `applyFlag != 0`: `FUN_011bb2f0(obj)`; **`(*(*obj+0xc0))(obj, index)`**; `FUN_011bb3b0(obj)`.
    - `(*(*obj+0xc0))` = `FUN_0122a000(obj, index)` -> if `obj+0x64==2`: `FUN_0122acf0(obj, 9, index, 0)`.
    - `FUN_0122acf0` = generic host dispatch: `host=FLplug_GetChannelPluginHost(obj); inst=(*(*host+0x20))(host);`
      `if(inst) (*(*inst+8))(inst, 9, index, 0);` — **opcode 9 = set preset/program**, implemented by
      both native plugins and the VST/VST3 wrapper -> this is what makes native vs VST uniform.
  - fetches the preset name via `(*(*(obj+0x94)+0x18))(provider, &name, index)`, sets status hint
    "...^^(Internal)".
- Else (`index < 0` or `index >= builtinCount`): treats `fstPath` as a `.fst` file and loads it via
  `FLproj_ReadFlpFile(fstPath, 0x30, obj, noUndo==0)`. FL passes `index = 0xFFFFFFFE (-2)` here.
  (`index == -3` has a special "paste preset" sub-branch.)
- At the very end: `if (index >= 0) FUN_011bf590(obj, index);` — stores current index at obj+0xf0.
  So for a builtin preset you do NOT need a separate `FUN_011bf590` call; the loader stores it.

Synchronous? It touches the plugin host, project reader (FLproj_ReadFlpFile), and UI (status hint,
WP panels via obj+0xb8) -> it MUST run on FL's main/GUI thread. See thread notes.

Mixer thunk (`FUN_011c3820`, mixer VMT+0xf0):
```c
void FUN_011c3820(obj, index, fstPath, displayName, applyFlag, noUndo) {
    FUN_011bb770(obj, index, fstPath, displayName, applyFlag, noUndo); // (tail; decompiler collapsed args)
    FUN_0117db90(*(int*)(obj+0x160), *(int*)(obj+0x164), fstPath, displayName, applyFlag, noUndo); // mixer strip UI refresh
}
```
Because the bridge calls the loader **indirectly** (`(*(*obj+0xf0))(...)`), the mixer thunk is used
automatically for mixer slots and the direct FUN_011bb770 for generators — one code path covers both.

## 3) next / prev  (@0xE27560 / @0xE27740)

Both resolve obj, gate on has-plugin, then build a deferred command and post it to the main thread:
```c
s = FUN_0040f9e0(&DAT_00e25c48, 1);
*(int*)(s+8)  = chanIdx;
*(int*)(s+0xc)= slotIdx;      // -1 for a channel generator, mixer slot idx otherwise
*(int*)(s+0x10)= dir;         // nextPreset: +1 ;  prevPreset: -1 (0xffffffff)
*(int*)(s+0x14)= useGlobal;
FUN_0088a570(mainThreadQueue, FUN_00e27400, s);   // mainThreadQueue = *(*PTR_DAT_014abca8 + 0x64)
```
Main-thread worker `FUN_00e27400` re-resolves obj and calls the nav core:
```c
FUN_011badb0(obj, dir, 0);
```

Nav core `FUN_011BADB0(obj, int dir, char noUndo)`:
```c
int cur = *(int*)(obj+0xf0);
if (cur < 0) FUN_011bf590(obj, (dir>0) ? 0 : -1);      // init
int total = builtinCount + fstCount;                    // same two sources as getPresetCount
if (total > 0) {
    int newIdx = ((*(int*)(obj+0xf0)) + dir + total) % total;
    FUN_011bf590(obj, newIdx);                          // store index FIRST
    if (newIdx < builtinCount)
        (*(*obj+0xf0))(obj, newIdx, 0, descStr, 1, noUndo);            // builtin
    else
        (*(*obj+0xf0))(obj, 0xFFFFFFFE, fstPathStr, descStr, 1, noUndo); // .fst by path
}
```
(`descStr` is a cosmetic "N/total" hint string; `fstPathStr` is the resolved `.fst` full path.)

## Bridge-ready pseudo-C

```c
// obj = channel generator or mixer FX slot, resolved as in param control. MAIN THREAD ONLY.
typedef void (*loader_t)(void* obj, int index, const wchar_t* fstPath,
                         const wchar_t* displayName, char applyFlag, char noUndo);

int  fl_getPresetCount(void* obj) {                 // FUN_011baad0 @0x11BAAD0
    return ((int(*)(void*))0x11BAAD0)(obj);         // = builtin bank + .fst files (FL bank count)
}

void fl_setPresetIndex(void* obj, int N) {          // absolute set, builtin range
    void** vtbl = *(void***)obj;
    loader_t load = (loader_t)vtbl[0xf0/8];         // = FUN_011bb770 (gen) / FUN_011c3820 (mixer)
    load(obj, N, 0, 0, /*applyFlag*/1, /*noUndo*/0);// applies preset N and stores obj+0xf0 = N
}
// (optional) load an FL .fst preset file by path:
void fl_setPresetFile(void* obj, const wchar_t* fstPath) {  // Delphi UnicodeString
    loader_t load = (loader_t)(*(void***)obj)[0xf0/8];
    load(obj, -2, fstPath, 0, 1, 0);
}

void fl_nextPreset(void* obj) { ((void(*)(void*,int,char))0x11BADB0)(obj, +1, 0); } // FUN_011badb0
void fl_prevPreset(void* obj) { ((void(*)(void*,int,char))0x11BADB0)(obj, -1, 0); }
```
Notes:
- `displayName`/`fstPath` are Delphi UnicodeString (wchar buffer w/ -4 length, -8 refcount). Passing
  NULL is safe for the builtin-index path (only used for the hint text / file branch).
- For next/prev the bridge can call `FUN_011badb0` directly (it is the exact core FL's deferred
  worker runs); no need to replicate the whole command-post machinery. Just ensure main thread.
- `fl_setPresetIndex` does not need a separate `FUN_011bf590` — the loader stores obj+0xf0 itself.

## Thread affinity

FUN_011bb770 / FUN_011badb0 touch the plugin host, FLproj_ReadFlpFile, and GUI (status hint bar +
WP panels via obj+0xb8). next/prev deliberately marshal to FL's main thread (FUN_0088a570 into
`*(*PTR_DAT_014abca8 + 0x64)`). The bridge MUST invoke all three on FL's main/GUI thread via the
existing SEH-guarded main-thread callabs. Do not call from a worker thread.

## 4) Native vs VST3

Same path for all. The generator subclasses (VMTs 0xEFD1A8/0xEFD4F8/0xEFD7C8) all point vtable+0xf0
at the identical FUN_011bb770; the mixer slot (0x11B4960) uses the FUN_011c3820 thunk to the same
function. The per-plugin-type difference happens INSIDE, at the generic host message interface:
`FUN_0122acf0` -> `inst-vtable+0x8 (inst, 9, index, 0)`. Native FL plugins (TFruityPlugin) and the
VST/VST3 wrapper both implement that dispatch (opcode 9 = select preset/program); the VST wrapper
translates it to the plugin's program change. So there is NO type-specific branch to write in the
bridge — call vtable+0xf0 and let FL dispatch.

## Limitation (important for Serum 2 / VST3)

getPresetCount and next/prev navigate FL's own preset **bank** = wrapper builtin presets + on-disk
`.fst` files, addressed by `obj+0x94` provider + the `.fst` scan. They do NOT enumerate a VST3's
internal preset browser. For Serum 2 expect the count to be small (roughly the number of `.fst`
files saved under FL's Serum preset folder), often 0 if none saved. Native FL plugins expose their
full factory bank here and will show large counts. If LLM-driven sound design needs Serum's own
library, that requires the VST3 preset-unit / program-list interface (a different path, not covered
here) or pre-exporting `.fst` files.

## Live-verify plan

1. Native, large bank (fast confidence): put **Sytrus** or **FL Keys** on channel 0.
   - `FLcr_ResolveChannelByIndex(0,0,&obj)`; `n = fl_getPresetCount(obj)` -> expect >0 (native bank).
   - Read `*(int*)(obj+0xf0)` (cur). Call `fl_setPresetIndex(obj, 5)`.
   - Expect: `*(int*)(obj+0xf0) == 5`; FL hint bar shows "6/n ...^^(Internal)"; the plugin's preset
     name changes; a known param (reuse the verified param-read path) changes to preset 5's value.
   - `fl_nextPreset(obj)` -> `obj+0xf0` becomes 6; `fl_prevPreset(obj)` -> back to 5.
2. VST3 (Serum 2): load Serum 2 on a channel.
   - `fl_getPresetCount(obj)` -> likely small (# of `.fst` files) or 0. This CONFIRMS the FL-bank
     limitation. If you first save a couple `.fst` presets in FL for Serum, the count rises by that
     many and `fl_setPresetIndex` within range loads them (Serum's patch name + params update).
   - Watch `obj+0xf0` and a Serum macro/param via the existing param-read path after each set.
3. Mixer slot: same obj resolved from g_MixerTrackArrayPtr; verify vtable+0xf0 auto-routes through
   the thunk and the mixer strip name refreshes.

Flag / uncertainties:
- Whether obj+0x94 provider exposes a VST's numPrograms for some VST2s (would make builtin count > 0
  for those) — needs the Serum live check. VST3 expected to be 0.
- `noUndo`/`applyFlag` exact undo semantics not fully traced; FL uses applyFlag=1, noUndo=0 — mirror that.
- Return value of FUN_011bb770 is ignored by FL; treat as void.
