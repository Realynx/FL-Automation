# Menus and toolbar

A plugin can contribute entries to FL Studio's own native menus and main toolbar. Both are obtained from
the plugin context (`IPluginContext.Menu`, `IPluginContext.Toolbar`) and are part of the **trust
boundary**: a contribution is only a caption plus a pair of managed callbacks — no raw memory, addresses,
or FL internals ever cross it. The host materializes the entries natively and routes clicks and
state-queries back to your callbacks by an opaque id.

See also: [FL control API](fl-control-api.md) · [Window embedding](window-embedding.md).

## Menus — `IFlMenuRegistrar`

FL has eight native top-level dropdowns, named by the `FlNativeMenu` enum: `File`, `Edit`, `Add`,
`Patterns`, `View`, `Options`, `Tools`, `Help`. `View` is where FL keeps its window show/hide toggles,
so a window toggle belongs there.

```csharp
// A plain command:
IDisposable cmd = ctx.Menu.AddCommand(FlNativeMenu.Tools, "Do the thing", onInvoke: () => { /* … */ });

// A checkable toggle — isChecked is POLLED when the menu is (re)built so the ✓ reflects live state:
IDisposable tog = ctx.Menu.AddToggle(
    FlNativeMenu.View, "Show my window",
    isChecked: () => _window is { IsVisible: true },
    onToggled: () => ToggleWindow());
```

- **`AddCommand(menu, caption, onInvoke)`** — a non-checkable item; `onInvoke` fires on click.
- **`AddToggle(menu, caption, isChecked, onToggled)`** — a checkable item; `isChecked` is polled to
  render the ✓, `onToggled` fires on click.
- **`Refresh()`** — ask the host to re-render this plugin's menu entries now. Call it when a toggle's
  checked state changed *without* a menu click (e.g. the window it tracks was closed another way) so the
  ✓ updates without waiting for the next natural rebuild. Cheap and safe from any thread.

Both add methods return an `IDisposable` handle; `Dispose()` removes that single entry.

## Toolbar — `IFlToolbarRegistrar`

Big square buttons on FL's main toolbar, alongside FL's own metronome / typing-keyboard toggles. Direct
analogue of the menu registrar.

```csharp
IDisposable toggle = ctx.Toolbar.AddToggle(
    caption: "AI",                 // short text/glyph on the button face (1–3 chars work best)
    tooltip: "Show the assistant", // shown in FL's hint bar on hover
    isActive: () => _window is { IsVisible: true },
    onToggled: () => ToggleWindow());

IDisposable button = ctx.Toolbar.AddButton("GO", "Run once", onClick: () => RunOnce());
```

- **`AddToggle(caption, tooltip, isActive, onToggled)`** — a lit toggle; `isActive` is polled for the lit
  state when the toolbar is (re)built.
- **`AddButton(caption, tooltip, onClick)`** — a momentary (non-lit) button.
- **`Refresh()`** — re-render this plugin's toolbar buttons now (same purpose as the menu `Refresh`).

`caption` is a **short text/glyph** drawn on the square button face — PNG icons are not supported yet.
Both add methods return an `IDisposable` handle.

## Threading — read this

**All menu/toolbar callbacks fire on FL's UI thread.**

- `isChecked` / `isActive` are **polled while the menu/toolbar is being (re)built.** Keep them fast and
  **non-blocking** — read a cached flag, never block on another thread or await a bridge call inside
  them. A slow poll stalls FL's menu/toolbar rendering.
- If a click handler touches UI that lives on a *different* thread (e.g. a WPF or Avalonia window on its
  own dispatcher), marshal to that thread yourself.
- Anything that awaits the [FL control surface](fl-control-api.md) should run off the UI thread — wrap it
  in `Task.Run` so the callback returns promptly.

## Disposal and the automatic sweep

Every contribution is scoped to the plugin that added it. Two things clean them up:

1. **Explicit disposal** — call `Dispose()` on a returned handle to remove that single entry early.
2. **Automatic sweep on disable** — when the plugin is disabled (or its assembly is unloaded), the host
   removes **all** of that plugin's menu and toolbar contributions as a set, even if the plugin's
   `DisableAsync` forgot to dispose them or threw.

Best practice is still to dispose your handles in `DisableAsync` (as the [`HelloFl`](../samples/HelloFl)
sample does) — it's tidy and lets you remove entries at any time — but the sweep is your safety net.
