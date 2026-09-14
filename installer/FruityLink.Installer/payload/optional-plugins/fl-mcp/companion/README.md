# FLMCP installed by the FruityLink framework installer

Start with [INSTALLER-SETUP.md](INSTALLER-SETUP.md). The framework installer bundles a private
CPython runtime that executes inside FL Studio, plus the FruityLink SDK, and can connect the AI apps you select. You do not
need to install Python, create a virtual environment, or run a separate registration script.

- [Installer setup and client selection](INSTALLER-SETUP.md)
- [Bundled Python runtime and isolation](python/RUNTIME.md)
- [Exact runtime provenance and wheel hash](python/RUNTIME-PROVENANCE.json)
- [Live FL verification](docs/live-verification.md)
- [Original standalone MCP source README](SOURCE-README.md)

SOURCE-README.md is retained unchanged from the MCP distribution and describes standalone
source deployment. The framework installer supplies the private embedded runtime and enables
the plugin when you select clients. SOURCE-SHA256SUMS.json preserves that original
distribution's hashes/layout; SHA256SUMS.json verifies this installed companion's layout.