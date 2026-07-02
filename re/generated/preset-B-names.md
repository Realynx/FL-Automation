# Preset name enumeration — RE findings (FLEngine_x64.dll, base 0x400000)

Goal: let the injected bridge enumerate a hosted plugin's PRESET NAMES so an LLM can pick a
preset by name ("Big Supersaw Lead"), for native FL plugins and hosted VSTs (Serum 2).

Reuses the live-verified object model from `re/10-plugin-param-control.md`:
- channel obj: `FLcr_ResolveChannelByIndex` @0xF22AC0 (slot==-1); mixer obj:
  `*(g_MixerTrackArrayPtr + track*0x1474 + 0x1324 + slot*8)` (g_MixerTrackArrayPtr = 0x14A7EB0).
- host: `FLplug_GetChannelPluginHost(obj,&host)` @0x122B320 (host = *(chan+0x38)+0x48).
- instance: `inst = (*(*host+0x20))(host)` (host vtbl+0x20 = GetInstance; 0 ⇒ no plugin).
- `inst` is a `TBaseAudioPlugin`-derived object: `TGenericPlugin` (native), `TVSTPlugin`, `TVST3Plugin`.

--------------------------------------------------------------------------------
## 1. The instance name method is FL-SDK `TFruityPlug.GetName`

`inst` vtbl **+0x20** = slot 4 = `FUN_0084cba0` (`TBaseAudioPlugin.vtbl_4`), SHARED by
TGenericPlugin / TVSTPlugin / TVST3Plugin. Body is a pure tail-forward:

```c
void TBaseAudioPlugin_vtbl_4(this){                     // GetName(Section,Index,Value,Name)
    (**(code**)**(void***)(this+0x78))(*(void***)(this+0x78)); // → flavorDriver->vtbl[0](args passthrough)
}
```

So GetName just forwards `(Section, Index, Value, Name)` to the "flavor driver" object at
`inst+0x78`, slot 0 — the concrete FL SDK `TFruityPlug` impl (native code for TGenericPlugin;
FL's VST/VST3 adapter, which queries the plugin's DLL, for TVSTPlugin/TVST3Plugin).

C prototype (this is the method the bridge already calls for params):
```c
void GetName(void* inst, int Section, int Index, int Value, char* Name /*out, ANSI, NUL-term*/);
// = (*(void(**)(void*,int,int,int,char*)) ((*(uintptr_t*)inst) + 0x20))(inst, Section, Index, Value, buf)
```
`Section` == the FL SDK FPN_* constant (== `local_34` "kind" in the Python layer, see §4).
`Name` is written as a **NUL-terminated ANSI C-string** into the caller's buffer (FL uses 0x1000).
Live-verified for Section 0/1 in `10-plugin-param-control.md`; strings for a VST come from the
plugin's own DLL.

--------------------------------------------------------------------------------
## 2. `plugins.getName` @0xE27DB0 — how kind=6 (preset) is served

Arg parse: format `"i|iiii"` → `index`(req), `slotIdx`(-1), `kind`(0), `subIdx`(-1), `useGroup`(0).
- `slotIdx==-1` ⇒ channel-rack channel; else mixer slot (`track=index, slot=slotIdx`).
- `kind == 6` (FPN_Preset) is SPECIAL-CASED → `FUN_011bab30(obj,&out,subIdx)` (the preset worker).
- any other kind → generic path: `GetName(inst, kind, subIdx, 0, buf)` then wrap buf as a string.

```c
if (local_34 == 6) FUN_011bab30(obj, &outStr, subIdx);   // preset worker (bank-aware)
else { if (subIdx<0) subIdx=0;
       (*(*inst+0x20))(inst, kind, subIdx, 0, buf);        // direct GetName -> ANSI buf
       ... }                                               // wrap buf -> Python str
```

### Preset-name worker `FUN_011bab30(channelObj, UnicodeString* out, int index)`
```c
FUN_00412d60(out);                                   // init Delphi UnicodeString (empty)
if (index==-1 && *(int*)(obj+0xf0) >= 0) {           // -1 => current preset; obj+0xf0 = current idx
    local_104c = *(int*)(obj+0xf0);                  // -> falls to BANK read below (out still empty)
} else {
    host=GetChannelPluginHost(obj); inst=(*(*host+0x20))(host);
    local_104c = index;
    if (inst) {
        buf[0]=0;
        (*(*inst+0x20))(inst, 6 /*FPN_Preset*/, index, 0, buf);  // ask plugin for preset name
        FUN_00413a60(&tmp, buf, 0x1000, 0);          // strnlen+copy ANSI buf -> string
        FUN_0041c650(out, tmp);                       // out = that string
    }
}
if (*out==0 && local_104c>=0) {                      // instance gave nothing => FL PRESET BANK
    int nBuiltin = (*(*(obj+0x94)+0x28))(*(obj+0x94));         // builtin/factory count
    if (local_104c < nBuiltin)
        (*(*(obj+0x94)+0x18))(*(obj+0x94), &s, local_104c);    // bank->getName(idx)
    else {                                                     // .fst file list
        list = FUN_00510130(&PTR_...RejectIncompatible,1);
        FUN_011bb1b0(obj, list);                               // enumerate L".FST" files (see below)
        local_104c -= nBuiltin;
        ... bounds-check ... (*(*list+0x18))(list,&s,local_104c);  // fstList[idx].name
        (*(*list-0x20))(list,1);                               // free list
    }
    FUN_0043c710(out, s);                                       // Delphi_UStrAsg -> out
}
```

