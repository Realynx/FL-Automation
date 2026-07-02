# Integration pending — "Plugins" menu (Tools > Plugins submenu)

Status: **built + proven live in FL Studio 2025.** A "Plugins" submenu is added inside FL's existing
**Tools** dropdown; it lists every installed plugin with its enabled state (✓) and toggles each through
the managed plugin manager (`PluginManagerLocator.Current`). End-to-end verified (see §5).

> Route history: started as a top-level toolbar entry (re/16 §3) — it rendered but FL's toolbar is a
> fixed-width, custom-painted strip (no room after Help, foreign child controls aren't painted, and a
> hand-flagged popup item crashed FL with a +0x45c null-deref). On the product owner's direction this
> was changed to a **Tools-submenu** item: native submenu rendering, **no toolbar width/clip problem,
> no custom popup**. That is the shipping design.

---

## (a) What was built (files — all in this agent's scope)

```
src/FruityLink.Host/
  PluginGlue.cs            NEW  two [UnmanagedCallersOnly] exports the native side calls:
                                  int ListJson(byte* buf,int len)  -> plugin list as JSON (UTF-8)
                                  int Toggle(byte* id,int idLen,int enable) -> enable/disable by id
                                read PluginManagerLocator.Current; null-degrade ("null" / -1) when absent.
  StubPluginManager.cs     NEW  TEST-ONLY IPluginManager (env-gated, never ships) — 4 fake plugins.
  HostEntry.cs             EDIT installs the stub when FRUITYLINK_PLUGIN_STUB=1 (test only); spawns a
                                background thread that calls plugins_button_install until the manager is
                                registered (so the submenu shows real plugins).
  FruityLink.Host.csproj   EDIT + ProjectReference to FruityLink.Plugins.Abstractions (Abstractions->Core
                                only; no cycle).

bootstrap/CLRHost/src/clrhost.cpp  EDIT resolves PluginGlue.ListJson + .Toggle (same mechanism as
                                Bootstrap) and exposes them to C++ via the exported
                                `void FlClr_GetPluginFns(void** outList, void** outToggle)`.

tools/bridge/dllmain.cpp   EDIT native menu glue + bridge commands (see §c). Talks to the managed
                                exports via GetModuleHandle("FlClrHost.dll") -> FlClr_GetPluginFns.

bootstrap/build-and-stage.ps1  EDIT also stages FruityLink.Plugins.Abstractions.dll into dist\FruityLink\.
```

Nothing outside this scope was changed. `FruityLink.slnx`, `Directory.*.props`, and every other
`src/*` are untouched. `FruityLink.Host.csproj` is still not in the solution (add it if you want IDE
builds: `<Project Path="src/FruityLink.Host/FruityLink.Host.csproj" />`).

## (b) The native↔managed↔manager wiring

```
FL main thread (menu click)                      managed (CoreCLR, in-FL)
  PluginItemClick(item)  ─ item+0x18 = tag         PluginGlue.Toggle(id,len,enable)
        │  callPluginToggle(id, !enabled)               │ PluginManagerLocator.Current.Enable/DisableAsync
        ▼                                                ▼
  FlBridge.dll ── GetModuleHandle("FlClrHost.dll")  ── FlClr_GetPluginFns(&list,&toggle)
        │            -> g_pluginToggleFn / g_pluginListFn (cached)
        └─ on open: callPluginList() -> PluginGlue.ListJson -> JSON -> parse -> build submenu items
```

- The CLR host (`FlClrHost.dll`) resolves the two managed function pointers right after `Bootstrap`
  and hands them out via `FlClr_GetPluginFns`. The bridge resolves them lazily (cached) the first time
  it needs the list/toggle. If the host isn't loaded (e.g. flprobe injected only `FlBridge.dll`), the
  lookup fails and the submenu degrades to a single disabled "Plugin host not ready" item.
- `ListJson` JSON shape: `[{"id":"..","name":"..","version":"..","enabled":true|false}, ...]`; only `\`
  and `"` are escaped (the native parser unescapes just those). Emits `"null"` when no manager is set.
- `Toggle` runs the async Enable/DisableAsync on the thread pool and blocks briefly (≤4 s) so it never
  deadlocks FL's UI thread; returns 1 ok / 0 fail / -1 no-manager.

## (c) Bridge commands (over the pipe or the in-proc FlBridge_Command)

```
plugins_button_install   add/refresh "Tools > Plugins" + its plugin children (MAIN thread)
plugins_button_remove    eject-safe removal: clear child onClick TMethods, free + detach our item
plugins_list             -> the managed plugin JSON ("{\"host\":0}" if no managed host)
plugins_toggle <id> <0|1>-> drive a toggle directly (bypasses the UI) -> {"ret":1|0|-1}
plugins_status           -> {"toolbarForm":..,"bar":..,"item":..,"hostFns":0|1}
```

`plugins_button_install` / `_remove` marshal onto FL's main thread via
`SendMessage(WM_BRIDGE_PLUGINSINSTALL/REMOVE)`. `plugins_list/_toggle` call the managed exports
directly (managed handles its own threading). After a UI toggle, the native click handler `PostMessage`s
WM_BRIDGE_PLUGINSINSTALL to itself so the submenu's checkmarks refresh after the popup closes.

