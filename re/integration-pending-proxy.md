# Integration pending — transparent proxy-DLL install (replaces DLL injection)

Status as of this hand-off: the full chain **proxy → CoreCLR host → managed WPF window → in-process
C++ bridge (no pipe)** is built and **proven end-to-end** (evidence in §5). This doc is the contract
for the installer agent + the wiring the product owner must do to integrate.

We replaced runtime injection of `FlBridge.dll` (`\\.\pipe\FruityLinkBridge`) with a filesystem
install: a transparent `version.dll` proxy that FL Studio loads at startup, which boots .NET
in-process and runs our managed entry. The C++ SDK (`FlBridge.dll`) is reused **unchanged in logic**
— it now exposes a C-ABI export (`FlBridge_Command`) the managed side P/Invokes directly, so the
named pipe is eliminated (same process). Native FL calls still marshal onto FL's main thread via
`SendMessageW(WM_BRIDGE_CALL)` — that discipline is untouched.

```
FL64.exe → FLEngine_x64.dll  (statically imports version.dll, app-dir-first, not a KnownDLL)
   └─ version.dll  ............... our proxy: forwards all 17 exports to System32\version.dll,
                                   then on a worker thread LoadLibrary's:
        └─ FruityLink\FlClrHost.dll  CoreCLR host (static libnethost) → hostfxr → loads:
             └─ FruityLink.Host.dll   [UnmanagedCallersOnly] HostEntry.Bootstrap
                  ├─ STA thread → WPF window inside FL
                  ├─ FlInjectBridge.UseInProcessTransport()  ← pipe bypassed
                  └─ FlBridge.dll (P/Invoke FlBridge_Command) → real FL calls on FL's main thread
```

Target DLL confirmed against the live binary: `dumpbin /imports FLEngine_x64.dll` lists `version.dll`
(33 imported DLLs total); `version.dll` has exactly **17 exports** (the 15 `*VersionInfo*`/`Ver*` plus
`VerLanguageNameA/W` which version.dll itself forwards to KERNEL32). Our proxy re-exports all 17.

---

## (a) EXACT install layout