Key point: for `kind=6`, the plugin instance is asked FIRST (`GetName(FPN_Preset,i)`); **only if it
returns an empty string** does FL fall back to its own preset bank = **builtin/factory presets +
`.fst` files** in the plugin's FL preset folder. `FUN_011bb1b0` enumerates `L".FST"` files
(confirmed: uses `L".FST"` literal + FUN_0073a030 dir walk). So `kind=6` = "the plugin's own preset
if it has one, otherwise the FL-wrapper preset bank".

### `out` string type (worker path)
`out` is a **Delphi UnicodeString** (UTF-16). `FUN_0043c710` → `Delphi_UStrAsg`; `FUN_00412d60`
inits it. Read recipe for `WideChar* p = *out`: `p==NULL ⇒ empty`; else UTF-16, NUL-terminated,
length(chars) = `*((int*)p - 1)`. Convert to UTF-8 for the LLM.

--------------------------------------------------------------------------------
## 3. `plugins.getPresetCount` @0xE27920 → `FUN_011baad0(obj)` — BANK-ONLY

```c
int FUN_011baad0(obj){
    list = FUN_00510130(&PTR_...RejectIncompatible,1);
    FUN_011bb1b0(obj, list);                        // .fst file list
    int nBuiltin = (*(*(obj+0x94)+0x28))(*(obj+0x94));   // builtin/factory count
    int nFst     = (*(*list+0x28))(list);                // .fst file count
    (*(*list-0x20))(list,1);                             // free
    return nBuiltin + nFst;                              // TOTAL = FL preset bank
}
```
**getPresetCount NEVER queries the plugin instance / VST programs.** It counts only FL's preset
bank (builtin + `.fst`). For a typical VST, builtin=0, so count == number of `.fst` presets the
user saved through FL's wrapper. This means the count is NOT a reliable loop bound for a VST's own
program list.

`plugins.nextPreset`/`prevPreset` (@0xE27560/@0xE27740) just marshal a command (obj+0xf0 current
index +/- 1) onto FL's main thread — they confirm `obj+0xf0` = current preset index.

--------------------------------------------------------------------------------
## 4. `kind` (FPN_*) constant table — GetName Section

`kind` (Python `local_34`) is passed straight through as the FL-SDK `GetName` Section. Values are the
FL SDK `fp_plugclass.h` FPN_* constants. **LIVE-verified in this codebase: 0 and 1.** The rest are
from the FL SDK; confirm live (see plan).

