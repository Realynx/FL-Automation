# Python backend validation — 2026-09-12

The reusable library, worker, typed query interface, scripting dispatcher, and standalone
plugin live in **FL-Automation**. **Fl-MCP** consumes this SDK and retains MCP session ownership,
workspace policy, and launch/save/render orchestration. SDK/Python version is **0.2.0**;
the Python endpoint protocol remains version **1**.

## Implemented scope

- Generated Python methods for all 123 `INativeFlControl` operations, with contract parity checks.
- Nine structured queries and typed snapshots for scripts; no parsing of display text for identity.
- Object APIs for project, transport, channels, patterns/notes, playlist/clips, arrangements,
  mixer/effects, plugins/parameters, and automation.
- Authenticated per-user named pipes with process/instance verification, bounded framing,
  connection retries, and strict argument/record validation.
- Serialized calls and non-transactional batches with partial results; cancellation drains a
  started native operation before releasing its gate.
- External Python execution with captured/bounded output and a dedicated response file.
- Shared memory reads now respect the native 4096-byte limit. Long project paths are preserved
  for identity and the untitled-project save guard.

## Completed checks

| Check | Result |
| --- | --- |
| SDK strict Release rebuild and tests | 167 passed, zero warnings/errors |
| Existing application regression suite | 500 passed |
| Native CTest suite, including managed symbol coverage | 3 checks passed |
| Python tests on 3.12.13 | 67 passed |
| Installed wheel tests on 3.11.15 and 3.13.14 | 67 passed on each |
| Python generation/version parity, Ruff, strict mypy | Passed; 39 files type checked |
| Real C# endpoint → Python SDK interoperability | Passed, including authentication and stale identity rejection |
| Wheel/source distribution and standalone plugin ZIP | Built and inspected |
| SDK Core, Abstractions, Scripting NuGet packages | Packed locally as 0.2.0 |
| Fl-MCP source and locked local-package Release tests | 58 passed in each mode, zero warnings |
| Packaged MCP server with packaged Python wheel | 10 integration tests passed; 60 manifest hashes verified |

Repeat the checks from the FL-Automation root:

```powershell
./scripts/codefactor-check.ps1
uv run --directory python python tools/check.py
ctest --test-dir native/bridge/build -C Release --output-on-failure
```

The managed gate includes Python interoperability; Python 3.11+ must be on PATH or selected
with `FRUITYLINK_TEST_PYTHON`. The native check assumes the native bridge has been built first.
CI also tests the Python package on 3.11 and 3.13.

## Deployment boundary

No production files were replaced, no FL Studio project was opened or edited, and no package
was published. The new Python plugin requires the matching **0.2.0 host**, because the host
supplies shared Core contract types. Live FL Studio 2025/2026 authoring and rendering with this
new Python path still need verification on a disposable project after installing the matching
host/plugin. Existing native signature evidence is recorded separately in the native docs.

This API exposes the existing native control surface and its structured inspection extensions.
It is not yet a complete equivalent of Blender's entire `bpy` API. Worker isolation does not
make FL a service-mode renderer, sandbox arbitrary Python, or roll back completed edits.
