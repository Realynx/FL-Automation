<div align="center">
<img src="assets/logo.svg" width="104" alt="FL Automate" />
<h1>FruityLink SDK</h1>
<p><strong>Build plugins and script FL Studio with C# and Python.</strong></p>
<p><a href="docs/index.md">Documentation</a> · <a href="docs/getting-started.md">Getting started</a> · <a href="https://discord.fl-automate.com">Discord</a></p>
</div>

FruityLink loads plugins inside FL Studio and gives them a shared API for project
editing, composition, mixing, automation, menus, and windows. C# plugins and Python
scripts operate on the same live project.

| I want to… | Start here |
| --- | --- |
| Understand the project and install it | [Getting started](docs/getting-started.md) |
| Write a C# plugin | [C# SDK](docs/csharp/index.md) |
| Write a Python script | [Python SDK](docs/python/index.md) |
| Connect an AI assistant | [FLMCP repository](https://github.com/Realynx/Fl-MCP) |
| Check supported features and FL builds | [Capabilities](docs/capabilities.md) |
| Build or contribute | [Development guide](docs/development.md) |

**Requirements:** Windows x64 and a compatible FL Studio engine build. This checkout
targets SDK **0.2.0**; its matching NuGet packages and local installer **0.1.22** are
not yet published. An installed, matching host
is required to run plugins. See [installation and compatibility](docs/installation.md)
before choosing a build.

The framework includes the [Hello FL C# sample](samples/HelloFl) and supports the
[Python IDE](src/FruityLink.Plugins.PythonIde/README.md),
[external Python endpoint](src/FruityLink.Plugins.Python/README.md), and separately
maintained [FLMCP plugin](https://github.com/Realynx/Fl-MCP). The FL Automate assistant
is a separate, closed-source consumer.

[MIT](LICENSE) © Realynx. Separately distributed plugins and dependencies retain
their own licenses.
