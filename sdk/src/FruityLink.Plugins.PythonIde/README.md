# FL Python IDE

A Python script editor inside the FruityLink plugin framework. Plugin ID: `fl-python-ide`. It uses the same in-process Python runtime and typed FL API as FLMCP, and works without an MCP client.

Install the framework with **FL Python IDE** selected, then enable **Tools > FL Plugins > FL Python IDE** in FL Studio. Installer selection installs the plugin; it does not enable it. Once enabled, use **FL Python IDE** in the View menu or the **PY** toolbar toggle to show or hide it. The editor requests its own FL frame, independent of other plugins; an unsupported native host opens the editor in an external window. Closing the window hides it. Disabling the plugin drains active Python work and detaches its native frame before closing the editor. A failed detach preserves the window for retry.

The starter script reads this project's tempo and identity. Press **F5** or **Run all** to execute it. `fl` is already connected to the current FL project. Use `print(...)` for Output; assign `result = ...` for the Result tab. Errors and Traceback remain available in separate tabs. **Browse API** filters the generated operation catalogue, including argument and return schemas.

```python
print(fl.transport.tempo)
result = fl.project.info

# Changes take effect immediately:
# fl.transport.tempo = 128
```

| Action | Shortcut |
| --- | --- |
| New / Open / Save Python script | Ctrl+N / Ctrl+O / Ctrl+S |
| Run entire script | F5 or Ctrl+Enter |
| Run selected text | Shift+Enter |
| Stop cooperatively | Shift+F5 |

New and Open ask before replacing unsaved edits. Save writes UTF-8 `.py` files atomically; cancelling a save keeps your work. Editing and file commands pause during file dialogs/I/O to prevent overlapping replacements. Drafts save after a short editing pause and on hide/disable under `%LOCALAPPDATA%/FruityLink/python-ide/drafts`. The next editor opens the most recent readable draft. Each window has its own draft file, so concurrent FL instances never overwrite one another's recovery file. Recovery retains older files; remove unwanted drafts from that directory when no editor is running.

Each run starts with fresh script globals, while imported modules and the shared interpreter remain loaded. Selection runs exactly the selected text, which must be valid Python. Runs are serialized; Stop requests cancellation and waits for actual Python/native FL work to finish. The timeout (1–300 seconds, default 60) is cooperative: a blocking native extension or active FL operation may delay it. No process is killed or interpreter finalized. The UI does not provide live streaming; bounded stdout/stderr are shown when execution finishes, with truncation indicated. Results are limited to 512 KiB; write large artifacts to files.

Scripts run with FL Studio's privileges and may modify files and the active project. Save your FL project before experimenting; completed operations are not automatically rolled back. The typed SDK surface excludes raw memory and arbitrary native calls. This is an editor for trusted local scripts, not a sandbox, debugger, language server, package manager, or full VS Code replacement.

The framework installer supplies the embedded CPython runtime and Python SDK wheel. No system Python, pip, or virtual environment is required. Keep the full publish directory together, including native Skia/HarfBuzz assets. Do not copy only the plugin DLL.

Historical hosting changes introduced shared Avalonia and the initial exclusive window host in installer **0.1.13**, corrected native geometry in **0.1.15**, and addressed repaint behavior through **0.1.17**. Independent plugin frames have been created in live FL **26.1.3.5570**, while title-bar visibility and drag stability still require live confirmation. Current hosting has not been live-tested on FL **25.2.5.5319**. See the [consumer hosting contract](../../docs/plugin-window-consumers.md) for the supported behavior and remaining limits.

Python starts lazily on the first Run. The runtime locator reuses an interpreter already loaded by the framework. Before first initialization it prefers a complete legacy `tools/fl-mcp/python` bundle when present, otherwise the common `host/python` bundle. Explicit runtime overrides must match the process's initialized runtime; change conflicting configuration and restart FL Studio. There is one resident interpreter, and no external Python execution process.

Build a standalone Windows x64 plugin ZIP from this SDK checkout:

```powershell
pwsh -File src/FruityLink.Plugins.PythonIde/pack.ps1
```

The archive goes to `artifacts/python-ide` under the repository root; it does not install or enable anything. See `THIRD-PARTY-NOTICES.md` for the included toolkit licenses.

UI/model tests run without FL Studio or Python. To render a preview while testing:

```powershell
$env:FRUITYLINK_IDE_PREVIEW = Join-Path $PWD 'artifacts/python-ide-preview.png'
dotnet test tests/FruityLink.Plugins.PythonIde.Ui.Tests -c Release -warnaserror
```
