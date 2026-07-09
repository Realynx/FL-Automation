# Plugin lifecycle

How the host discovers, loads, isolates, activates, persists, pre-warms, and hot-reloads plugins. The
mechanics here live in `FruityLink.Plugins.Host` (`PluginManager`, `PluginLoadContext`,
`PluginHotReloader`, `PluginHost`).

See also: [Getting started](getting-started.md) · [FL control API](fl-control-api.md).

## Discovery

At startup the host scans the plugins directory (default `<host-dir>\plugins`) in two layouts:

- **Per-plugin folder:** `<plugins>\<pluginId>\MyPlugin.dll` plus its private dependencies.
- **Flat:** `<plugins>\MyPlugin.dll` directly in the plugins directory.

A dll qualifies as a plugin **only if its PE metadata references `FruityLink.Plugins.Abstractions`**.
This is a cheap metadata read (no assembly load) done before the expensive load path, so a plugin
package's dozens of framework/UI dependency dlls (Avalonia, SkiaSharp, …) are filtered down to the one
actual plugin dll. Native dlls (no managed metadata) and the contract/framework assemblies themselves
are skipped.

For each qualifying dll the host loads the assembly and registers every type that is:

- `public`, a class, non-abstract,
- assignable to `IFlPlugin`,
- and has a public parameterless constructor.

Each such type is instantiated once (cheaply — do no real work in the constructor) to read its `Id`,
`Name`, `Description`, and `Version`. A blank `Id` is skipped; a duplicate `Id` is ignored (first wins).

Discovery reads the persisted enabled-set so the plugin list reports the right on/off state, but does
**not** activate anything.

## Isolation: collectible ALC + shadow copy

Every plugin package is loaded into its **own collectible `AssemblyLoadContext`** (`PluginLoadContext`),
from a **shadow copy** rather than the original file. This buys two things:

- **Unloadable plugins.** Disabling a plugin requests an ALC unload so its assemblies can be dropped.
  (CoreCLR unloads *lazily* — the physical unmap happens after the GC observes no managed references
  remain into the context. The host drops its references and prods the GC, but a plugin that leaks a
  rooted reference — e.g. an un-removed static event handler — can block the unmap. A reload always
  loads the new version into a *fresh* context, so a lingering old one never breaks reloading.)
- **Rebuildable dlls / hot reload.** Because the assembly is loaded from a copy under the shadow root
  (default `%LocalAppData%\FruityLink\plugin-shadow`), the **original file stays writable** — a
  `dotnet build` over it succeeds and triggers the watcher.

### Shared (unified) assemblies

The contract assemblies are deliberately **shared** between the host and every plugin's load context, so
their types have a single identity across the boundary:

- `FruityLink.Plugins.Abstractions`
- `FruityLink.Core`

`PluginLoadContext` hands back the host's exact `Assembly` instance for those two. Without this, casting
the plugin instance to `IFlPlugin`, or passing an `IPluginContext` across, would fail with a
type-identity mismatch (the classic "unify the contract" ALC gotcha). **Everything else** — your
plugin's own code and its private dependencies — loads privately from the plugin folder via the
`deps.json`-driven dependency resolver.

Practical consequence: do not ship your own copy of the contract assemblies with a plugin expecting to
override the host's; they are always resolved from the host.

## Enable / disable and persistence

- **Enable** loads the assembly (if not already), builds the plugin's `IPluginContext`, and awaits
  `EnableAsync`. Idempotent — enabling an already-active plugin is a no-op.
- **Disable** awaits `DisableAsync`, then sweeps the plugin's menu/toolbar contributions (belt-and-
  suspenders, even if `DisableAsync` forgot), drops the instance + context, and requests the ALC unload.
- A plugin that throws from `EnableAsync`/`DisableAsync` is caught and logged — it never takes the host
  down. All state-changing operations are serialized.

The set of enabled plugin ids is persisted to `%LocalAppData%\FruityLink\plugins.json`, rewritten on
every enable/disable. At startup the host re-activates every persisted-enabled plugin.

## Two-phase boot: `IFlPreWarmPlugin`

Startup is split so heavy, **FL-independent** work overlaps FL's own load instead of running after it:

1. **Pre-warm (before FL is ready).** For each persisted-enabled plugin that implements
   `IFlPreWarmPlugin`, the host calls `PrepareAsync` *concurrently with FL's UI load*. This is where a
   UI toolkit cold-start / first off-screen paint / kernel build belongs.
2. **Enable (after FL is ready).** The host calls `IFlPlugin.EnableAsync` for the FL-dependent part
   (e.g. reparenting an already-built window into FL's chrome, reading the live project).

The net effect: the plugin's UI appears *with* FL's UI rather than seconds later.

`PrepareAsync` contract:

- **No FL calls.** FL is not ready — there is no live song/channel/window yet. Anything needing FL
  state belongs in `EnableAsync`.
- **Best-effort / fail-safe.** A failure here is never fatal; the host proceeds to `EnableAsync`, which
  is expected to build cold as a fallback.
- **Idempotent.** Safe if somehow invoked more than once.
- **Same context instance.** The `IPluginContext` handed to `PrepareAsync` is the *same* instance later
  passed to `EnableAsync`, so state you stash on the plugin instance carries across.

Plugins that don't implement `IFlPreWarmPlugin` behave exactly as before — all work in `EnableAsync`.

## Hot reload

Hot reload is driven by a `FileSystemWatcher` over the plugins directory (including subfolders):

- Editors and build tools emit a storm of events and briefly lock the output dll mid-write, so events
  are **coalesced over a ~600 ms debounce window** and each changed dll is confirmed **unlocked and
  size-stable** before the reload fires (never mid-write).
- On reload the host stops the affected plugin(s), unloads the old ALC, loads the current bytes into a
  **fresh** context, and re-enables anything that was enabled — preserving state.
- Structural changes (added/removed folders, `deps.json`, etc.) trigger a reconcile that discovers new
  plugins and drops entries whose backing file vanished.

Disable hot reload with `FRUITYLINK_PLUGIN_HOTRELOAD=0` (also accepts `false`/`off`/`no`). Reload is
also available programmatically regardless of the watcher.
