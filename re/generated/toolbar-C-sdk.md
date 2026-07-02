# toolbar-C-sdk — C# SDK + host↔bridge protocol for plugin toolbar buttons (+ FL Agent window toggle)

Design only (no code changed). Grounded in the live codebase. This is the **managed SDK + host↔bridge
protocol + FlAgent wiring** track; siblings `toolbar-A-buttons.md` / `toolbar-B-add.md` own the native
widget RE (the `TQuickBtn` create/parent/onClick/teardown recipe) which this consumes conceptually.

The whole design is a **near-clone of the existing menu-contribution pipeline** (`IFlMenuRegistrar` →
`MenuContributionRegistry` → `MenuGlue` → `FlClr_GetMenuFns` → `menu_contrib_*` → `DoMenuContribInstall`).
The ONLY genuinely new native code is the item-creation call (`FLmenu_CreateItem_CaptionClick` for a menu
row ⇒ the `TQuickBtn` recipe for a square button). Everything else is copy-shape-rename.

---

## 0. What already exists (reuse verbatim, don't reinvent)

| Layer | Menu pipeline (existing) | File |
|-------|--------------------------|------|
| Contract (write) | `IFlMenuRegistrar` (`AddToggle`/`AddCommand`/`Refresh`) | `src/FruityLink.Plugins.Abstractions/IFlMenuRegistrar.cs` |
| Contract (read) | `IFlMenuContributions` (`ListJson`/`Invoke`/`Checked`) + `FlMenuRegistryLocator` | `src/FruityLink.Plugins.Abstractions/FlMenuRegistryLocator.cs` |
| Context | `IPluginContext.Menu` | `src/FruityLink.Plugins.Abstractions/IPluginContext.cs` |
| Host registry | `MenuContributionRegistry` (`ScopeFor`/`Add`/`Changed`/`RemoveByPlugin`) | `src/FruityLink.Plugins.Host/MenuContributionRegistry.cs` |
| Manager wiring | `PluginManager._menu`, `.MenuRegistry`, `ScopeFor(id)` in `ActivateAsync_NoLock`, `RemoveByPlugin` in `DeactivateAsync_NoLock` | `src/FruityLink.Plugins.Host/PluginManager.cs:75,97,531,550` |
| Publish + refresh | `PluginHost.Initialize` sets `FlMenuRegistryLocator.Current`; `HostEntry` subscribes `Changed → menu_contrib_refresh` | `PluginHost.cs:63`, `HostEntry.cs:271` |
| Managed glue | `MenuGlue.ContributionsJson/Invoke/Checked` (`[UnmanagedCallersOnly]`) | `src/FruityLink.Host/MenuGlue.cs` |
| CLR host export | `FlClr_GetMenuFns` (resolves 3 methods via `loadAndGet`) | `bootstrap/CLRHost/src/clrhost.cpp:74,242` |
| Native transport | `resolveMenuFns`, `callMenuList`, `parseMenuJson`, `DoMenuContribInstall/Remove`, `MenuContribClick`, cmds `menu_contrib_install/refresh/remove/list` | `tools/bridge/dllmain.cpp:722,739,1207,1287,1304,1317,2262` |
| Deferred install driver | `HostEntry.InstallPluginsToolbarButtonDeferred` (retry loop until manager ready) | `HostEntry.cs:218` |
| Window host (target of the toggle) | `winhost_embed/show/close/min/max` + `EmbeddedChatHost` (`IsHostVisible`/`SetVisible`/`TryEmbed`/`Close`) | `dllmain.cpp:2281`, `src/FruityLink.Plugins.FlAgent/Ui/EmbeddedChatHost.cs` |

### 0.1 What `plugins_button_*` actually provides (important — it is NOT a square button)
`plugins_button_install` / `plugins_button_remove` / `plugins_status` (`dllmain.cpp:2208-2226`) despite the
"button" name build **the "FL Plugins" submenu prepended into FL's *Tools* dropdown** (`DoPluginsInstall`,
`dllmain.cpp:1130`), not a toolbar widget. So from `plugins_button_*` we reuse:
- the **deferred-install-with-retry** pattern (`HostEntry.cs:218`) — FL's toolbar form isn't up at Bootstrap,
  so a background thread retries every 1.5 s until it succeeds and the manager is registered;
