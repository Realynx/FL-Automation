---
search:
  boost: 2
---

# Python API guide

`Studio` groups domain objects around one request transport. Begin with [the quickstart](getting-started.md) if you have not connected yet. Code examples here assume an existing `fl`; see [execution modes](execution.md) for how that connection is supplied.

## Studio and discovery

| Entry point | Purpose |
| --- | --- |
| `connect(pid=None, *, endpoint=None, transport=None, timeout=30.0)` | Select an endpoint and return a `Studio`; see [connection rules](execution.md#external-programs) |
| `Studio(transport)` | Wrap a caller-managed transport, trusting the caller's identity policy |
| `fl.status` | Read the host's status text |
| `fl.timebase` | Fetch the current project PPQ as a `Timebase` snapshot |
| `fl.catalog()` | Read operation documentation, argument/return schemas, defaults and capability requirements |
| `fl.capabilities()` | Read host identity and supported capabilities |
| `fl.ops` | Concrete generated native operations and optional typed queries |
| `fl.batch(stop_on_error=True)` | Build a sequential batch |

`fl.close()` and context-manager exit never close FL Studio. Creating a handle, connecting, enumerating or looking up capabilities does not invoke property setters.

## Objects and collections

| Object | Main members |
| --- | --- |
| `fl.project` | `info`, `info_text()`, `new()`, `open(path)`, `save(path)`, `save_as(path)`, `save_copy(path)`, `save_new_version()` |
| `fl.transport` | `tempo`, `song_mode`, `play()`, `stop()`, `toggle_record()`, `seek_beats()`, `seek_ticks()`, `loop_beats()`, `loop_ticks()`, `clear_loop()`, `add_marker_beats()` |
| `fl.channels` | `list()`, `find(name)`, `add(plugin, name=None)`, `add_sample(path, name=None)`, `[index]` |
| Channel | `name`, `volume`, `pan`, `pitch`, `muted`, `mixer_track`, `parameters`, `select()`, `toggle_solo()`, `replace_sample(path)` |
| `fl.patterns` | `list()`, `find(name)`, `current`, `create(name=None)`, `[index]` |
| Pattern | `name`, `notes`, `select()`, `clear()`, `clone()`; clone can return `None` for an empty source |
| Notes | `list(channel=-1)`, `add(records)`, `add_beats(...)`, `add_ticks(...)`, `edit(edits)`, `delete(refs)` |
| `fl.playlist` | `list()`, `[track]`, `add_pattern(...)`, `add_pattern_ticks(...)`, `add_patterns(records)` |
| PlaylistTrack | `name`, `color`, `muted`, `collapsed`, `select()`, `toggle_solo()` |
| `fl.clips` | `list(track=-1)`, `[index]`, bulk `move()`, `resize()`, `delete()`, `set_muted()` |
| Clip | `muted`, `move_beats()`, `move_ticks()`, `resize_beats()`, `resize_ticks()`, `slice_beats()`, `slice_ticks()`, `duplicate()`, `delete()` |
| `fl.arrangements` | `list()`, `current`, `add(name)`, `[index]`; a handle has `name`, `select()`, `clone()`, `delete()` |
| `fl.mixer` | `[track]`, `master`, `list()`, `add(name=None, after=None)`, `find(name)`, `list_text()` |
| MixerTrack | `name`, `volume`, `pan`, `muted`, `send_to(destination, level=1.0)`, `set_eq_gain(band, value)`, `effects[slot]` |
| EffectSlot | `load(plugin)`, `clear()`, `clone_type_to(slot)`, `parameters` |
| Parameters | `page(filter=None, offset=0, limit=64)`, lazy `iter(filter=None, page_size=64)`, `list(filter=None)`, `find(name)`, `set(index, value)` |
| `fl.plugins` | `available_text(effects=False)`, `samples_text()`, `channel_parameters()`, `effect_parameters()` |
| `fl.automation` | `create(target, track, start_tick, length_tick, name=None)`, `[channel]` |
| AutomationCurve | `list()`, `add_beats(time, value, tension=0)`, `add_ticks(tick, value, tension=0)`, `delete(index)`, `set_points(points)`, `add_clip(track, start_tick, length_tick)` |

The table is a discovery guide; methods can require keyword-only arguments. See [recipes](examples.md) for valid calls and the [typed source](../../python/src/fruitylink/studio.py) for complete signatures.

`list()` returns immutable record snapshots. Indexing returns handles whose properties read and write remotely. `find(name)` requires exactly one exact name match and raises `LookupError` for missing or duplicate names. Handles are positions, not persistent IDs: opening a project, switching arrangements, deleting items or editing elsewhere can invalidate them. Requery after structural changes.

## Indices and units

| Value | Convention |
| --- | --- |
| Channel, mixer track, clip index | Zero-based; mixer track 0 is Master |
| Arrangement index | Zero-based |
| Effect slot | Zero-based, 0..9 |
| Pattern | One-based |
| Playlist track | One-based, 1..500 |
| Note / clip native time fields | Ticks |
| `*_beats` helper inputs | Beats, converted with the current project PPQ |
| Automation point time | Beats, even when playlist placement uses ticks |
| Plugin parameter setter value | Normalized 0..1 |
| Channel / mixer volume | Native integers 0..12800, not normalized floats |
| Channel / mixer pan | Native integers 0..12800; 6400 is center |
| Channel pitch | Cents; 0 is center |

Query the project's resolution rather than assuming 96 ticks per beat:

```python
timebase = fl.timebase
four_beats = timebase.ticks(4)
print(timebase.ppq, four_beats, timebase.beats(four_beats))
```

A retained `Timebase` is a snapshot. Fetch another after opening or changing projects. `ticks()` rounds fractional ticks to the nearest integer using Python's ties-to-even rule. See each operation's catalogue for its other native ranges.

## Generated operations

`fl.ops` exposes concrete methods with editor completion and type hints, generated from the C# native contract. Python names use snake_case; the SDK maps argument names to the wire contract.

```python
tempo = fl.ops.get_tempo()
project = fl.ops.query_project()
print(tempo, project)
```

Use `fl.ops.invoke(operation, **arguments)` for catalogue operations or capability-dependent extensions. `invoke` and `Batch.add` accept snake_case or native camelCase arguments, reject duplicate aliases, and preserve keys inside user dictionaries. The [generated source](../../python/src/fruitylink/operations.py) documents exact signatures and native scales.

## Query snapshots

Typed queries return records; legacy `*_text` helpers return human-readable text. The SDK does not parse arbitrary names from text when a structured query is unavailable.

`query_notes`, `query_clips` and `query_plugin_parameters` return `Page` objects with `items`, `next_offset` and `total`. Offsets refer to raw collection slots. A filtered page can be empty and still have a continuation. Follow `next_offset` instead of adding the item count yourself. High-level `list()` follows all pages; enumeration is not an atomic snapshot, so avoid concurrent structural edits.

### Browse large plugin parameter sets

Return a bounded page when a plugin exposes many parameters:

```python
parameters = fl.channels[0].parameters
page = parameters.page(filter="cutoff", offset=0, limit=32)
result = page
```

In Python, follow `page.next_offset`. Serialized SDK records use the wire field `nextOffset`. Keep the same filter in later requests and stop when the continuation is `None`. `limit` is 1..512 and bounds raw slots scanned, not result bytes. Filters are case-insensitive substrings; `find(name)` still requires an exact, case-sensitive unique match across all pages.

Iteration fetches pages lazily. `list()` intentionally fetches everything. Embedded and worker results are limited to 512 KiB; small pages or selected fields may be needed for unusually long names and values. A serialization failure after edits does not reverse those edits.

`channel.parameters` describes a hosted generator such as 3xOsc or a VST. Built-in Sampler envelopes and sample settings are not exposed through this interface; use supported channel controls and sample operations. `effect.parameters.set(index, value)` uses the effect's own parameter index and a normalized value.

`effect.clone_type_to(slot)` loads the same plugin type into another slot on that mixer track; it does not copy the plugin's parameter state.

## Add a mixer insert

`fl.mixer.list()` returns Master and active ordinary inserts with their real indices and `kind` (`master` or `insert`). Iteration and `len(fl.mixer)` use that set. The special Current track and dormant slots are excluded. Never convert the low-level native mixer count into a range of physical track IDs.

`fl.mixer.add("Bus")` appends an insert. `fl.mixer.add("Bus", after=3)` inserts after track 3; `after=0` inserts after Master. The returned handle identifies the added insert. Naming is a separate edit, so a naming failure leaves the insert created.

Insertion shifts later indices. Requery tracks and routes and reacquire retained mixer, effect and parameter handles before further edits. Creation does not load effects or set routing. Consult capabilities before depending on native insertion support.

## Linked automation and complete envelopes

Use `AutomationTarget.channel_volume`, `channel_pan`, `channel_pitch`, `mixer_volume`, `mixer_pan`, or `plugin_parameter(index, parameter, slot=-1)` with `fl.automation.create(...)`.

For plugin parameters, `slot=-1` selects a generator channel; slots 0..9 select mixer effects. Parameter indices come from the plugin parameter API. Native target identifiers are not public API. Creation links its initial target; these helpers do not attach additional targets to an existing automation channel.

`set_points()` replaces the whole envelope. Supply 2..4000 `AutomationPointSpec` records: first time zero, strictly increasing times, values 0..1, tension -1..1, and linear curve 0. Point times are beats. Clip placement uses ticks, and clip length does not automatically set the envelope's last point. [The automation recipe](examples.md#create-linked-automation) shows both spans explicitly.

Creation can partially complete before an error. Inspect channels and playlist clips before retrying. `add_clip()` reuses an existing automation generator at another playlist position.

## Batches and bulk edits

Use `NoteSpec`, `NoteEdit`, `NoteRef`, `ClipMove`, `ClipResize` and `PatternClipSpec` for native bulk operations. A single bulk operation refreshes FL once. A general batch sequences multiple independent operations, up to 256:

```python
batch = fl.batch(stop_on_error=True)
batch.add("set_tempo", bpm=125)
batch.add("set_song_mode", song=True)
outcome = batch.run()
for item in outcome.results:
    print(item.operation, item.error)
outcome.raise_for_errors()
```

`BatchResult` exposes `results`, `stopped_on_error`, `succeeded` and `raise_for_errors()`. A batch runs once and cannot be replayed. Its context manager runs on a successful body exit and raises the first reported remote error. Earlier operations remain applied when a later one fails; batching provides no transaction or automatic undo.

## Offline measurements

`fl.analysis.wav(...)` and `fl.analysis.pcm(...)` return local audio analysis objects; the same API is available without FL through `from fruitylink.analysis import Analysis`. Return `.summary()`, `.windows(...)`, `.spectral()` or `.spectral_windows(...)` from a script.

These APIs read supplied audio; they do not capture live output or render a project. `note_density(...)` measures pattern-local symbolic notes, independently of audio loudness or repeated Playlist playback. See [audio analysis](../python-audio-analysis.md), [spectral analysis](../python-spectral-analysis.md) and [an offline example](examples.md#analyze-a-wav-without-fl-studio).

## Errors and recovery

| Exception | Meaning and response |
| --- | --- |
| `fruitylink.ConnectionError` | Discovery, transport or identity failed. Check the selected FL process and endpoint, then reconnect. |
| `fruitylink.ProtocolError` | An invalid response arrived. Inspect project state before retrying an edit. |
| `fruitylink.RemoteError` | The host rejected or failed an operation. Inspect `code`, `message` and optional `data`; check capabilities for unsupported operations. |
| `LookupError` | A `find()` call matched zero or multiple names. Inspect the collection and select an explicit index. |
| `IndexError` | An index handle is outside its allowed range. Check whether that collection starts at zero or one. |
| `ValueError` / `TypeError` | Local argument validation failed. Correct units, types or bounds. |

The first three share `FruityLinkError`. Import the SDK's `ConnectionError` explicitly if you need to distinguish it from Python's built-in exception of the same name.

After a timeout or a response failure, an edit may already have happened. Read current state before retrying mutations. Opening an export dialog is not proof of completed rendering; application session ownership and verified render workflows sit above this API.
