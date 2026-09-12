# FL MCP

Control FL Studio through MCP using the public [FruityLink plugin SDK](https://github.com/Realynx/FL-Automation). A small plugin runs inside FL Studio without its own UI. A separate stdio MCP server manages a disposable project session and exposes 24 tools, including Python execution, API discovery, music authoring, project snapshots, and WAV rendering.

The reusable Python library lives in the **FruityLink SDK repository**, under `python/`. FL MCP calls that package; it does not contain a second Python DAW implementation. The existing 21 tools remain compatible facades over the shared `FruityLink.Scripting` dispatcher. Session/process ownership, workspace policy, and command-line rendering belong to this MCP adapter.

This is an initial implementation, with automated protocol and orchestration tests. **End-to-end authoring and rendering in FL Studio 2025/2026 have not yet been live-verified.** Exact FL build support depends on the installed FruityLink bridge. Do not infer support for every build from the FL Studio year.

Licensed under **[PolyForm Noncommercial 1.0.0](LICENSE)**. This is source-available software with noncommercial restrictions; it is not described as OSI open source. The license is the verbatim [official plaintext](https://polyformproject.org/licenses/noncommercial/1.0.0.txt). Third-party software retains its own terms.

## Requirements

- Windows x64 with a licensed FL Studio installation and FruityLink installed for that exact executable/build.
- A normal logged-in Windows desktop session. "Headless" here means no FL MCP plugin window and unattended orchestration where FL permits it. FL remains a desktop application. Session 0, Windows services, containers, Linux, and a guaranteed invisible FL host are not supported.
- .NET 10 runtime for the companion; FruityLink's .NET 9 host runtime for the plugin. Building requires the .NET 10 SDK and the .NET 9 runtime for tests/host use.
- Python 3.11 or newer with the SDK's `fruitylink-python` 0.2.0 package for Python execution. A matching FruityLink 0.2.0 host is required for the shared structured-query contract.
- A saved `.flp` template prepared with licensed instruments and resolvable samples. Start with a minimal empty template, choose suitable render settings in FL once, and save it. No FL project, instrument, sample, or license is bundled.

## Build and install

The new SDK 0.2.0 dependencies are prepared locally and are **not assumed published** to NuGet or PyPI. Build against an explicit SDK checkout containing `src/FruityLink.Scripting` and `python`:

```powershell
./scripts/codefactor-check.ps1 -FruityLinkSdkRoot C:\source\FL-Automation
uv run --directory C:\source\FL-Automation\python --locked python -m build
./scripts/package.ps1 -FruityLinkSdkRoot C:\source\FL-Automation -PythonWheel C:\source\FL-Automation\python\dist\fruitylink_python-0.2.0-py3-none-any.whl
```

The package contains separate `server`, `plugin/fl-mcp`, and `python` directories. Copy the entire `fl-mcp` directory to your FruityLink host's `plugins` directory. Keep `FlMcp.Plugin.dll`, `FlMcp.Plugin.deps.json`, `FlMcp.Protocol.dll`, and `FruityLink.Scripting.dll` together. Do not copy the stdio server into the host plugins folder. FruityLink provides its shared contract assemblies.

Install the bundled SDK wheel into a dedicated interpreter environment:

```powershell
python -m venv C:\Tools\FlMcp\.venv
C:\Tools\FlMcp\.venv\Scripts\python.exe -m pip install C:\Tools\FlMcp\python\fruitylink_python-0.2.0-py3-none-any.whl
```

Start FL normally once and enable **FL MCP** in FruityLink's Plugins menu. It will log that it is idle in an ordinary session; this is expected. FruityLink persists enabled plugin IDs in `%LocalAppData%\FruityLink\plugins.json`. Close FL after this one-time setup. The companion's launch inherits the same user account and enabled plugin state. Install/build does not change your FL setup automatically.

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
| `FL_MCP_PYTHON` | Python executable with `fruitylink-python` installed; defaults to `python` |
| `FL_MCP_PYTHON_PATH` | Optional development path to the SDK's `python/src`; overrides worker `PYTHONPATH` |

The session token is generated privately for each managed FL process. Do not add `FL_MCP_SESSION_TOKEN` to client settings. The bridge is a Windows named pipe restricted to the same user and launched process identity, with an additional random token. It has no network listener.

## Execute Python

Call `fl_python_docs` for execution conventions, then start a managed project and use `fl_python_api` (optionally filtered by operation name/description). The catalog comes from the shared typed SDK interfaces. `fl_execute_python` runs the actual SDK package in a separate interpreter with a connected `fl` object. It returns `{ok, result, stdout, stderr, traceback?, error?}` and stream truncation flags.

```python
fl.transport.tempo = 120
print(fl.project.info)
result = fl.channels.list()
```

Use the SDK's `fl.channels`, `fl.patterns`, `fl.playlist`, `fl.mixer`, `fl.plugins`, `fl.automation`, and `fl.arrangements` helpers. `fl.ops` exposes the complete generated low-level API when a helper is insufficient. Python keyword arguments use snake_case. Inspect `fl.capabilities()` before depending on optional native features. See [the authoring example](examples/author.py), the SDK Python README bundled under `python/`, and [the Blender MCP design research](docs/blender-mcp-design.md).

`fl_execute_python` executes trusted arbitrary Python with the account's filesystem and network permissions. It is **not a security sandbox**. Code is limited to 128 KiB, captured stdout/stderr to 64 KiB each, and the full response to 1 MiB (large script results are rejected). The default worker deadline is 60 seconds, configurable from 1 to 300. Raw subprocess output is drained separately so it cannot corrupt MCP stdio.

Each worker belongs to a Windows job that kills its descendants on success, failure, timeout, or cancellation. Its relay is a fresh authenticated pipe restricted to the same user; endpoint tokens never appear in tool results. All FL calls recheck the managed project identity. The script owns the session gate, while its relay calls an internal callback that does not reacquire that gate. On cancellation, an already-started native call must finish before the gate releases. A hung native call can therefore delay cancellation completion; completed edits remain and are not rolled back.

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

Every edit and save rechecks the owned project identity. Keep hands off the managed FL window while authoring; changing its project causes further calls to fail. The server serializes changes. Tools may partially apply if FL fails between operations; read state before retrying note/clip additions because they append.

Launch defaults to 120 seconds (maximum 300), ordinary bridge calls to 30 seconds, save to 60, and render to 600 (maximum 3600). MCP cancellation disconnects the pipe and cancels subsequent plugin steps; already-issued native changes cannot be rolled back. Render/launch cancellation stops only processes created by this companion. A render error includes its preserved snapshot path; after explicit client cancellation, use your last known saved recovery path. Existing or partially written outputs are preserved, so retry with a new filename.

Closing the MCP client also ends its disposable FL session. Save frequently. Native operations expose only the public typed SDK contract; no raw-memory or reflection dispatch is exposed.

FL's CLI can still encounter missing-asset, registration, recovery, or third-party plugin dialogs. Hidden launch is a request to Windows, not a guarantee that FL never shows UI. Render completion currently requires FL to exit; if a particular build leaves its render process open, the call times out rather than guessing that a stable file is complete. WAV RIFF output below 4 GiB is supported; RF64 and non-WAV exports are not validated in this initial version. Audio quality, sample rate, and tail handling come from FL's saved export settings.

## Verification and release status

`scripts/codefactor-check.ps1` performs a Release build with all warnings as errors, CA1502 complexity at most 15, CS0105 duplicate-using detection, and the test suite. Package builds use locked restore; source builds keep separate locks under `obj`. Tests use a real official MCP stdio client, real local named pipes, and the real external Python worker, including descendant cleanup and native-drain cancellation. FL process orchestration and SDK calls use explicit fakes; fixtures exercise file envelopes, not musical correctness.

CI builds a local distribution artifact only. Full integration requires repository variable `FRUITYLINK_SDK_REF` or a manual workflow `sdk_ref` selecting an explicit published commit/tag in `Realynx/FL-Automation`; until configured, it reports the missing dependency and skips the integration job. This avoids silently building against an older SDK or assuming unpublished NuGet packages exist. There is no automatic GitHub release, package publication, or installation. Before publishing a stable release, follow the [live verification checklist](docs/live-verification.md) on each supported exact FL build.

References: [official MCP C# SDK](https://github.com/modelcontextprotocol/csharp-sdk), [Image-Line command-line export](https://www.image-line.com/fl-studio-learning/fl-studio-online-manual/html/fformats_save_export.htm#commandline_export), [FL project format and external assets](https://www.image-line.com/fl-studio-learning/fl-studio-online-manual/html/fformats_open_flp.htm), [PyFLP's researched FLP envelope](https://pyflp.readthedocs.io/en/stable/architecture/flp-format.html).