| Section | FPN_ name        | GetName returns                                   | verified |
|--------:|------------------|---------------------------------------------------|----------|
| 0 | FPN_Param          | parameter name (Index=param#)                      | ✅ live |
| 1 | FPN_ParamValue     | param value as display text (Index=param#, Value=raw) | ✅ live |
| 2 | FPN_Semitone       | note/semitone name (Index=note#)                   | SDK |
| 3 | FPN_Patch          | **patch/PROGRAM name** (Index=patch#) — VST program list route | SDK — probe live |
| 4 | FPN_VoiceLevel     | per-voice level (articulator) name                 | SDK |
| 5 | FPN_VoiceLevelHint | per-voice level hint                               | SDK |
| 6 | FPN_Preset         | **preset name** (Index=preset#) — routed to FL preset system (§2) | SDK — this task |
| 7 | FPN_OutCtrl        | output controller name                             | SDK |
| 8 | FPN_VoiceColor     | per-voice color/articulation name                  | SDK |
| 9 | FPN_OutVoice       | output voice name                                  | SDK |

So `kind=6` is the correct "preset name" Section, and the Python layer routes it through FL's
bank-aware worker. Note **FPN_Patch=3** is the sibling that (per SDK) carries a plugin's
patch/PROGRAM names and is the more likely route to a VST's host-visible program list — reachable
via the generic path `GetName(inst,3,i,0,buf)` (NOT bank-wrapped).

--------------------------------------------------------------------------------
## 5. FL preset BANK vs plugin-internal — and the Serum answer

Two distinct name sources:
1. **Plugin-internal** = `GetName(inst, Section, i, 0, buf)` (flavor driver → plugin DLL). Section 6
   (FPN_Preset) and 3 (FPN_Patch) are the preset/program-ish ones.
2. **FL preset bank** = builtin/factory presets (`obj+0x94`) + user `.fst` files in the plugin's FL
   preset folder (`FUN_011bb1b0`, `L".FST"`).

`plugins.getName kind=6` = internal FPN_Preset if non-empty, ELSE the FL bank.
`plugins.getPresetCount` = FL bank only.

**Will the LLM see Serum's REAL preset library via kind=6? Almost certainly NO (in the general
case).** VST wrappers normally return empty for FPN_Preset (VSTs have no FL "preset" concept), so
kind=6 falls through to FL's bank = only the presets the user saved via FL's wrapper (`.fst`) +
factory `.fst` FL ships (usually none for a 3rd-party VST). Serum's real library ("Big Supersaw
Lead", thousands of factory patches) is file-based inside Serum's own browser and is NOT in FL's
`.fst` bank.

Serum's factory list is reachable from the host ONLY if Serum exposes it as a VST program list
(VST2 `getProgramNameIndexed` / VST3 `IUnitInfo`/program-list). If so, it surfaces via **FPN_Patch=3**
(and possibly FPN_Preset=6). Historically Serum exposes a limited/near-empty program list to hosts,
so even FPN_Patch likely won't yield the full library. **Must be verified live per plugin.** If the
plugin exposes nothing useful, the only route to Serum's full library is reading Serum's own preset
files from disk (out of scope for this engine RE).

--------------------------------------------------------------------------------
## 6. One-shot list builder?

No single call returns ALL names. `FUN_011bb1b0` builds the `.fst` file list object in one shot but
that's ONLY the `.fst` files (not builtins, not the plugin's own programs). Enumeration is
per-index: either `getName(i)` in a loop, or (for internal names) `GetName(inst,section,i,..)` in a
loop until an empty string is returned.

--------------------------------------------------------------------------------
## 7. Bridge-ready recipes

### A) Plugin-internal / VST-program names (direct, matches the verified param path)
```c
// section: try 3 (FPN_Patch, VST programs) and 6 (FPN_Preset). buf read == getParamName.
int listInternalNames(void* inst, int section, char out[][256], int maxN){
    typedef void (*GetName_t)(void*,int,int,int,char*);
    GetName_t GetName = *(GetName_t*)((*(uintptr_t*)inst) + 0x20);
    char buf[4096]; int n=0;
    for (int i=0;i<maxN;i++){ buf[0]=0;
        GetName(inst, section, i, 0, buf);   // SEH-guarded callabs, FL main thread
        if (!buf[0]) break;                  // empty => end of list
        strncpy(out[n++], buf, 255); }
    return n;
}
```
- String: NUL-term ANSI C-string in caller buffer (max 0x1000). No Delphi string handling.
- Loop bound: stop on first empty (independent of getPresetCount, which is bank-only).

### B) Exactly what FL's wrapper preset dropdown shows (bank-aware, incl. `.fst`)
```c
// count = FL bank; name[i] = plugin FPN_Preset or bank (builtin + .fst)
int   FUN_011baad0(void* channelObj);                       // @0x11BAAD0  -> preset count (bank)
void  FUN_011bab30(void* channelObj, void** outUStr, int i);// @0x11BAB30  -> name[i] (i=-1 => current)
// outUStr is a Delphi UnicodeString: WideChar* p=*outUStr; p?  UTF-16, len=*((int*)p-1); else empty.
```
Call these on FL's main thread; they take the CHANNEL obj (not inst) and resolve host/inst internally.

Recommendation: run BOTH. Use B (kind=6 worker) to match FL's UI presets; use A with section=3 to
probe the VST's own program list. Pick whichever surfaces Serum's real names (verify live).

--------------------------------------------------------------------------------
## 8. Live-verify plan (bridge already reads params live, so this is cheap)
1. Load Serum 2 in the channel rack. Resolve `obj` + `inst` (already working).
2. `A) listInternalNames(inst, 6, ...)` — record names + where they stop. Likely empty / few.
3. `A) listInternalNames(inst, 3, ...)` — FPN_Patch; this is the real VST-program probe. Compare to
   Serum's own preset browser names.
4. `B) FUN_011baad0(obj)` count, then `FUN_011bab30(obj,&u,i)` for i=0..count-1 — record (these are
   FL `.fst`/builtin bank names).
5. Repeat with a native FL plugin (e.g. Sytrus/3xOsc via TGenericPlugin) to confirm kind=6 returns
   its factory preset names there.
6. Decide: if step 3 yields Serum's library → use section 3 loop; else expose FL bank (B) + document
   that Serum's full library needs on-disk preset-file reading.

## 9. Uncertainties / flags
- FPN_ constants 2–9 are FL-SDK values, not proven in this binary (0,1 proven). 3 vs 6 for VST
  programs is the key unknown — resolve in step 2/3.
- Whether Serum's VST3 exposes any program list to the host is plugin behavior, not decidable from
  FLEngine; historically limited. Expect the LLM will NOT get Serum's full factory library from this
  API without reading Serum's preset files.
- `obj+0x94` assumed = builtin/factory preset bank object (vtbl+0x28 count, +0x18 getName(out,idx));
  not independently opened here but consistent across getName/getPresetCount.
- The direct A-path with section=6 BYPASSES the `.fst` bank fallback (that fallback only exists in
  the FUN_011bab30 worker). To include `.fst`, use path B.
