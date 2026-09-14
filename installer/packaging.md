# Installer packaging

`package.ps1` produces both `fl-automate-installer-v<version>.zip` and
`fruitylink-installer-v<version>.zip`. Both include the optional FLMCP component and its
private Python runtime. The community ZIP excludes `payload/FruityLink/plugins/fl-agent/`.

## Build the distribution

From the repository root:

```powershell
cmake -S sdk/native/bridge -B sdk/native/bridge/build -A x64 -DFRUITYLINK_DEBUG=OFF
cmake --build sdk/native/bridge/build --config Release
ctest --test-dir sdk/native/bridge/build -C Release --output-on-failure
./installer/package.ps1
```

The native build must finish before packaging: the installer build copies the existing
`sdk/native/bridge/build/Release/FlBridge.dll` and does not compile C++ itself. Verify the
archived bridge hash matches the build used in the release's FL launch smoke test.

Packaging refreshes the framework/host, builds the sibling `Fl-MCP` checkout against this
repository's SDK, stages the Python runtime, and then publishes the installer. It uses
`sdk/python/dist/fruitylink_python-0.2.0-py3-none-any.whl`; if missing, it builds the wheel
with the SDK's locked `uv` environment. Build-time requirements are .NET build tools,
PowerShell 7 for the MCP source build, the separate MCP checkout, and `uv` when rebuilding
the wheel. A reviewed prebuilt MCP distribution can be staged with PowerShell 5.1 or 7:

```powershell
./installer/package.ps1 -McpDistributionPath C:\artifacts\fl-mcp-reviewed
```

`-McpSourceRoot` and `-McpPythonWheel` select other source/wheel paths. SDK 0.2.0 is required
by the plugin, scripting dependency, wheel, and staged shared host contracts. The installer
product version remains separate, from the repository-root `VERSION` file.
The shared host includes `FruityLink.Scripting.dll` beside `FruityLink.Host.dll`; staging
and publish validation require SDK 0.2.0 and the exact same scripting assembly in the MCP
plugin closure. Sharing that assembly keeps the embedded interpreter alive across plugin reloads.
The final payload also requires byte-identical Core, plugin abstractions, and Avalonia hosting
assemblies in the shared host and each bundled UI plugin. This prevents an older host from
silently receiving a plugin compiled against newer window-hosting APIs, even when assembly
version numbers match. Run `./installer/tests/shared-ui-payload.tests.ps1` to verify that gate.

## Pinned private Python

