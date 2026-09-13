# Window embedding

Plugins can host their UI *inside* FL Studio's own window chrome using a native FL form. The
`IFlWindowHost` capability (exposed on `IPluginContext` as `Windows`) accepts a Win32 `HWND` and
preserves a regular external window when native hosting is unavailable.

## Independent plugin windows

The current host gives each plugin an independent session through `context.Windows`. New
integrations use `IAsyncFlWindowHost`: native creation, visibility and release run on workers
while the child's UI dispatcher keeps pumping. Blocking a shared Avalonia thread on FL could
otherwise block every embedded child on that thread.

`FlWindowOptions` supplies the caption and preferred/minimum content sizes in **physical pixels**.
Avalonia's `view.GetWindowOptions(caption)` converts its DIP dimensions using the current rendering
scale. Creation returns a hidden native form; the adapter reparents on the child's owning thread
and awaits a separate bind acknowledgement before native resize tracking begins.

The native content container uses the form's verified inner client rectangle, bounded by the HWND
client area and mapped FL caption. No internal content-control pointer is assumed. Replies describe the
container's own client coordinates (`cx=0`, `cy=0`), so consumers do not apply a second titlebar inset.

```csharp
// Execute on the Avalonia dispatcher. These awaits keep it free for other windows.
view.PrepareForEmbedding();
if (context.Windows is IAsyncFlWindowHost host &&
    await host.TryEmbedAsync(view.Handle, view.GetWindowOptions("My plugin"), show: false))
{
    view.PinToHostContent(host.LastInsetX, host.LastInsetY);
    await host.SetVisibleAsync(true, activate: false);
    view.ForceRender();
}
else
{
    view.ShowExternal();
}
```

Do not call synchronous `IsBridgeAvailable` from a shared UI thread. Attempt async embedding
directly or await `IsBridgeAvailableAsync`. Explicit menu/toolbar reopening may use
`SetVisibleAsync(true, activate: true)` and then focus the intended input control. Initial
presentation and hiding should not steal focus. External fallback preserves its position after
the first show rather than recentering each toggle.

Plugins needing additional windows call
`((IFlWindowHostFactory)context.Windows).CreateWindowHost("browser", "Plugin browser")`.
Identifiers are local to the plugin; the same identifier retrieves its existing scope. Disabling
the plugin closes all its scopes. One plugin cannot control another plugin's native window.

Teardown awaits detachment before destroying the toolkit window:

```csharp
if (!await host.CloseAsync())
    throw new InvalidOperationException("Detach is incomplete; retain the view and retry.");
await view.CloseAsync();
```

A false embed result means the child was restored for external fallback. An exception can mean
cleanup was not confirmed; propagate it to lifecycle handling instead of showing an uncertain
still-parented child externally. Failed teardown retains the instance, load context and assets
until a retry succeeds. Cancellation waits for started native work rather than abandoning a
creation task that could complete later.

Log `LastEmbedReply` when embedding returns false. A native `reason`, such as
`native-content-bounds-unavailable`, identifies the failed creation step; it does not mean that
the bridge is absent. The Python IDE records this diagnostic as a bounded single log line before
using its external window. Do not turn an unconfirmed cleanup exception into a normal fallback.

The synchronous API below remains for compatibility. New scoped sessions refuse synchronous
embedding with an actionable diagnostic, preserving external fallback for legacy callers. The
legacy root adapter retains its single-slot behavior. Use the async lifecycle for new plugins.

