# 02 — Control Strategy: From "MIDI sandbox" to "Total Control"

## Where we are today (the limitation we're removing)

The current bridge talks to FL through a **controller script** (Python running
inside FL's controller-script sandbox). On FL 2025 (scripting v40) that sandbox
**neuters file I/O, sockets, and mmap**, so the only available IPC is chunked SysEx
over loopMIDI. That works and is verified, but it constrains us to:

- low-bandwidth, half-duplex MIDI framing, and
- the subset of FL's API the controller-script context exposes (e.g. piano-roll
  note authoring needs a *separate* piano-roll script + a manual "Apply").

The injected-DLL approach removes both constraints at once.

## Key insight: an injected native DLL is **not** a controller script

The sandbox restrictions are applied to *controller-script interpreter contexts*.
A DLL we inject into the FL process is plain native code. It can:

- open **any** IPC it wants (named pipe / TCP / shared memory) — no MIDI bottleneck;
- call the **standard CPython C-API** — `python312.dll` exports `Py_*` by name, so we
  need zero RE to drive Python itself;
- call **native engine functions** in `FLEngine_x64.dll` directly;
- read/write the engine's **in-memory project model** directly.

## The three control surfaces

Ordered by effort-to-reward. We will combine them.

### A. Drive FL's embedded Python API from native code  *(fastest broad coverage)*
FL's scripting modules — `channels`, `patterns`, `mixer`, `playlist`,
`arrangement`, `plugins`, `transport`, `ui`, `general` (+ the piano-roll `score`
API) — are **documented by Image-Line** and already cover most of the requirement
list. Their implementations live in the engine. From our DLL we can, on FL's thread
and holding the GIL, do the equivalent of `PyImport_ImportModule("channels")` and
call `setGridBit`, `plugins.setParamValue`, the playlist/arrangement functions, etc.
— **without the controller-script sandbox**, because we are not a controller script.

- Pros: documented + stable across versions; smallest RE surface; reuses
  battle-tested code paths.
- Cons: must correctly acquire the GIL / run on the right thread; need to confirm
  the FL modules are importable from our context (vs. only inside a script host).
- Status: **hypothesis to validate in Phase 3.** This is the highest-leverage path.

### B. Call native engine functions directly  *(for gaps + speed)*
For anything not exposed (or too slow) through Python, locate the native function in
`FLEngine_x64.dll`, recover its signature/`this`, and call it from our DLL on the
correct thread. The Python `PyMethodDef` tables in the engine are the **Rosetta
Stone**: each scripting method name sits next to a pointer to its native
implementation — that's a direct, labelled index into the engine's control
functions.

- Pros: full reach, fast.
- Cons: more RE; version-fragile (prefer signature/pattern scanning over hardcoded
  offsets); some functions may be VMProtect-virtualized and impractical to call.

### C. Manipulate engine data structures directly  *(last resort)*
Directly edit the project model in memory (channel/pattern/playlist/mixer structs).
Most powerful, most fragile — only where A and B can't reach. Requires mapping
struct layouts and respecting the engine's invariants/locks + UI invalidation.

### Recommended: **hybrid, A-first**
Injected DLL = transport + dispatcher. Use **A** for breadth, drop to **B** for gaps
and hot paths, and **C** only when unavoidable. This minimizes RE while reaching the
"every option" goal.

## Requirement → API map

The user's explicit targets, and where each is reached:

| Requirement | Primary surface | Notes |
|---|---|---|
| Piano-roll scores (notes, timing, velocity, etc.) | A: piano-roll `score`/notes API; B: native note funcs | Removes the current "stage notes + manual Apply" step. |
| Patterns (create/select/rename/clear) | A: `patterns` | |
| Channel rack — add/insert instruments, step data | A: `channels` (`setGridBit`, add plugin); B for inserts | Step writing already proven via `channels.setGridBit`. |
| Playlist — items, timing, location, length | A: `playlist` + `arrangement` | The big win vs. MIDI bridge. |
| Mixer — routing, inserts, FX add/replace | A: `mixer` + `plugins` | |
| FX/plugin parameter changes ("every option") | A: `plugins.setParamValue`/`getParamCount`; B: native param graph | Per-plugin total param control. |
| Names (channels/patterns/tracks/mixer) | A: respective `set*Name` | |
| Transport / global options | A: `transport`, `general`, `ui` | |

## How it plugs into the existing C# codebase

The injected DLL is **additive** — it becomes a new transport behind the existing
abstraction, so the Agent layer is unchanged:

- Today: `IFlRpc` has `SysExRpcClient` (MIDI) and `FileRpc` (files), with shared DTO
  mapping in `FlBridge` and the typed `IFlBridge` surface (Core).
- Add: **`NamedPipeRpcClient : IFlRpc`** (or TCP) in `FruityLink.FlStudio`, speaking
  to the injected DLL's IPC server. Same `CallAsync(method, params)` contract and the
  same JSON method names the Python handlers already use, so `FlBridge`/`IFlBridge`
  and every `[KernelFunction]` plugin keep working.
- Select the transport in `App/ServiceConfiguration.cs` (as today).
- A new C# **injector** (or a tiny native launcher) loads the DLL into FL.

Net effect: the LLM agent keeps calling the same `IFlBridge` methods; we've just
swapped a constrained MIDI pipe for an unrestricted in-process bridge with a far
larger command vocabulary.

## Coexisting with FL's protections (the rules we follow)

1. **Never patch or modify Image-Line binaries** (on disk or in memory). Our DLL is a
   separate module; FL's own Authenticode checks on its files stay valid.
2. **Never touch the licensing / DRM path.** This is interop, not circumvention.
3. **Inject after FL's startup integrity checks** (user-triggered or delayed) to
   avoid load-time interference.
4. **Don't debug the live process** under VMProtect anti-debug; our DLL *calls* code,
   it doesn't single-step FL. (RE/tracing happens in Ghidra and in a separate,
   disposable analysis instance.)
5. **Fail safe.** Wrap engine calls in SEH and marshal them onto FL's thread so a bad
   call can't corrupt or crash the user's session silently.
