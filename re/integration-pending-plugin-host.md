# Integration pending — FruityLink.Plugins.Host (plugin host / manager + hot-reload)

New project `src/FruityLink.Plugins.Host` (`net9.0-windows`) implements the open-source plugin system
against the stable contract in `src/FruityLink.Plugins.Abstractions` (which I did NOT touch). It
discovers installed plugins, persists which are enabled, enables/disables them at runtime, and
(for dev) hot-reloads rebuilt plugins. It self-wires; the in-FL host just makes ONE call at startup.

## What I built (all under `src/FruityLink.Plugins.Host/`)

- `FruityLink.Plugins.Host.csproj` — `ManagePackageVersionsCentrally=false`; needs nothing beyond the
  in-box BCL (System.Text.Json) + a single `ProjectReference` to `FruityLink.Plugins.Abstractions`
  (which transitively brings `FruityLink.Core`). `net9.0-windows` so WPF plugins load against the same
  Windows-desktop runtime. (Not added to `FruityLink.slnx` — out of scope.)
- `PluginManager : IPluginManager, IDisposable` — discovery, enable/disable, persistence, hot-reload.
- `PluginContext : IPluginContext` — the host-provided capability handle (Fl + Services + Log).
- `PluginLoadContext : AssemblyLoadContext` — collectible, per-plugin, shares the contract assemblies
  with the host so `IFlPlugin`/`IPluginContext`/`INativeFlControl` have one type identity.
- `PluginHotReloader` — debounced, file-stable `FileSystemWatcher` driver.
- `PluginHost` — the static `Initialize(...)` entry that sets `PluginManagerLocator.Current`.

## >>> The one-line bootstrap wiring you (FruityLink.Host.Bootstrap) must add <<<

