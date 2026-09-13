# Build and contribute

Run these commands from the **FL-Automation repository root**, the directory containing
`FruityLink.Sdk.slnx`. This repository builds the SDK, plugins, and native bridge;
the complete installer/bootstrap projects are maintained outside this checkout.

## Prerequisites

- Windows x64 and the .NET 9 SDK for managed builds.
- CMake and MSVC x64 tools, including MASM, for the native bridge.
- Python 3.11+ and `uv` for Python development checks.
- A matching installed host and compatible FL build for live validation.

## Build the sample or the whole SDK

```powershell
dotnet build samples/HelloFl/HelloFl.csproj -c Release
dotnet build FruityLink.Sdk.slnx -c Release -warnaserror
```

To run the sample inside FL, follow [First C# plugin](csharp/first-plugin.md).

## Build and test the bridge

```powershell
cmake -S native/bridge -B native/bridge/build -A x64 -DFRUITYLINK_DEBUG=OFF
cmake --build native/bridge/build --config Release
ctest --test-dir native/bridge/build -C Release --output-on-failure
```

The Release output is `native/bridge/build/Release/FlBridge.dll`. Keep it paired with
the managed build. See [Native bridge](native-bridge.md) for debug-pipe behavior and
engine validation requirements.

## Python checks

```powershell
uv sync --directory python
uv run --directory python python tools/check.py
```

The gate covers generated API parity, Ruff, strict typing, tests, and packaging.
See [Python validation](python-validation.md) for environment-specific checks.

## Managed quality gate

First configure the pinned interpreter and external Python executable described in
[embedded Python validation](embedded-python.md#validation), then run:

```powershell
./scripts/codefactor-check.ps1
```

The gate rebuilds with warnings as errors, checks C# method complexity at most 15,
and runs regression tests. A successful build alone is not the full gate.

## Validate behavior, not just compilation

Native tests inspect copied engine images or use fixtures. They do not replace live
editing, save/reopen, playback, UI lifecycle, and render checks. State the exact FL
engine build and distinguish binary analysis, automated fixtures, and live validation
when documenting a new capability.

Keep user-facing guides task-oriented, and put signatures, internals, and evidence in
reference pages. Update the relevant guide when changing an API, release requirement,
or supported workflow; the root README should remain a short entry point.
