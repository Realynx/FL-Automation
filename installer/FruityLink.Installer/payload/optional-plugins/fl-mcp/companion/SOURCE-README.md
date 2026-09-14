# FL MCP

Control FL Studio through MCP using the public [FruityLink plugin SDK](https://github.com/Realynx/FL-Automation). A small plugin runs inside FL Studio without its own UI. A separate stdio MCP server can attach to your current project or manage a disposable project, with 27 tools for Python execution, API discovery, music authoring, snapshots, and WAV rendering.

The reusable Python library lives in the **FruityLink SDK repository**, under `python/`. Python is embedded inside FL Studio's process and calls the shared `FruityLink.Scripting` dispatcher directly. There is no separate Python worker or Python-to-FL pipe. The existing 21 tools remain compatible facades over that dispatcher. Session/process ownership, workspace policy, and command-line rendering belong to this MCP adapter.

This is an initial implementation, with automated protocol and orchestration tests. Managed launch, same-process Python, channel inspection, snapshot save/close, and attachment to saved and untitled projects were live-verified on FL Studio **26.1.3.5570** on September 12, 2026. Full musical authoring and WAV rendering remain unverified. Exact FL build support depends on the installed FruityLink bridge; do not infer support for every build from the FL Studio year.

Licensed under **[PolyForm Noncommercial 1.0.0](LICENSE)**. This is source-available software with noncommercial restrictions; it is not described as OSI open source. The license is the verbatim [official plaintext](https://polyformproject.org/licenses/noncommercial/1.0.0.txt). Third-party software retains its own terms.

## Requirements

- Windows x64 with a licensed FL Studio installation and FruityLink installed for that exact executable/build.
- A normal logged-in Windows desktop session. "Headless" here means no FL MCP plugin window and unattended orchestration where FL permits it. FL remains a desktop application. Session 0, Windows services, containers, Linux, and a guaranteed invisible FL host are not supported.
- .NET 10 runtime for the companion; FruityLink's .NET 9 host runtime for the plugin. Building requires the .NET 10 SDK and the .NET 9 runtime for tests/host use.
- The private Windows x64 CPython 3.14.6 runtime and SDK `fruitylink-python` 0.2.0 wheel supplied by the FruityLink installer. No system Python, virtual environment, pip installation, or dependency download is needed when using that offline installer. A matching FruityLink 0.2.0 host with embedded-Python support is required.
- A saved `.flp` template prepared with licensed instruments and resolvable samples. Start with a minimal empty template, choose suitable render settings in FL once, and save it. No FL project, instrument, sample, or license is bundled.

## Build and install

The new SDK 0.2.0 dependencies are prepared locally and are **not assumed published** to NuGet or PyPI. Build against an explicit SDK checkout containing `src/FruityLink.Scripting` and `python`:

```powershell
./scripts/codefactor-check.ps1 -FruityLinkSdkRoot C:\source\FL-Automation
uv run --directory C:\source\FL-Automation\python --locked python -m build
./scripts/package.ps1 -FruityLinkSdkRoot C:\source\FL-Automation -PythonWheel C:\source\FL-Automation\python\dist\fruitylink_python-0.2.0-py3-none-any.whl
```

The raw developer package contains separate `server`, `plugin/fl-mcp`, and `python` directories. The FruityLink installer adds the pinned official CPython runtime and installs the plugin, companion, wheel, and runtime together. Use that installer for an offline end-user installation.

For manual developer deployment, copy `plugin/fl-mcp` to `<FL>/FruityLink/plugins/fl-mcp` and the companion directories to `<FL>/FruityLink/tools/fl-mcp`. Place the official Windows x64 CPython 3.14.6 embeddable payload under `python/runtime`; the SDK loads its private `python314.dll`. Keep the wheel alongside that runtime directory. The raw developer package does not download or supply this runtime. Keep `FlMcp.Plugin.dll`, its `.deps.json`, and `FlMcp.Protocol.dll` together. The matching FruityLink host shares its `FruityLink.Scripting` assembly to keep one interpreter owner across plugin reloads. Do not copy the stdio server into the host plugins folder.

Selecting MCP clients in the FruityLink installer enables **FL MCP** for that user and writes the selected client settings. For manual deployment, enable FL MCP once in FruityLink's Plugins menu. FruityLink persists enabled IDs in `%LocalAppData%\FruityLink\plugins.json`. An ordinary FL session then advertises itself for explicit attachment. If FL was already running when the installer updated that file, enable FL MCP in its Plugins menu or restart FL. Close existing FL sessions before starting a disposable managed project. Building this repository alone changes neither your FL setup nor client settings.

FL MCP hosts the shared scripting dispatcher itself. The SDK's separate **Python Scripting** plugin is for standalone Python connections and is not required for MCP-managed scripts.

For development against an SDK checkout instead of the published package:

```powershell
dotnet build FlMcp.slnx -c Release -p:FruityLinkSdkRoot=C:\source\FL-Automation -p:NuGetLockFilePath=obj/source-sdk.packages.lock.json -p:ShouldUnsetParentConfigurationAndPlatform=false
```

`FruityLinkSdkRoot` must point to the SDK repository root containing `src/FruityLink.Plugins.Abstractions`. The source-build lock file stays under each project's ignored `obj` directory, preserving the published-package locks. The configuration flag keeps external SDK project references in the requested build configuration.

The tracked NuGet locks currently describe the exact locally prepared SDK 0.2.0 packages. Before enabling a public package-based build or release, regenerate and verify these locks against the actual published SDK artifacts; a later package build may have different content hashes. Source-checkout builds use their separate `obj` locks throughout.

## Connect an MCP client

Use [examples/mcp-settings.json](examples/mcp-settings.json) as the client configuration. Replace the executable, template, workspace, and companion DLL paths with your own absolute paths. The server uses stdio and puts logs on stderr. It does not need an AI provider key or the production AI gateway.

| Setting | Purpose |
| --- | --- |
| `FL_MCP_FL_EXE` | Exact FL executable; no implicit version discovery |
| `FL_MCP_TEMPLATE` | Existing absolute FLP path copied for each new project |
| `FL_MCP_WORKSPACE` | Absolute output/sample workspace; defaults to `%LocalAppData%\FlMcp\Projects` |
| `FL_MCP_PYTHON_RUNTIME` | Private CPython runtime directory; defaults to `<FL>/FruityLink/tools/fl-mcp/python/runtime` |
| `FL_MCP_PYTHON_PATH` | SDK wheel or development source directory; defaults to the installed wheel under `<FL>/FruityLink/tools/fl-mcp/python` |

Session and attachment tokens are generated privately. Do not add `FL_MCP_SESSION_TOKEN` to client settings. Discovery and control use Windows named pipes restricted to the same user; the client verifies the pipe server's actual Windows process ID before sending or reading credentials. Tokens never appear in discovery/attach tool results. There is no network listener.

## Assist your current project

1. Open FL normally and enable FL MCP in FruityLink's Plugins menu. Call `fl_instances` to discover matching instances; discovery only returns the exact executable configured by `FL_MCP_FL_EXE`.
2. Call `fl_attach` with the returned `processId`. This explicitly selects the current project and returns `ownership: "attached"`, its status, and the effective snapshot/sample workspace. The project itself may be outside that workspace or untitled. No launch template is needed for attachment.
3. Inspect `fl_status`, `fl_python_api` and channels, then use the authoring tools or embedded Python to assist your work. `fl_project_save` creates a new workspace snapshot without changing the active filename, playback or song mode.
4. Call `fl_detach` when finished. Detach and closing the MCP client leave FL open. `fl_project_close` and `fl_project_render` refuse attached sessions; save a snapshot, detach, and close FL yourself before starting a disposable render session from that snapshot.

Only one companion may hold an instance's attachment lease. Explicit detach releases it. A companion process that exits leaves a stale lease which the next attachment can replace; Windows process creation time prevents PID reuse from inheriting it. The same companion can retry an interrupted attachment. Plugin reload changes discovery identity and requires attachment again.

The lease uses this client's configured workspace, returned by `fl_attach`. Runtime/package overrides are passed during attachment because an already-running FL cannot inherit the companion's environment. The default paths are relative to the actual FL executable. Once the private interpreter has initialized, changing its runtime configuration requires restarting FL.

Project identity is **best effort**: typed title, path and untitled state are checked before operations and embedded SDK callbacks. Wait for startup/project loading to finish before attaching; if loading changes the initial project identity, attach again. After a detectable project switch, `fl_status` reports `requiresReattach` and edits/saves require `fl_attach` again. FL exposes no reliable generation ID, so replacing one untitled project with another bearing identical fields cannot be detected. Human edits can occur concurrently; checks and batches are not transactions and cannot prevent a project switch between a check and a native call.

## Execute Python

Call `fl_python_docs` for execution conventions, then attach or start a managed project and use `fl_python_api` (optionally filtered by operation name/description). The catalog comes from the shared typed SDK interfaces. `fl_execute_python` lazily initializes the private interpreter inside FL Studio and supplies a typed `fl` object backed by direct managed callbacks. It returns `{ok, result, stdout, stderr, traceback?, error?}` and stream truncation flags.

```python
fl.transport.tempo = 120
print(fl.project.info)
result = fl.channels.list()
```

Use the SDK's `fl.channels`, `fl.patterns`, `fl.playlist`, `fl.mixer`, `fl.plugins`, `fl.automation`, and `fl.arrangements` helpers. `fl.ops` exposes the complete generated low-level API when a helper is insufficient. Python keyword arguments use snake_case. Inspect `fl.capabilities()` before depending on optional native features. See [the authoring example](examples/author.py), the SDK Python README bundled under `python/`, and [the Blender MCP design research](docs/blender-mcp-design.md).

`fl_execute_python` executes trusted Python inside FL's memory space with the account's filesystem and network permissions. It is **not a security sandbox**: unsafe native extensions or process-exit calls can crash or terminate FL. Code is limited to 128 KiB, captured stdout/stderr to 64 KiB each, and the full response to 1 MiB. The interpreter is initialized only when a script runs, and is retained for the lifetime of the FL process.

The default 60-second deadline (1..300 configurable) requests cooperative cancellation. Python and active native calls must finish before another session operation, plugin unload, or FL close can proceed. Blocking native code can delay cancellation indefinitely; there is no separately killable worker. The companion holds the session gate while the plugin executes one request. Callbacks recheck project identity and invoke the shared dispatcher directly, without reacquiring that outer gate or sending a request back through its pipe. Completed edits remain and are not rolled back.

Use MCP project tools for start/save/close/render. Under this adapter, Python `new_project`, `open_project`, `save_project`, `save_project_as`, and `save_new_version` are refused. `save_copy` requires a fresh workspace `.flp` path; sample operations require existing workspace files. Batch path/lifecycle policy is checked before any batch item runs, but batches are not transactions. Standalone SDK Python clients can use the broader project lifecycle API; this restriction belongs to the managed MCP session.

## Author and render

1. Call `fl_project_start` with a fresh workspace path, such as `sessions/first.flp`. It refuses to reuse any already-running FL process. It waits for a responding bridge and the exact copied project path before returning readiness, tempo, and PPQ.
2. Read channels, installed plugins, and patterns; load the desired generators or workspace WAV samples. Create and name a pattern, then immediately author its notes before creating the next empty pattern.
3. Use the returned PPQ to express note and clip timing. At PPQ 96, one quarter note is 96 ticks and one 4/4 bar is 384 ticks. Place each pattern into the playlist with `fl_clip_add`; notes in an unarranged pattern alone do not make a rendered song.
4. Set routing, mixer levels, effects, and plugin parameters using discovered indices. Save a known recovery path with `fl_project_save`, for example `versions/first-v1.flp`.
5. Call `fl_project_render` with a fresh output path such as `audio/first.wav`. This saves another snapshot, checks its FLP envelope and complete data chunk, ends only the disposable managed editor, then runs FL's documented command-line WAV export. It requires successful process exit and a structurally nonempty WAV before returning the audio and snapshot paths. A header check is not proof that every FL event or external asset is valid.
6. After rendering, restart from the template or pass `sourceProjectPath` to `fl_project_start` to copy a previous workspace snapshot into a fresh session. Use `fl_project_close` to save and close without rendering.

The `fl_` names are suitable alongside a Blender MCP server in the same client. Hand Blender the returned WAV path (or copy the file to its machine if needed) and use the same planned duration/tempo. This repository does not couple to a particular Blender MCP implementation.

## Boundaries and recovery

Output overwrite, path traversal, Windows device names, alternate data streams, and workspace symlink/junction traversal are refused. Stage WAV samples inside the workspace. The workspace is a path guard, not an OS sandbox against other processes running as your user.

Every edit and save rechecks the selected project identity. Keep hands off a disposable managed FL window while authoring; attached sessions support concurrent human edits with the limitations described above. The server serializes its own changes. Tools may partially apply if FL fails between operations; read state before retrying note/clip additions because they append.

Launch defaults to 120 seconds (maximum 300), ordinary bridge calls to 30 seconds, save to 60, and render to 600 (maximum 3600). Embedded execution cancellation signals the plugin and waits for its completion acknowledgement while preserving the pipe and session gate. If the bridge breaks before acknowledgement, FL is left running until a later serialized status reply confirms the interpreter is idle. Already-issued changes cannot be rolled back. Render/launch cancellation stops only processes created by this companion. A render error includes its preserved snapshot path; after explicit client cancellation, use your last known saved recovery path. Existing or partially written outputs are preserved, so retry with a new filename.

Closing the MCP client ends its disposable FL session only after embedded execution has drained. A session whose execution completion is unknown remains open. An attached FL process is never terminated by the companion, including after a lost connection. Save frequently. The typed `fl` API exposes no raw-memory or reflection dispatch; arbitrary in-process Python still has the host's privileges.

FL's CLI can still encounter missing-asset, registration, recovery, or third-party plugin dialogs. Hidden launch is a request to Windows, not a guarantee that FL never shows UI. Render completion currently requires FL to exit; if a particular build leaves its render process open, the call times out rather than guessing that a stable file is complete. WAV RIFF output below 4 GiB is supported; RF64 and non-WAV exports are not validated in this initial version. Audio quality, sample rate, and tail handling come from FL's saved export settings.

## Verification and release status

`scripts/codefactor-check.ps1` performs a Release build with all warnings as errors, CA1502 complexity at most 15, CS0105 duplicate-using detection, and the test suite. Package builds use locked restore; source builds keep separate locks under `obj`. Adapter tests use an official MCP stdio client, real local named pipes, and an injected embedded-runtime contract to verify direct routing and native-drain cancellation. The SDK validates its real CPython embedding separately in a disposable test host. FL orchestration and SDK calls use explicit fakes; fixtures exercise file envelopes, not musical correctness.

To include this adapter's real CPython integration test, set `FRUITYLINK_TEST_PYTHON_RUNTIME` to the private 3.14.6 runtime directory before running the gate. Set `FRUITYLINK_TEST_PYTHON_PACKAGE` to the rebuilt wheel or SDK `python/src` directory; the source-build gate defaults it to that SDK source directory. The test embeds Python in the isolated .NET test process, checks the process ID, and exercises direct fake-FL callbacks through the plugin. It never starts FL. Without the runtime variable, this one integration test is explicitly skipped.

CI builds a local distribution artifact only. Full integration requires repository variable `FRUITYLINK_SDK_REF` or a manual workflow `sdk_ref` selecting an explicit published commit/tag in `Realynx/FL-Automation`; until configured, it reports the missing dependency and skips the integration job. This avoids silently building against an older SDK or assuming unpublished NuGet packages exist. There is no automatic GitHub release, package publication, or installation. Before publishing a stable release, follow the [live verification checklist](docs/live-verification.md) on each supported exact FL build.

References: [official MCP C# SDK](https://github.com/modelcontextprotocol/csharp-sdk), [Image-Line command-line export](https://www.image-line.com/fl-studio-learning/fl-studio-online-manual/html/fformats_save_export.htm#commandline_export), [FL project format and external assets](https://www.image-line.com/fl-studio-learning/fl-studio-online-manual/html/fformats_open_flp.htm), [PyFLP's researched FLP envelope](https://pyflp.readthedocs.io/en/stable/architecture/flp-format.html).
