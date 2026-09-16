# How it works

Think of FruityLink as a programming layer around the **live FL Studio project**.
Your code requests an operation, the SDK passes it to a bridge inside FL, and FL
performs the edit. The DAW remains the source of truth.

## The pieces

```text
C# plugin ─────────────────────────────┐
Python IDE / MCP embedded script ──────┼─ Shared SDK → Native bridge → FL project
External Python → local endpoint ──────┘
```

| Piece | Job |
| --- | --- |
| Host | Starts inside FL, discovers plugins, and manages enable/disable and reload |
| Plugin | Adds a feature such as an editor, an MCP endpoint, or a menu command |
| C# SDK | Supplies the plugin contract and typed asynchronous control interfaces |
| Python SDK | Supplies the `fruitylink` library, object collections, batches, and generated operations |
| Native bridge | Resolves supported FL engine functions and runs native operations on FL's main thread |

C# plugins receive an `IPluginContext` with `Fl`, `Menu`, `Toolbar`, `Windows`,
`Services`, and `Log`. Python code uses a `Studio` client, typically named `fl`.
Embedded Python lives inside FL; external Python connects through an authenticated
local endpoint. The [Python guide](python/index.md) shows how each route is initialized.

MCP is another access route to this same framework. Its client connection, tool
catalogue, attachment, and rendering workflows live in the
[FLMCP documentation](https://github.com/Realynx/Fl-MCP/tree/master/docs).

## The project model

Start with this sequence when authoring music:

1. A **channel** holds a generator or sample.
2. A **pattern** holds notes for one or more channels.
3. A **playlist clip** places a pattern or other source at a time on a playlist track.
4. A channel routes audio to a **mixer track**, which has levels, sends, and effects.
5. An **automation clip** stores a curve linked to a control and can be placed in the playlist.

A playlist track organizes clips visually. A mixer track processes audio. Naming
both “Drums” does not route one to the other: set the channel's mixer route explicitly.

## Addresses and musical time

Most operations address objects by index. **Query first**, then act on the returned
objects or indices. Names help you find objects, but are not guaranteed unique.
Adding, removing, or reordering objects can invalidate saved indices.

| Value | Convention |
| --- | --- |
| Channel index | Zero-based |
| Pattern index | One-based; only use a “current pattern” sentinel where the method documents it |
| Playlist track | One-based, `1..500` |
| Mixer track | Master is `0`; query active ordinary inserts instead of assuming a fixed count |
| Notes and clip placement | PPQ ticks; read the project's PPQ to convert from beats |
| Automation envelope time | Quarter-note beats |
| Hosted-plugin parameter value | Normalized `0..1` |

One quarter-note beat is `ppq` ticks. A four-beat span is `4 * ppq`; a sixteenth note
is `ppq / 4`. Read PPQ from the project instead of hardcoding a familiar resolution.
The [C# reference](fl-control-api.md#value-conventions) documents other native scales.

## Operations are live edits

A successful write changes the open project. Batches group work and reduce repeated
refreshes; they are **not transactions** and do not guarantee one undo step or rollback.
If an operation reports that state may have changed, inspect the project before retrying.
Blindly retrying “create” can create another object.

Capabilities depend on the loaded FL engine and bridge. A successful tempo read
does not establish that automation, rendering, or native windows are available.
See [Capabilities](capabilities.md) for feature-specific limits.

## Execution and lifetime

C# menu and toolbar callbacks run on FL's UI thread. Keep them short and move bridge
work off that thread; blocking it while waiting for the bridge can stall FL. Cancel
and finish background work when disabling your plugin, and dispose its registrations
and windows. See [Plugin lifecycle](plugin-lifecycle.md).

The embedded interpreter is shared for the FL process lifetime. Reloading a plugin
does not replace Python itself. Native extensions can delay cooperative cancellation;
restart FL when updating the embedded runtime or library.

Plugins and embedded scripts run with FL Studio's permissions. The typed API narrows
the supported integration surface; plugin load contexts and Python execution are
not a security sandbox. Install code you trust.