At startup, once you have an `INativeFlControl` (e.g. the app's `FlInjectBridge`) and ideally your DI
`IServiceProvider`, call:

```csharp
FruityLink.Plugins.Host.PluginHost.Initialize(fl, services);
// fl       : INativeFlControl  (e.g. FlInjectBridge — the safe, typed FL surface)
// services : IServiceProvider  (your host container; pass an empty one if you have none yet)
```

That's it. It discovers plugins, re-enables the user's persisted-enabled set (awaiting their
`EnableAsync`), starts the hot-reload watcher, and publishes `PluginManagerLocator.Current` for the
native "Plugins" toolbar menu (re/16) to list/toggle. It is **idempotent** (returns the same manager on
repeat calls). It **blocks until restore completes**, so call it on a worker/startup thread — NOT on a
message-pump thread that a restored plugin's `EnableAsync` might marshal back to.

If you have no DI container at the call site, an empty provider is fine:
```csharp
PluginHost.Initialize(fl, EmptyServiceProvider.Instance); // any IServiceProvider returning null is OK
```

Optional overrides:
```csharp
PluginHost.Initialize(fl, services, pluginsDir: @"D:\custom\plugins", hotReload: false);
```

## Plugins directory layout (for the installer + the FL Agent plugin to target)

Default plugins root: **`<host-dir>\plugins\`** (i.e. `Path.Combine(AppContext.BaseDirectory,
"plugins")`, next to the host dll). Override via the `pluginsDir` arg to `Initialize`. Two layouts are
scanned (both supported simultaneously):

```
<host-dir>\plugins\
  ├─ fl-agent\                 ← RECOMMENDED: one folder per plugin package
  │    ├─ FruityLink.Plugins.FlAgent.dll   (the plugin)
  │    ├─ <private deps>.dll                (its own dependencies, ships alongside)
  │    └─ FruityLink.Plugins.FlAgent.deps.json
  └─ SomeOther.dll             ← also OK: a flat dll directly in the plugins root
```

- The **per-plugin folder** layout is preferred: the whole folder is shadow-copied per load, so a
  plugin's private dependencies resolve correctly (via its `.deps.json` +
  `AssemblyDependencyResolver`). Plugin projects should build with `<EnableDynamicLoading>true</...>`
  so a `.deps.json` is emitted.
- Contract assemblies (`FruityLink.Plugins.Abstractions`, `FruityLink.Core`) and `System.*`/
  `Microsoft.*` dlls are skipped as candidates and are SHARED from the host's default load context — a
  plugin may ship copies in its folder; they're ignored so contract types stay identity-equal. (So the
  installer doesn't need to special-case them, but it can omit them to save space.)
- Each plugin must be a **public class** implementing `IFlPlugin` with a **public parameterless ctor**
  (the manager instantiates it to read Id/Name/Description/Version). A dll may expose multiple plugin
  types. Construction must be cheap + side-effect-free — real work goes in `EnableAsync`.

## Persistence + data file locations

Data root: **`%LocalAppData%\FruityLink\`** (i.e. `C:\Users\<you>\AppData\Local\FruityLink\`), or the
path in env var `FRUITYLINK_PLUGINHOST_DIR` if set (handy for tests / portable installs).

| file/dir | purpose |
|---|---|
| `%LocalAppData%\FruityLink\plugins.json` | persisted **enabled** plugin ids. Format: `{ "Enabled": ["id1","id2"] }`. Read at discovery, rewritten on every enable/disable. |
| `%LocalAppData%\FruityLink\logs\plugin-host-yyyyMMdd.log` | host + plugin (`IPluginContext.Log`) diagnostics (also `Debug.WriteLine`). |
| `%LocalAppData%\FruityLink\plugin-shadow\<guid>\` | per-load shadow copies of plugin assemblies (auto-deleted on unload; whole root is purged at startup). |

## Public API (what the toolbar glue / a future "Reload" button can call)

Via `PluginManagerLocator.Current` (type `IPluginManager`):
- `IReadOnlyList<PluginInfo> List()` — every discovered plugin; `Enabled` = persisted choice,
  `Loaded` = assembly currently live in-process.
- `Task<bool> EnableAsync(string id)` / `Task<bool> DisableAsync(string id)` — idempotent,
  exception-safe, persist the choice. Disable also requests an ALC unload.
- `bool IsEnabled(string id)`.

Extra members on the concrete `PluginManager` (NOT on the interface — see note):
- `Task<bool> ReloadAsync(string id)` — manual reload of one plugin (stop → unload → load new bytes →
  re-enable if it was enabled). For a UI "Reload" button.
- `Task<bool> ReloadDllAsync(string originalDllPath)` — reload/add/drop by dll path.
- `void EnableHotReload()` / `void DisableHotReload()` / `bool HotReloadEnabled`.
- `void Discover()`, `Task RestoreEnabledAsync()` (used by `PluginHost.Initialize`).
- `IDisposable` — stops the watcher.

> **Reload is deliberately NOT on `IPluginManager`** (I don't own `FruityLink.Plugins.Abstractions`,
> and sibling agents depend on it). If we want a UI "Reload" button, promote `ReloadAsync` to the
> interface during integration; until then cast `PluginManagerLocator.Current` to `PluginManager`.

## Hot-reload design + toggle

- **Toggle:** default **ON** (dev productivity). Force via `Initialize(..., hotReload: true|false)`, or
  env var **`FRUITYLINK_PLUGIN_HOTRELOAD`** (`0`/`false`/`off`/`no` → off; anything else / unset → on).
  For a shipped/end-user build you'll likely pass `hotReload: false`.
- **Watcher:** `FileSystemWatcher` on the plugins dir (recursive). Events are **debounced** (~600ms
  quiet window) and each changed dll is confirmed **unlocked + size-stable** (retry-open loop, up to
  8s) before reload, so we never load mid-write.
- **On change:** rebuilt dll → if enabled: `DisableAsync` → unload old ALC → load NEW bytes into a
  fresh context → re-`EnableAsync` (enabled state preserved); if disabled: just refresh metadata/List.
  New dll/folder → discovered (disabled unless persisted-enabled). Removed → disabled + unloaded +
  dropped from `List()`.
- **Shadow-copy (key for dev):** assemblies are loaded from a private copy under the shadow root, never
  the original — so the original dll stays writable and a `dotnet build` over it succeeds AND triggers
  the watcher. (Verified: overwriting a loaded+enabled plugin in place succeeds and reloads it.)
- **Unload robustness (CoreCLR caveat):** `AssemblyLoadContext.Unload()` only *requests* an unload; the
  unmap is lazy (after GC sees no refs remain). On disable/reload we drop all refs and prod the GC
  (`GC.Collect` + `WaitForPendingFinalizers`, bounded). Even if an old context lingers (e.g. a plugin
  leaked a rooted ref), reload always loads the new version into a FRESH, versioned context, so reload
  keeps working. **Plugins MUST release everything in `DisableAsync`** (timers, event handlers, windows,
  background tasks) for a clean unload — document this for plugin authors.

## Build & verify (no FL needed — done)

```sh
dotnet build src/FruityLink.Plugins.Host/FruityLink.Plugins.Host.csproj -c Release   # 0 warn / 0 err
```
Offline smoke test (stub `IFlPlugin` + stub `INativeFlControl`, no FL launched): discovery, List /
EnableAsync / DisableAsync / IsEnabled, persistence round-trip, `PluginHost.Initialize` (restore +
idempotency + locator), and hot-reload (rebuild a loaded plugin → reloads v1→v2 with enabled state
preserved; original was overwritable, proving shadow-copy; removal drops it from List). **All 49
assertions passed.** (Smoke harness lives in the agent scratchpad, not committed.)

## Strictly NOT touched (per scope)

`src/FruityLink.Plugins.Abstractions/`, `src/FruityLink.Plugins.FlAgent/`, `src/FruityLink.Host/`, all
other `src/*`, `tools/`, `bootstrap/`, `installer/`, `marketing/`, `FruityLink.slnx`,
`Directory.*.props`. No git ops. Everything new lives under `src/FruityLink.Plugins.Host/` (plus this
notes file). To add the project to the solution later: `dotnet sln FruityLink.slnx add
src/FruityLink.Plugins.Host/FruityLink.Plugins.Host.csproj`.
