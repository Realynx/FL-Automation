---
search:
  boost: 0.3
---

# SDK reliability review — September 2026

This review covers the open-source SDK, native bridge, plugin lifecycle, and UI hosting. The application,
websites, analytics, and AI gateway are separate consumers. Existing working-tree edits were preserved.

## Changes and evidence

| Area | Defect addressed | Verification |
| --- | --- | --- |
| Version resolution | Legacy native and managed address calculations could bypass version-aware resolution. Major-only detection trusted unverified layouts. | Native scanner regressions and read-only inspection of installed PE files. |
| Native responses | Growing a response buffer repeated the command, potentially repeating mutations or draining queues twice. | Owned-response API, legacy compatibility tests, and replies over 70 KiB. |
| Plugin activation | Failed activation and prewarm could retain resources and contributions; retry could reuse a failed instance. | Activation, cleanup, retry, and disposal tests using isolated fixture assemblies. |
| Reload | Dependency edits targeted the dependency DLL, busy output could be loaded prematurely, and invalid replacement bytes stopped working plugins. | Package-target mapping, stable-file handling, and transactional replacement tests. |
| Multiple FL instances | Starting a host removed another process's shadow files. Module guards could inspect another running FL version. | Per-load shadow cleanup tests; host-process-first module discovery. |
| Shared native scratch memory | Concurrent operations could overwrite names/output parameters; save preparation cleared its path; output slots extended beyond the buffer. | Interleaving, cancellation, save-path integrity, and bounds regression tests. |
| Window hosting | Repeated embedding lost original styles, fallback windows stayed offscreen, UI startup raced, and synchronous menu calls could block FL's message loop. | Hidden Win32 window and UI dispatcher tests; native callback moved off FL's main thread. |

## Repeatable checks

From the SDK directory:

```powershell
./scripts/codefactor-check.ps1
cmake -S native/bridge -B native/bridge/build -A x64
cmake --build native/bridge/build --config Release
ctest --test-dir native/bridge/build -C Release --output-on-failure
```

The initial managed build produced 63 compiler warnings. The checked-in gate rebuilds with warnings
treated as errors, C# cyclomatic complexity limited to 15, and duplicate-using diagnostics enabled.
The native bridge is built and regression-tested separately; its large dispatch implementation still
needs a separate complexity review. The C# analyzer does not measure C++.

The final managed gate passed with zero warnings/errors and 55 SDK tests (21 bridge/scratch,
16 lifecycle, 18 hosting). Release and Debug native builds passed both CTest suites, containing
64 assertions. All four public SDK NuGet packages also passed a local packaging check.

The parent application/installer regression run passed 500 tests with payload staging disabled:

```powershell
dotnet test FruityLink.slnx -c Release -p:HasSourceTree=false --blame-hang-timeout 60s
```

No installed FL binaries or production payloads were replaced during this review.

Cancellation and timeout bound the caller's wait; an already-dispatched native operation can still
finish later. In-process scratch leases remain held until that actual completion. Another scratch
operation fails after a two-second busy wait instead of overwriting memory still in use. This tracking
covers the production in-process transport; the legacy debug pipe cannot coordinate independent
managed processes.

## Compatibility limits and live checks

