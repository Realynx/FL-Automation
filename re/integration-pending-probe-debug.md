# Integration pending — flprobe debug pipe (plugin loop) + attach (proxy-install model)

`flprobe` (`tools/FruityLink.Probe/`) is the FL runtime + plugin dev harness. With FruityLink moving
from DLL injection to the transparent **proxy-DLL filesystem install**
(`re/integration-pending-proxy.md`), the bridge is loaded **in-process** by the proxy chain and serves
the SAME named pipe `\\.\pipe\FruityLinkBridge`. flprobe therefore talks to it WITHOUT injecting.

This doc is the **contract for the bridge/Host side**: the NEW text pipe commands flprobe's
`flprobe plugin …` loop depends on. Implement these handlers in the same pipe dispatcher that already
serves `ping`/`info`/`peek`/`poke`/`call`/`callabs`/`key`/`shutdown`/`status` — those stay EXACTLY as
they are. The new commands are line/message-mode text, request → single response, same framing as the
existing protocol (message-mode pipe; flprobe reads until `IsMessageComplete`).

## What flprobe already does without any bridge changes

- `flprobe attach` — connects straight to the pipe (no injection), prints `ping` + `info`. It does NOT
  gate on the `FlBridge*` module heuristic (a proxy/in-process bridge does not surface that module), so
  the ONLY success criterion is that the pipe answers. Works today against the existing pipe.
- `flprobe bridge <cmd>` — sends `<cmd>` straight to the pipe (no module pre-flight). Works today.
- `flprobe plugin install|update|dev` — copy the publish closure into `<pluginsDir>\<id>\`. Because the
  host watches that dir (FileSystemWatcher + shadow-copy) the copy ALONE hot-reloads the plugin. The
  pipe commands below are a best-effort nudge + the read side of the loop; the copy works regardless.

## NEW pipe commands the bridge/Host side must implement

These should be backed by the managed `PluginManager` published at
`FruityLink.Plugins.Host.PluginManagerLocator.Current` (`re/integration-pending-plugin-host.md`).
Wire them on the in-process side that owns the bridge dispatcher (it can P/Invoke into managed or call
managed directly, depending on how the Host exposes the manager to the bridge).

| request | response (success) | response (failure) | maps to |
|---|---|---|---|
| `plugins_list` | a JSON **array** of objects (see schema below) | `err: <reason>` | `PluginManager.List()` |
| `plugins_dir` | the **absolute path** of the active plugins directory (one line, no quotes) | `err: <reason>` | the `pluginsDir` passed to `PluginHost.Initialize` (default `<host-dir>\plugins`) |
| `plugin_enable <id>` | `ok` | `err: <reason>` | `await PluginManager.EnableAsync(id)` |
| `plugin_disable <id>` | `ok` | `err: <reason>` | `await PluginManager.DisableAsync(id)` |
| `plugin_reload <id>` | `ok` | `err: <reason>` | `await ((PluginManager)mgr).ReloadAsync(id)` |

### `plugins_list` JSON schema

A JSON array; each element:

```json
{
  "id":      "fl-agent",        // string  — IFlPlugin.Id (the stable id used by enable/disable/reload)
  "name":    "FL Agent",        // string  — IFlPlugin.Name (display)
  "version": "1.0.0",           // string  — IFlPlugin.Version
  "enabled": true,              // bool    — PluginInfo.Enabled (persisted choice in plugins.json)
  "loaded":  true               // bool    — PluginInfo.Loaded  (assembly live in-process right now)
}
```

flprobe pretty-prints this array verbatim (System.Text.Json, indented). Field names are
**lower-case** exactly as above. Extra fields are allowed (flprobe ignores them); the five above are
what the loop relies on. An empty list is `[]` (not `err:`).

### Conventions flprobe relies on

- **Error sentinel:** any response whose first non-space characters are `err` (case-insensitive — e.g.
  `err: no such plugin 'foo'`) is treated by flprobe as a failure: it prints the raw text and exits
  non-zero. So success responses for `plugins_dir` must NOT start with `err` (an absolute Windows path
  never does).
- `ok` is the success token for the three toggle/reload commands (leading/trailing whitespace is
  trimmed by flprobe; any non-`err` reply is treated as success and echoed).
- `<id>` is the `IFlPlugin.Id`. NOTE: flprobe also uses `<id>` as the **folder name** under
  `plugins\<id>\` when copying (install/dev). If a plugin's `Id` differs from its publish folder/dll
  name, the user passes `--id <Id>` so the copy target folder and the enable/reload id match. The host
  discovers plugins by scanning folders and reading `IFlPlugin.Id`; the folder name itself is not
  required to equal the id, but keeping them equal makes the one-command loop "just work".
- `plugin_reload <id>`: if the id isn't loaded yet (fresh copy just landed and the watcher hasn't
  fired), returning `ok` after a discover+load, or `err: not found` (flprobe treats reload as
  best-effort during install — it only warns), are both acceptable.

## How the loop runs live (once the handlers land)

```
flprobe plugin dev src\FruityLink.Plugins.FlAgent --id fl-agent
  → dotnet publish -c Debug  → temp stage (full closure: dll + deps + .deps.json)
  → resolve plugins dir: pipe 'plugins_dir'  (else --plugins-dir, else <FL install>\FruityLink\plugins)
  → copy stage\*  →  <pluginsDir>\fl-agent\         (host FileSystemWatcher hot-reloads)
  → pipe 'plugin_reload fl-agent'                    (best-effort nudge)
  → pipe 'plugin_enable fl-agent'                    (best-effort)
