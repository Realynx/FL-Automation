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
| `fl.transport` | `tempo`, `song_mode`, `play()`, `stop()`, `toggle_record()`, `seek_beats()`, `seek_ticks()`, `loop_beats()`, `loop_ticks()`, `clear_loop()`, `markers_text()`, `markers()`, `add_marker_beats()`, `add_marker_ticks()`, `delete_marker(index_or_name)`, `set_song_end(bar=None, tick=None, name="End")` |
| `fl.channels` | `list()`, `find(name)`, `add(plugin, name=None)`, `add_sample(path, name=None)`, `[index]` |
| Channel | `name`, `volume`, `pan`, `pitch`, `muted`, `mixer_track`, `parameters`, `select()`, `toggle_solo()`, `replace_sample(path)`, `load_state(path)`, `get_state()` |
| `fl.patterns` | `list()`, `find(name)`, `current`, `create(name=None)`, `[index]` |
| Pattern | `name`, `notes`, `select()`, `clear()`, `clone()`, `gaps(channel=None, min_ticks=None, min_beats=1.0)`; clone can return `None` for an empty source |
| Notes | `list(channel=-1)`, `add(records)`, `add_beats(...)`, `add_ticks(...)`, `edit(edits, allow_multiple=False)`, `delete(refs, allow_multiple=False)` |
| `fl.playlist` | `list()`, `[track]`, `add_pattern(...)`, `add_pattern_ticks(...)`, `add_patterns(records)`, `first_free_track(start_tick, end_tick, above=1)`, `gaps(start_bar, end_bar, channel=None, min_beats=1.0)` |
| PlaylistTrack | `name`, `color`, `muted`, `collapsed`, `select()`, `toggle_solo()` |
| `fl.clips` | `list(track=-1)`, `[index]`, bulk `move()`, `resize()`, `delete()`, `set_muted()` |
| Clip | `muted`, `move_beats()`, `move_ticks()`, `resize_beats()`, `resize_ticks()`, `slice_beats()`, `slice_ticks()`, `duplicate()`, `delete()` |
| `fl.arrangements` | `list()`, `current`, `add(name)`, `[index]`; a handle has `name`, `select()`, `clone()`, `delete()` |
| `fl.mixer` | `[track]`, `master`, `list()`, `add(name=None, after=None)`, `find(name)`, `list_text()` |
| MixerTrack | `name`, `volume`, `pan` (signed, 0 = center), `muted`, `send_to(destination, level=1.0)`, `set_eq_gain(band, value)`, `effects[slot]` |
| EffectSlot | `load(plugin)` (tolerant name: "FabFilter Pro-R 2" loads "Pro-R 2"; ambiguous names are refused with the candidates), `clear()`, `clone_type_to(slot)`, `parameters`, `load_state(path)`, `get_state()` |
| Parameters | `page(filter=None, offset=0, limit=64)`, lazy `iter(filter=None, page_size=64)`, `list(filter=None)`, `read(index)`, `find(name)`, `set(index, value)`, `set_named(name, value)`, `set_verified(index_or_name, value, attempts=6, delay=0.05, settle_display=True)` |
| `fl.plugins` | `available_text(effects=False)`, `samples_text()`, `channel_parameters()`, `effect_parameters()` |
| `fl.automation` | `create(target, track, start_tick, length_tick, name=None)`, `pump(target, start_tick, length_tick, track=..., depth=0.5, recovery_beats=0.75, beats_per_hit=1, floor=None, name=None)`, `[channel]` |
| AutomationCurve | `list()`, `add_beats(time, value, tension=0)`, `add_ticks(tick, value, tension=0)`, `delete(index)`, `set_point(index, value, tension=0)`, `set_points(points)`, `add_clip(track, start_tick, length_tick)` |

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
| Channel pan | Native integers 0..12800; 6400 is center |
| Mixer track pan | Signed native integers -6400..6400; 0 is center, negative is left (a different scale from channel pan) |
| Channel pitch | Cents; 0 is center |

Query the project's resolution rather than assuming 96 ticks per beat:

