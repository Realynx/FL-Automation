# 01 — FL Studio Module Architecture

**Target:** FL Studio 2025, file version **25.2.5.5319**, x86-64.

## TL;DR

`FL64.exe` is a **thin launcher shell**. It contains almost none of FL Studio's
actual functionality — it resolves a path from the registry, Authenticode-verifies
the engine, loads `FLEngine_x64.dll`, and hands off. **All meaningful control logic
lives in `FLEngine_x64.dll`** — that is the real reverse-engineering target.

| Module | Size | Role |
|--------|------|------|
| **FL64.exe** | 1.05 MB | Launcher/bootstrapper. CRT + loader + signature checks. Only export is `entry`. |
| **FLEngine_x64.dll** | **48.8 MB** | **The FL Studio engine — the entire DAW.** Channels, patterns, playlist, mixer, piano roll, plugin host, project model, audio engine. *Not yet imported into Ghidra.* |
| **FLMManaged.dll** | 8.9 MB | .NET (managed) component. FL64.exe hosts the CLR (`mscoree.dll`) to run it. |
| **ILTools_x64.dll** | — | Image-Line shared tooling, loaded by the shell. |
| **System\Plugin\ReWire\FLReWire_x64.dll** | — | ReWire host/device support. |
| **Shared\Python\python312.dll** | — | **CPython 3.12** — the runtime for FL's controller scripts and piano-roll scripts. Exports the standard `Py_*` C-API by name. Strategically important (see `02-control-strategy.md`). |
| `Plugins\Fruity\**` | many | Native instrument/effect plugins (Sytrus, Harmor, FLEX, Patcher, Edison…). Out of scope for host control. |

## Startup / loader chain (FL64.exe)

Reconstructed from the disassembly. Ghidra symbols below are the human-readable
names we applied (see `03-roadmap.md` for the full list).

```
entry  (0x140030e44)                         PE entry point
  └─ MSVC CRT bootstrap (__security_init_cookie, __scrt_common_main_seh)
       ├─ C++ global constructors run, building DLL-path strings:
       │     cppinit_FLEngineDllPath (0x140001030)  -> g_pFLEngineDllName = L"FLEngine_x64.dll"
       │     cppinit_FLReWireDllPath (0x140001000)  -> g_pFLReWireDllPath = L"System\Plugin\ReWire\FLReWire_x64.dll"
       │     cppinit_ILToolsDllPath  (0x140001060)  -> L"ILTools_x64.dll"
       │
       └─ main:
            FL_ResolveEngineDllPath_FromRegistry (0x140005000)   [called via fn-ptr table @ 0x1400864b0]
              ├─ RegOpenKeyExW  HKCU then HKLM  \Software\Image-Line\Shared\Paths
              ├─ RegQueryValueExW  value "FL Studio engine"   (= engine install directory)
              ├─ SetCurrentDirectoryW(that directory)
              └─ wstr_concat_path(dir, g_pFLEngineDllName)  -> full path to FLEngine_x64.dll
            -> Authenticode verify (WinVerifyTrust + CRYPT32, see below)
            -> LoadLibraryW(full engine path)
            -> GetProcAddress(engine entry export)
            -> hand control to the engine
```

Key facts the chain establishes for the injection design:

- The engine directory is **discoverable at runtime** from
  `HKCU\Software\Image-Line\Shared\Paths` → `FL Studio engine`. Our injector/DLL can
  read the same key to locate `FLEngine_x64.dll` and compute module bases.
- The engine is loaded by **path via `LoadLibraryW`**, then entered via
  `GetProcAddress`. FL64.exe statically exports only `entry`; everything else is
  resolved dynamically (and partly through VMProtect import stubs).
- `SetCurrentDirectoryW` to the engine dir means relative DLL loads later resolve
  against the FL install dir.

## Protections (must-know for injection)

FL64.exe imports and behaviors confirm three protection layers:

1. **VMProtect** — `VMProtectSDK64.dll` is referenced; parts of the shell (and very
   likely the engine) are virtualized/obfuscated and include anti-debug. The tiny
   `0x1400010xx` import thunks are consistent with VMProtect's protected imports.
2. **Authenticode signature verification** — the import table includes the full
   chain: `WinVerifyTrust`, `CryptQueryObject`, `CryptMsgGetParam`,
   `CertFindCertificateInStore`, `CertGetNameStringA`, `CryptDecodeObject`,
   `CertFreeCertificateContext`, `CertCloseStore`. The shell verifies that the
   modules it loads are signed by Image-Line.
3. **`IsDebuggerPresent`** — basic debugger check (in addition to VMProtect's).

Implications (detailed in `02-control-strategy.md`):
- We **do not patch signed binaries** — our DLL is a separate, additive module, so
  the Authenticode checks on FL's own files are never invalidated.
- Prefer **injecting after** FL has finished its own startup integrity checks.
- Avoid attaching an external debugger to the live FL process during normal
  operation (VMProtect anti-debug). Our DLL calls functions directly; it does not
  single-step FL.

## What's confirmed vs. open

**Confirmed**
- FL64.exe is a loader; FLEngine_x64.dll is the engine.
- Registry-driven engine path resolution + cwd switch.
- Authenticode + VMProtect + IsDebuggerPresent present in the shell.
- CPython 3.12 ships and is the scripting runtime.

**Open (next phases)**
- The engine's entry export name and its host-interface signature (need the call
  site after `FL_ResolveEngineDllPath_FromRegistry`, which dispatches via the
  fn-ptr table at `0x1400864b0`).
- Everything inside `FLEngine_x64.dll` (not yet imported into Ghidra).
- Exactly how the engine registers the Python scripting modules (`channels`,
  `patterns`, `mixer`, `playlist`, `arrangement`, `plugins`, `transport`, `ui`,
  `general`) — the map from script API → native functions.