- the **`resolvePluginFns` / SEH-guarded `callXxxRaw` / graceful-degrade** pattern (`dllmain.cpp:649-685`);
- the **`item+0x18` tag → cache index → managed invoke** click-dispatch pattern (`PluginItemClick`,
  `dllmain.cpp:929`).

The actual **big square toggle button** widget is the `TQuickBtn` path from `re/16-toolbar-plugins-menu.md`
§4 (proven-live toolkit), which siblings A/B are pinning:
`FLwp_CreateButtonControl@0xF0DDB0` → parent onto toolbar form content (vtbl `[0x138]` SetParent) → size
(vtbl `[0x188]` SetBounds) → caption `FUN_005d0ae0` → realize `FUN_005ceef0(btn,6)` → render
`FUN_0077adb0(btn)`; **onClick TMethod at `btn+0x1e4 (code)/+0x1ec (data)`**; teardown = restore `+0x1e4`,
`SetParent(0)`, destroy.

---

## 1. SDK surface (new — in `FruityLink.Plugins.Abstractions`)

Mirrors `IFlMenuRegistrar` exactly. Same trust-boundary rule: only ids/captions/callbacks + an opaque icon
blob cross — never addresses or raw memory (re/17-drm-guard). Callbacks fire on FL's UI thread; `isActive`
is polled while the toolbar is (re)built, so keep it a cheap cached-flag read.

```csharp
namespace FruityLink.Plugins.Abstractions;

/// <summary>Momentary (fires once per click) vs toggle (lit state reflects IsActive).</summary>
public enum FlToolbarButtonKind { Momentary, Toggle }

/// <summary>
/// How a plugin's square toolbar button draws its face. Provide ONE (checked in priority order
/// Png → GlyphName → Text). RECOMMENDED: <see cref="Png"/> — a plugin ships its own art so the button
/// carries its brand and is legible at the toolbar's square size; the host converts it to an FL bitmap
/// once. <see cref="GlyphName"/> reuses a named glyph from FL's own skin atlas (native look, limited set);
/// <see cref="Text"/> is a 1–3 char last resort drawn on the face.
/// </summary>
public sealed record FlToolbarIcon
{
    /// <summary>PNG (RGBA) bytes. Ship a square source (e.g. 32×32 / 48×48); the host scales to the bar.</summary>
    public byte[]? Png { get; init; }
    /// <summary>Named FL skin glyph (see the toolbar glyph catalog); used when <see cref="Png"/> is null.</summary>
    public string? GlyphName { get; init; }
    /// <summary>1–3 char text drawn on the face when neither PNG nor glyph is given.</summary>
    public string? Text { get; init; }

    public static FlToolbarIcon FromPng(byte[] png)   => new() { Png = png };
    public static FlToolbarIcon FromGlyph(string name)=> new() { GlyphName = name };
    public static FlToolbarIcon FromText(string text) => new() { Text = text };
}

/// <summary>Immutable description of one toolbar button. Id is stable + plugin-unique.</summary>
public sealed record FlToolbarButtonSpec
{
    /// <summary>Stable unique id, e.g. "fl-agent.window". Scoped/prefixed with the plugin id by the host.</summary>
    public required string Id { get; init; }
    /// <summary>Hover text shown in FL's hint bar (reuses the native `hint` setter).</summary>
    public required string Tooltip { get; init; }
    /// <summary>Face art. Default = empty (host falls back to first char of the tooltip).</summary>
    public FlToolbarIcon Icon { get; init; } = new();
    /// <summary>Sort hint among a plugin's own buttons (ascending). Ties broken by add order.</summary>
    public int Order { get; init; }
}

/// <summary>
/// Live handle to a registered button. Disposing removes it (all of a plugin's buttons are also removed
/// automatically on disable, exactly like menu contributions). Mutators let a plugin change the face/hint
/// without re-adding; <see cref="Invalidate"/> asks the host to re-query <c>isActive</c> + repaint now
/// (e.g. the tracked window was closed another way) — same role as <see cref="IFlMenuRegistrar.Refresh"/>.
/// </summary>
public interface IFlToolbarButton : IDisposable
{
    string Id { get; }
    void SetTooltip(string tooltip);
    void SetIcon(FlToolbarIcon icon);
    void Invalidate();
}

/// <summary>
/// Lets a plugin contribute big square buttons to FL's main toolbar. Obtained from
/// <see cref="IPluginContext.Toolbar"/>. Direct analogue of <see cref="IFlMenuRegistrar"/>.
/// </summary>
public interface IFlToolbarRegistrar
{
    /// <summary>
    /// Add a TOGGLE button. <paramref name="isActive"/> is polled when the toolbar is (re)built / refreshed
    /// so the lit state reflects reality (e.g. window open); <paramref name="onToggled"/> fires on click.
    /// </summary>
    IFlToolbarButton AddToggle(FlToolbarButtonSpec spec, Func<bool> isActive, Action onToggled);

    /// <summary>Add a MOMENTARY button; <paramref name="onClick"/> fires on click (no lit state).</summary>
    IFlToolbarButton AddButton(FlToolbarButtonSpec spec, Action onClick);

    /// <summary>Re-render this plugin's buttons now (refresh lit states). Cheap, any thread.</summary>
    void Refresh();
}
```

