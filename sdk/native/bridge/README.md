# FlBridge native bridge

The in-process bridge resolves FL Studio symbols and marshals engine operations onto FL's main thread.
Release builds expose no named pipe. Debug builds, or `-DFRUITYLINK_DEBUG=ON`, enable the development
pipe. The managed plugin host exposes typed operations; raw addresses stay internal.

## Build and test

From the SDK repository root, using x64 MSVC and CMake:

```powershell
cmake -S native/bridge -B native/bridge/build -A x64 -DBUILD_TESTING=ON -DFRUITYLINK_DEBUG=OFF
cmake --build native/bridge/build --config Release
ctest --test-dir native/bridge/build -C Release --output-on-failure
```

This creates `native/bridge/build/Release/FlBridge.dll` without deploying it. Native tests cover malformed
patterns, duplicate matches, unreadable scan regions, displacement bounds, verified build identity,
legacy address mapping, fallback refusal, and the owned-response command exports. A synthetic-window
test also covers hidden FL-window discovery, 3,200 concurrent native calls, window recreation, and
restoring the original window procedure. These tests do not launch FL Studio.

## Compatibility

The bridge injects an `IFlSignatureScanner` for FL 2025 or FL 2026 once at startup. Other families
select an unsupported implementation. Shared scanning mechanics remain independent of version policy;
plugins use the same managed interfaces. See [scanner architecture](../../docs/scanner-architecture.md).

Exact fallback addresses remain recorded for **25.2.5.5319** and **26.1.0.5530**. Complete mixer
and generic native-window layouts are independently verified for installed **25.2.5.5319** and
**26.1.3.5570**. Newer patches cannot inherit either addresses or layouts. Native plugin windows
use the concrete TFLBaseVectorForm and its real caption controls, never TScriptDialog stubs.
This latest window factory has offline/fixture verification; live chrome validation is pending.

To inspect an installed binary without starting FL or loading its DLL:

```powershell
$fl2025Engine = 'C:/Program Files/Image-Line/FL Studio 2025/FLEngine_x64.dll'
$fl2026Engine = 'C:/Program Files/Image-Line/FL Studio 2026/FLEngine_x64.dll'
& native/bridge/build/Release/FlBridgeSigscanTests.exe --inspect-engine $fl2025Engine
& native/bridge/build/Release/FlBridgeSigscanTests.exe --inspect-engine $fl2026Engine
```

The diagnostic copies PE sections into a private, non-executable buffer and emits the `syms` report.
It checks static symbol resolution; it does not verify running FL objects, callbacks, or field layouts.
Validate both supported FL installations before deploying any bridge change.

Static inspection on 2026-09-12 (substitute your actual installation paths above):

| Engine file version | Scanner | Resolved | Unresolved |
| --- | --- | ---: | --- |
| 25.2.5.5319 | `fl-2025` | 109 / 109 | None |
| 26.1.3.5570 | `fl-2026` | 104 / 109 | Five legacy UI/loading helpers |

The expanded catalog includes every literal managed and native legacy alias; CTest checks coverage.
All managed-operation aliases resolve on both installed builds. The five 2026 gaps are explicitly
listed in `syms`; they are not authorized by a raw 2025 fallback. The newer build reports a complete
mixer layout, `pluginMenu:true`, `legacyBrowserUi:false`, and `windowEmbedding:true`. See [the analysis report](../../docs/fl-version-analysis-2026-09-12.md).

## Managed transport

Main-thread dispatch selects the current process's `TFruityLoopsMainForm` window, including when
hidden or minimized. Hook installation is synchronized and refuses self-chaining; each synchronous
message carries its own call arguments/results. Window destruction clears the cached hook state.
The `fl_ready` response requires this HWND as well as initialized main-form, toolbar, song and channel
objects. A successful `ping` only establishes that the bridge is loaded.

The host waits for readiness before activating plugins, continuing after a warning if startup takes
more than 30 seconds. Automatic native startup probes are opt-in through
`FRUITYLINK_STARTUP_PROBES=1`; diagnostic-window probes also check readiness before engine calls.

- `FlBridge_CommandAlloc(req, &response)` executes a command once and returns its UTF-8 response length.
  Free every non-null response with `FlBridge_FreeResponse(response)`. Empty responses return length 0
  and a null pointer; errors return -1. The response is not null-terminated.
- `FlBridge_Command(req, buffer, length)` remains available for older hosts. Its returned full length
  may exceed the buffer; retrying repeats the command, so new hosts should use the allocation API.
- `resolve sym:NAME` returns the resolved runtime address as hexadecimal or an explicit error. This
  supports addresses passed as internal function arguments, such as Delphi array type information.

Native plugin-menu toggles run on a worker so the FL main thread remains available during enable and
disable. The callback holds a temporary module reference until it returns; `BridgeStop` prevents new
work and restores native hooks before normal unload.

See [the native bridge overview](../../docs/native-bridge.md) for the architecture and protocol.

## Native window sessions

`winhost_create`, `winhost_bind`, `winhost_show`, `winhost_close`, and `winhost_status` take a per-window
hexadecimal ID. `winhost_list` reports every registered native form and its content/child HWNDs.
Creation stays detached until bind verifies the managed child has been parented on its owning
UI thread. Close refuses a still-parented child and succeeds idempotently for a missing ID. The
legacy unkeyed commands use reserved slot zero through the same factory. Docking remains refused.

The native caption close button hides the form; native maximize is enabled. Minimize/menu/captionize
buttons retain hidden defaults (the base form's minimize action would minimize the whole application).
The registry owns a separate content HWND below the native caption, so content coordinates start at
zero. Live two-plugin, focus, DPI, theme and lifecycle checks are pending while user FL sessions remain
untouched. Creating a host pins FlBridge until process exit; restart FL to replace the native DLL.

`FLapp_SetTitle` corrects the old misnamed `FLwp_SetFormCaption` catalogue entry. The old name remains
lookup-only compatibility. It is an application title setter, never a form caption setter. Form,
caption and toolbar controls use the verified Delphi UnicodeString `FLwp_SetButtonCaption` API.

The 0.1.14 candidate's `native-content-bounds-unavailable` fallback was traced to an inherited
misidentification of `form+0x11C`: published RTTI names it `Touch`, not a content control. The current
factory obtains native client geometry through the verified VMT+0x310 getter and mapped caption HWND.
The real factory test includes a PAGE_NOACCESS Touch sentinel and 104 hidden-window checks; all six
native CTest groups pass. Successful creation and stage-specific geometry errors include PID/handles
in `%TEMP%/fruitylink-bridge.log`, without enabling an external debug pipe.

FL paints its native title strip and buttons through the parent surface. Do not add
`WS_CLIPCHILDREN` to that native host: the 0.1.16 candidate demonstrated that it hides the chrome.
The registry instead excludes only its bound, visible foreign container rectangle from the
parent update region before FL handles `WM_PAINT`. FL retains its own buffered-region capture
and BeginPaint/EndPaint lifecycle. Existing paint/erase DCs get a scoped exclusion with caller
state restored. The separate foreign container keeps its own child clipping; unchanged geometry
and pure host moves do not request redundant child positioning. Exact binary evidence and live
versus fixture limits are recorded in [the analysis report](../../docs/fl-version-analysis-2026-09-12.md).
