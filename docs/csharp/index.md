# C# SDK

A FruityLink plugin is a .NET class library loaded inside FL Studio. Implement
`IFlPlugin`, receive an `IPluginContext`, and use its typed API to interact with the
project or contribute a menu, toolbar button, or window.

## Your first plugin

Follow [First C# plugin](first-plugin.md) to build a small tempo logger. It covers
the project reference, complete plugin class, deployment, and how to verify it works.
The [Hello FL sample](https://github.com/Realynx/FL-Automation/tree/master/samples/HelloFl)
adds a menu action and toolbar toggle when you are ready for UI contributions.

## What you receive from the host

| `IPluginContext` member | Use it for |
| --- | --- |
| `Fl` | Read and edit the project using `INativeFlControl` |
| `Menu` | Register commands and toggles in native FL menus |
| `Toolbar` | Register toolbar buttons and toggles |
| `Windows` | Host a plugin window with native FL chrome when available |
| `Services` | Resolve shared host services, where provided |
| `Log(string)` | Write a diagnostic line to the plugin host log |

The shared interfaces live in `FruityLink.Plugins.Abstractions` and
`FruityLink.Core.Abstractions`. Plugins target `net9.0-windows`. Use a project
reference to the matching checkout for the current unpublished SDK 0.2.0;
see [Installation](../installation.md#release-status).

## Choose a guide

- [First C# plugin](first-plugin.md): build, deploy, enable, verify, and reload.
- [Recipes](recipes.md): structured inspection, PPQ-aware notes, and mixer routing.
- [Control API reference](../fl-control-api.md): operation families, scales, and version diagnostics.
- [Plugin lifecycle](../plugin-lifecycle.md): discovery, shared assemblies, cleanup, pre-warm, and reload.
- [Menus and toolbar](../menus-and-toolbar.md): registrations, callbacks, and threading.
- [Window hosting](../window-embedding.md) and [Avalonia](../avalonia-ui.md): plugin window integration.
- [Automation clips](../automation-clips.md): linked creation, curve editing, and exact-build support.

## Read this before adding background work

Keep construction cheap. Start resources in `EnableAsync`; stop and release them
in `DisableAsync`, including when enable only partially completed. The host also
sweeps UI contributions, but it cannot cancel work or remove event handlers you
did not give it ownership of.

Menu and toolbar callbacks execute on FL's UI thread. Return promptly and perform
bridge work asynchronously off that thread. Track longer operations so disable
can cancel and drain them before unloading. Native operations can change the
project before an error reaches your code; inspect state before retrying a write.
