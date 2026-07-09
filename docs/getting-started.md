# Getting started

This walkthrough takes you from an empty folder to a running FruityLink plugin.

See also: [Plugin lifecycle](plugin-lifecycle.md) · [FL control API](fl-control-api.md) ·
[Menus and toolbar](menus-and-toolbar.md).

## Prerequisites

- **FL Studio** with the **FL Automate / FruityLink host installed.** The host is the in-process
  component (installed via FL's `version.dll` proxy → `FlClrHost.dll` → `FruityLink.Host`) that
  discovers and runs plugins. Without it, there is nothing to load your plugin.
- **.NET 9 SDK** (`dotnet --version` ≥ 9). Plugins target `net9.0-windows` because they load into the
  Windows-desktop runtime alongside the host.
- A FruityLink SDK checkout (for the `FruityLink.Plugins.Abstractions` project reference), or a package
  reference once the contract is published.

## 1. Create the project

A plugin is an ordinary class library:

```sh
dotnet new classlib -n MyPlugin -f net9.0-windows
```

Edit `MyPlugin.csproj` to reference the plugin contract. Referencing
`FruityLink.Plugins.Abstractions` is what marks the dll as a plugin — discovery reads the PE metadata
and only considers dlls that reference this assembly.

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net9.0-windows</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="path\to\src\FruityLink.Plugins.Abstractions\FruityLink.Plugins.Abstractions.csproj" />
  </ItemGroup>
</Project>
```

The [`samples/HelloFl`](../samples/HelloFl) project is a complete, buildable copy of this setup.

## 2. Implement `IFlPlugin`

`IFlPlugin` has four metadata properties and two lifecycle methods. Keep the constructor empty — do the
work in `EnableAsync` and undo *all* of it in `DisableAsync`. Both must be idempotent, and neither may
let an exception escape into the host.

```csharp
using FruityLink.Plugins.Abstractions;

public sealed class MyPlugin : IFlPlugin
{
    public string Id => "my-plugin";          // stable, unique; used for enable/disable persistence
    public string Name => "My Plugin";        // shown in the Plugins list
    public string Description => "What it does.";
    public string Version => "1.0.0";

    private IDisposable? _cmd;

    public Task EnableAsync(IPluginContext ctx, CancellationToken ct = default)
    {
        // Read FL state through the safe surface, contribute UI, start services…
        _cmd = ctx.Menu.AddCommand(FlNativeMenu.Tools, "Log tempo",
            onInvoke: () => _ = Task.Run(async () =>
                ctx.Log($"tempo = {await ctx.Fl.GetTempoAsync()} BPM")));
        return Task.CompletedTask;
    }

    public Task DisableAsync(CancellationToken ct = default)
    {
        _cmd?.Dispose();     // remove what you added
        _cmd = null;
        return Task.CompletedTask;
    }
}
```

`IPluginContext` gives you:

- `Fl` — the typed [FL control surface](fl-control-api.md) (`INativeFlControl`).
- `Menu` / `Toolbar` — [menu and toolbar registrars](menus-and-toolbar.md).
- `Services` — the host service provider (may be empty).
- `Log(string)` — appends a line to the host log.

Callbacks from `Menu`/`Toolbar` fire on **FL's UI thread**. Anything that awaits the bridge (like
`GetTempoAsync`) should run off that thread — the sample wraps it in `Task.Run`.

## 3. Build and deploy

Build the plugin, then place its output under the host's plugins directory. Two layouts work:

- **Per-plugin folder (recommended):** `<host-dir>\plugins\MyPlugin\MyPlugin.dll` plus its private
  dependencies. The whole folder is shadow-copied per load.
- **Flat:** `<host-dir>\plugins\MyPlugin.dll` directly (its `.deps.json`/`.pdb` sidecars are copied too).

The default plugins directory is `<host-dir>\plugins` (next to the host dll).

```sh
dotnet build MyPlugin.csproj -c Release
# copy bin\Release\net9.0-windows\* into <host-dir>\plugins\MyPlugin\
```

In FL Studio, open **Tools ▸ FL Plugins**, find your plugin, and enable it. The enabled state is
persisted, so it will be re-enabled automatically on the next launch.

## 4. Hot reload

Hot reload is **on by default**. With FL running, rebuild your plugin dll straight into the plugins
folder and the host reloads the new version live (preserving its enabled state). The watcher debounces
~600 ms and waits for the file to finish writing before reloading, so a normal `dotnet build` over the
deployed dll triggers exactly one reload.

Because each plugin loads from a **shadow copy**, the original dll stays writable — that is what lets a
rebuild overwrite it while FL holds the plugin loaded.

To disable hot reload (e.g. for a production install), set the environment variable
`FRUITYLINK_PLUGIN_HOTRELOAD=0` before launching FL.

See [Plugin lifecycle](plugin-lifecycle.md) for the mechanics.

## 5. Logs

The plugin host writes a daily log to:

```
%LocalAppData%\FruityLink\logs\plugin-host-YYYYMMDD.log
```

Discovery results, enable/disable, hot-reload events, and any message you pass to `context.Log(...)`
land here. It is the first place to look when a plugin does not appear or does not behave.

Related state files (all under `%LocalAppData%\FruityLink`):

- `plugins.json` — the set of enabled plugin ids (rewritten on every enable/disable).
- `plugin-shadow\` — per-load shadow copies of plugin assemblies.

## Troubleshooting

- **Plugin not listed.** Confirm the dll actually references `FruityLink.Plugins.Abstractions`
  (discovery skips any dll that does not) and that it is under `<host-dir>\plugins`. Check the host log
  for a `discover:` line naming your file.
- **Type not instantiated.** The plugin class must be `public`, non-abstract, and have a public
  parameterless constructor. A non-empty `Id` is required; duplicate ids are ignored (first wins).
- **Enable failed.** The host log records `enable FAILED for '<id>'` with the exception. A throw from
  `EnableAsync` is caught and reported — it never takes the host down.
- **Nothing happens when a menu/toolbar callback runs.** Remember callbacks are on FL's UI thread;
  a blocking bridge call there can stall the UI. Do async work on the thread pool (`Task.Run`).
- **Hot reload didn't pick up my build.** Ensure you built into the deployed location, that
  `FRUITYLINK_PLUGIN_HOTRELOAD` is not set to `0`, and check the log for a `hot-reload:` line.