Read side (glue-facing) + locator — mirror `IFlMenuContributions` / `FlMenuRegistryLocator`:

```csharp
namespace FruityLink.Plugins.Abstractions;

/// <summary>Read view of the toolbar-button registry that the native glue drives. Plugins never use it.</summary>
public interface IFlToolbarButtons
{
    /// <summary>
    /// All buttons in display order as compact JSON (icon bytes NOT inlined — fetched out-of-band):
    /// <c>[{"id":"..","tooltip":"..","kind":"toggle"|"button","active":true|false,"order":0,
    /// "icon":"png"|"glyph"|"text","glyph":"..","text":".."}]</c>. <c>active</c> is evaluated live.
    /// Only <c>\</c> and <c>"</c> escaped (native unescaper handles just those).
    /// </summary>
    string ListJson();

    /// <summary>Fire the button's onClick/onToggled by id. False = unknown id. Never throws.</summary>
    bool Invoke(string id);

    /// <summary>Live lit state: 1 = active, 0 = inactive/momentary, -1 = unknown id.</summary>
    int Active(string id);

    /// <summary>Copy the button's PNG face bytes to <paramref name="png"/> (out-of-band, fetched once). False if none/unknown.</summary>
    bool TryGetIconPng(string id, out byte[] png);
}

public static class FlToolbarRegistryLocator
{
    public static IFlToolbarButtons? Current { get; set; }
}
```

`IPluginContext` gains one property (alongside `Menu`, `IPluginContext.cs:26`):

```csharp
/// <summary>Contribute big square buttons to FL's main toolbar (e.g. a window show/hide toggle).</summary>
IFlToolbarRegistrar Toolbar { get; }
```

---

## 2. Host registry (new — `FruityLink.Plugins.Host/ToolbarButtonRegistry.cs`)

A near-copy of `MenuContributionRegistry` (`MenuContributionRegistry.cs`). Same `Scoped`/`Handle` inner
classes, same `_sync` lock, same insertion-order list, same exception-guarded `EvalActive` (rename of
`EvalChecked`), same `RemoveByPlugin`, same `Changed` event. Additions over the menu version:

- record stores `Kind`, `FlToolbarIcon`, `Order`, `Func<bool>? IsActive`, `Action Handler`, plus mutable
  `Tooltip`/`Icon` for the `SetTooltip`/`SetIcon` handle mutators (each raises `Changed`).
- `ListJson()` emits the shape in §1 (adds `active`/`order`/`icon`-kind, omits blob bytes).
- new `Active(id)` (mirrors `Checked`), new `TryGetIconPng(id, out byte[])` reading the stored `Icon.Png`.
- implements `IFlToolbarButtons`; `ScopeFor(pluginId)` returns the per-plugin `IFlToolbarRegistrar`.