If you host **Avalonia** UI, use [FruityLink.Ui.Avalonia.Hosting](avalonia-ui.md) instead of driving
the raw HWND embed yourself. If you host **WPF** UI, use `FruityLink.Ui.Wpf.Hosting`
([below](#wpf-windows)) — a raw WPF window *will* embed, and will then hit three known Win32/WPF bugs
that package exists to fix.

The surface (see the interface's XML docs for the fine print):

```csharp
public interface IFlWindowHost
{
    bool   IsBridgeAvailable();              // is the native bridge loaded in-process?
    bool   TryEmbed(IntPtr childHwnd, bool show);  // create FL host form + reparent your window
    bool   IsHostVisible();
    void   SetVisible(bool visible);
    void   Close();
    void   SetStatusHint(string text);       // write FL's status/hint bar
    string LastEmbedReply { get; }           // raw bridge reply of the last embed (diagnostics)
    int    LastInsetX { get; }               // content-area insets of the host form
    int    LastInsetY { get; }
}
```

See also: [Avalonia UI](avalonia-ui.md) · [Menus and toolbar](menus-and-toolbar.md) ·
[Native bridge](native-bridge.md).

## What it does

The host creates a native FL host form and reparents your window's `HWND` into it as a `WS_CHILD`. Your
window then behaves like a first-class FL sub-window: it carries FL's border/close chrome, can be shown
and hidden (drive that from a `View`-menu toggle or a toolbar toggle — see
[Menus and toolbar](menus-and-toolbar.md)), and is resized to fit the host form's content area.

Under the hood this uses native bridge messages for creation, binding, visibility and release. The
current native chrome supports close and maximize; minimize/menu controls are hidden and docking
requests are refused. The host marshals each operation to the correct thread (see below).

## The legacy embed threading contract

Reparenting a window across process-internal UI threads is the part that goes wrong if you improvise.
The rules the host follows, and that you must respect:

- **Phase A — the FL host form is created on FL's main thread.** `TryEmbed` first asks the bridge
  (a blocking call marshalled to FL's main thread) to create and show the host form. This phase never
  touches your window, so it cannot deadlock against your UI thread.
- **Phase B — `SetParent` + child restyle happen on the CALLER's thread.** Call `TryEmbed` from the
  thread that owns your window (its UI thread). Windows only reparents a window cleanly from its owning
  thread, so the actual `SetParent(child, hostContent)` and the `WS_CHILD` style flip run right there on
  your thread — never on FL's.
- **Never issue a blocking bridge call while parented, from the thread that owns the window.** A
  synchronous bridge round-trip blocks your UI thread; if FL's thread is simultaneously waiting on your
  window to pump (as it can be once you are its child), that is a deadlock. Do FL control calls off the
  UI thread once embedded.

If you host Avalonia via the hosting package, all of this is handled for you; the rules above are what
that package implements.

## Rendering note

A window reparented as a `WS_CHILD` of FL's (non-.NET) host form hits the classic DWM "airspace" problem
— a GPU-composited child's redirection surface is not presented inside the foreign parent and goes blank
until something invalidates it. The fix is **software rendering** for the embedded child plus a forced
repaint after embed/re-show. The Avalonia hosting package does this for you; if you embed your own HWND,
render it in software while it is parented. This is covered in detail for Avalonia in
[avalonia-ui.md](avalonia-ui.md#gotchas).

## WPF windows

`FruityLink.Ui.Wpf.Hosting` is the WPF counterpart of the Avalonia hosting package:

- **`WpfUiThread`** — provides the WPF `Dispatcher` to host your window on: it reuses the host
  process's WPF UI thread when one exists (inside FL Studio it does — the FruityLink host is a WPF
  app), else spins up a private STA dispatcher thread.
- **`EmbeddedWpfView`** — wraps any `Window` and applies the live-verified workarounds a WPF window
  needs to survive as a `WS_CHILD` of FL's non-WPF host form:
  1. **Software rendering with an opaque backdrop.** WPF's DWM/hardware composition hits the airspace
     bug inside a foreign parent (blank until input); and without an opaque backdrop, empty child areas
     render transparent, letting FL's own content bleed through.
  2. **A `WM_WINDOWPOSCHANGING` position pin.** Once it's a child, WPF keeps re-applying
     `Window.Left/Top` and moves the window off the host (the classic WPF-window-as-child coordinate
     bug); the pin forces it to fill the host's content area, inset below FL's titlebar.
  3. **Re-present after resize/re-show.** In software mode inside a foreign parent WPF does not
     re-present on its own after the host is resized, maximized, docked, or re-shown — the view stays
     blank until an input event. The wrapper forces a synchronous repaint (and, on real resizes, posts
     a synthetic mouse-move that kicks WPF's full render+present cycle — posted, so it never moves the
     OS cursor or clicks).

The whole flow, on the window's UI thread:

```csharp
var ui = new WpfUiThread();
ui.Start();
ui.Dispatcher.Invoke(() =>
{
    var window = new MyPluginWindow();
    var view   = new EmbeddedWpfView(window);          // backdrop defaults to the window's Background

    view.PrepareForEmbedding();                        // borderless, off-screen, no taskbar/activation
    window.Show();                                     // force a WPF layout/render pass before reparenting
    bool embedded = context.Windows.TryEmbed(view.EnsureNativeHandle(), show: true);
    if (embedded)
    {
        view.PinToHostContent(context.Windows.LastInsetX, context.Windows.LastInsetY);
        view.ForceRerender();                          // synchronous first paint (else blank until a click)
    }
    else
    {
        view.RestoreExternalChrome();                  // external top-level fallback (always works)
        window.Show();
        window.Activate();
    }
});
```

Call `view.ForceRerender()` after every re-show (`context.Windows.SetVisible(true)`), for the same
reason as workaround 3.

## Status hint bar

FL's status/hint bar (the strip that shows the name/tooltip of whatever is under the mouse) is readable
through the control surface via `INativeFlControl.GetStatusAsync()`, and the host can *write* a message
to it (FL Automate uses this for its "loading…" indicator during boot, cleared once the UI is embedded).
Use it for brief, transient status — it is FL's shared hint bar, not a private log.

## Fail-soft when the host is absent

Window embedding depends on the native bridge being present and FL's host form existing. Treat it as a
capability that **may not be available** (older/unpatched host, bridge not injected, an FL build where
the host form couldn't be resolved). Design your plugin to fail soft:

- Check availability and fall back to an ordinary top-level window (an external OS window) when embedding
  isn't possible, rather than failing to show any UI at all.
- Keep the *content* of your window independent of whether it ended up embedded or external, so the same
  UI works both ways.

This external-window fallback is also the simplest way to develop your UI before wiring up the embed.
