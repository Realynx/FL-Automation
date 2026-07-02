# 03 — Roadmap & Ghidra Symbol Log

## Phases

### Phase 0 — Map the shell  ✅ DONE
- Identified FL64.exe as a loader; FLEngine_x64.dll as the engine.
- Reconstructed + annotated the startup/loader chain in Ghidra.
- Documented protections (VMProtect, Authenticode, IsDebuggerPresent).
- Output: `01-module-architecture.md`, this log.

### Phase 1 — Import & survey the engine  🔄 IN PROGRESS
- ✅ Imported `FLEngine_x64.dll` into the Ghidra `FlStudio` project (53.6k functions).
- ✅ Toolchain identified: **Delphi/Embarcadero** (RTTI available — readability win).
- ✅ Engine entry = export **`CreateFruityInstance`** (ordinal 3, RVA `0xE69F00`);
  host context struct stashed at global `0x14A7E40`.
- ✅ VMProtect footprint mapped: `.avm0–.avm3` virtualized; the ~18 MB `.text` bulk is
  plain native code. Python is **not** statically linked (loaded on demand) — peripheral.
- 🔄 **Running full `reanalyze`** to populate strings/data/RTTI (the import pass skipped
  them; guaranteed strings were undefined). Blocks Phase 2/3 string + RTTI searches.
- ⛔ MCP script execution is gated off (`run_script_inline`/`run_ghidra_script`
  disabled). If the reanalyze doesn't recover Delphi RTTI well, **fallback:** user runs
  a Delphi RTTI script (Dhrake/IDR) once in the Ghidra GUI; we then continue over MCP.
- Full detail: `04-engine-survey.md`.

### Phase 2 — Find the scripting-API → native map  (the Rosetta Stone)
- Locate the CPython `PyModuleDef` / `PyMethodDef` tables for `channels`,
  `patterns`, `mixer`, `playlist`, `arrangement`, `plugins`, `transport`, `ui`,
  `general`. Each entry = `{ "methodName", &native_impl, flags, "doc" }`.
- Rename every native impl in Ghidra from its method name → instant, labelled index
  into the engine's full control surface.

### Phase 2.5 — DLL Dev Harness (hot load/unload loop)  ✅ BUILT + VALIDATED LIVE (see 07-dev-harness.md)
Result: inject/eject/reload all clean against live FL; **injection NOT blocked by VMProtect**;
RE addresses verified against live memory (mixer count=127, host-context ptr, setParamValue prologue).
A small **injector/eject harness** (extend the existing `flprobe`, tools/FruityLink.Probe)
so the agent can iterate on the injected DLL against a *live* FL without restarting it:
**eject → rebuild → inject → call → observe → repeat.**

Commands the harness exposes (driveable by the LLM/agent):
- `inject [path]` — load the bridge DLL into the FL process.
- `eject` — unload it cleanly (the linchpin — see below).
- `reload` — eject + inject (after a rebuild).
- `status` — is it loaded? module base? last error? is FL alive (watchdog)?
- `call <cmd> [args]` — invoke a bridge command and return its result/log to the agent.

**Clean-unload requirements (must hold every cycle or FL crashes / file stays locked):**
1. **Join all threads** the DLL started (IPC server, workers) before returning from the
   eject path; never `FreeLibrary` while one of our threads runs our code.
2. **Revert every hook/patch/subclass** (any inline hook, `SetWindowsHookEx`, window
   subclass, vectored handler) before unload — leaving one pointing into freed code = crash.
3. **Deregister all callbacks/timers/objects** handed to FL that point into our module.
4. **Do not pin** the module (no `GET_MODULE_HANDLE_EX_FLAG_PIN`); ensure refcount → 0 so
   `FreeLibrary` truly unmaps. Verify with a module-list check in `status`.
5. **Tear down GIL/COM/TLS** state we created.
6. **Robustness trick:** inject a *copy* of the freshly built DLL under a unique temp name
   each cycle, so a rebuild never collides with a Windows file lock even if an eject is
   imperfect; the harness manages the temp copies.
7. **Crash-safety:** wrap engine calls in SEH so a bad call returns an error to the agent
   instead of killing FL; harness watchdog detects/report FL death and offers relaunch.

Design so the **stable injector lives in the external C# process** (`flprobe`/app), and the
**volatile, iterated logic lives entirely in the swappable DLL** — that separation is what
makes the rebuild loop safe.