Wiring into `PluginManager` (mirror the four menu touch-points):
- field `private readonly ToolbarButtonRegistry _toolbar;` built in the ctor beside `_menu`
  (`PluginManager.cs:75`), and a `public ToolbarButtonRegistry ToolbarRegistry => _toolbar;`
  beside `MenuRegistry` (`:97`).
- `ActivateAsync_NoLock` builds the context with a toolbar scope too (`:531`):
  `new PluginContext(_fl, _services, _log, _menu.ScopeFor(id), _toolbar.ScopeFor(id))`.
- `DeactivateAsync_NoLock` backstop-removes (`:550`): `_toolbar.RemoveByPlugin(entry.Id);`
- `PluginContext` (`PluginContext.cs`) gains a `Toolbar` ctor param + property.

Publish + refresh (mirror `menu_contrib`):
- `PluginHost.Initialize` sets `FlToolbarRegistryLocator.Current = manager.ToolbarRegistry;` next to the
  menu locator (`PluginHost.cs:63`).
- `HostEntry.InitializeRealPluginHost` subscribes `pm.ToolbarRegistry.Changed += () =>
  InProcBridge.Raw("toolbar_button_refresh");` next to the menu subscription (`HostEntry.cs:271`), and calls
  `toolbar_button_add` in the deferred loop (`HostEntry.cs:230`) + post-init (`:283`).

Managed glue (new — `FruityLink.Host/ToolbarGlue.cs`, mirrors `MenuGlue.cs`), four
`[UnmanagedCallersOnly]` entry points reading `FlToolbarRegistryLocator.Current`:
- `ButtonsJson(byte* buf, int len)` → `ListJson()` (buffer-resize contract, returns full length).
- `Invoke(byte* idPtr, int idLen)` → `Invoke(id)` (1/0/-1).
- `Active(byte* idPtr, int idLen)` → `Active(id)` (1/0).
- `IconPng(byte* idPtr, int idLen, byte* buf, int len)` → copies `TryGetIconPng` bytes (buffer-resize
  contract; returns byte length, -1 none). New shape vs menu (menu had no icons).

CLR host (`bootstrap/CLRHost/src/clrhost.cpp`, mirror `FlClr_GetMenuFns` at `:74,242`):
- add `kToolbarGlueType = L"FruityLink.Host.ToolbarGlue, FruityLink.Host"`, four `void* g_toolbar*Fn`,
  four `loadAndGet(...)` resolves, and `extern "C" __declspec(dllexport) void FlClr_GetToolbarFns(void**
  outList, void** outInvoke, void** outActive, void** outIcon)`.

---

## 3. Host → bridge protocol (new bridge commands in `tools/bridge/dllmain.cpp`)

**Reconcile-from-managed-list model** (identical to `menu_contrib_*`): the host does NOT push per-button
create/update payloads; it calls one refresh and the native side pulls the whole button list + per-button
lit state + icons from the glue and reconciles. This keeps the protocol tiny and the source-of-truth in
managed code.

| Command | Direction | Payload | Meaning | Mirrors |
|---------|-----------|---------|---------|---------|
| `toolbar_button_add` | host→bridge | none | (re)materialize all buttons; `SendMessage(WM_BRIDGE_TOOLBARINSTALL)` | `menu_contrib_install` (`dllmain.cpp:2262`) |
| `toolbar_button_refresh` | host→bridge | none | same handler; re-query `Active` + repaint (rebuild only if id-set changed) | `menu_contrib_refresh` |
| `toolbar_button_remove` | host→bridge | none | eject-safe teardown of our buttons | `menu_contrib_remove` (`:2269`) |
| `toolbar_button_list` | host→bridge | none | diagnostics: raw managed JSON | `menu_contrib_list` (`:2273`) |
| `toolbar_button_icon <id>` | bridge internal | id string | fetch one button's PNG bytes (buffer-resize) | new (icons only) |

Reply JSON mirrors the menu replies, e.g. `toolbar_button_add` →
`{"ok":1,"count":N,"hostFns":1}`.