```python
timebase = fl.timebase
four_beats = timebase.ticks(4)
print(timebase.ppq, four_beats, timebase.beats(four_beats))
```

A retained `Timebase` is a snapshot. Fetch another after opening or changing projects. `ticks()` rounds fractional ticks to the nearest integer using Python's ties-to-even rule. `bar_start(bar, beats_per_bar=4)` gives the tick of a one-based bar (bar 1 is tick 0; FL's time signature is not readable, so pass `beats_per_bar` for other meters). See each operation's catalogue for its other native ranges.

### Scales and conventions

| Topic | Convention |
| --- | --- |
| Channel pan | 0..12800, 6400 is centre (`Channel.pan`, `AutomationTarget.channel_pan`) |
| Mixer pan | Signed -6400..6400, 0 is centre (`MixerTrack.pan`, `AutomationTarget.mixer_pan`) |
| Channel volume automation | Point values 0..1 map to the 0..12800 channel scale (0.55 reads back as 7040); 1.0 is full scale, above FL's default channel level |
| Automation point times | Beats relative to the clip start, not song position; a clip at bar 105 starts at beat 0 |
| `NoteEdit` fields | `new_key`, `new_start_tick`, `new_length`, `new_velocity`, `muted` (there is no `velocity=`) |
| Notes longer than their clip | Keep sounding past the clip end and extend the song and render length |
| Time markers | Extend the song, play range and render to the last marker (`set_song_end` relies on this; `delete_marker` shortens) |
| Parameter readback | A display read in the same request as a write can show the old value; use `Parameters.set_verified` or read again in the next request |
| `add_mixer_effect` / `EffectSlot.load` | Needs the plugin database name exactly (`"Pro-R 2"`, not `"FabFilter Pro-R 2"`); take it from `fl.plugins.available_text(effects=True)` / `list_available_plugins` |

## Note validation

Notes must reference existing zero-based channels. Query `fl.channels.list()` or
use the index returned by channel creation. `NoteSpec` values require keys 0..131,
velocities 0..127, nonnegative start ticks, positive lengths, and an end tick no
greater than 2,147,483,647. The SDK rejects invalid values rather than clamping them.
The entire add batch is checked before any note is added. Edited notes must also
remain within those time and value ranges.

Saving checks existing notes for missing channel references before invoking FL's
serializer. An error identifies the pattern and note; it does not delete notes or
close the project. See [note integrity](../note-integrity.md).

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

In Python, follow `page.next_offset`. Serialized SDK records use the wire field `nextOffset`. Keep the same filter in later requests and stop when the continuation is `None`. `limit` is 1..512 and bounds raw slots scanned, not result bytes. Filters are case-insensitive substrings; `find(name)` and `set_named(name, value)` require an exact, case-sensitive unique match across all pages. `set_named` returns the resolved parameter index, which avoids copying plugin-specific numeric indices into scripts. Name resolution and writing are separate operations, so no atomicity is guaranteed. Query separately when read-back is required, or use `set_verified(index_or_name, value)`, which writes once and re-reads the slot (up to `attempts` times, `delay` seconds apart) until the raw value decodes to the written normalized value (within `NORMALIZED_TOLERANCE`, 2^-20; values are compared, never display strings) or differs from the pre-write value, and then, with `settle_display=True`, keeps re-reading within the same budget until the display string has moved as well. The result's `verified` flag is the raw-value oracle; `display_changed` is False when the display did not move in time or the new value shares the previous label; `normalized_after` is the decoded readback. Why: the raw value is the wrapper's own and reflects a bus write immediately, but the display string comes from the plugin instance, which sees the change only after FL delivers it (audio-thread/idle sync), so a display read in the same request right after a write or preset load can still show the previous value while the next request is always correct (live: Serum 2, Pro-L 2). No bridge call is known that forces that delivery, so the helper waits instead; when `display_changed` is False, read again in a later request before quoting the display.

`page(filter=...)` scans only `limit` raw slots from `offset`; a filter on a parameter beyond that window returns an empty page with a continuation, not a miss. Use `find(name)` or `read(index)` to target one parameter, and `list(filter)` to scan every slot.

