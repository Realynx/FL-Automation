<div align="center">

<img src="assets/logo.svg" width="104" alt="FL Automate" />

<h1>FruityLink SDK</h1>

<p><strong>Build plugins and script FL Studio.</strong><br/>
The open-source plugin framework behind <a href="https://fl-automate.com">FL Automate</a>.</p>

<p>
<a href="https://www.nuget.org/packages/FruityLink.Plugins.Abstractions"><img src="https://img.shields.io/nuget/dt/FruityLink.Plugins.Abstractions?style=flat-square&amp;color=22d3ee&amp;label=NuGet%20downloads" alt="FruityLink.Plugins.Abstractions total NuGet downloads" /></a>
<a href="https://discord.fl-automate.com"><img src="https://img.shields.io/badge/Discord-join%20us-5865F2?style=flat-square&logo=discord&logoColor=white" alt="Discord" /></a>
<img src="https://img.shields.io/badge/license-MIT-8b5cf6?style=flat-square" alt="MIT license" />
<img src="https://img.shields.io/badge/.NET-9.0-7c3aed?style=flat-square" alt=".NET 9" />
<img src="https://img.shields.io/badge/platform-Windows%20x64-d946ef?style=flat-square" alt="Windows x64" />
</p>

<p>
<a href="https://fl-automate.com">Website</a> ·
<a href="https://discord.fl-automate.com">Discord</a> ·
<a href="#available-plugins">Plugins</a> ·
<a href="#quickstart">Quickstart</a> ·
<a href="#documentation">Docs</a>
</p>

</div>

FruityLink runs C# plugins inside FL Studio and exposes a shared DAW API to C# and
Python. Plugins can contribute native menus, toolbar buttons, and windows, while
using the same project, composition, and mixing operations.

## Available plugins