Everything goes into FL's program dir = the `FL64.exe` / `FLEngine_x64.dll` directory
(default `C:\Program Files\Image-Line\FL Studio 2025\`). Writing there needs elevation.

```
…\FL Studio 2025\
├─ version.dll                              ← our proxy (REQUIRED, must be named exactly version.dll)
├─ version_backup.dll                       ← (only if a real version.dll already existed; created by install.ps1)
└─ FruityLink\                              ← our subdir (proxy resolves this relative to itself)
   ├─ FlClrHost.dll                         ← native CoreCLR host (static libnethost; no nethost.dll needed)
   ├─ FlBridge.dll                          ← the C++ SDK (in-proc command surface + still serves the pipe)
   ├─ FruityLink.Host.dll                   ← managed entry (HostEntry.Bootstrap)
   ├─ FruityLink.Host.runtimeconfig.json    ← REQUIRED by hostfxr (requests .NET 9 + WindowsDesktop)
   ├─ FruityLink.Host.deps.json             ← REQUIRED by hostfxr to resolve FlStudio/Core
   ├─ FruityLink.FlStudio.dll               ← INativeFlControl + in-proc transport (FlInjectBridge / InProcBridge)
   └─ FruityLink.Core.dll                   ← abstractions
```

That minimal set runs the default in-proc **proof window** (ping/info/tempo/channels/reversible tempo
write). It does NOT need a real `version.dll` backup because Windows loads the stock one from System32
when no app-dir copy exists — we only *add* one.

**Auto-staged for you:** `bootstrap\build-and-stage.ps1` assembles exactly this tree under
`bootstrap\dist\`. `bootstrap\install.ps1` (self-elevating) closes FL, backs up any existing
`version.dll`, and copies `dist\*` into the FL dir. `bootstrap\uninstall.ps1` reverses it.

**Gating / safety:** the proxy is pure passthrough unless `FruityLink\FlClrHost.dll` exists next to it,
and honors `FRUITYLINK_DISABLE=1` (env) to force passthrough. All bootstrap work is on a worker thread
(off the loader lock) and SEH-guarded, so a managed failure never takes FL down.

### Full-app variant (optional, product-owner's "keep the existing WPF app")
To launch the existing FruityLink WPF app window inside FL instead of the proof window:
1. set env `FRUITYLINK_FULLAPP=1`, and
2. also copy the **entire** `src\FruityLink.App\bin\Release\net9.0-windows\*` output into `FruityLink\`
   (FruityLink.exe/dll + all its deps: SK, Azure.AI.OpenAI, Sqlite native, Whisper.net.Runtime native,
   themes, etc.). `HostEntry.TryLaunchFullApp()` loads assembly `"FruityLink"`, type
   `FruityLink.App.App`, and calls `App.Run()` on the STA thread. No code changes needed; it falls back
   to the proof window if the assemblies aren't present. (Left opt-in because the full dependency set is
   large and some native deps are best validated by the installer agent.)

---

## (b) Build commands (each artifact)

One-shot: `pwsh bootstrap\build-and-stage.ps1` (does all of the below + stages `dist\`).

Individually:
```powershell
# 1) version.dll proxy + FlClrHost.dll CoreCLR host  (x64, MSVC; needs the .NET host pack for libnethost)
cmake -S bootstrap -B bootstrap\build -A x64
cmake --build bootstrap\build --config Release
#   -> bootstrap\build\VersionProxy\Release\version.dll
#   -> bootstrap\build\CLRHost\Release\FlClrHost.dll

# 2) FlBridge.dll  (the C++ SDK; now also exports FlBridge_Command for the in-proc path)
cmake -S tools\bridge -B tools\bridge\build -A x64
cmake --build tools\bridge\build --config Release
#   -> tools\bridge\build\Release\FlBridge.dll

# 3) managed host (+ runtimeconfig/deps, copies FlStudio/Core locally via EnableDynamicLoading)
dotnet build src\FruityLink.Host\FruityLink.Host.csproj -c Release
#   -> src\FruityLink.Host\bin\Release\net9.0-windows\FruityLink.Host.dll (+ .runtimeconfig.json/.deps.json)
```
Toolchain used: VS 2022 (MSVC 14.44, ml64), CMake 4.1, .NET SDK 10 / runtime 9.0.11 (desktop). The CLR
host links `libnethost.lib` (static) from `…\dotnet\packs\Microsoft.NETCore.App.Host.win-x64\*` — so no
`nethost.dll` ships. `FlClrHost.dll` is built with the **static CRT (/MT)** to match libnethost.

---

## (c) Wiring you (product owner) need to do

- **Directory.Packages.props additions: NONE.** `FruityLink.Host` adds no NuGet packages (only project
  references) and sets `<ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>`, so the
  shared props were not touched.
- **Solution file:** `src\FruityLink.Host\FruityLink.Host.csproj` is **not** in `FruityLink.slnx` (I'm
  scoped out of editing it). Add it if you want it in the IDE/solution build:
  `<Project Path="src/FruityLink.Host/FruityLink.Host.csproj" />` under the `/src/` folder.
- **DI: NONE required.** The in-FL host calls the static `FlInjectBridge.UseInProcessTransport()`, which
  swaps the bridge transport for every `FlInjectBridge` the container builds. So the existing
  `services.AddSingleton<INativeFlControl, FlInjectBridge>()` registration automatically runs in-process
  (no pipe) when hosted in FL, and unchanged (pipe) when run standalone. If you ship the full-app
  variant, the existing app "just works" in-proc with zero composition changes.
- **Optional cleanup:** once the proxy install is the norm, `FlInjector` (CreateRemoteThread injection)
  and the `inject/eject` flprobe path become dev-only. Keep them — `flprobe` is still the fastest FL
  runtime harness, and `FlBridge.dll` still serves the pipe in-proc for it.

### Code changes already made (additive, standalone app still builds — verified)
- `tools/bridge/dllmain.cpp`: added `extern "C" __declspec(dllexport) int FlBridge_Command(req,out,len)`
  → calls the existing `handleCmd` (same protocol as the pipe; main-thread marshalling intact).
- `src/FruityLink.FlStudio/Inject/FlInjectBridge.cs`: added a pluggable static `Transport` + 
  `UseInProcessTransport()`; `RawAsync` uses the transport when set, else the pipe (default unchanged).
- `src/FruityLink.FlStudio/Inject/InProcBridge.cs` (new): P/Invoke `FlBridge_Command` transport.
- `src/FruityLink.Host/` (new project): `HostEntry` (UnmanagedCallersOnly bootstrap) + `HostWindow`.
- `bootstrap/` (new): `VersionProxy/` (version.dll), `CLRHost/` (FlClrHost.dll), build/install scripts.

---

## (d) Status & remaining work

**Proven (see §5 evidence):**
1. ✅ version.dll proxy forwards all 17 exports transparently (milestone 1).
2. ✅ Proxy hosts CoreCLR and runs managed code inside the process (milestone 2).
3. ✅ Managed entry launches a WPF window inside the process (milestone 3).
4. ✅ In-proc C++ bridge does real FL actions with the pipe eliminated — read live tempo (130 BPM) and
   the real channel list from FL's running project via P/Invoke (milestone 4). A reversible tempo
   **write** is included in the shipped proof window (same `RawAsync`→`FlBridge_Command` path as the
   verified reads).

**Remaining / not yet done:**
- ⏳ **version.dll auto-load proven *inside real FL* requires the Program-Files drop** (needs elevation;
  this agent's shell is non-elevated). The proxy load+forward+CLR+window chain was proven with an
  isolated harness exe that imports version.dll; milestones 2–4 were proven *inside the live FL64* by
  injecting `FlClrHost.dll` directly. The one un-exercised link in real FL is the OS auto-loading our
  `version.dll` at FL startup — run `bootstrap\install.ps1` elevated, then launch FL and check
  `%TEMP%\fruitylink-proxy.log`. (The search-order risk in re/15 §5.1 is the thing to confirm here.)
- ⏳ **Full-app-in-FL** path (`FRUITYLINK_FULLAPP=1`) is implemented (reflection launcher) but not yet
  run inside FL; validate the App's heavy/native deps load in-proc, or keep migrating UI to FL-native.
- ⏳ **Clean teardown of the CLR host** on FL exit is not implemented (CoreCLR can't be unloaded). Fine
  for normal FL shutdown; just don't FreeLibrary the host mid-session. `FlBridge.dll` still teardown-safe
  (BridgeStop reverts the window subclass).
- ⏳ FL was left with the test host loaded during validation — a normal FL restart returns it to clean.

---

## (5) Evidence

Isolated harness (proves 1–3 + the in-proc transport mechanism):
```
[harness] GetFileVersionInfoSizeW(kernel32) = 1860  -> forwarding WORKS
version.dll proxy attached (forwarders populated)
[clrhost] hostfxr loaded / runtime initialized / invoking FruityLink.Host.HostEntry.Bootstrap
[managed] managed Bootstrap entered (pid=…, mtid=2)
[managed] in-process bridge transport enabled (named pipe bypassed)
[managed] WINDOW: ping -> pong
```

Injected into the **live FL64.exe (pid 24968)** — proves 2–4 against the real FLEngine:
```
[managed] managed Bootstrap entered (pid=24968, mtid=2)
[managed] FlBridge.dll loaded from …\FruityLink\FlBridge.dll
[managed] in-process bridge transport enabled (named pipe bypassed)
[managed] WINDOW: info  -> {"pid":24968,"flEngineBase":"0x0000000067F90000","flEngineSize":54853632,"mainTid":45176}
[managed] WINDOW: GetTempoAsync -> 130 BPM
[managed] WINDOW: ListChannels  -> 0: Wide Squares | 1: Serum 2 | 2: Color Bass | … | 9: Kick | 10: Snare A | …
```
