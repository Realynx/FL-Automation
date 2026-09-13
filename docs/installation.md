# Installation and compatibility

Running a FruityLink plugin requires a host installed inside FL Studio. A NuGet
package supplies developer-facing contracts; the Python package supplies a client
library. Neither installs the FL host.

## Requirements

| What you want to do | Requirements |
| --- | --- |
| Run plugins in FL | Windows x64, a compatible FL engine, and a matching FruityLink host bundle |
| Write C# plugins | The host above, .NET 9 SDK, and matching SDK source/contracts |
| Run Python in the IDE or MCP | The host and relevant plugin; its bundled private CPython runtime |
| Connect external Python | Python 3.11+, the `fruitylink` library, and the enabled scripting endpoint |
| Build the native bridge | CMake and MSVC x64 tools, including MASM |

The embedded runtime recorded for this checkout is CPython **3.14.6**. It is supplied
by the framework installer; IDE users do not need system Python or pip. External
Python is a separate execution route. See [Python SDK](python/index.md).

## Release status

This checkout targets **SDK 0.2.0**. As checked on September 13, 2026:

- Matching **0.2.0 NuGet packages have not yet been published**.
- Installer **0.1.26 exists locally but has not been published as a GitHub release**.

Use the [repository releases](https://github.com/Realynx/FL-Automation/releases)
and their notes to identify an actual downloadable bundle and its matching SDK.
An earlier package or installer does not establish support for every feature on
these pages. Until a matching release is available, running this snapshot requires
a matching development host bundle from the project maintainer.

The public SDK checkout contains host and bridge source, but does **not** contain
the complete installer/bootstrap projects. Building its solution produces SDK and
host binaries; it does not by itself install FL's startup loader. You can still
[build the SDK and samples](development.md) without running FL Studio.

## Set up a matching host bundle

These steps describe the matching installer bundle once you have obtained it; they
do not imply that installer 0.1.26 is already downloadable from GitHub.

1. Extract the **whole bundle** to a folder. Keep its `payload` folder beside
   `FruityLink.Installer.exe`; moving only the executable leaves the installer without
   the runtime files it needs.
2. Save your project and close FL Studio. Host/native binaries are loaded for the
   lifetime of the FL process.
3. Run `FruityLink.Installer.exe`. Use **Detect**, or **Browse** to the FL installation
   folder containing `FL64.exe`. Check the selected installation if you have several
   FL versions installed.
4. Select the optional plugins you need, such as FLMCP or the Python IDE, where the
   bundle offers them. The IDE is still under development. Click **Install** and
   complete the Windows administrator prompt if required for your installation folder.
5. When installation completes, launch FL Studio and open **Tools → FL Plugins**.
6. Enable the plugin you intend to use. For C# development, follow
   [First C# plugin](csharp/first-plugin.md); for scripting, use [Python SDK](python/index.md).
7. Read the tempo to verify the chosen plugin's connection before making edits.

Bundle contents and optional plugins can differ between releases. Use that bundle's
release notes for release-specific steps and removal instructions.

The standard bundle layout places the host under `<FL-install-dir>\FruityLink`.
Throughout these docs, **`<host-dir>` means the directory containing
`FruityLink.Host.dll`**. Verify the actual path in your installation; the default
plugin directory is `<host-dir>\plugins`.

## FL Studio compatibility

Compatibility is based on the **full engine file version**, not just “FL 2025” or
“FL 2026.” The recorded evidence for this feature set is:

| Engine build | Evidence and limits |
| --- | --- |
| **25.2.5.5319** | Binary analysis and automated fixtures. The current feature set has not been live-tested on this build. |
| **26.1.3.5570** | Binary analysis, automated tests, and recorded live project edits, embedded Python, mixer insertion, automation playback/persistence, and WAV rendering. |

These results do not establish every operation on either build. Other patches,
older versions, and future majors require their own validation. Some legacy UI
helpers remain unavailable on the inspected FL 2026 build. Native window title-bar
and drag stability, DPI, and theme behavior still need broader live confirmation.

The bridge checks capabilities for the loaded engine. A supported scanner family
does not mean that every function or layout is available. Deploy the managed SDK
and native bridge from a matching build: newer mixer operations need layout
metadata that older bridges do not provide.

Read [version policy and evidence](native-bridge.md#fl-version-support) and
[native window validation](native-window-validation.md) for the detailed boundaries.

## Updating plugins

Managed plugins support [hot reload](plugin-lifecycle.md#hot-reload). Put each plugin
and its private dependencies in its own folder under `<host-dir>\plugins`; replacing
the output triggers reload when the watcher is enabled. Keep shared host contracts
matched to the host rather than attempting to override them with plugin-local copies.

Restart FL Studio when replacing the host, native bridge, Python runtime, or embedded
Python library. For startup or discovery failures, see [Troubleshooting](troubleshooting.md).
