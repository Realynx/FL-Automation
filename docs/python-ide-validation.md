---
search:
  boost: 0.3
---

# Python IDE validation — September 12, 2026

Framework installer **0.1.13** installed 463 files successfully into FL Studio **2026 26.1.3.5570**. Fifteen critical host, toolkit, native bridge and plugin files match their build outputs, published installer payload and installed copies. Both installer ZIPs contain the verified payloads. The SDK/plugin version is **0.2.0**; Python is private, embedded **CPython 3.14.6**.

Validation covered 258 SDK cases, 146 installer tests and 36 published CLI checks. The first SDK run passed 255 cases; three real-interpreter cases failed because `FRUITYLINK_TEST_PYTHON_RUNTIME` was missing. All three passed when rerun with the verified runtime. There were no skipped cases. Shared-host tests and an actual private-load-context render smoke covered both editor-first and FL-Agent-first startup, fresh plugin instances after unload, and current-assembly product resources.

Live checks used the installed editor in FL Studio:

- **F5** ran the starter script in FL process 57692.
- Draft recovery loaded the verification script in process 37160. **F5** changed tempo from 128 to 129 and back to 128, queried five channels, and wrote a successful result with the same process ID.
- A deliberate `ValueError` selected the Traceback output and displayed the Python error status.
- A second run entered a loop. **Shift+F5** stopped it and the editor reported that Python and FL work had finished.

The user stopped computer control with Escape before a third recovery run. That live rerun was **not verified**. FL and the verification draft remained open; the starter draft was not restored. Interpreter recovery is covered by the real-interpreter tests, separately from these live observations.

Detailed hashes, source snapshots and evidence paths are in the ignored local artifact `artifacts/python-ide-verification/report.json`. The standalone ZIP verifies all 57 entries against its own manifest. Its separately published plugin DLL differs in hash from the installed build; the report preserves both hashes and does not claim byte identity. The preview is `artifacts/python-ide-preview.png`.

The IDE has not been live-verified on FL Studio 2025. One native embedding slot is shared; another UI plugin uses its floating-window fallback. Script cancellation is cooperative and completed operations are not rolled back.
