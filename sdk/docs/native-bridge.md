# Native bridge (`FlBridge.dll`)

`FlBridge.dll` is the small native (C++) component that actually reaches into FL Studio. Managed code
(`FruityLink.FlStudio`'s `INativeFlControl` implementation) never touches FL memory directly — it sends
text commands to the bridge, and the bridge executes them on FL's own main thread. Plugins don't use this
directly (they use [`INativeFlControl`](fl-control-api.md)); this page explains what's underneath.

Source: [`native/bridge`](../native/bridge) (`dllmain.cpp`, `sigscan.h`/`sigscan.cpp`, `version_scanner.*`, `delphi_classref.*`, `callthunk.asm`,
`CMakeLists.txt`, [`README.md`](../native/bridge/README.md)).

## Architecture and protocol

- **Text verbs in, text out.** The command surface is a simple request→response protocol: one text
  command (`ping`, `info`, `syms`, `fl_ready`, `peek`, `call`, `hint`, plus the typed control verbs),
  one text reply (often JSON-ish, e.g. `info` returns `{pid, bridgeBase, flEngineBase, flEngineSize}`;
  errors come back as `err:...`).
- **In-process export (production path).** `FlBridge_CommandAlloc(reqUtf8, &response)` executes once
  and returns the full UTF-8 response length. The caller releases the owned buffer with
  `FlBridge_FreeResponse(response)`. The legacy fixed-buffer `FlBridge_Command` export remains
  available. Managed clients fall back to it for older bridges but refuse oversized replies instead
  of repeating commands, which could repeat a mutation or drain a queue twice.
- **Dev named pipe (`\\.\pipe\FruityLinkBridge`).** A named-pipe server exposing the same command handler
  exists **only in debug builds** (compiled in when the `_DEBUG` config or the `FRUITYLINK_DEBUG` CMake
  option is set). A plain Release/production build ships **no** pipe. It's for dev tooling (attach,
  inspect memory, drive commands) — not the plugin path.
- **Main-thread call marshaling.** Most FL engine routines are not thread-safe, so the bridge runs calls
  on FL's main/UI thread: it subclasses FL's main top-level window and dispatches a private
  `SendMessage`, whose window proc runs synchronously on the owning (main) thread. Every call is
  SEH-guarded to contain ordinary native faults where possible. This is not a guarantee
  against process crashes or state changes after a failed call.
- **`sym:` tokens.** A call site can address an engine function by name — a `sym:NAME` token — instead of
  a raw address. The bridge resolves the name through its signature-scan table (below). This is additive:
  the legacy hex-address path is untouched, and an unknown `sym:` is refused loudly (`err:unknown-sym:…`)
  rather than guessed.

## Fail-safe signature scanning

One bridge binary supports multiple FL versions by resolving FL addresses **at runtime** via byte-
signature scanning of the loaded `FLEngine_x64.dll`, instead of a hardcoded `base + (ghidra - 0x400000)`
rebase (`sigscan.*`).

It is **fail-safe by construction** — a wrong address is an uncatchable access violation inside FL, so
every path *refuses* rather than guesses:

- 0 matches → `NotFound`; more than 1 match → `Ambiguous` (never pick one).
- A per-version fallback address is never trusted on an unknown FL version.
- A resolved address that fails its byte self-check is refused.
- All raw reads are SEH-guarded.