### How the menu item is created (re/16 map, runtime-verified)
- `mainForm   = *(void**)(*(void**)rb(0x14a8750))`  ← **double-deref**: the `PTR_DAT_*` globals in this
  build hold the *address* of the real global (same as the proven `readPlaying` path). Single-deref
  gives the global's address, not the object.
- `actionList = *(mainForm + 0x760)`,  `masterRoot = *(actionList + 0x7c)`.
- `toolbarForm = *(void**)(*(void**)rb(0x14aa4c8))`,  `bar = *(toolbarForm + 0x878)` (diagnostics only).
- Find "Tools" among masterRoot children by caption `item+0x78` == `&Tools`.
- Append "Plugins" to Tools' submenu via `FLmenu_CreateItem_CaptionClick@0x70e1a0(toolsItem, -1,
  makeUStr(L"Plugins"), {0,0})`. Children added the same way with TMethod {PluginItemClick, ctx};
  set only the standard tag field `item+0x18` (do NOT poke +0x140/+0x80/+0x81 — that crashed FL's
  popup renderer). Enabled state shown via a `✓` caption glyph. No bar rebuild / no LayoutBar / no
  width change (it's a dropdown item).

## (d) Build

```powershell
pwsh bootstrap\build-and-stage.ps1     # builds version.dll, FlClrHost.dll, FlBridge.dll, FruityLink.Host
                                        # and stages bootstrap\dist\ (incl. FruityLink.Plugins.Abstractions.dll)
# or individually:
cmake --build tools\bridge\build   --config Release      # FlBridge.dll
cmake --build bootstrap\build       --config Release      # FlClrHost.dll + version.dll
dotnet build src\FruityLink.Host\FruityLink.Host.csproj -c Release
```

## (e) Startup install — what the in-FL host should do
The in-FL host already does this automatically: after `FlInjectBridge.UseInProcessTransport()`,
`HostEntry` spawns a thread that calls `InProcBridge.Raw("plugins_button_install")` repeatedly until the
plugin manager is registered (the toolbar/Tools menu may not exist for the first second of FL startup,
and the manager may be set later).

**For the real plugin host:** the moment you set `PluginManagerLocator.Current`, also call
`InProcBridge.Raw("plugins_button_install")` once to refresh the submenu with the real list immediately
(don't wait for the poll). It is idempotent — it reuses the existing "Plugins" item by caption.

## (f) Eject ordering (teardown — all on FL's MAIN thread, BEFORE FreeLibrary)
`BridgeStop()` does, in order:
1. `SendMessage(WM_BRIDGE_PLUGINSREMOVE)` → `DoPluginsRemove` on the main thread:
   - clear each plugin child's onClick TMethod (`+0x100/+0x108 = 0`) so our thunk can't be called,
   - `FUN_0040faa0(child)` frees each child, then `FUN_0040faa0(pluginsItem)` removes "Plugins" from
     Tools and frees the subtree.
2. `forceRestoreHook()` backstop (any thread, memory-only): `clearChildrenThunksMem(g_pluginsItem)`
   clears child thunks + hides the item — in case the main-thread path couldn't run.
3. revert the window subclass, then the loader frees the DLL.
This leaves Tools pristine (verified — the dropdown returns to its stock items, no crash).

Note: the in-process FlBridge.dll is pinned by the CLR host (CoreCLR can't unload), so it is not
FreeLibrary'd mid-session in production; the teardown path above is exercised by the flprobe
inject/eject dev loop.

## (g) Test (dev loop)
- Native-only / degrade: `flprobe inject` then `flprobe bridge plugins_button_install` →
  Tools > Plugins shows "Plugin host not ready" (no managed host). `flprobe eject` removes it cleanly.
- Full managed round-trip: launch FL with `FRUITYLINK_PLUGIN_STUB=1` in its environment, inject
  `bootstrap\dist\FruityLink\FlClrHost.dll` by full path (so its deps + FlBridge resolve from there).
  Then `flprobe bridge plugins_list` returns the stub JSON, `plugins_toggle <id> <0|1>` round-trips,
  and Tools > Plugins shows the 4 stubs with ✓ checkmarks; clicking one toggles it.
- **Restart FL clean** between tests — a normal restart drops the injected dev chain (the proxy is not
  installed to Program Files here; that needs elevation).

## (5) Evidence (live FL Studio 2025, FLEngine base 0x67F90000)
```
plugins_status  -> {"toolbarForm":"0x623C1C0","bar":"0x6249340","item":"0xB40B920","hostFns":1}
plugins_list    -> [{"id":"synthwave",...,"enabled":true},{"id":"autochord",...,"enabled":false},
                    {"id":"loudness",...,"enabled":true},{"id":"miditools",...,"enabled":false}]
plugins_toggle autochord 1 -> {"ret":1}   (list re-reads autochord.enabled=true)
plugins_toggle synthwave 0 -> {"ret":1}   (list re-reads synthwave.enabled=false)
```
Screenshots (scratchpad): Tools dropdown shows "Plugins ▸"; its submenu lists
"✓ Synthwave Pack / AutoChord / ✓ Loudness Meter / MIDI Tools". Clicking "AutoChord" in the submenu
flipped `autochord` false→true via the managed manager (confirmed by plugins_list before/after), FL did
not crash. `plugins_button_remove` returned Tools to its stock items with no crash.