flprobe plugin list                                  → JSON table of installed plugins
```

`plugin install <publishDir>` is the same minus the publish step. `enable`/`disable`/`reload <id>` are
direct pipe calls.

## Plugins-dir resolution (so the bridge answer is authoritative)

flprobe resolves the target dir in this order:
1. pipe `plugins_dir` (authoritative — the running host tells us exactly where it scans),
2. `--plugins-dir <dir>` (explicit override / offline),
3. derived default: directory of the running `FL64.exe` + `\FruityLink\plugins`, else a Program Files
   `Image-Line\FL Studio*\FruityLink\plugins` scan.

So implementing `plugins_dir` is the single most useful handler — it removes all guessing about where
the host actually watches (it should return the exact `pluginsDir` `PluginHost.Initialize` was given).

## Current bridge state (observed 2026-06-30) — reconcile to the contract above

The live `tools/bridge/dllmain.cpp` pipe dispatcher ALREADY has a partial, differently-named surface
(it returns `err:unknown` for everything else — which flprobe treats as a clean fallback, so the loop
degrades gracefully today):

| already in the bridge | this contract wants | action for the bridge author |
|---|---|---|
| `plugins_list` → `callPluginList()` JSON, or `{"host":0}` when host glue is unavailable | `plugins_list` → array of `{id,name,version,enabled,loaded}` | confirm `callPluginList()` emits exactly that array shape; `{"host":0}` (host not wired) is fine — flprobe just prints it |
| `plugins_toggle <id> <0|1>` → `{"ret":N}` | `plugin_enable <id>` / `plugin_disable <id>` → `ok`/`err:` | add `plugin_enable`/`plugin_disable` (can delegate to the same toggle), returning `ok`/`err:` |
| *(none)* | `plugin_reload <id>` → `ok`/`err:` | add it → `((PluginManager)mgr).ReloadAsync(id)` |
| *(none)* | `plugins_dir` → absolute path | add it → return the dir `PluginHost.Initialize` watches |
| `plugins_button_install` / `plugins_button_remove` / `plugins_status` (re/16 toolbar) | n/a | unrelated to flprobe; leave as-is |

flprobe deliberately uses the contract names (`plugin_enable`/`plugin_disable`/`plugin_reload`,
`plugins_dir`), NOT `plugins_toggle`, so the bridge side should add those names (aliasing to existing
logic is fine).

## What the bridge/Host side still needs to do for the loop to work live

1. Add the 5 handlers above to the bridge pipe dispatcher (the one serving the existing text protocol),
   delegating to `PluginManagerLocator.Current` (cast to `PluginManager` for `ReloadAsync`).
2. Ensure `PluginHost.Initialize(fl, services)` has run in the proxy-hosted process so the locator is
   published (per `re/integration-pending-plugin-host.md` — call it on a startup/worker thread).
3. Return `plugins_dir` = the SAME directory `PluginHost.Initialize` watches (default
   `Path.Combine(AppContext.BaseDirectory, "plugins")` next to the host dll).
4. Marshalling: `EnableAsync`/`DisableAsync`/`ReloadAsync` are awaited and may marshal to the UI/STA
   thread; do not call them on a thread that would deadlock the pipe worker — hop to a worker/Task and
   write the response when it completes (the existing pipe worker already serializes requests).

Until then, `flprobe plugin install/dev` still lands files and the host's watcher hot-reloads;
`plugins_list` / `plugins_dir` / `plugin_*` just report the raw pipe error and exit non-zero (flprobe
never throws). End-to-end live verification is the follow-up once these handlers ship.