### 3.1 Payload / JSON contract
`ButtonsJson` output (the transport for add/refresh reconcile) — icon bytes deliberately **out-of-band**
(icons are static + potentially large; inlining base64 would bloat the frequently-refreshed list):
```json
[{"id":"fl-agent#1","tooltip":"FL Automate — show/hide","kind":"toggle","active":true,
  "order":0,"icon":"png"}]
```
Native parse = `parseToolbarJson` (clone of `parseMenuJson`, `dllmain.cpp:1207`; same fixed-field order,
same `jsonFindKey`/`jsonReadStr`). `icon`∈{`png`,`glyph`,`text`}; for `glyph`/`text` the extra `glyph`/
`text` string field is read; for `png` the native side then calls `toolbar_button_icon <id>` once at create
time and decodes the PNG → an FL bitmap assigned to the button face.

### 3.2 Icon transport
- On create, native issues the internal `toolbar_button_icon <id>` (buffer-resize protocol exactly like
  `callMenuList` at `dllmain.cpp:739-748`: try 64 KB, if `n>size` resize + retry).
- Managed `ToolbarGlue.IconPng` returns the raw PNG bytes for that id.
- Native decodes once (WIC/GDI+ or FL's own bitmap loader per sibling A findings), caches the FL bitmap by
  id, assigns to the `TQuickBtn` face, frees on button teardown. Re-fetch only if `SetIcon` bumped a
  per-id icon revision (carry an optional `"iconrev":N` field so refresh can detect a changed face).

### 3.3 Click callback (event-driven; no click polling)
Native `ToolbarBtnClick` thunk (clone of `MenuContribClick`, `dllmain.cpp:1304`), bound to the button's
onClick TMethod (`btn+0x1e4 code / btn+0x1ec data`, re/16 §4 / sibling B):
1. read the button's tag (index into `g_toolbarBtns`) → contribution id;
2. `callToolbarInvoke(id)` synchronously on FL's UI thread (managed `Invoke` self-marshals per SDK
   contract, so this returns promptly — no deadlock, same as menus);
