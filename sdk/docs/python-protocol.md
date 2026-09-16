# Python scripting architecture and protocol

The reusable Python API lives in this repository under [`python`](../python). The MCP adapter
in [FLMCP](https://github.com/Realynx/Fl-MCP) imports that library in its embedded interpreter. DAW
operations, their parameter schemas, and native compatibility rules belong to this SDK.

```text
Python script / MCP execution tool
    -> fruitylink.Studio and its object collections
    -> authenticated local request transport
    -> FruityLink.Scripting dispatcher
    -> INativeFlControl / optional IFlStructuredQuery
    -> version-selected native bridge -> FL Studio
```

The standalone `FruityLink.Plugins.Python` plugin hosts the dispatcher for ordinary Python
programs. FLMCP hosts the same dispatcher through its own plugin and direct interpreter callback; it does
not need a second Python plugin enabled. The dispatcher exposes the typed SDK interfaces only.
Managed implementation details, arbitrary reflection, memory addresses, and native calls are
not part of the Python operation API.

## Transport

The standalone Windows endpoint is `FruityLinkScripting-{pid}`. Connections use
`PipeOptions.CurrentUserOnly` and require a cryptographically random per-instance token.
Discovery records live at `%LOCALAPPDATA%/FruityLink/scripting/{pid}.json`:

```json
{
  "apiVersion": 1,
  "pid": 12345,
  "instanceId": "a UUID for this dispatcher lifetime",
  "pipeName": "FruityLinkScripting-12345",
  "token": "a secret, never include it in logs or tool results",
  "createdAt": "UTC timestamp"
}
```

Clients select one process explicitly or require exactly one discovered instance. A capabilities
handshake checks API version, PID, and instance ID before returning a connected Studio. Reusing
a PID after restarting FL does not preserve endpoint identity. The plugin removes only its own
discovery record when it stops. This is a local same-user interface, not a network service.

Each frame is a four-byte unsigned little-endian length followed by UTF-8 JSON, at most 4 MiB.
Request IDs are strings. Responses echo the request ID and contain either `result` or `error`.
Operation names use snake_case; JSON argument and DTO field names use camelCase. Python accepts
snake_case keyword arguments and maps them to the wire contract.

```json
{"id":"1","token":"...","method":"invoke","params":{"operation":"set_tempo","arguments":{"bpm":124}}}
```

Methods:

- `catalog`: `{}` or `{ "filter": "notes" }`; returns operation documentation, parameter schemas,
  defaults, mutation classification, and capability requirements.
- `capabilities`: `{}`; returns API/instance identity, availability, native scanner diagnostics,
  and known unavailable operations.
- `invoke`: `{ "operation": "get_tempo", "arguments": {} }`.
- `batch`: `{ "operations": [{ "operation": "set_tempo", "arguments": { "bpm": 124 } }],
  "stopOnError": true }`; 1–256 operations, ordered per-operation results/errors.

Unknown operation names and arguments are rejected. Missing required values, invalid types,
and non-finite numbers fail validation. The server inserts cancellation tokens internally.
An error contains `code`, `message`, and optional `data`. Capability diagnostics distinguish
an unavailable implementation from an empty project collection.

## Structured data and units

`IFlStructuredQuery` adds structured channels, patterns, notes, playlist tracks, clips,
arrangements, plugin parameters, project identity, and automation points. The older string
operations remain for existing consumers; Python identifies these as text where appropriate.
Query DTOs read native fields directly and preserve names without parsing presentation text.

Notes, clips, and plugin parameters use pages `{items,nextOffset,total}`. Limits are 1–512;
offsets and total count raw slots before filtering. An empty filtered page can have a non-null
continuation. Clients must follow `nextOffset` until null. Queries inspect a changing DAW;
they are not atomic snapshots, and collection edits can invalidate indices or pagination.

Channel, mixer, arrangement, parameter, effect-slot, and raw collection indices are zero-based.
Pattern and playlist-track indices are one-based. Mixer track zero is Master. Notes use PPQ
ticks; automation point times use quarter-note beats. The Python `Timebase` converts beats and
ticks from the actual project PPQ. A beat count does not imply a 4/4 bar count. Plugin raw
parameter values retain their native representation; display text is separate, and no float
interpretation is guessed. `Project.untitled` describes an unassigned filename, not unsaved edits.

## Execution, cancellation, and compatibility

FLMCP uses the SDK's [embedded Python runtime](embedded-python.md) inside FL Studio.
The host supplies a direct dispatcher callback, so scripts use the typed Studio object without
a named-pipe round trip. The installer supplies private CPython and the SDK wheel. The standalone
named-pipe client and optional external worker remain available for independent applications.
MCP retains its launch/save/render lifecycle policy; the SDK owns Python and DAW dispatch.

Embedded execution is trusted local code with FL's filesystem and process permissions. It uses
bounded output and cooperative cancellation; a blocking native call must finish before timeout
or disposal completes. The interpreter remains resident across plugin disable/re-enable and is
never forcibly finalized. In-process native crashes can affect FL. Completed edits are not undone.

The dispatcher serializes operations and batches. Cancelling a caller stops queued work but
does not release the active operation gate until the started native task finishes. Disable
drains active calls. Batches are not transactions: successful earlier operations remain when
a later operation fails. Callers should save a checkpoint when they need recovery.

For the in-process bridge, the dispatcher requests `IFlNativeCompletionScope` so even an
internal transport timeout cannot finish the managed operation while its native task continues.
That scope is an optional host lifetime interface, not a Python operation. `DrainAsync` is a
barrier for callers that receive cancellation before the active operation finishes. Ordinary
unscoped SDK/UI calls retain their existing bounded timeout behavior. The scope does not prove
completion in a separate process using the legacy debug transport.

Native scanning and object-layout policies remain below the scripting layer. The supported
version families are FL Studio 2025 and 2026, with capabilities determined by the installed
build's verified signatures/layouts; see [native version support](native-bridge.md#fl-version-support).
Python does not make unverified FL functionality available. The current API covers the existing
123 native SDK operations plus nine structured queries; it is extensible and is not yet a
complete equivalent of Blender's entire `bpy` object model.