The subsequent [version analysis](fl-version-analysis-2026-09-12.md) expanded the catalog to cover
previously omitted consumers: 107/107 symbols resolve on 25.2.5.5319 and 102/107 on 26.1.3.5570.
The current 2026 class reference now resolves, but its full window layout remains unverified, so it
uses an external window. The recorded 26.1.0.5530 build was not installed for this inspection. See [native version support](native-bridge.md#fl-version-support).

Before distributing the bridge, use disposable projects on each exact supported build to check:

1. Enable, disable, re-enable, and hot-reload a plugin while menus remain responsive.
2. Rename channels/patterns/arrangements concurrently and confirm each resulting name.
3. Add notes and clips, seek, resize clips, edit automation, then audition playback.
4. Save and reopen a copy and confirm notes, clip lengths, names, and automation persist.
5. Exercise embedding or external-window fallback, resizing, closing, and host shutdown.

Static symbol matches and isolated tests do not prove live FL object-layout compatibility. The separate
MCP plugin's unattended launch and render flow also requires a live FL test before being described as
verified end to end.


## Follow-up: version-scanner injection and Ghidra verification

The bridge now injects one family-specific scanner, reports completed failures distinctly from
startup, and supplies exact-build mixer layouts through the existing managed abstraction. Ghidra
found changes to the mixer track size, sends, and FX array; all those fields now come from the
profile. The managed mixer validates indices and layout bounds before writing. Six previously
omitted managed symbols were recovered in both binaries, and five unsupported legacy UI/loading
aliases are now explicitly reported. Native readers use a fully published symbol snapshot; static
inspection copies immutable definitions. Startup diagnostic probes no longer change project tempo.

The follow-up strict SDK gate passes **119 tests** with zero warnings/errors. Native Release and
Debug each pass three CTest checks, including catalog coverage, with **111 native assertions**.
All **500 parent consumer/installer tests** pass with payload staging disabled. All **38 MCP tests**
also pass against the local SDK source in Release; the MCP README source-build command now keeps
its lock files separate and preserves Release configuration for external project references. The evidence is static and isolated; live FL
editing and MCP rendering remain to be exercised with disposable projects.

## Follow-up: Sampler parameter diagnostics

During composition on FL 26.1.3.5570, a loaded, playable Sampler channel had no
hosted-plugin parameter interface. Existing wrapper bindings support native plugins
such as 3xOsc and hosted VST plugins; they do not establish a verified Sampler envelope
interface. Parameter queries and writes now explain this capability boundary, and the
legacy text list uses the same diagnostic. They do not substitute channel controls for
plugin parameter indices or call an unverified native function. Regression cases cover
missing interfaces, refusal before parameter calls/writes, distinct empty-FX diagnostics,
and continued access to channel volume.

The same project had 16 mixer inserts, plus Master and Current. Its valid range `0..17`
was correct. A regression covers the actual 18-track boundary and rejects out-of-range
routing before any mutation.

An independent code review noted that the older channel volume/pan/pitch/mute/routing
helpers in `FlInjectBridge.Params.cs` still derive record-event IDs from the channel
index. Their existing comment limits that assumption to unreordered projects. The
plugin-parameter resolver instead reads the channel's stored record-event ID. This is
an **unverified follow-up for reordered/deleted channels**: verify event identity in both
supported FL versions before changing those write paths. No such change was made here.

## Follow-up: composition bugs and live verification, 0.1.19

Creating and rendering Glass Satellites exposed invalid timeline object indirection,
version-specific marker layout, unbounded parameter readback, and misleading Sampler
parameter errors. The framework now resolves the actual arrangement object, uses
verified marker layouts for FL 25.2.5.5319 and 26.1.3.5570, and supplies the complete
loop-call argument list. Python parameter discovery supports bounded pages and lazy
iteration; the complete-list method remains available. Sampler diagnostics describe
its unsupported hosted-plugin parameter interface without issuing speculative writes.

Live playback then exposed a separate reporting bug: GetSongState and DiagTransport
read the song-position slider domain as the playback range. Two signature-resolved
native globals now supply the actual transport range, preserving the existing inclusive
text display. The native end is exclusive. Missing or invalid range data is reported as
unknown rather than replaced with slider bounds.

The installed **0.1.19** package passed live checks on **FL 26.1.3.5570** through the
installed MCP server and embedded Python:

- Unicode marker creation/readback and persistence after saving and reopening.
- A loop at ticks `[384,1152)` reported as `playRange=[384..1151]`; playback crossed
  the end and wrapped to tick 459. Clearing twice restored `0..1535` idempotently.
- Serum 2 discovery across 4,240 slots using 16-slot pages and lazy iteration; a
  normalized Main Vol write read back as 40%. Empty filtered pages retained their cursor.
- Sampler parameter query/write refusal with the shared diagnostic, with channel
  volume unchanged. Sampler envelope editing is still unsupported.
- A four-bar, 120 BPM project saved, rendered through FL to an 8-second stereo WAV,
  and reopened with all 16 notes and both markers intact. Peak was -10.34 dBFS with
  no clipped or nonfinite samples. This was a technical render check, not an audition.

The strict managed build had zero warnings/errors; all **325 SDK tests**, **6 native
CTest groups**, and **36 published-package checks** passed. The initial test invocation
omitted FRUITYLINK_TEST_PYTHON_RUNTIME; rerunning tests with the verified bundled runtime
passed, including the real embedded-interpreter cases. Both installer editions and
installed critical DLLs/wheel were hash-checked against their build outputs. The Python
paging change separately passed 87 tests, Ruff, and strict mypy before packaging.

FL 2025 has exact-binary signature/layout analysis and isolated regression coverage;
these latest changes were live-tested on FL 2026 only. Earlier historical validation
limits above describe their respective milestones. Raw live and build receipts are in
`sdk/artifacts/live-regression-0.1.18` and `sdk/artifacts/live-regression-0.1.19` in the
parent workspace (generated artifacts, not a publication claim). No commits or pushes
were made by this follow-up.

### Glass Satellites revision: parameter display completion boundary

The 120-bar revision on installed 0.1.19 / FL 26.1.3.5570 exercised 25 clip moves,
66 new patterns, replacement of one existing pattern, targeted note-length edits,
and 13 native section markers. All 5,341 notes and all clip placements matched the
revision plan after saving and reopening; the other 77 original patterns retained
their exact note data. The transport reported the full range `[0..46079]`.

An immediate Serum 2 parameter query after a successful setter sometimes displayed
the previous value (for example, Filter 1 On reported Off). Later read-only queries
reported the requested raw normalized values and the updated displays, without
repeating any writes. The saved/reopened patch also retained those values. There is
no Python parameter cache: each page queries native state. Setter completion means
the FL command handler returned; it does not acknowledge completion of any deferred
plugin update or display refresh. The immediate receipt recorded requested values,
not observed raw values, so it cannot distinguish stale controller state from stale
display text. This is an observed completion boundary, not a confirmed setter bug.

For verification, query only the relevant parameter IDs after yielding on the Python
execution thread. If convergence matters, use a bounded read-only polling loop and
record raw values, display text, and elapsed time. Do not replay a successful write
because its first display read is stale, and do not sleep or pump messages on FL's UI
thread. A framework-wide synchronization change would require stronger evidence.

Receipts and the reproducible score are under
`sdk/artifacts/glass-satellites-v2-20260912` in the parent workspace. No framework
binary change or installer update was needed for these checks.

The full revised song rendered through MCP to 192 seconds of stereo 48 kHz float32
audio, with no full-scale or nonfinite samples (sample peak -1.975 dBFS). The first
two build bars measured 9.4 dB lower after a 2 kHz high-pass than the original render.
These are technical measurements, not an audition.

Attaching immediately during a subsequent ordinary FL startup captured the correct
project path with an empty title. Once loading populated the title, the identity
guard refused the next read-only Python invocation. Explicit detach/reattach after
loading restored access, and final state verification passed. No edits were lost or
replayed. This exposes a startup attachment readiness edge case; preserve the
conservative project-change guard when considering a readiness improvement.
