# Native window candidate validation

## Caption paint follow-up: 0.1.17

The blanket `WS_CLIPCHILDREN` modification on FL's native frame is removed. Instead, normal parent `WM_PAINT` validates only the visible, bound plugin content rectangle before forwarding the original message once. FL retains its own `GetUpdateRgn` / `BeginPaint` / buffered paint lifecycle and can paint its complete native caption strip and buttons. Supplied paint/erase DCs receive a scoped content-only exclusion; their original state is restored afterward. The separate plugin container retains its child clipping, and the queued Avalonia repaint / idempotent layout fixes remain unchanged.

All six native test groups pass, including 222 native registry checks and 106 actual-factory checks. They test full caption-strip and button pixels, protected plugin content, the original update-region lifecycle, DC state restoration, and hidden/unbound/detached cases. Normal paint tests run on a private desktop that is never displayed; they do not interact with FL or the user's desktop. These fixtures model the verified FL paint behavior rather than establishing live visual correctness.

Both 0.1.17 installer editions passed independent verification of 21 build/stage/publish comparisons and 45 critical archive entries. The new native DLL is 197,632 bytes, SHA256 `4032ECDB446205423CD5BB74A6EFFEB60AD4237D8149C991840148177001D757`. The tested managed hosting DLL is unchanged from 0.1.16. Fresh published CLI checks pass (36); unchanged managed and installer unit-test results are explicitly carried forward in `artifacts/native-window-verification-0.1.17/report.json`.

Installation succeeded with exit code 0 after FL was already closed. The installed hashes match. FL 26.1.3.5570 process 52760 created both native frames with the original host style `0x84000000` (the blanket child-clipping bit is absent). Embedded Python executed in that same process and read the unchanged saved project at 128 BPM with five channels. MCP detached and FL remains open. **Live title-bar visibility and drag stability still require user confirmation.** No computer-control interaction was attempted in this follow-up; prior screenshot errors and user interruption prevented automated visual checks. Detailed startup/backend evidence is in `artifacts/native-window-verification-0.1.17/live.json` and `live-backend.log`.

## Repaint follow-up: 0.1.16

**User-reported regression:** after this update, the entire plugin window title bar disappeared, with its invisible buttons still clickable. Exact FL binary analysis confirms that FL paints its native caption controls through the parent, so blanket child clipping suppresses that chrome. The 0.1.16 checks below prove installation, configured styles and backend operation, not correct chrome rendering.

The shared Avalonia host now coalesces layout notifications into a deferred, non-erasing whole-child invalidation. Repaint no longer uses synchronous `RedrawWindow` with background erasure during native callbacks. The position handler preserves `NOSIZE` and `NOMOVE` when the embedded child already has the correct bounds. Queued repaint is cancelled after successful close.

The native host now enables `WS_CLIPCHILDREN`, and unchanged geometry no longer triggers child/container positioning calls during moves. All six native test groups pass, including 135 registry and 106 actual-factory checks. Strict managed checks pass: Hosting 32, IDE UI 23, and IDE execution 15, including the isolated embedded-Python test host. Installer tests (146) and published-package checks (36) pass.

The shared hosting DLL is 18,432 bytes, SHA256 `79F75CA5AB6BF60EE31D6C8A07926702FD024BADFCAC09B1E74C2149D89A8F91`; the native DLL is 196,608 bytes, SHA256 `0D539627FB33A66CD6458D0BFDC9A4C1FE67BFDFE5D6D0721A47B31F528A7524`. Independent verification confirms 21 build/stage/publish mappings and 45 critical archive entries. Evidence is recorded in `artifacts/native-window-repaint-managed-tests.json` and `artifacts/native-window-verification-0.1.16/report.json`.

Installation completed with exit code 0, and both installed DLL hashes match. FL 26.1.3.5570 process 52148 successfully created both plugin frames with native host style `0x86000000`, including child clipping. An MCP call executed Python inside that same FL process and read the unchanged saved project at 128 BPM with five channels. MCP detached afterward, leaving FL running. See `artifacts/native-window-verification-0.1.16/live.json` and `live-backend.log`.

The user had already closed FL before this update, so no running project was interrupted. **Drag/flicker stability still needs visual confirmation.** Screenshot capture failed in the prior live checks (`SetIsBorderRequired`, `0x80004002`); successful startup, clipping flags and backend checks do not prove pixel behavior while dragging. FL 2025 live validation also remains pending.

## Live follow-up: 0.1.15