- **[FLMCP](https://github.com/Realynx/Fl-MCP)** — Connect MCP clients to FL Studio for project authoring, embedded Python, managed WAV rendering, and explicit attachment to an existing FL session.
- **[FL Python IDE](src/FruityLink.Plugins.PythonIde/README.md)** — Edit and run Python inside FL Studio, with syntax highlighting, an API browser, output, tracebacks, and cancellation. The framework installer supplies private CPython; users do not need system Python or pip.
- **[Python scripting endpoint](src/FruityLink.Plugins.Python/README.md)** — Let external Python programs connect to the shared API through an authenticated local endpoint.
- **[Hello FL](samples/HelloFl)** — A small C# example contributing menu and toolbar actions.

FLMCP is maintained in a separate repository. The FL Automate assistant is a
separate, closed-source consumer of this framework.

## Capabilities

- **Plugin framework:** discovery, enable/disable, shadow copies, hot reload, and scoped menu/toolbar contributions. Plugins have separate load contexts; the host shares the interpreter and UI toolkit where their process-wide lifetime requires it.
- **Embedded Python:** one resident CPython interpreter inside FL, shared by the IDE and MCP. The typed `fruitylink` library provides collections, batches, structured queries, and generated `fl.ops` methods from the native contract.
- **Music editing:** transport, channels, notes, patterns, playlist clips, arrangements, plugin parameters, and mixer insert creation with typed track discovery.
- **[Automation clips](docs/automation-clips.md):** create a linked channel and playlist clip, place an existing curve again, and read or replace validated envelopes. Targets include channel/mixer controls and hosted-plugin parameters.
- **[Audio measurements](docs/python-audio-analysis.md):** RMS, energy, crest, occupancy, gated loudness, estimated true peak/PSR, and [spectral summaries](docs/python-spectral-analysis.md) from supplied WAV files or PCM. These utilities also work offline; they do not capture isolated live FL channels.
- **[Window hosting](docs/window-embedding.md):** independent plugin windows using native FL chrome, with Avalonia/WPF helpers and an external-window fallback when embedding is unavailable.

Plugins and embedded scripts run with FL Studio's permissions. Load contexts are
not a security sandbox, and batches or failed edits do not provide automatic rollback.

## Verified FL builds

Windows x64 is required. Validation is specific to these engine builds:

| Engine build | Evidence |
| --- | --- |
| **25.2.5.5319** | Binary analysis and automated fixtures; the current feature set has not been live-tested on this build. |
| **26.1.3.5570** | Binary analysis, automated tests, and live project editing, embedded Python, mixer insertion, automation playback/persistence, and WAV rendering. |

This does not establish every operation on either build. Other patches, older
versions, and future majors require their own validation. Check operation
capabilities before use; some legacy UI functions remain unavailable on 2026.
Native window title-bar/drag stability and broader DPI/theme behavior still need
live confirmation. See [version policy](docs/native-bridge.md#fl-version-support)
and [window validation](docs/native-window-validation.md).

## Quickstart

Current source targets **SDK 0.2.0**. Its matching NuGet packages are **not yet
published**; build the host and plugins from the matching checkout. Installer
**0.1.22** is available locally but has **not been published as a GitHub release**.
An installed FruityLink host is required to load plugins into FL Studio.

From this repository's root, build the complete C# example:

```powershell
dotnet build samples/HelloFl/HelloFl.csproj -c Release
```

For your own plugin, target `net9.0-windows`, reference the matching
[`FruityLink.Plugins.Abstractions` project](src/FruityLink.Plugins.Abstractions/FruityLink.Plugins.Abstractions.csproj),
and implement `IFlPlugin`. This minimal example reads the tempo when enabled:

```csharp
using System;
using System.Threading;
using System.Threading.Tasks;
using FruityLink.Plugins.Abstractions;

public sealed class TempoLogger : IFlPlugin
{
    public string Id => "tempo-logger";
    public string Name => "Tempo Logger";
    public string Description => "Logs the current tempo when enabled.";
    public string Version => "1.0.0";

    public async Task EnableAsync(IPluginContext context, CancellationToken ct = default)
    {
        try
        {
            double tempo = await context.Fl.GetTempoAsync(ct);
            context.Log($"Tempo: {tempo:0.###} BPM");
        }
        catch (Exception error)
        {
            context.Log($"Tempo read failed: {error.Message}");
        }
    }

    public Task DisableAsync(CancellationToken ct = default) => Task.CompletedTask;
}
```

Deploy the plugin output and its private dependencies under
`<host-dir>\plugins\<plugin-name>\`, then enable it in FruityLink's plugin menu.
Keep the host's shared dependencies from the matching build. See
[getting started](docs/getting-started.md) for deployment and
[plugin lifecycle](docs/plugin-lifecycle.md) for cleanup and reload rules.

For Python, start with the IDE or the [library quickstart](python/README.md).
External clients require Python 3.11 or later; embedded IDE/MCP execution uses the
installer's private CPython 3.14.6 runtime.

## Build and test

Development requires Windows x64, the .NET 9 SDK, and CMake with MSVC x64 tools.
Python checks require Python 3.11+ and `uv`. Run these commands from the repository root:

```powershell
dotnet build FruityLink.Sdk.slnx -c Release -warnaserror
cmake -S native/bridge -B native/bridge/build -A x64 -DFRUITYLINK_DEBUG=OFF
cmake --build native/bridge/build --config Release
ctest --test-dir native/bridge/build -C Release --output-on-failure
uv sync --directory python
uv run --directory python python tools/check.py
```

For the full managed gate, first configure the pinned interpreter and external
Python test executable using the [embedded Python test setup](docs/embedded-python.md#validation), then run:

```powershell
./scripts/codefactor-check.ps1
```

The managed gate rebuilds with warnings as errors, enforces C# method complexity
at most 15, and runs regression tests. Python checks cover generated API parity,
Ruff, strict typing, tests, and packaging. Native tests inspect copied engine
images or use fixtures; passing them does not replace live FL validation.

## Documentation

- [C# control API](docs/fl-control-api.md) · [Python API](python/docs/api.md)
- [Embedded interpreter](docs/embedded-python.md) · [Scripting protocol](docs/python-protocol.md)
- [Menus and toolbar](docs/menus-and-toolbar.md) · [Avalonia hosting](docs/avalonia-ui.md)
- [Automation creation](docs/automation-clips.md) · [Audio analysis](docs/python-audio-analysis.md) · [Spectral analysis](docs/python-spectral-analysis.md)
- [Native bridge](docs/native-bridge.md) · [Roadmap](docs/roadmap.md) · [API gaps](docs/api-gaps.md)

## License

[MIT](LICENSE) © Realynx. Separately distributed plugins and bundled dependencies
retain their own licenses.