Iteration fetches pages lazily. `list()` intentionally fetches everything. Embedded and worker results are limited to 512 KiB; small pages or selected fields may be needed for unusually long names and values. A serialization failure after edits does not reverse those edits.

`channel.parameters` describes a hosted generator such as 3xOsc or a VST. Built-in Sampler envelopes and sample settings are not exposed through this interface; use supported channel controls and sample operations. `effect.parameters.set(index, value)` and `effect.parameters.set_named(name, value)` use a normalized value. They use the same existing setter validation; `set_named` only adds exact-name index resolution.

`effect.clone_type_to(slot)` loads the same plugin type into another slot on that mixer track; it does not copy the plugin's parameter state.

## Add a mixer insert

`fl.mixer.list()` returns Master and active ordinary inserts with their real indices and `kind` (`master` or `insert`). Iteration and `len(fl.mixer)` use that set. The special Current track and dormant slots are excluded. Never convert the low-level native mixer count into a range of physical track IDs.

`fl.mixer.add("Bus")` appends an insert. `fl.mixer.add("Bus", after=3)` inserts after track 3; `after=0` inserts after Master. The returned handle identifies the added insert. Naming is a separate edit, so a naming failure leaves the insert created.

Insertion shifts later indices. Requery tracks and routes and reacquire retained mixer, effect and parameter handles before further edits. Creation does not load effects or set routing. Consult capabilities before depending on native insertion support.

## Linked automation and complete envelopes

Use `AutomationTarget.channel_volume`, `channel_pan`, `channel_pitch`, `mixer_volume`, `mixer_pan`, or `plugin_parameter(index, parameter, slot=-1)` with `fl.automation.create(...)`.

For plugin parameters, `slot=-1` selects a generator channel; slots 0..9 select mixer effects. Parameter indices come from the plugin parameter API. Native target identifiers are not public API. Creation links its initial target; these helpers do not attach additional targets to an existing automation channel.

The first and last points of a curve are protected endpoints: `delete(index)` refuses them. `set_point(index, value, tension=0)` changes any point in place, including both endpoints, and refuses curves that contain non-linear points.

`set_points()` replaces the whole envelope. Supply 2..4000 `AutomationPointSpec` records: first time zero, strictly increasing times, values 0..1, tension -1..1, and linear curve 0. Point times are beats. Clip placement uses ticks, and clip length does not automatically set the envelope's last point. [The automation recipe](examples.md#create-linked-automation) shows both spans explicitly.

Creation can partially complete before an error. Inspect channels and playlist clips before retrying. `add_clip()` reuses an existing automation generator at another playlist position.

## Composition helpers

These build on the queries above and compute locally; they issue no new native operations.

| Helper | Behaviour |
| --- | --- |
| `pattern.gaps(channel=None, min_ticks=None, min_beats=1.0, *, end_tick=None)` | Rest regions from the pattern's note snapshot as `Gap` records (`channel`, `start_tick`, `end_tick`, `length_tick`, `start_beat`, `length_beats`), sorted by time. A note occupies `[start, start + length)`; muted notes are silent. The span ends at the later of the host-reported pattern length and the last note end unless `end_tick` is given. `channel=None` covers every channel with notes; a named channel without notes yields one gap over the whole span. `min_ticks` overrides `min_beats` (converted at the project PPQ). |
| `fl.playlist.gaps(start_bar, end_bar, channel=None, min_beats=1.0, *, beats_per_bar=4)` | The same over an arrangement range, bars `start_bar..end_bar` inclusive (one-based), in absolute ticks. Reads unmuted pattern clips overlapping the range and each pattern's notes once. A note starting inside its clip sounds for its full length; notes starting after the clip end do not. Clips are treated as starting at their pattern's beginning (sliced clips with an offset cannot be distinguished). Automation and audio clips are ignored. |
| `fl.playlist.first_free_track(start_tick, end_tick, *, above=1)` | Lowest one-based track `>= above` with no clip of any kind (pattern, audio, automation; muted included) overlapping `[start_tick, end_tick)`. Raises `LookupError` when tracks up to 500 are all used. |
| `fl.automation.pump(target, start_tick, length_tick, *, track, depth=0.5, recovery_beats=0.75, beats_per_hit=1, floor=None, ceiling=1.0, tension=0.5, name=None)` | Sidechain-style curve: creates and places the clip with `create(...)`, then `set_points(...)`. Each hit drops to `floor` (default `ceiling - depth`), recovers to `ceiling` over `recovery_beats` (`tension` shapes that segment), holds, and drops one tick before the next hit; the last point sits at the clip end. Returns `PumpResult(clip, point_count)`. Over 4000 points is refused before any write; split long spans. |
| `fl.transport.set_song_end(bar=None, *, tick=None, name="End", beats_per_bar=4)` | Places a single end marker at a one-based bar start or an absolute tick, deleting any marker with the same name first, and returns the `Marker` as the host lists it afterwards. It relies on FL extending the song length, play range and render to the last time marker, so a marker past the final note adds silence or room for tails. |

