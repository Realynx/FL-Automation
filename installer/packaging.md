# Installer packaging

`package.ps1` produces both the commercial `fl-automate-installer-v<version>.zip` and community `fruitylink-installer-v<version>.zip`. Both contain the optional FL MCP component locally. The community ZIP continues to exclude `payload/FruityLink/plugins/fl-agent/`.

Build from the repository root:

```powershell
./installer/package.ps1
```

The default refreshes the framework/host, resolves the sibling `Fl-MCP` checkout, and builds its distribution against this repository's `sdk` source. It uses `sdk/python/dist/fruitylink_python-0.2.0-py3-none-any.whl`; if missing, it builds the wheel with the SDK's locked `uv` environment. This requires the normal .NET build tools, PowerShell 7 (`pwsh`) for the MCP source build, the separate Fl-MCP source checkout, and `uv` when the Python wheel needs building. A prebuilt distribution can be staged with Windows PowerShell 5.1. Build-time dependency restore may use the network. Installation never downloads FL MCP from GitHub.

For reproducible packaging of an already reviewed distribution, supply its directory explicitly:

```powershell
./installer/package.ps1 -McpDistributionPath C:\artifacts\fl-mcp-reviewed
```

`-McpSourceRoot` and `-McpPythonWheel` override the default source checkout and wheel when building a new distribution. A prebuilt distribution must contain its original `SHA256SUMS.json`. SDK 0.2.0 is required by the plugin, its scripting dependency, Python wheel, and staged host contract assemblies. The installer product version is separate and still comes from the repository-root `VERSION`.

The staging layout is:

```text
payload/optional-plugins/fl-mcp/
  plugin/       # contents of distribution/plugin/fl-mcp
  companion/    # server, Python wheel, docs, examples, notices and licenses
  SHA256SUMS.json
```

The selected component installs `plugin/` to `FruityLink/plugins/fl-mcp/` and `companion/` to `FruityLink/tools/fl-mcp/`. The installer controls the default selection and component manifest. Unchecking FL MCP leaves the bundle unused.

The companion also includes this installer's `register-flmcp-codex.ps1` as `register-codex.ps1`, covered by the generated checksums. After installation, this helper provides the user's explicit registration step; packaging does not run it or change Codex settings.

After installing into FL Studio 2026, register the installed MCP with Codex:

```powershell
powershell -ExecutionPolicy Bypass -File "C:\Program Files\Image-Line\FL Studio 2026\FruityLink\tools\fl-mcp\register-codex.ps1"
```

Python 3.11+, .NET 10, and Codex must be on `PATH`. The helper supports `-FlPath`, `-Python`, `-Name`, `-Workspace`, and `-WhatIf`; it creates a per-user Python environment from the bundled wheel and registers the companion with Codex. Enable **FL MCP** once under **Tools > FL Plugins**, then close FL before starting a managed session. Restart Codex after registration.

`stage-mcp.ps1` verifies complete source manifest coverage and hashes, required files, runtime dependency closure, SDK assembly versions, wheel metadata, and the exact official PolyForm Noncommercial 1.0.0 license. It rejects traversal, duplicate manifest entries, unlisted files, and reparse points. Staging omits PDBs, preserves the original manifest as `companion/SOURCE-SHA256SUMS.json`, and writes new checksums for the installed companion and full optional bundle. The original manifest documents the source distribution and its original directory layout; use the regenerated manifests to verify staged files.

Read-only validation, without staging or building:

```powershell
./installer/stage-mcp.ps1 -DistributionPath C:\artifacts\fl-mcp-reviewed -HostDirectory C:\artifacts\host-0.2.0 -ValidateOnly
```

`tests/stage-mcp.tests.ps1` exercises staging and rejection cases using scratch artifacts; it does not install into FL Studio. The main packaging script validates the optional bundle again after publish and confirms both ZIPs contain its plugin, companion, wheel, and license.

FL MCP retains PolyForm Noncommercial 1.0.0; the SDK and Python library retain MIT, and other bundled dependencies retain their notices. Including FL MCP does not change the framework's license. The companion still needs .NET 10, Python 3.11+ with the supplied wheel installed, and user configuration for the FL executable, template, and workspace. Bundling files is not live verification of authoring/rendering or automatic configuration of a user's MCP client. See the included FL MCP README and live-verification checklist.
