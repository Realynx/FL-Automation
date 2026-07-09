# Avalonia UI inside FL

`FruityLink.Ui.Avalonia.Hosting` hosts an [Avalonia](https://avaloniaui.net/) UI inside FL Studio's
window chrome. It handles the Avalonia lifetime and the reparent/rendering details of
[window embedding](window-embedding.md) so your plugin only supplies views.

> Alongside the API (`EmbeddedAvaloniaHost`, `EmbeddedAvaloniaView`), this page documents the
> **gotchas** that will bite you if you ignore them. The gotchas are hard-won and version-independent;
> treat them as rules.

See also: [Window embedding](window-embedding.md) · [Plugin lifecycle](plugin-lifecycle.md).

## `EmbeddedAvaloniaHost`

Avalonia has **one** application/lifetime per process. `EmbeddedAvaloniaHost` owns that once-per-process
setup:

- It starts Avalonia from an **`AppBuilder` factory** you provide (so you control themes, fonts, and
  services) and runs the UI on a dedicated Avalonia thread:
  `EmbeddedAvaloniaHost.EnsureStarted(() => AppBuilder.Configure<MyApp>().UsePlatformDetect())`.
  The first starter supplies the factory; the host appends the software-rendering /
  redirection-surface options the embedded surface requires (see the gotchas), then runs
  `SetupWithoutStarting` on a dedicated STA thread with a permanent main loop.
- It exposes **`Invoke` / `Post`** to marshal work onto the Avalonia UI thread — use these for anything
  that touches Avalonia objects from your plugin's other threads (enable/disable, bridge callbacks).
- It is intended to be pre-warmed: doing this cold-start during
  [`IFlPreWarmPlugin.PrepareAsync`](plugin-lifecycle.md#two-phase-boot-iflprewarmplugin) — which must not
  touch FL — overlaps the Avalonia/Skia startup and first paint with FL's own load, so the UI appears
  *with* FL rather than seconds after.

Because there is only one host per process, initialize it once and reuse it; do not attempt to stand up a
second Avalonia application.

## `EmbeddedAvaloniaView`

A wrapper that puts an Avalonia control/window into an embeddable Win32 surface, so the host can reparent
it into FL's editor host form (the [window embedding](window-embedding.md) path). You build your view
tree normally; the wrapper provides the `HWND` the embed needs and manages software rendering + repaint
on attach/re-show.

## Gotchas

Each of these is a *why*, not just a *don't*.

### Continuous XAML animations crash the embedded host

**Why:** the embedded surface is **software-rendered** (see below). A continuously running Avalonia
`Animation` (e.g. an infinite-iteration spinner) that runs fine in a standalone desktop head takes FL
**down** a few seconds after the plugin is enabled when it runs in that software-rendered, reparented
surface.

**Instead:** show busy/active states **statically** (a glyph, an accent-lit button, a "Transcribing…"
label). If you must animate, step a property yourself with a `DispatcherTimer` and **only tick while the
view is attached and visible** — stop the timer when it isn't. Never re-add a continuous `Style`
animation without proving the *embedded* host survives it; a standalone desktop head is **not** valid
proof, because it uses normal desktop rendering.

### Software rendering + a redirection surface are mandatory

**Why:** a window reparented as a `WS_CHILD` of FL's non-Avalonia host form hits the DWM "airspace" bug —
a GPU presenter's redirection surface isn't composited inside the foreign parent, so the child goes blank
and only repaints the strip under a moving cursor. GPU presenters go blank; software rendering paints via
the normal `WM_PAINT` path and composes correctly inside the foreign parent. Keep the embedded child in
software-render mode with its redirection surface for the whole time it is parented.

### Force a repaint after embed / re-show

**Why:** even in software mode, right after the reparent (and after hiding then re-showing) the child can
stay **blank until the first input** arrives. Call the host's force-render after you embed and on every
re-show to get a synchronous repaint. (Do **not** try to fix this with a ±1px size-nudge — it makes the
lazy-render *worse*.)

### Avalonia cannot be torn down — hide, never stop the UI thread

**Why:** Avalonia's per-process application/lifetime is not restartable. Once started, you cannot cleanly
stop and re-start it. On plugin **disable**, **hide** your window and release your view state, but leave
the Avalonia host/UI thread running so a later **enable** can re-show instantly. Do not shut down the
Avalonia thread.

### Do not collide with the host's settings files

**Why:** FL Automate and the host keep their own settings/prefs files under the shared FruityLink data
folder. If your plugin writes UI prefs to a file the host already owns, you can wipe the host's config
(this exact collision has happened). **Pick your own filename under your own folder** for any state your
plugin persists.

## A silent failure to know about

If your view throws at **runtime** (a XAML load error — bad binding, an un-animatable property, etc.),
the host that wraps it may fall back to a different UI **silently**. XAML errors are *runtime*, not build:
`dotnet build` is green and the throw only happens when the view is instantiated. Always **run** your UI
(a desktop head is fine for catching XAML throws specifically) before deploying, and log loudly around
view construction so a fallback isn't mistaken for "the wrong UI showed up".
