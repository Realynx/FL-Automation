# Window embedding

FL Automate hosts its UI *inside* FL Studio's own window chrome — a plugin window sits in a real FL
editor host form, next to FL's VST/plugin editors, rather than floating as a detached OS window. The
`IFlWindowHost` capability (exposed on `IPluginContext` as `Windows`) lets your plugin do the same with
any Win32 `HWND`.

If you host **Avalonia** UI, use [FruityLink.Ui.Avalonia.Hosting](avalonia-ui.md) instead of driving
the raw HWND embed yourself.

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

Under the hood this rides the native bridge's window-host messages (embed, show/hide, close, minimize,
maximize, dock) — the host marshals each to the correct thread for you (see below).

## The embed threading contract

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
