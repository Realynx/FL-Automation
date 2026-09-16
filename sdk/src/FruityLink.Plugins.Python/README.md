# FL Python plugin

This plugin connects ordinary Python programs to FruityLink's typed FL Studio API. It starts
an authenticated local named pipe when enabled and creates no plugin window. The Python client
and reusable dispatcher belong to the [FL-Automation SDK](https://github.com/Realynx/FL-Automation).
FLMCP can host the same dispatcher itself, so MCP users do not need a second endpoint plugin.

## Requirements

- Windows x64, FL Studio 2025 or 2026, and the matching FruityLink SDK host.
- **Plugin 0.2.0 requires host 0.2.0**, including its Core and Plugins.Abstractions assemblies.
  The host shares these contracts with plugins; copying newer contract DLLs into a plugin
  folder does not upgrade an older host.
- Python 3.11 or later with the SDK's `fruitylink` package for Python clients.

Availability depends on the installed FL build's verified native signatures and layouts.
The endpoint does not launch FL Studio, suppress modal dialogs, or add a headless rendering
engine. FLMCP provides its own project and process lifecycle around the same SDK operations.

## Build a package

From the SDK repository root, with .NET SDK 9 installed:

```powershell
pwsh -File src/FruityLink.Plugins.Python/pack.ps1
```

The command builds Release with warnings treated as errors and writes a versioned ZIP,
SHA-256 checksum, and reviewable staging directory under `artifacts/python-plugin`.
It does not install anything or launch FL Studio. `-OutputDirectory` selects another output
directory; `-SkipBuild` packages an already-built Release output.

The ZIP contains one `fl-python` folder with:

```text
FruityLink.Plugins.Python.dll
FruityLink.Plugins.Python.deps.json
FruityLink.Plugins.Python.xml
FruityLink.Scripting.dll
FruityLink.Scripting.xml
README.md
LICENSE
SHA256SUMS
```

Core and Plugins.Abstractions remain supplied by the matching host.

## Install and use

1. Extract the ZIP's `fl-python` folder into the host's configured `plugins` directory.
2. Enable **FL Python** in FL Studio's plugin manager (**Tools → FL Plugins**).
3. Install the Python SDK from this repository with `python -m pip install ./python`, or
   install its built wheel. Then connect to the FL process:

```python
from fruitylink import connect

fl = connect()  # Requires exactly one discoverable endpoint; use pid=... for multiple FL instances.
fl.ops.set_tempo(bpm=124)
print(fl.ops.get_tempo())
print(fl.ops.query_project())
```

The endpoint is `FruityLinkScripting-{pid}`. Discovery metadata is written to
`%LOCALAPPDATA%\FruityLink\scripting\{pid}.json`, with a fresh identity and secret on each
activation. The directory and pipe permit only the current Windows user. Do not publish the
discovery token. The client validates process and instance identity before exposing the API.

The dispatcher exposes the existing 123 typed operations and, when supported by the control
implementation, nine structured queries. `catalog` supplies operation documentation, argument
and return schemas, defaults, and capability requirements. Transport limits are 4 MiB per
frame, one request per connection, and 256 operations per batch. The default server deadline
is two minutes per request; a host may inject `ScriptingServerOptions` to configure it.

Requests execute in order within the dispatcher. Disconnecting or cancelling stops queued
work and the rest of a batch. A started native call retains the execution gate until it
finishes, so disabling the plugin may wait for it. Completed edits are not rolled back.

## Tests and embedding

```powershell
dotnet test tests/FruityLink.Scripting.Tests -c Release -warnaserror
dotnet test tests/FruityLink.Plugins.Python.Tests -c Release -warnaserror
```

The scripting tests include a real named-pipe server and Python client talking to a fake
typed FL control. Set `FRUITYLINK_TEST_PYTHON` to a Python executable when it is not on PATH.
These tests do not execute FL Studio.

Other plugins can reference `FruityLink.Scripting` and host `FlScriptingDispatcher` directly
through `HandleRequestAsync`, `InvokeAsync`, or `BatchAsync`. A transport that owns the FL
process must await `DrainAsync` before acknowledging cancellation and terminating that
process. Dispose the dispatcher to reject queued calls and drain active work.

This SDK plugin is MIT licensed. FLMCP is maintained in its separate repository under its
own license.