### Phase 3 — Feasibility prototype (de-risk)
- Minimal injected DLL: load into FL, open a named pipe, and on FL's main thread
  acquire the GIL and execute **one** trivial API end-to-end (e.g. start/stop
  transport, or read project title) via the embedded Python C-API (surface **A**).
- Confirm plain `LoadLibrary` injection does not trip VMProtect/anti-tamper in
  normal operation.
- Decision gate: if surface **A** works, it becomes the backbone and Phase 2 RE is
  needed only for gaps.

### Phase 4 — Dispatcher + coverage
- Command dispatcher in the DLL (JSON over the pipe, same method names as the
  current bridge). Implement the requirement→API matrix from `02-control-strategy.md`.
- C# side: `NamedPipeRpcClient : IFlRpc` + injector; wire in `ServiceConfiguration.cs`.
  Agent plugins unchanged.

### Phase 5 — Hardening
- Version resilience: signature/pattern scanning instead of hardcoded addresses;
  resolve via exported `Py_*` + `PyMethodDef` lookups where possible.
- SEH around engine calls; thread/GIL correctness; reconnect; structured errors;
  audit logging (reuse `IOperationAuditSink`).

## Risks & mitigations

| Risk | Mitigation |
|---|---|
| VMProtect virtualizes a function we need | Prefer surface **A** (Python C-API, unvirtualized exports) and **C** over calling virtualized natives. |
| Version drift breaks offsets each FL update | Resolve by exported symbol + `PyMethodDef` tables + byte-pattern scan; avoid hardcoded RVAs. |
| GIL / wrong-thread call crashes FL | Marshal onto FL's main thread; proper `PyGILState_Ensure/Release`; SEH guards. |
| Anti-tamper trips on injection | Inject post-startup; never patch signed code; validate empirically in Phase 3. |
| A bad call corrupts the user's open project | Fail-safe wrappers; test on throwaway projects; keep the verified MIDI bridge as fallback. |

## Open questions to resolve next session
- Engine entry export name + host-interface signature (follow the fn-ptr table at
  `0x1400864b0` / the call site after `FL_ResolveEngineDllPath_FromRegistry`).
- Does `FLEngine_x64.dll` statically import `python312.dll`, or load it dynamically?
- Are FL's script modules importable from an arbitrary in-process GIL context, or
  only within a script-host interpreter? (Determines surface **A** viability.)

---

## Ghidra symbol log

Program: **FL64.exe** (Ghidra project `FlStudio`, image base `0x140000000`).
All entries below were renamed/commented via the MCP bridge and **saved**.

| Address | New name | What it is |
|---|---|---|
| `0x140030e44` | `entry` *(commented)* | PE entry → MSVC CRT bootstrap → mainCRTStartup. |
| `0x140005000` | `FL_ResolveEngineDllPath_FromRegistry` | Reads `HKCU/HKLM\Software\Image-Line\Shared\Paths` → `"FL Studio engine"`, `SetCurrentDirectoryW`, joins `FLEngine_x64.dll`, returns full path. Called via fn-ptr table `@0x1400864b0`. |
| `0x140001030` | `cppinit_FLEngineDllPath` | C++ global ctor → `g_pFLEngineDllName = L"FLEngine_x64.dll"`. |
| `0x140001000` | `cppinit_FLReWireDllPath` | C++ global ctor → `g_pFLReWireDllPath = L"System\Plugin\ReWire\FLReWire_x64.dll"`. |
| `0x140001060` | `cppinit_ILToolsDllPath` | C++ global ctor → `L"ILTools_x64.dll"`. |
| `0x140001900` | `wstr_assign` | MSVC `std::wstring::assign(this, src, len)` (SSO-aware). |
| `0x140007200` | `wstr_concat_path` | std::wstring concat; joins engine dir + DLL name *(inferred)*. |
| `0x140043fb8` | `crt_CorExitProcess_shim` | CRT managed-exit shim (`mscoree`/`CorExitProcess`) — FL64 hosts the CLR. |
| `0x140072c10` | `g_pFLEngineDllName` | `std::wstring` = `L"FLEngine_x64.dll"`. |
| `0x140072bf0` | `g_pFLReWireDllPath` | `std::wstring` = ReWire DLL path. |

Other identified-but-unnamed helpers referenced above: `FUN_1400326b0` (= wmemcpy),
`FUN_140001330` (wstring copy/normalize, unconfirmed), `FUN_140007200`'s callees.
Data table of FL64 function pointers at `0x1400864b0` (dispatches the path resolver).
