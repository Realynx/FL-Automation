# FruityLink SDK

The open-source plugin system and programmatic FL Studio control library that powers
[FL Automate](https://fl-automate.com).

FruityLink lets you write **C# plugins that run inside FL Studio** and **drive FL Studio
programmatically** — set the tempo, author piano-roll notes, arrange playlist clips, tweak mixer and
plugin parameters, add menu items and toolbar buttons, and (with the Avalonia or WPF hosting
packages) embed your own UI inside FL's window chrome.

> The FL Automate AI assistant itself is a **separate, closed-source plugin built on this SDK** — it is
> not part of this repository. What you get here is the same host, control surface, and plugin contract
> the assistant runs on, so anything it does, your own plugin can do too.

## Features

- **A stable, tiny plugin contract** (`FruityLink.Plugins.Abstractions`) — implement one interface,
  `IFlPlugin`, and you have a plugin.
- **A safe, typed FL control surface** (`INativeFlControl`) covering channels, patterns, piano-roll
  notes, the mixer, the playlist/arrangements, transport, markers, project lifecycle, plugin
  parameters, and automation.
- **Native menu + toolbar integration** — contribute commands and toggles to FL's own dropdowns and
  main toolbar.
- **UI embedding** — host any Win32 `HWND`, an Avalonia UI, or a WPF window inside a real FL editor
  host form (the Avalonia/WPF hosting packages carry the rendering workarounds embedded UI needs).
- **Hot reload** — rebuild a plugin dll and the host reloads it live, preserving enabled state.
- **Isolated loading** — each plugin runs in its own collectible `AssemblyLoadContext` from a shadow
  copy, so a plugin can be unloaded and its original dll stays writable for rebuilds.

## Architecture

FruityLink runs *inside* FL Studio's process. The install chain gets managed code loaded, and a small
native bridge (`FlBridge.dll`) executes FL engine calls on FL's own main thread:

```
FL Studio (FL64.exe)
  └─ version.dll                      transparent proxy (forwards the real version.dll exports)
       └─ FlClrHost.dll               native CoreCLR host (hostfxr) — starts the .NET runtime in-process
            └─ FruityLink.Host        HostEntry.Bootstrap — managed entry point
                 ├─ FlBridge.dll      in-process native bridge; runs FL engine calls on FL's main thread
                 │    (dev tooling can also reach it over the named pipe \\.\pipe\FruityLinkBridge)
                 ├─ FruityLink.FlStudio    INativeFlControl implementation over the bridge
                 └─ FruityLink.Plugins.Host
                      └─ your plugin   discovered in <host-dir>\plugins, loaded in its own ALC
```

Every plugin is handed an `IPluginContext` on enable. That context is the **trust boundary**: it exposes
only the typed `INativeFlControl` surface plus menu/toolbar registrars — never the bridge's raw
memory/call primitives.

## Quickstart

A plugin is a class library that references `FruityLink.Plugins.Abstractions` and implements `IFlPlugin`:

```csharp
using FruityLink.Plugins.Abstractions;

public sealed class MyPlugin : IFlPlugin
{
    public string Id => "my-plugin";
    public string Name => "My Plugin";
    public string Description => "Logs the current tempo from a Tools-menu command.";
    public string Version => "1.0.0";

    private IDisposable? _cmd;

    public Task EnableAsync(IPluginContext ctx, CancellationToken ct = default)
    {
        _cmd = ctx.Menu.AddCommand(FlNativeMenu.Tools, "Log tempo",
            onInvoke: () => _ = Task.Run(async () =>
                ctx.Log($"tempo = {await ctx.Fl.GetTempoAsync()} BPM")));
        return Task.CompletedTask;
    }

    public Task DisableAsync(CancellationToken ct = default)
    {
        _cmd?.Dispose();
        return Task.CompletedTask;
    }
}
```

Build it, then drop the output into the host's plugins directory:

```
<host-dir>\plugins\MyPlugin\MyPlugin.dll
```

Start FL Studio (with the FL Automate / FruityLink host installed), open the **Tools ▸ FL Plugins**
submenu, and enable your plugin. See [docs/getting-started.md](docs/getting-started.md) for the full
walkthrough. A complete, buildable example lives in [`samples/HelloFl`](samples/HelloFl).

## Repository layout

| Path | What it is |
| --- | --- |
| `src/FruityLink.Core` | FL-control abstractions (`INativeFlControl`, `IFlSymbolResolution`) + `Music/*` helpers. |
| `src/FruityLink.FlStudio` | `INativeFlControl` implementation talking to the native bridge. |
| `src/FruityLink.Plugins.Abstractions` | The stable plugin contract (`IFlPlugin`, `IPluginContext`, menu/toolbar registrars). |
| `src/FruityLink.Plugins.Host` | Plugin discovery, isolated loading, enable/disable persistence, hot reload. |
| `src/FruityLink.Host` | The in-process managed entry point (`HostEntry.Bootstrap`). |
| `src/FruityLink.Ui.Avalonia.Hosting` | Generic Avalonia-in-FL hosting (`EmbeddedAvaloniaHost`, embedded view wrapper). |
| `src/FruityLink.Ui.Wpf.Hosting` | WPF-in-FL hosting helpers (`WpfUiThread`, `EmbeddedWpfView`). |
| `samples/HelloFl` | Minimal buildable sample plugin. |
| `native/bridge` | The native `FlBridge.dll` (C++/CMake). |
| `docs/` | Documentation (index below). |

## Building from source

Managed projects (SDK + sample), with the .NET 9 SDK:

```sh
dotnet build sdk/FruityLink.Sdk.slnx
# or just the sample:
dotnet build sdk/samples/HelloFl/HelloFl.csproj
```

The native bridge, with CMake + MSVC (must be x64 to match FL Studio):

```sh
cmake -S sdk/native/bridge -B sdk/native/bridge/build -A x64
cmake --build sdk/native/bridge/build --config Release
# -> sdk/native/bridge/build/Release/FlBridge.dll
```

## Documentation

- [Getting started](docs/getting-started.md) — write, build, deploy, and debug your first plugin.
- [Plugin lifecycle](docs/plugin-lifecycle.md) — discovery, isolation, persistence, pre-warm, hot reload.
- [FL control API](docs/fl-control-api.md) — the `INativeFlControl` surface and FL's value conventions.
- [Menus and toolbar](docs/menus-and-toolbar.md) — contributing commands, toggles, and buttons.
- [Window embedding](docs/window-embedding.md) — hosting your own window inside an FL editor form,
  including the WPF helpers.
- [Avalonia UI](docs/avalonia-ui.md) — hosting Avalonia UI inside FL, and the gotchas that matter.
- [Native bridge](docs/native-bridge.md) — the `FlBridge.dll` protocol, build, and version policy.

## Safety and compatibility

FruityLink controls FL Studio through reverse-engineered engine entry points. The native bridge resolves
those addresses at runtime by byte-signature scanning and **fails safe** — an address it cannot uniquely
resolve is refused rather than guessed, because a wrong address would be an uncatchable crash inside FL.
Symbol resolution is currently validated against the FL Studio 2025 / 2026 line. Tools that depend on a
symbol which did not resolve on the running FL build are hidden rather than fired. See
[docs/native-bridge.md](docs/native-bridge.md).

## License

MIT — see [LICENSE](LICENSE).