`pump` on `AutomationTarget.channel_volume` recovers to `ceiling`, and 1.0 there is the 12800 channel maximum, above FL's default level. Pass `ceiling` (0.78 is roughly the default) or automate `mixer_volume` instead. The pure builder `fruitylink.automation.pump_points(length_beats, ppq=...)` returns the envelope without a connection.

## Batches and bulk edits

Use `NoteSpec`, `NoteEdit`, `NoteRef`, `ClipMove`, `ClipResize` and `PatternClipSpec` for native bulk operations. A single bulk operation refreshes FL once. `NoteRef` and `NoteEdit` address a note by `(channel, key, start_tick)`; FL allows stacked duplicates sharing that triple, so both carry an optional `length_tick` (the note's current length) to pick one, and `notes.edit(...)` / `notes.delete(...)` refuse a target that still matches several notes unless `allow_multiple=True`, which addresses all of them (nothing is written on a refusal). A general batch sequences multiple independent operations, up to 256:

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

## Render one section

FL's command-line exporter has no range switch (its documented options are `/R`, `/E`, `/F`, `/O` and `/D`), and it renders from tick 0 to the later of the last clip end and the last time marker, so auditioning one section normally costs a full-length render. `fruitylink.audition` trims the *live* project instead, on top of the ordinary clip and marker operations:

```python
from fruitylink.audition import isolate_bars, isolate_range

report = isolate_bars(fl, 49, 64)                      # inclusive one-based bars, 4/4 by default
report = isolate_range(fl, start_tick=18432, length_tick=6144, cut_clips=True)
report = isolate_bars(fl, 49, 64, tail_beats=8)        # keep two bars of reverb/release tail
report.kept_clips, report.deleted_clips, report.cut_clips, report.deleted_markers, report.tail_ticks
```

Only the current arrangement changes: clips outside the range are deleted, clips running past the range end are shortened (their source start is untouched), the survivors move to tick 0, every time marker is removed and the loop selection is cleared. Nothing is written when no clip lies in the range. A clip that begins before the range start must be cut there; because the SDK's slice restarts a *pattern* clip's second half from the pattern's first beat (audio and automation clips keep their source offset), such clips are refused unless `cut_clips=True`, so prefer a start on a clip boundary. With the markers gone FL stops the render at the last surviving clip end, so by default the WAV is exactly the range and reverb or release tails are cut (live, 2026-09-14: bars 49-64 gave 27.4286 s = sixteen bars). `tail_beats` places an `End` marker that many beats past the range (through `fl.transport.set_song_end`) so the render keeps ringing; `tail_ticks` reports the ticks added.

Apply it to a disposable copy; the edit is not reversible from Python. `StudioSession.render_range(output, start_tick=..., length_tick=..., cut_clips=False, tail_beats=0, timeout=180)` runs the transform and then `render()`, leaving the working copy on disk untouched. Under FL MCP, `fl_project_render` accepts `startBar`/`endBar`/`tailBeats` and preserves the untrimmed project as `<name>-full.flp` first.

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
