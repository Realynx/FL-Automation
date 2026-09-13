<div align="center">
<img src="assets/logo.svg" width="104" alt="FL Automate" />
<h1>FruityLink SDK</h1>
<p><strong>Build plugins and script FL Studio with C# and Python.</strong></p>
<p>
<a href="https://www.nuget.org/packages/FruityLink.Plugins.Abstractions"><img src="https://img.shields.io/nuget/dt/FruityLink.Plugins.Abstractions?style=flat-square&amp;color=22d3ee&amp;label=NuGet%20downloads" alt="FruityLink.Plugins.Abstractions total NuGet downloads" /></a>
<a href="https://discord.fl-automate.com"><img src="https://img.shields.io/badge/Discord-join%20us-5865F2?style=flat-square&amp;logo=discord&amp;logoColor=white" alt="Discord" /></a>
<img src="https://img.shields.io/badge/license-MIT-8b5cf6?style=flat-square" alt="MIT license" />
<img src="https://img.shields.io/badge/.NET-9.0-7c3aed?style=flat-square" alt=".NET 9" />
<img src="https://img.shields.io/badge/platform-Windows%20x64-d946ef?style=flat-square" alt="Windows x64" />
</p>
<p><a href="https://realynx.github.io/FL-Automation/"><strong>Documentation site</strong></a> · <a href="https://realynx.github.io/FL-Automation/getting-started/">Getting started</a> · <a href="https://realynx.github.io/FL-Automation/csharp/">C# SDK</a> · <a href="https://realynx.github.io/FL-Automation/python/">Python SDK</a> · <a href="https://discord.fl-automate.com">Discord</a></p>
</div>

Read the **[documentation site](https://realynx.github.io/FL-Automation/)** or [browse its Markdown source](docs/index.md).

FruityLink loads plugins inside FL Studio and gives them a shared API for project
editing, composition, mixing, automation, menus, and windows. C# plugins and Python
scripts operate on the same live project.

| I want to… | Start here |
| --- | --- |
| Understand the project and install it | [Getting started](https://realynx.github.io/FL-Automation/getting-started/) |
| Write a C# plugin | [C# SDK](https://realynx.github.io/FL-Automation/csharp/) |
| Write a Python script | [Python SDK](https://realynx.github.io/FL-Automation/python/) |
| Connect an AI assistant | [FLMCP repository](https://github.com/Realynx/Fl-MCP) |
| Check supported features and FL builds | [Capabilities](https://realynx.github.io/FL-Automation/capabilities/) |
| Build or contribute | [Development guide](https://realynx.github.io/FL-Automation/development/) |

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