The `syms` diagnostic reports the full engine file version, selected scanner, completion state,
verified layouts, and every symbol that did **not** resolve. Legacy `ver`, `ok`, `fail`, and
`unresolved` fields remain available. The managed side surfaces this via
[`IFlSymbolResolution`](fl-control-api.md#version-gating-iflsymbolresolution) so tools depending on an
unresolved symbol are hidden rather than fired.

## FL version support

The native composition root injects a scanner for the loaded **FL 25** or **FL 26** family. Other
major versions and missing version metadata select an unsupported scanner and refuse resolution.
Supported families share the proven scanning algorithm; each family owns its exact-build layout
policy. See [scanner architecture](scanner-architecture.md).

Hardcoded address fallbacks remain restricted to recorded builds **25.2.5.5319** and **26.1.0.5530**.
The installed **26.1.3.5570** uses independently matched signatures and validated Delphi metadata;
it does not inherit the older 2026 addresses. Its mixer and generic native window layouts are
independently verified. Native windows use TFLBaseVectorForm, never the old script-dialog stub path.

The managed mixer consumes the complete selected layout instead of choosing a stride from `ver`.
Ghidra confirmed changes in track stride, send-table offset, and effect-slot offset. A missing full
layout refuses raw mixer operations before mutation. **Deploy the updated managed SDK with its
matching native bridge:** older bridges do not report the full layout required by these operations.

The automation-enabled SDK snapshot inspected on 2026-09-12 used copied PE sections
without loading or executing FL code:

| Engine build | Scanner | Resolved symbols | Mixer layout | Window layout |
| --- | --- | ---: | --- | --- |
| 25.2.5.5319 | `fl-2025` | 113 / 113 | Verified in this pass | Generic base form, binary verified |
| 26.1.3.5570 | `fl-2026` | 108 / 113 | Verified in this pass | Generic base form, binary verified |

Local evidence is recorded in `artifacts/automation-creation/inspect-2025.json`,
`inspect-2026.json`, and the accompanying `verification.json`. These counts describe
signature resolution; layout checks and live behavior have separate validation.
The additional resolved symbol is the [automation clip creator](automation-clips.md),
independently matched in both exact engine builds.

The five unresolved 2026 entries are legacy UI/loading helpers: `FLui_MarkKbCapture`,
`FLbrz_SelectTabById`, `TQuickEdit_SetText`, `TQuickEdit_Refresh`, and `LoadingFlag`. They were
previously absent from the catalog; adding them makes the diagnostic coverage accurate. Legacy
browser widget creation is gated before mutation because its QuickEdit fields changed. Plugin-menu
discovery uses separately verified fields and remains available on both inspected builds. Literal
managed operation addresses have catalog entries; callers must still check capability and resolution status.
A CTest coverage check prevents another uncatalogued literal alias from being introduced.

`TransportRangeStart` and `TransportRangeEnd` resolve the actual loop bounds, independently of
the toolbar slider's full-song seek domain. Song-state and transport diagnostics read these
native globals and retain inclusive text formatting (`endExclusive - 1`). Missing or invalid
range data reports `[-1..-1]`; it never substitutes the slider bounds for an unknown loop.

`mixer_add <afterTrack>` performs count/topology validation, one `FLmx_InsertTracks` call, and
postvalidation together on FL's main thread. `-1` appends; `0` inserts after Master. The exact
mixer layout and insertion symbol must be available. Native cardinality includes Current,
but ordinary indices are only `1..count-2`; Current is fixed physical index 501, not `count-1`.
The typed mixer query excludes Current and dormant slots. Ambiguous insertion failures report
that state may have changed and are never retried automatically.

The earlier 26.1.0.5530 binary was not installed in this pass. Its historical address data remain
separate; its generic window, full mixer and menu-discovery layouts are not asserted. Live 2026
native-frame creation succeeded, then exposed geometry and repaint defects documented in the
evidence report. The latest narrow paint correction still requires live validation; 2025 window
behavior remains binary/fixture verified only.
See [the Ghidra evidence](fl-version-analysis-2026-09-12.md) for exact hashes, signatures, field
comparisons, and semantic spot checks. These results do not replace live editing, save/reopen,
playback, lifecycle, and render tests.

## Building

Startup dispatch supports hidden FL main windows. The bridge matches `TFruityLoopsMainForm` in its
own process rather than selecting an arbitrary visible window. A serialized hook installation and
per-message call state prevent concurrent bootstrap requests from chaining the hook to itself or
overwriting another call. `IsAvailableAsync` now checks initialized FL state plus its main HWND;
`IsLoadedAsync` only checks the bridge's `ping` response. The host waits for readiness and does not
activate plugins after a timeout with incomplete state.

The 2026-09-12 live regression exercised hidden launch on 26.1.3.5570, native plugin initialization,
same-process embedded Python, structured channel/tempo reads, save and clean close. See
[the recorded validation](fl-version-analysis-2026-09-12.md#live-startup-validation).

x64 (must match FL Studio), MSVC via CMake:

```sh
cmake -S native/bridge -B native/bridge/build -A x64 -DFRUITYLINK_DEBUG=OFF
cmake --build native/bridge/build --config Release
# -> native/bridge/build/Release/FlBridge.dll
```

The build uses MASM (`callthunk.asm` provides the XMM-capable call path for float-arg/float-return engine
functions) — `enable_language(ASM_MASM)` is already in `CMakeLists.txt`.

**Debug pipe.** Pass `-DFRUITYLINK_DEBUG=ON` (or build the Debug config) to compile in the external
named-pipe server and the debug tooling commands. Leave it **off** for production — the in-process
`FlBridge_Command` surface is always present regardless.

## Why raw memory verbs aren't exposed to plugins

The bridge has low-level verbs — `peek`/`poke` (raw memory read/write) and `call` (invoke an arbitrary
address). Those are **never** handed to plugins. `IPluginContext` exposes only the typed
[`INativeFlControl`](fl-control-api.md) surface; the raw primitives stay internal and capability-locked.
No address crosses these typed APIs. This narrows the supported integration surface,
but in-process plugins execute with FL Studio's permissions and are not sandboxed.
This is the same API boundary described in
[the FL control API](fl-control-api.md#error-behavior-and-the-trust-boundary).

## Clean-unload contract

Creating a native plugin form pins the bridge for process lifetime, so outstanding window callbacks
cannot point into an unloaded DLL. `BridgeStop()` restores ordinary hooks and releases detached
native forms on FL's main thread; a still-parented plugin child must first detach on its own UI
thread. Process-termination `DllMain` does not wait for threads or invoke UI cleanup. Replacing the
native bridge binary therefore requires restarting FL. Plugin disable/re-enable remains supported
through independent keyed sessions. See [window embedding](window-embedding.md).
