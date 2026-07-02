# wave-verify — running-build health + clip→pattern fix confirmation (2026-06-30)

Verification of the freshly-reinstalled FruityLink build on the LIVE FL session (pid 12032,
started 17:15:35, flEngineBase 0x5E560000). flprobe = read-only pipe diagnostics. No source edits,
no inject, no New/Open/Save, no install/uninstall. The user's project was NOT touched.

Tooling note: FL64 runs at HIGH integrity, so its `\\.\pipe\FruityLinkBridge` rejects a
medium-integrity client (ACCESS_DENIED). flprobe was run once via an elevated wrapper that does
**read-only flprobe calls only** (no install/uninstall/build script). Install proxy `version.dll`
and `FruityLink\` are intact; FL stayed up throughout.

## Part A — read-only health sweep  → ALL PASS
| check | result | value |
|---|---|---|
| attach (ping/pong + info) | PASS | `[OK] attached (no injection); ping -> pong`; info pid=12032, bridgeBase=0x7FF9F04A0000, flEngineBase=0x5E560000, mainTid=20224 |
| bridge fl_ready | PASS | `1` |
| bridge settings_get | PASS | `{"debug":0,"hostFns":1}` (hostFns wired; debug window hidden by default) |
| bridge menu_contrib_list | PASS | `[{"id":"fl-agent#1","menu":"View","caption":"FL Agent","kind":"toggle","checked":true}]` — View ▸ FL Agent present + enabled |
| bridge plugins_list | PASS | `[{"id":"fl-agent","name":"FL Agent","version":"1.0.0","enabled":true,"loaded":true}]` — fl-agent loaded |
| proxy.log readiness | PASS | `FL ready after 1263ms`; `ListChannels -> 0: Sampler` (populated); no crash/fault |

Tempo-write fault: NONE. The log's `Tempo r/w -> (no project?) 'e' is an invalid start of a value`
is a benign startup self-probe (tempo read returned an `err:` string that the probe tried to
JSON-parse) — FL did not fault and reached "FL ready" normally. The readiness fix holds.

## Part B — clip→pattern fix  → LIVE write-test DEFERRED; fix DECISIVELY confirmed on the running binary
The minimal reversible LIVE write-test (create pattern K, add notes, place 1 clip, read back, cleanup)
was **deferred — needs a scratch project**, because:
- The high-level reversible commands the test requires are **not exposed over the DEBUG pipe**.
  Empirically confirmed on the running build: `create_pattern`, `list_clips`, `add_pattern_clip`
  all return `err:unknown`. The pipe surface is low-level only (peek/call/poke/key + diagnostics);
  the FL ops live in the in-process managed `FlInjectBridge` (FL Agent plugin), unreachable from flprobe.
- Authoring + deleting a clip via raw `call`/`poke` on the user's LIVE project is risky and not
  reversible-by-design — exactly the case the brief says to defer rather than risk the user's work.

Instead the fix was confirmed by reading the IL of the **installed/running** assemblies (read-only),
which is conclusive that the K→K mapping is what FL is executing right now:

- `FruityLink.Agent.dll` (built 16:44) `NativeControlPlugin.AddPatternClipAsync` MoveNext:
  calls `fl.AddPatternClipAsync`, **sub=0** → passes `pattern` straight through (the old `pattern - 1` is gone).
- `FruityLink.FlStudio.dll` (built 16:45) `FlInjectBridge.AddPatternClipAsync` MoveNext:
  calls **`ValidatePattern`**; constants 0x50005000, 0xC0, 0x50, shift 16; **sub=0** →
  `sourceID = 0x50005000 + (pattern << 16)` from the **1-based** pattern, no decrement; auto-length
  reads `patArr + pattern*0xC0 + 0x50`.
- `FlInjectBridge.ListClipsAsync` MoveNext: constant 0x50000000 with `sub`+`shr` →
  `pattern = (src - 0x50000000) >> 16`, no extra `+1` (matches source line 1015).
- Source DLLs built 16:44–16:45, AFTER the fix landed in source at 16:36; current FL session (17:15)
  loaded these post-fix DLLs.

### Decisive read-back numbers (from the running build's code)
- K → stored sourceID = `0x50005000 + (K << 16)`; e.g. K=2 → `0x50025000`, K=5 → `0x50055000`.
- engine/ListClips decode = `(sourceID - 0x50000000) >> 16` = **K**  (clean K→K, no off-by-one).
- K=1 → `0x50015000` → decodes to **1** (the old "pattern 0" symptom is fixed).
- `AddPatternClipAsync(0,…)` → `ValidatePattern(0)` THROWS `Pattern 0 is out of range (1..9999)`.

## Cleanup / footprint
No live clip/pattern was created (write-test deferred), so there are NO FL artifacts to clean up —
the user's project is untouched. Install + FL process intact (version.dll proxy present, FruityLink\
present, FL64 still running). All temp files (flprobe driver, IL inspector) are in the session
scratchpad, outside the repo and FL.

## Verdict
Part A: PASS (all checks). Part B: fix CONFIRMED on the running build via IL (K→K, K=1→1, pattern 0
throws); the live in-FL placement is DEFERRED (needs a scratch project — not doable safely over the
flprobe DEBUG pipe, which exposes no high-level clip command). Nothing failed.
