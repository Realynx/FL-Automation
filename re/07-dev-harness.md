# 07 — Dev Harness (built + validated live)

Hot load/unload bridge for the LLM self-improvement loop (Phase 2.5). **Working against
live FL Studio 2025.**

## Components
- **`tools/bridge/`** — native `FlBridge.dll` (C++/MSVC via CMake). v1 = read-only:
  named-pipe server (`\\.\pipe\FruityLinkBridge`) + SEH-guarded memory reads. No hooks.
- **`tools/FruityLink.Probe/`** (`flprobe`) — injector + pipe client.

## Build
```sh
cmake -S tools/bridge -B tools/bridge/build -A x64 && cmake --build tools/bridge/build --config Release
dotnet build tools/FruityLink.Probe/FruityLink.Probe.csproj -c Debug
```

## Commands
```
flprobe inject [dllPath]    copy FlBridge.dll -> unique temp name, LoadLibrary into FL64
flprobe eject               pipe 'shutdown' (worker stops) then FreeLibrary  (clean unload)
flprobe reload [dllPath]    eject + inject  (the iterate loop; temp-copy dodges file locks)
flprobe bridge              status (FL? bridge loaded? ping)
flprobe bridge ping|info|peek <ghidraHex> <len>|peekabs <hex> <len>
```
`peek` maps a Ghidra address to live memory as `flEngineBase + (addr - 0x400000)`.

## Clean-unload contract — VALIDATED
One worker thread; overlapped `ConnectNamedPipe` waits on the stop event; `BridgeStop()`
joins it; `DllMain DETACH` is the safety-net join; no hooks/timers/callbacks; module not
pinned. Tested: `inject → eject → reload → eject` with **FL alive through every cycle.**

## Live verification results (2026-06-29) — big de-risking wins
- **Injection is NOT blocked** by VMProtect/anti-tamper. Plain `CreateRemoteThread(LoadLibraryW)`
  of our unsigned DLL loaded fine; FL survived inject/eject/reload repeatedly.
- **Ghidra→live mapping confirmed:** `real = flEngineBase + (ghidraAddr - 0x400000)`.
  Live `flEngineBase` (ASLR, per run) was `0x67F90000`; `info` reports it each session.
- **RE addresses verified against live memory:**
  | Ghidra addr | what | live result |
  |---|---|---|
  | `0xE270A0` | `setParamValue` | real prologue `55 56 53 48 81 EC C0 00 00 00 …` ✓ |
  | `0x14A7E40` | host-context global | non-null ptr `0x69026E38` → populated struct ✓ |
  | `0x14A9850` | mixer-count ptr | `*ptr = 0x7F = 127` tracks ✓ |
  | `0x14A7EB0` | mixer track array | inline; first field a valid engine ptr (stride `0x1474`) ✓ |

## Next
1. Extend the bridge from **read** to **call** — invoke engine functions / write structs. The
   key constraint: most engine routines must run on **FL's main/UI thread** (not our worker).
   Plan: a safe main-thread dispatch (e.g., a `WM_*`/`SendMessage` hook on FL's main window, or
   a timer/callback we register and immediately revert) + SEH guards. This is the orchestrator (#5).
2. Continue struct mapping (#3): walk the host-context object, channel/pattern/score/playlist
   models — all now verifiable live via `peek`.

## v2 — main-thread call mechanism (VALIDATED)
Bridge v2 adds main-thread calls: subclass FL's main window + `SendMessage` a private message
(runs our handler on the UI thread), SEH-guarded, reverted on eject. Commands: `tid`, `apctest`,
`call <ghidraHex> [args]`, `callabs`, `callhere`.
- ✅ `apctest`: `GetCurrentThreadId` executed on the main thread returned FL's main TID (14876).
  **Arbitrary main-thread execution works.**
- ✅ SEH safety proven: calling the Python-layer context getters (`FUN_00e0c4d0`/`e0fca0` =
  `(*global)()`) faulted because those globals are **NULL until the Python scripting subsystem
  initializes** (no controller script ran). The guard returned `ok:0` and **FL did not crash** —
  bad calls are safe to attempt.
- Implication: direct control must use the engine's OWN singletons (reachable from host-context
  root `0x14A7E40`), not the Python-binding helpers. Next: locate the project/mixer singleton +
  dispatcher signatures, then a visible setter via `FL_DispatchOpEvent`.
