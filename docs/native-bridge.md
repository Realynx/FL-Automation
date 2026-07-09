# Native bridge (`FlBridge.dll`)

`FlBridge.dll` is the small native (C++) component that actually reaches into FL Studio. Managed code
(`FruityLink.FlStudio`'s `INativeFlControl` implementation) never touches FL memory directly — it sends
text commands to the bridge, and the bridge executes them on FL's own main thread. Plugins don't use this
directly (they use [`INativeFlControl`](fl-control-api.md)); this page explains what's underneath.

Source: [`native/bridge`](../native/bridge) (`dllmain.cpp`, `sigscan.h`/`sigscan.cpp`, `callthunk.asm`,
`CMakeLists.txt`, [`README.md`](../native/bridge/README.md)).

## Architecture and protocol

- **Text verbs in, text out.** The command surface is a simple request→response protocol: one text
  command (`ping`, `info`, `syms`, `fl_ready`, `peek`, `call`, `hint`, plus the typed control verbs),
  one text reply (often JSON-ish, e.g. `info` returns `{pid, bridgeBase, flEngineBase, flEngineSize}`;
  errors come back as `err:...`).
- **In-process export (production path).** The bridge exports
  `int FlBridge_Command(const char* reqUtf8, char* outBuf, int outLen)`. It returns the full response
  length (the caller resizes and retries if the buffer was too small) and writes up to `outLen` bytes.
  This is what the managed host calls in-process; it uses the *same* command handler as the pipe, so
  every typed control op works identically with the named pipe simply bypassed.
- **Dev named pipe (`\\.\pipe\FruityLinkBridge`).** A named-pipe server exposing the same command handler
  exists **only in debug builds** (compiled in when the `_DEBUG` config or the `FRUITYLINK_DEBUG` CMake
  option is set). A plain Release/production build ships **no** pipe. It's for dev tooling (attach,
  inspect memory, drive commands) — not the plugin path.
- **Main-thread call marshaling.** Most FL engine routines are not thread-safe, so the bridge runs calls
  on FL's main/UI thread: it subclasses FL's main top-level window and dispatches a private
  `SendMessage`, whose window proc runs synchronously on the owning (main) thread. Every call is
  SEH-guarded so a fault can't take FL down through the bridge.
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

The `syms` diagnostic reports the detected FL version plus every symbol that did **not** resolve on this
build (`{"ver":N,"ok":N,"fail":M,"unresolved":[...]}`). The managed side surfaces this via
[`IFlSymbolResolution`](fl-control-api.md#version-gating-iflsymbolresolution) so tools depending on an
unresolved symbol are hidden rather than fired.

## FL version support

Signature resolution is currently validated against the **FL Studio 2025 / 2026** line (2025 = 25.2.5,
2026 = 26.1.0). On an unrecognized version the bridge still runs, but any symbol it cannot uniquely
resolve is refused — so the surface degrades safely (some tools hidden) rather than misbehaving. When FL
ships a new build, new signatures/fallbacks are added to the sigscan table; the wire protocol and the
managed contract don't change.

## Building

x64 (must match FL Studio), MSVC via CMake:

```sh
cmake -S sdk/native/bridge -B sdk/native/bridge/build -A x64
cmake --build sdk/native/bridge/build --config Release
# -> sdk/native/bridge/build/Release/FlBridge.dll
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
No address ever crosses the plugin trust boundary — so a third-party plugin cannot reach FL's internals
(or its DRM), only the vetted, typed operations. This is the same boundary described in
[the FL control API](fl-control-api.md#error-behavior-and-the-trust-boundary).

## Clean-unload contract

The bridge registers no permanent hooks/timers/callbacks with FL and never pins its own module. On
teardown (`BridgeStop()` / detach) it reverts the main-window subclass and any temporary hooks on FL's
main thread before unload, so load/unload stays clean.
