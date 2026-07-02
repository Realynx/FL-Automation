# 04 — FLEngine_x64.dll Survey (Phase 1)

First-pass inventory of the engine after importing it into the Ghidra `FlStudio`
project. (Image base in Ghidra = `0x00400000`; at runtime it loads elsewhere via
ASLR — work in RVAs.)

## Size & shape
- **53,600+ functions**, ~54,800 symbols, ~54.8 MB mapped. This is the whole DAW.
- `executable_format`: PE32+, `x86:LE:64`.

## Toolchain: **Delphi / Embarcadero RAD Studio** (Object Pascal)
Strong, multiple signals:
- Exports `__dbk_fcall_wrapper` + `dbkFCallWrapperAddr` — Delphi **debug kernel** hooks.
- A `.didata` section — Delphi **delayed import** data.
- Imports dominated by **VCL + OLE Automation**: `SafeArray*`, `Variant*`,
  `SysAllocStringLen`, `GetActiveObject`, plus a huge Win32 GUI surface
  (menus/windows/carets/clipboard/GDI) — the entire FL GUI lives in this DLL.

**Why this matters for RE / readability:**
- Delphi binaries carry rich **RTTI**: class VMTs embed the class name (ShortString)
  and published-method tables (name + address). Recovering RTTI renames large swaths
  of the 53k functions to real `TWhatever.Method` names — the fastest route to a
  human-readable engine and a direct index to control functions
  (channel rack, pattern/score, playlist, mixer, plugin wrapper, etc.).
