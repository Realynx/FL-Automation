# Troubleshooting

Start by identifying the layer that failed: host startup, plugin discovery, connection,
or a specific FL operation. A missing host menu and an unsupported automation operation
need different fixes.

## Tools → FL Plugins is missing

Confirm that the host was installed into the FL installation you actually launched.
Compiling a plugin or installing the Python library does not install the startup host.
Check your bundle's instructions and [Installation](installation.md). After replacing
host or native files, fully close FL and launch it again.

## My C# plugin is not listed

Check that the output is under `<host-dir>\plugins\<plugin-name>\`, beside its private
dependencies. Its assembly must reference `FruityLink.Plugins.Abstractions`; its plugin
class must be public, non-abstract, implement `IFlPlugin`, and have a public parameterless
constructor. Give it a nonempty, unique `Id`.

The host log records `discover:` messages. A DLL in a build directory elsewhere on disk
is not the deployed plugin. See [First C# plugin](csharp/first-plugin.md).

## A plugin is listed but fails to enable

Look for `enable FAILED` in the host log. Match the plugin's SDK version with the host's
shared assemblies, and deploy the plugin's private dependencies. A copy of a newer
contract in the plugin folder does not override the host's shared contract.

Activation failures can occur after some resources are created. Plugin authors should
make `DisableAsync` safe after partial initialization and report exceptions with enough
context to identify the failed operation.

## FL is responsive, but a particular operation fails

Confirm readiness with a small read. Then inspect capabilities for the operation and
the full engine version. Do not assume that all mixer, parameter, automation, and window
operations are available just because tempo works.

Query object indices again if the project changed. For mixer tracks, use structured
discovery rather than deriving indices from the native track count. For a Sampler's
internal settings, read [parameter limitations](capabilities.md#important-boundaries).

If an error says the project **may have changed**, inspect the affected objects before
retrying the mutation. Batches and creation workflows do not roll back automatically.

## FL stalls when my callback runs

Menu/toolbar callbacks run on FL's UI thread. Do not use `.Wait()`, `.Result`, or a
blocking bridge call there. Move asynchronous bridge work to a background task and
catch/report failures. Track and cancel long-running work during plugin disable.

## Hot reload does not pick up a change

Make sure the new build reached the deployed folder and the
`FRUITYLINK_PLUGIN_HOTRELOAD` environment variable is not `0`, `false`, `off`, or `no`.
Inspect `hot-reload:` log entries. The watcher debounces writes and waits for stable
files; it does not promise that every build tool's sequence produces exactly one event.

Reload applies to managed plugin packages. Updating the host, native bridge, or
embedded Python runtime/library requires an FL restart. See [Plugin lifecycle](plugin-lifecycle.md).

## Python will not connect or run

First identify whether you are using the **embedded IDE/MCP interpreter** or an
**external Python process**. Embedded execution uses the bundled interpreter;
external execution needs the enabled local endpoint and its authentication details.
Follow the appropriate route in [Python SDK](python/index.md), including its
connection and cancellation guidance.

## FL reports invalid notes when opening or saving

This warning can mean that a note references a channel that does not exist in the
project. Earlier SDK builds accepted those indices. The current SDK validates the
entire note batch before adding it, and checks existing notes before saving.

Use channel indices returned by `fl.channels.list()` or a channel creation call;
requery after structural changes. A save error identifies the pattern, note and
missing channel. Inspect that note and explicitly repair or remove it before
retrying. The check leaves the project open and does not delete notes. See
[note integrity](note-integrity.md) for the verified behavior and limits.

## Plugin windows reopen every time FL starts

The host remembers native window X actions and menu visibility toggles separately
for each plugin window. Python IDE and FL Agent also remember their external
window visibility. Reopen a hidden plugin through its View menu entry or toolbar
button; being hidden does not disable the plugin.

Preferences live in `plugins.windows` beside `plugins.json`. Shutdown and plugin
reload preserve the last user-selected visibility. Update the shared host and
native bridge together, then restart FL. The first launch after upgrading has no
saved visibility preference yet; close an unwanted window once to record it.

## Find logs and report a problem

The default host log is:

```text
%LocalAppData%\FruityLink\logs\plugin-host-YYYYMMDD.log
```

It contains discovery, enable/disable, reload, and `context.Log(...)` output. The same
data directory contains `plugins.json` (enabled IDs) and `plugin-shadow\` (load copies).
`FRUITYLINK_PLUGINHOST_DIR` can override this data directory for tests or portable setups;
it does not itself change the plugin deployment directory.

For an actionable [issue report](https://github.com/Realynx/FL-Automation/issues), include
the full FL engine version, host/SDK/plugin versions, execution route, minimal steps,
and the relevant log/traceback excerpt. Describe expected and actual behavior. Remove
endpoint tokens and private project paths before sharing logs.
