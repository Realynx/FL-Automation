# Getting started

FruityLink connects your code to the project already open in FL Studio. Your first
step is to get the host running; your second is to choose how you want to use it.

## 1. Check the requirements

You need **Windows x64**, an FL Studio engine build with the required capabilities,
and a matching FruityLink host installation. The host is what loads plugins inside
FL Studio. Installing a Python library or compiling a C# class library alone does
not install it.

Read [Installation](installation.md) for the current release status and exact-build
compatibility. If you already have a matching host, launch FL Studio and look for
**Tools → FL Plugins**. Its presence confirms that the host reached the plugin menu;
individual operations still depend on the loaded engine's capabilities.

## 2. Choose your route

| Your goal | What runs | Next step |
| --- | --- | --- |
| Automate a project with a script | Python inside FL through the IDE | [First Python script](python/index.md) |
| Connect an existing Python program | Python outside FL, using the local scripting endpoint | [Python SDK](python/index.md) |
| Build a plugin with menus or a window | A C# class library inside FL | [First C# plugin](csharp/first-plugin.md) |
| Use an MCP client or AI assistant | FLMCP inside FL, connected to your client | [FLMCP documentation](https://github.com/Realynx/Fl-MCP/tree/master/docs) |

For an overview of how these pieces connect, read [How it works](concepts.md).

## 3. Make your first operation a read

Start by reading the tempo or listing channels. This checks the connection without
changing the project. The [C# walkthrough](csharp/first-plugin.md) logs the tempo
when its plugin is enabled; the [Python guide](python/index.md) explains where to
run the equivalent script and how to connect an external client.

Success means that you can see a tempo value from the live project. If the host
menu is missing, return to installation. If a plugin appears but a read fails,
use [Troubleshooting](troubleshooting.md).

## 4. Understand edits before making them

FruityLink edits FL Studio's live state. Save your work and use a scratch project
for your first editing examples. Query the channels, patterns, and mixer tracks
you intend to change; indices belong to the current project and can shift after
edits. Read the project's PPQ when converting beats to note or clip ticks.

See the [project mental model](concepts.md#the-project-model) and
[capability guide](capabilities.md). Batches improve throughput, but they do not
provide automatic rollback.

## Where to go next

- [C# SDK](csharp/index.md): plugin lifecycle, typed APIs, menus, and windows.
- [Python SDK](python/index.md): execution choices, collections, recipes, and reference.
- [Capabilities](capabilities.md): what works today and where the limits are.
- [Development](development.md): build and validate the repository from source.
