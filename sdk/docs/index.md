# FruityLink documentation

**Build plugins and script FL Studio with C# and Python.** FruityLink runs inside
the DAW and exposes a shared API for the live project: channels, notes, patterns,
playlist clips, mixer controls, and automation.

Start with one path. You can return to the reference when you need a specific operation.

| Start here | What you will learn |
| --- | --- |
| [Getting started](getting-started.md) | Install the host, choose a client, and verify a first read |
| [How it works](concepts.md) | Understand the host, plugins, SDK, bridge, and project model |
| [Installation](installation.md) | Requirements, deployment, release status, and FL compatibility |
| [C# SDK](csharp/index.md) | Create and deploy your first plugin |
| [Python SDK](python/index.md) | Run scripts inside FL or connect from external Python |
| [Capabilities](capabilities.md) | Find supported operations and their limits |

## Which project is this?

**FruityLink / FL-Automation** is the open-source framework and SDK in this repository.
The **host** loads plugins inside FL Studio. Plugins build on that foundation:

- **[FLMCP](https://github.com/Realynx/Fl-MCP/tree/master/docs)** connects an MCP client
  to FL Studio, including project authoring, embedded scripts, and managed WAV rendering.
- **[FL Python IDE](https://github.com/Realynx/FL-Automation/blob/master/src/FruityLink.Plugins.PythonIde/README.md)**
  provides an editor and script runner inside FL. It is under development; its plugin
  documentation describes the current UI and validation status.
- **[Python scripting endpoint](python/index.md)** accepts authenticated local
  connections from external Python programs.
- **[Hello FL](csharp/index.md)** is a small C# example with a menu action and toolbar toggle.

## Read the right level of detail

The guides explain everyday use. [C# reference](fl-control-api.md) and
[Python reference](python/api.md) cover individual API surfaces. The engineering
pages on the [native bridge](native-bridge.md), [plugin lifecycle](plugin-lifecycle.md),
and [window hosting](window-embedding.md) explain implementation and validation.

This documentation describes the checked-in SDK. Read
[release status and compatibility](installation.md#release-status) before installing
an older published package or combining it with another host build.