- Calling convention is often Delphi `register` (params in RAX/RDX/R8/R9-ish on x64
  it's MS x64 ABI, but `Self` is passed as the first arg). Watch the `this`/`Self`.
- Strings are Pascal `UnicodeString`/`AnsiString` (length+refcount header, char
  payload is null-terminated for C-compat) and RTTI `ShortString`s.

## Exports (the host handoff)
| Export | RVA | Role |
|---|---|---|
| `CreateFruityInstance` | `0xE69F00` (Ghidra `0x1269F00`), ordinal 3 | **Engine entry** that FL64.exe calls after LoadLibrary. |
| `entry` | `0xE6ADB0` | DLL entry/init. |
| `__dbk_fcall_wrapper`, `dbkFCallWrapperAddr` | — | Delphi debug-kernel exports (not relevant to control). |

### `CreateFruityInstance(param_1, param_2, param_3)` — decompiled
- `param_3` = pointer to a **host context struct** (passed in by FL64.exe). The engine:
  - stashes `param_3` into global **`0x14A7E40`** (the engine's host-context pointer),
  - installs a callback `FUN_010ACD10` into global `0x14A77C0`,
  - reads `param_3+0x8` and `param_3+0xC` (likely host handles / dimensions),
  - calls engine init (`FUN_012697E0`), then `FUN_0116B230(1)` and builds an object
    via `FUN_0043DC00(&out, &UNK_0126AC3C, …)`.
- Implication for injection: the **host-context global at `0x14A7E40`** is a high-value
  root pointer — from it we can likely reach the global app/project objects.

## Sections — VMProtect footprint is *partial*
```
.text   0x00401000–0x012967ff   ~18.4 MB  normal native code  <-- the bulk, analyzable
.data / .bss / .idata / .didata / .edata / .rdata / .reloc / .pdata / .rsrc
.avm0   0x01b5c000–0x022669ff   ~7.1 MB \
.avm1   0x02267000–0x02a883ff   ~8.5 MB  | VMProtect virtualized sections
.avm2   0x02a89000–0x030d4dff   ~6.5 MB  | (~28 MB of .avm* region, but much is
.avm3   0x030d5000–0x0384adff   ~7.8 MB /  data/padding; only selected funcs are VM'd)
```
Takeaway: VMProtect **virtualizes a selected subset** of functions (licensing,
anti-tamper, a few hot/critical routines). The **majority of control logic in `.text`
is plain native code** we can read and call. We route *around* any virtualized
function rather than trying to devirtualize it.

## Python is peripheral
`FLEngine_x64.dll` does **not** statically import `python312.dll`; it has
`LoadLibraryA`/`GetProcAddress`, so Python is loaded **on demand** for scripting.
This confirms the pivot: the scripting subsystem is optional and limited — the
**native engine + object model is the real, complete control surface.** We keep the
Python `PyMethodDef` tables only as a *labelled hint* to locate some native functions.

## Blocker found: strings/RTTI not yet defined
`read_memory` confirms human-readable text is physically present, but Ghidra's
**Strings/Data analyzers had not run** after the import (guaranteed terms like
"Image-Line"/"FL Studio" returned zero defined strings). MCP script execution is
**gated off** (`run_script_inline`/`run_ghidra_script` disabled; `list_scripts` needs
the GUI), so a one-shot Delphi-RTTI script can't be run over MCP right now.

**Action taken:** triggered a full `reanalyze` (running in the background; function
count already climbing) to populate strings + data + RTTI. Once it settles, the
scripting method names, RTTI class/method names, and dispatch tables become
searchable — that's when Phase 2 (scripting→native map) and Phase 3 (object model)
really open up.

**If MCP-side analysis doesn't recover Delphi RTTI well**, the fallback is for the
user to run a Delphi RTTI recovery script in the Ghidra GUI (e.g. *Dhrake* or
*IDR*-derived scripts) once, which names classes/methods wholesale; we then continue
over MCP. (Noted as an open item in `03-roadmap.md`.)

## Confirmed: scripting→native map works (Rosetta Stone)
After strings were defined, the Python **PyMethodDef** tables are intact and usable
*without* a script: a method-name string's **data xref** lands on the
`{name, func, flags, doc}` struct (32 bytes on x64); the native impl =
**qword at `name_xref + 8`**. Verified + named in Ghidra:

| Python method | PyMethodDef @ | native impl (RVA) | Ghidra name |
|---|---|---|---|
| `channels.setGridBit` | `0x14062B0` | `0xE02D30` | `FL_py_channels_setGridBit` |
| `plugins.setParamValue` | `0x140DDC8` | `0xE270A0` | `FL_py_plugins_setParamValue` |
| `plugins.getParamCount` | `0x140DD48` | `0xE26200` | `FL_py_plugins_getParamCount` |

**Engine param/automation model** (recovered from `setParamValue`):
- **Mixer tracks:** global array `@0x14A7EB0`, **stride `0x1474` bytes/track**; 10
  effect-slot plugin pointers at `track+0x1324`; track count via `*(int*)0x14A9850`
  (`g_pMixerTrackCount`).
- **Param IDs:** channel plugin = `channelBase + idx + 0x8000`; mixer effect =
  `FL_MixerEffectParamBase(track,slot) + idx + 0x70008000` (`0x70000000` = mixer flag).
- **Setter:** `FL_SetParamEventValue` (`0xE2C960`); float→fixed-point via `×2^30`.
- Python C-API resolved dynamically (`PyArg_ParseTupleAndKeywords` ptr `@0x14ABAC8`).

These native functions and the mixer-track array are **directly callable / readable
from the injected DLL** — i.e., even though the *Python API surface* is thin, the
engine functions beneath it are the real, reusable control points.

## Blocker for bulk work: Ghidra script engine (OSGi)
`run_script_inline` fails with `GhidraPlaceholderBundle cannot be cast to
GhidraSourceBundle` — the `C:\Users\poofi\ghidra_scripts` dir is a *placeholder*, not
an enabled *source* bundle. Fix in GUI: **Window ▸ Bundle Manager ▸ enable (or remove
+ re-add) `~/ghidra_scripts`**; if that fails, delete the OSGi/felix cache under
`~/.ghidra/.ghidra_<ver>/` and restart. Until then, bulk RTTI recovery + full
module-table enumeration are blocked (single-function xref/decompile/rename still work).