Installer **0.1.15** was installed successfully on FL **26.1.3.5570**. The installed native DLL matches SHA256 `CAE92E065EB1C18380D4B8EFBE060EF8724480B96A91E6986B72B0B32AA87495`. In FL process 53760, native creation logs confirm independent `TFLBaseVectorForm` hosts for FL Automate (540×800 content) and Python IDE (1120×780 content). Accessibility inspection shows both plugin surfaces inside FL's main window, and FL Automate logs successful attachment.

The correction removes the mistaken use of `TControl.Touch` at `form+0x11C` as a content-control pointer. Geometry now uses FL's verified inner client rectangle and actual caption HWND. Six native test groups pass, including 104 checks against the real window factory. Updated IDE UI tests (23), execution tests (15), installer tests (146), and published installer checks (36) also pass. Independent package verification is recorded in `artifacts/native-window-verification-0.1.15/report.json`.

Screenshot capture failed with `SetIsBorderRequired` / `0x80004002`; pointer input consequently lacked geometry. The user stopped keyboard testing with Escape before Python execution was verified in the new frame. The subsequent user report confirms improved native chrome but exposes **flickering and gray plugin content during dragging**, temporarily restored by mouse hover. Native creation is working; rendering stability is not yet established. FL 2025 live validation remains pending.

## Earlier candidate: 0.1.14

Installer **0.1.14**, SDK **0.2.0**, automated checks completed on 2026-09-12. The user subsequently installed this candidate on FL 2026. Live use exposed a native window creation failure: FL Agent fell back to an external window with `native-content-bounds-unavailable`. The installed native DLL matches the packaged SHA256 below, and the runtime scanner reports `windowEmbedding:true` for FL 26.1.3.5570. This is a content geometry failure after version detection, not a stale installation. FL 2025 live validation remains pending.

The implementation provides independent native window sessions for plugins, with asynchronous creation, visibility and teardown. It uses the verified FL base vector form with a separate content HWND. Close and maximize use native chrome; minimize and menu controls are hidden, and docking is refused. Failed teardown retains ownership for retry. See [window embedding](window-embedding.md) and [consumer integration](plugin-window-consumers.md) for the contract.

| Automated evidence | Result |
| --- | --- |
| Full SDK quality gate | 270 passed, zero warnings/errors |
| Final bounded Hosting regression run | 27 passed, replacing the earlier 26; **271 aggregate unique cases** |
| Plugin host tests, included above | 39 passed |
| Product strict build | Passed, zero warnings/errors |
| Installer tests | 146 passed |
| Published installer CLI, scratch profiles only | 36 passed |
| Native CTest | 5 groups passed; 122 registry, 95 signature, 17 Delphi and 16 response-transport checks; 3,200 concurrent dispatch calls |
| Shared UI packaging gate | 6 checks passed |
| Binary inspection | FL 25.2.5.5319: 109/109; FL 26.1.3.5570: 104/109 with the same five optional version-locked gaps |

The final native DLL is **192,512 bytes**, SHA256 `F6B8BF9D452D6789B435F2C684A796BFF5FFC6837C674836EE8153B852E5E0D3`. Nineteen critical files match across build output, staged payload and published payload, including the shared host, plugin contracts, Avalonia host/toolkit, native renderers, Python IDE and FL Agent assemblies.

| Installer archive | Bytes | SHA256 |
| --- | ---: | --- |
| `fl-automate-installer-v0.1.14.zip` | 438,084,910 | `3EF9FF8EF3CF747136BF97DB565528A2757B1543C346ABB91F057423CBF4DEBD` |
| `fruitylink-installer-v0.1.14.zip` | 294,794,796 | `417A8317B21F96F4B125493694C0961234B7CD999A116B5745533C582EACE07B` |

The commercial archive has 469 entries; 22 selected critical entries match the published files. The community archive has 305 entries; its 20 selected critical entries match, and it contains **no FL Agent payload entries**. Both include the private CPython 3.14.6 runtime and the verified SDK wheel. These comparisons do not claim that every archive entry was rehashed.

Live validation is still required for close/maximize, simultaneous SaaS and IDE windows, mixed DPI transitions, FL themes, focus and keyboard behavior, and repeated disable/re-enable. Hidden Win32 fixture tests and binary inspection do not establish these live outcomes. Computer control and installation were intentionally paused while the user worked elsewhere.

Machine-readable evidence, per-file hashes, exact log paths, inspector gaps and the scratch CLI report are recorded in the ignored local artifact `artifacts/native-window-verification/report.json`. The full SDK log is `artifacts/native-window-verification/sdk-quality.log`; the final Hosting log is `artifacts/native-window-hosting-managed-tests.log`.
