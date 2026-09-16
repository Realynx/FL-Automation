<div align="center">

<img src="sdk/assets/logo.svg" width="104" alt="FL Automate" />

<h1>FL Automate</h1>

<p><strong>Build plugins and script FL Studio with C# and Python.</strong></p>

<p>
<img src="https://img.shields.io/badge/license-MIT-8b5cf6?style=flat-square" alt="MIT license" />
<img src="https://img.shields.io/badge/.NET-9.0-7c3aed?style=flat-square" alt=".NET 9" />
<img src="https://img.shields.io/badge/platform-Windows%20x64-d946ef?style=flat-square" alt="Windows x64" />
</p>

<p>
<a href="https://realynx.github.io/FL-Automation/">Documentation</a> ·
<a href="https://realynx.github.io/FL-Automation/getting-started/">Getting started</a> ·
<a href="https://realynx.github.io/FL-Automation/csharp/">C# SDK</a> ·
<a href="https://realynx.github.io/FL-Automation/python/">Python SDK</a>
</p>

</div>

---

FL Automate is the open-source FruityLink SDK and its accompanying tools. It
provides a shared API for project editing, composition, mixing, automation,
menus, and windows. C# plugins and Python scripts operate on the same live FL
Studio project.

This repository contains only the public SDK, its tools, samples, and
documentation.

## Get started

- [Documentation site](https://realynx.github.io/FL-Automation/)
- [Getting started](https://realynx.github.io/FL-Automation/getting-started/)
- [C# SDK](https://realynx.github.io/FL-Automation/csharp/)
- [Python SDK](https://realynx.github.io/FL-Automation/python/)
- [Capabilities](https://realynx.github.io/FL-Automation/capabilities/)
- [Development guide](https://realynx.github.io/FL-Automation/development/)

The SDK source, samples, and detailed contributor documentation live in
[`sdk/`](sdk/README.md). The repository-level solution is
[`FruityLink.slnx`](FruityLink.slnx), so opening the repository root loads the
SDK, samples, public tests, and diagnostics together.

## Build

Requirements: Windows x64, the .NET SDK, and a compatible FL Studio engine
build for running plugins.

```powershell
dotnet restore FruityLink.slnx
dotnet build FruityLink.slnx --no-restore
```

For the first plugin, see the [Hello FL sample](sdk/samples/HelloFl) and the
[SDK documentation](sdk/README.md).

## Repository layout

| Path | Purpose |
| --- | --- |
| [`sdk/`](sdk/README.md) | SDK, samples, SDK tests, and documentation source |
| [`tests/`](tests) | Public integration and regression tests |
| [`tools/`](tools) | Developer diagnostics and probes |

[MIT](sdk/LICENSE) © Realynx. Separately distributed plugins and dependencies
retain their own licenses.