[python-runtime.json](python-runtime.json) pins the official **CPython 3.14.6 Windows x64
embeddable package**, its HTTPS URL, byte length, and SHA-256. The hash is copied from the
[official Python 3.14.6 release](https://www.python.org/downloads/release/python-3146/):

```text
URL: https://www.python.org/ftp/python/3.14.6/python-3.14.6-embed-amd64.zip
SHA256: df901e84a896ff1ee720ad03377e0c8d8c2244fda79808aeeaff6316df1cb75c
Bytes: 12570832
```

The first packaging run downloads that exact artifact and checks both length and hash
before extraction. Subsequent runs revalidate the cached ZIP and work offline. The default
cache is `installer/artifacts/python-runtime-cache`, outside the shipped payload. Set
`-McpPythonRuntimeCacheDirectory` on `package.ps1`, or `-PythonRuntimeCacheDirectory` on
`stage-mcp.ps1`, to use another cache. To prepare an offline build machine, copy the verified
ZIP into that cache under its pinned filename. A damaged cache is rejected, not silently
accepted or overwritten. Packaging never queries a dynamically selected latest release.

All files from the official archive are retained, including `LICENSE.txt` with Python's
license and bundled third-party notices. Only `python314._pth` is customized:

```text
python314.zip
.
../fruitylink_python-0.2.0-py3-none-any.whl
```

This follows Python's [embedding guidance](https://docs.python.org/3.14/using/windows.html#the-embeddable-package).
Paths resolve relative to the private runtime. The standard library and native modules stay
beside `python.exe`; the pure-Python SDK imports directly from its wheel. There is no `import
site`, pip invocation, venv, user-Python requirement, or PATH change. External `PYTHONPATH`,
`PYTHONHOME`, user site-packages, and Python registry settings do not select its imports.
FLMCP loads the private CPython DLL inside the connected FL Studio process and executes user
scripts there. Scripts share FL Studio's process and OS permissions; this is not a security
sandbox. The separate python.exe launch is only an installation import check. Cancellation
does not automatically roll back completed FL edits or safely interrupt arbitrary native extensions.

## Installed layout and client selection

```text
payload/optional-plugins/fl-mcp/
  plugin/                         # distribution/plugin/fl-mcp contents
  companion/
    server/
    python/
      fruitylink_python-0.2.0-py3-none-any.whl
      runtime/                    # official CPython archive, with configured ._pth
      RUNTIME-PROVENANCE.json      # source pin, exact wheel hash, deliberate modification
      RUNTIME.md
    SOURCE-SHA256SUMS.json         # unchanged original MCP distribution manifest
    SOURCE-README.md               # unchanged standalone-source instructions
    INSTALLER-SETUP.md             # installed client selection/setup flow
    SHA256SUMS.json                # regenerated for the installed companion
    ...                           # original MCP docs, examples, licenses and notices
  SHA256SUMS.json                  # regenerated for the whole optional component
```

Installation maps `plugin/` to `FruityLink/plugins/fl-mcp/` and `companion/` to
`FruityLink/tools/fl-mcp/`. The selected component therefore supplies
`FruityLink/tools/fl-mcp/python/runtime/`. The installer configures selected clients with
`FL_MCP_PYTHON_RUNTIME` pointing to this directory and `FL_MCP_PYTHON_PATH` pointing to the
installed SDK wheel. The MCP server passes both into the FL process for embedded execution.
There is no user Python installation or pip step. The installed README points to
`INSTALLER-SETUP.md`; standalone deployment instructions remain in `SOURCE-README.md`.

In the GUI, keep **FLMCP** selected and check the AI apps to connect. Only checked clients
are configured. Supported choices include Codex, Claude Desktop (including discovered MSIX
installations), Claude Code, Cursor, VS Code's default profile, Gemini CLI, Windsurf,
OpenCode, and an export for another client. Template and workspace paths can be customized.
Client setup also enables FLMCP in the user's persisted plugin state. Restart selected clients
and FL Studio after installing updated files. For an already running instance, enable FLMCP
in its Plugins menu; persisted state alone applies to subsequent launches. Use `fl_instances`
and `fl_attach` to select a running session, or close other FL sessions before `fl_project_start`
to create a new managed project. Client approval settings remain under the user's control.

`fl_detach` and AI-client exit leave an attached FL process open. Attached saves create fresh
workspace copies; project close and rendering remain managed-session operations. Untitled
projects can be attached, with best-effort project identity checks and reattachment required
after detected project switches. Attach does not require the starting template, while installer
configuration keeps validating it for the managed-launch workflow.

Before changing files, install and uninstall stop MCP server processes only when their full
executable path belongs to the selected FL installation. Servers for other installations and
child processes are left alone. If a selected server cannot exit or its AI client keeps
restarting it, file changes are aborted; close that client and retry. Dry-run does not stop
processes. Updating an installation still requires FL Studio to be closed and restarted.

The same flow is available directly in the installer CLI:

```powershell
.\FruityLink.Installer.exe --list-mcp-clients
.\FruityLink.Installer.exe --install --fl-path 'C:\Program Files\Image-Line\FL Studio 2026' --mcp-clients codex,claude-code
.\FruityLink.Installer.exe --configure-mcp --fl-path 'C:\Program Files\Image-Line\FL Studio 2026' --mcp-clients codex --dry-run
```

`--configure-mcp` connects clients to an already installed component; omit `--dry-run` to
apply the reviewed configuration. `--mcp-template` chooses a saved FLP (default: the selected
FL installation's `Data/Templates/Empty/Empty.flp`), and `--mcp-workspace` chooses the output
workspace (default: the original user's local application data under `FlMcp/Projects`).
`--mcp-python-runtime` optionally chooses a compatible private CPython 3.14.6 x64 directory.
Updating existing matching entries removes the obsolete `FL_MCP_PYTHON` executable setting.
`--without-mcp` excludes the MCP component. The framework's embedded Python backend stays
installed for other plugins. There is no separate end-user setup script.

## Python IDE plugin

The installer also offers **FL Python IDE**, a separate MIT-licensed Avalonia editor plugin.
Both local plugins are selected by default and can be deselected independently. Use
`--without-python-ide` to omit the editor in unattended installs. After installation, enable
**Tools > FL Plugins > FL Python IDE** inside FL Studio. No MCP client setup is needed.

`package.ps1` runs `stage-python-ide.ps1` after validating the existing Python bundle. It
publishes the editor with its AvaloniaEdit/Skia dependencies to
`payload/optional-plugins/fl-python-ide/`, installed as `FruityLink/plugins/fl-python-ide/`.
The verified CPython runtime and SDK wheel are also staged under `payload/FruityLink/python/`,
part of the base framework independently of either plugin selection. Existing MCP paths are
preserved for compatibility. The interpreter remains process-owned by `FruityLink.Scripting`.
These developer packaging scripts are not part of the end-user setup flow.

## Serum support extension

The MIT-licensed **Serum support** Python extension is also selected by default and can be
deselected independently with `--without-serum-support`. Serum itself, commercial preset banks,
and plugin binaries are never bundled. `stage-serum-support.ps1` builds and verifies the separate
`fruitylink-serum` wheel, then stages it as
`payload/optional-plugins/serum-support/fruitylink_serum.whl`. The installer places that directory
at `FruityLink/python/extensions/serum-support/`; the stable installed filename makes upgrades
replace the previous wheel instead of leaving multiple versioned wheels for the runtime to find.

## Verification and licenses

`stage-mcp.ps1` verifies source manifest coverage/hashes, required files, .NET dependency
closure, SDK assembly versions, wheel metadata, and the verbatim PolyForm license. It rejects
traversal, duplicate manifest entries, unlisted files, reparse points, and overlapping roots.
Staging omits source PDBs and preserves the source manifest. New manifests cover the retained
source files, private Python runtime, and generated provenance. The main packaging script
checks the published bundle and confirms both ZIPs contain Python, the SDK wheel, plugin,
companion, provenance, and license.

Read-only source validation, without building, downloading, or staging:

```powershell
./installer/stage-mcp.ps1 -DistributionPath C:\artifacts\fl-mcp-reviewed -HostDirectory C:\artifacts\host-0.2.0 -ValidateOnly
```

Scratch tests, run under both PowerShell versions:

```powershell
pwsh -NoProfile -File installer/tests/stage-mcp.tests.ps1 -DistributionPath C:\artifacts\fl-mcp-reviewed -HostDirectory C:\artifacts\host-0.2.0
powershell.exe -NoProfile -ExecutionPolicy Bypass -File installer/tests/stage-mcp.tests.ps1 -DistributionPath C:\artifacts\fl-mcp-reviewed -HostDirectory C:\artifacts\host-0.2.0
```

These tests launch only the bundled interpreter to import `fruitylink.embedding`, check SDK and
Python versions, and load `ctypes`, `asyncio`, `ssl`, and `sqlite3`. They verify isolated imports
with deliberately invalid external Python environment paths, reject altered runtime/cache
bytes and import configuration, and rerun staging with network downloads blocked. They do
not install into FL, change client settings, or connect to a DAW.

FLMCP retains PolyForm Noncommercial 1.0.0. The SDK and Python library retain MIT. CPython and
all other bundled dependencies retain their licenses/notices. The installer also ships the
complete Tomlyn 2.10.1 BSD-2-Clause notice under `licenses/`, with a top-level dependency notice.
The companion still needs its
.NET 10 runtime and a licensed FL installation; bundling Python does not establish live
FL authoring/rendering support. Follow the included MCP live-verification checklist.