3. `PostMessageW(g_mainWnd, WM_BRIDGE_TOOLBARINSTALL, 0, 0)` so the lit state repaints after the handler
   flips it (mirrors the menu's post-click rebuild).

### 3.4 State-sync loop (keeping a toggle's lit face == `isActive`)
**Primary — push on `Changed`** (reuses the wired pattern): any registrar mutation or a plugin's
`Invalidate()`/`Refresh()` raises `ToolbarButtonRegistry.Changed` → `HostEntry` fires
`toolbar_button_refresh` → native re-queries `Active(id)` for each toggle and repaints (no rebuild when the
id-set is unchanged; `Active` on FL's UI thread is a cheap cached-flag read per the SDK contract). This is
the exact path FlAgent already exercises for the View ✓ (see §4).

**Secondary — optional low-frequency poll backstop** (defensive): a native `WM_TIMER` on FL's main thread
(~500 ms–1 s) walks `g_toolbarBtns`, calls `Active(id)`, and repaints any button whose lit state drifted —
covers a state change a plugin forgot to announce. Recommended OFF by default (push covers FlAgent's cases:
native-X-hide, external-window close, menu/keyboard toggle); include as a one-line enable if a future plugin
mutates state silently.

### 3.5 Native materialization + teardown (the one genuinely new native part)
- `DoToolbarInstall` (clone of `DoMenuContribInstall`, `dllmain.cpp:1317`): remove prior buttons →
  `callToolbarList` → `parseToolbarJson` → for each, create a `TQuickBtn` via the sibling-B recipe
  (`FLwp_CreateButtonControl@0xF0DDB0`, SetParent vtbl[0x138] onto the toolbar-form content, SetBounds
  vtbl[0x188] as a square laid out at the far right of / next to the menu strip, caption `FUN_005d0ae0`
  when text-face, realize `FUN_005ceef0(btn,6)`, render `FUN_0077adb0`), assign the decoded icon bitmap,
  set onClick `btn+0x1e4=&ToolbarBtnClick; btn+0x1ec=g_toolbarCtx`, tag = list index, track in
  `g_toolbarBtns`.
- Toolbar-form pointer resolution: same as re/16 §5 (one-shot capture hook on
  `TToolbarForm.NewMainMenuPopup@0xcb9ee0` / `FLmenu_BarMouseDown@0x7059a0`, or the toolbar globals already
  read by `readToolbarPtrs`, `dllmain.cpp:870`).
- **Teardown (eject-safe)** = clone of `removeMenuContribItems` (`dllmain.cpp:1230`) + the memory-only
  backstop `clearMenuContribThunksMem` (`:1346`): clear `btn+0x1e4/+0x1ec` first (any thread), then on the
  main thread `SetParent(btn, 0)` + destroy + free the cached bitmap. Wire into the same unload path that
  already fires `menu_contrib_remove` (`dllmain.cpp:2488`).
- **Re-assert on toolbar recreate** (re/16 §6): FL rebuilds the toolbar form on UI-scale / "reset toolbars"
  changes, dropping our buttons; the deferred-install loop + a capture on FormCreate re-materializes them.

---

## 4. FL Agent window-toggle wiring (dogfood — real methods in `FlAgentPlugin.cs`)

The AI window IS a plugin, so its toolbar toggle is contributed through the SAME SDK — exactly as the plugin
already dogfoods the View-menu toggle today (`FlAgentPlugin.cs:99-104`). No special-casing.

**Register** — in `EnableAsync`, right beside the existing `_menuToggle = context.Menu.AddToggle(...)`
(`FlAgentPlugin.cs:99`), add a field `IFlToolbarButton? _toolbarButton;` and:
```csharp
_toolbarButton = context.Toolbar.AddToggle(
    new FlToolbarButtonSpec {
        Id      = "fl-agent.window",
        Tooltip = "FL Automate — AI assistant (show/hide)",
        Icon    = FlToolbarIcon.FromPng(LoadEmbeddedIcon()),   // ship the brand PNG with the plugin
    },
    // SAME predicate the View toggle uses (FlAgentPlugin.cs:102): lit == window actually visible.
    isActive:  () => _embedded ? EmbeddedChatHost.IsHostVisible() : _chatVisible,
    // SAME handler the View toggle uses (FlAgentPlugin.cs:103 / :250): show if hidden, hide if shown.
    onToggled: ToggleChatVisible);
```
- `isActive` reuses the identical expression at `FlAgentPlugin.cs:102` — `_embedded ?
  EmbeddedChatHost.IsHostVisible() : _chatVisible` — so the button lights exactly when the window is shown
  (incl. after the native X, which the WM_CLOSE handler turns into a hide; `EmbeddedChatHost.cs:160`).
- `onToggled` reuses `ToggleChatVisible()` verbatim (`FlAgentPlugin.cs:250`): when embedded it flips
  `EmbeddedChatHost.SetVisible(!vis)` (`:278`, drives `winhost_show`/`ShowWindow` on the host form); when
  external it calls `avChat?.SetVisible(show)` (`:291`) or WPF `window.Show()/Hide()` (`:299-301`).

**Keep the lit state in sync** — the three places that already flip `_chatVisible` + call
`_context?.Menu.Refresh()` add a toolbar refresh (or the plugin holds the `IFlToolbarButton` and calls
`_toolbarButton?.Invalidate()`):
- `ToggleChatVisible` after driving visibility (`FlAgentPlugin.cs:284`);
- `AvaloniaChatView.HiddenByUser` handler (`FlAgentPlugin.cs:139-143`, external X → hide);
- `OnChatWindowClosing` (`FlAgentPlugin.cs:311-318`, WPF X → hide).

**Teardown** — in `DisableAsync`, dispose it beside `_menuToggle?.Dispose()` (`FlAgentPlugin.cs:354`):
`try { _toolbarButton?.Dispose(); } catch { }` (and null it under `_gate` with the other fields at `:344`).
`PluginManager.DeactivateAsync_NoLock` also calls `_toolbar.RemoveByPlugin("fl-agent")` as a backstop
(mirror `MenuContributionRegistry` line `:550`).

Net UX: a big square button on FL's main toolbar, lit whenever the FL Automate chat window is visible;
clicking it shows/hides that window; closing the window any way un-lights the button. Identical semantics to
the existing View ▸ FL Automate toggle, just on the toolbar.

---

## 5. Phased build plan (existing = reuse, new = add)

**Phase 0 — consume sibling RE.** Wait for `toolbar-A-buttons.md` (widget struct/bitmap API) +
`toolbar-B-add.md` (add/parent/onClick/teardown recipe); confirm `FLwp_CreateButtonControl@0xF0DDB0`,
onClick `+0x1e4/+0x1ec`, and the toolbar-form content parent live via flprobe. *(No managed dependency —
runs in parallel with Phase 1.)*

**Phase 1 — Managed SDK + registry + glue (NO native dependency; unit-testable without FL).** *New:*
Abstractions types (`IFlToolbarRegistrar`, `IFlToolbarButton`, `FlToolbarButtonSpec`, `FlToolbarIcon`,
`FlToolbarButtonKind`, `IFlToolbarButtons`, `FlToolbarRegistryLocator`, `IPluginContext.Toolbar`);
`ToolbarButtonRegistry`; `ToolbarGlue`. *Reuse (extend):* `PluginContext` ctor, `PluginManager`
(`_toolbar`/`ToolbarRegistry`/scope/remove), `PluginHost.Initialize` (publish locator). Test: ListJson /
Invoke / Active / TryGetIconPng round-trip, `RemoveByPlugin`, `Changed` firing — all in-proc, no FL. Ships
inert (native missing ⇒ no buttons, graceful degrade — same contract as menus).

**Phase 2 — Native materialization.** *New:* in `dllmain.cpp` — `resolveToolbarFns` +
`FlClr_GetToolbarFns` (in `clrhost.cpp`), `callToolbarList/Invoke/Active/Icon`, `parseToolbarJson`,
`DoToolbarInstall/Remove`, `ToolbarBtnClick`, `WM_BRIDGE_TOOLBARINSTALL/REMOVE`, commands
`toolbar_button_add/refresh/remove/list/icon`, PNG decode+cache, eject-safe teardown, re-assert on toolbar
recreate. *Reuse:* the SEH-guard/degrade helpers, the buffer-resize list protocol, the deferred-install
loop (`HostEntry.InstallPluginsToolbarButtonDeferred` shape), the `Changed → refresh` subscription.
Verify via flprobe (`toolbar_button_add`, click a test button, `toolbar_button_list`) BEFORE touching
FlAgent.

**Phase 3 — FlAgent toggle button (dogfood; delivers the built-in AI-window toggle).** *New (small):*
`_toolbarButton` field + `AddToggle` in `EnableAsync`, dispose in `DisableAsync`, toolbar refresh in the 3
visibility-change paths, ship the PNG asset. *Reuse:* `ToggleChatVisible` + the `isActive` predicate +
`EmbeddedChatHost` unchanged.

**Phase 4 — polish.** Icon caching/scaling + `iconrev` diffing, optional poll backstop (§3.4), per-id
granular repaint to avoid full rebuild on frequent toggles, plugin-author docs, and (optional) a granular
`toolbar_button_update <id>` if profiling shows full-list refresh is too coarse.

---

## 6. Key decisions (summary)
- **Shape = clone the menu pipeline.** 90% is copy-rename of `IFlMenuRegistrar`/`MenuContributionRegistry`/
  `MenuGlue`/`FlClr_GetMenuFns`/`menu_contrib_*`; only native item-creation differs (menu row ⇒ `TQuickBtn`).
- **Icon delivery = plugin-supplied PNG (recommended), out-of-band fetch**, with `GlyphName` (FL skin atlas)
  and `Text` fallbacks. Out-of-band keeps the frequently-refreshed list JSON small.
- **Click = event-driven** native thunk → managed `Invoke(id)` → post-refresh (no click polling).
- **State-sync = push on `Changed`** (primary, already wired for FlAgent's window visibility) + optional
  low-freq poll backstop.
- **FL Agent toggle = a normal plugin contribution** via the new SDK, reusing `ToggleChatVisible()` and the
  existing `isActive` predicate — dogfooded exactly like today's View-menu toggle.
