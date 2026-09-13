# Python object API

`Studio` composes version-independent domain objects over one request transport.
`fl.ops` exposes every native operation and optional structured queries as concrete
typed methods. Python names use snake_case; wire argument names remain camelCase.
`ops.invoke(name, **arguments)` and `Batch.add` accept either spelling, reject
duplicate aliases, and preserve keys inside user dictionaries.

| Object | Typical operations |
|---|---|
| `fl.project` | `info`, `info_text()`, `new()`, `open(path)`, `save(path)`, `save_as(path)`, `save_copy(path)`, `save_new_version()` |
| `fl.transport` | `tempo`, `song_mode`, `play()`, `stop()`, `toggle_record()`, `seek_beats()`, `loop_ticks()`, `add_marker_beats()` |
| `fl.channels` | `list()`, `find(name)`, `add(plugin)`, `add_sample(path)`, `[index]` |
| Channel | `name`, `volume`, `pan`, `pitch`, `muted`, `mixer_track`, `parameters`, `select()`, `toggle_solo()` |
| `fl.patterns` | `list()`, `find(name)`, `current`, `create(name)`, `[one_based_index]` |
| Pattern | `name`, `notes`, `select()`, `clear()`, `clone()` (None if source empty) |
| Notes | `list(channel=-1)`, `add(records)`, `add_beats()`, `add_ticks()`, `edit(edits)`, `delete(refs)` |
| `fl.playlist` | `list()`, `[one_based_track]`, `add_pattern(...,start_beats=...)`, `add_pattern_ticks()`, `add_patterns(records)` |
| PlaylistTrack | `name`, `color`, `muted`, `collapsed`, `select()`, `toggle_solo()` |
| `fl.clips` | `list(track=-1)`, `[index]`, bulk `move`, `resize`, `delete`, `set_muted` |
| Clip | `muted`, `move_beats()`, `resize_ticks()`, `slice_beats()`, `duplicate()`, `delete()` |
| `fl.arrangements` | `list()`, `current`, `add(name)`, `[index]` → `name`, `select()`, `clone()`, `delete()` |
| `fl.mixer` | `[track]`, `master`, `list()`, `add(name=None,after=None)`, `find(name)`, `list_text()` |
| MixerTrack | `name`, `volume`, `pan`, `muted`, `send_to()`, `set_eq_gain()`, `effects[slot]` |
| EffectSlot | `load(plugin)`, `clear()`, `clone_type_to(slot)`, `parameters` |
| Parameters | `page(filter=None,offset=0,limit=64)`, lazy `iter(filter=None,page_size=64)`, `list(filter=None)`, `find(name)`, `set(index,value)` normalized 0..1 |
| `fl.plugins` | `available_text(effects=False)`, `samples_text()`, `channel_parameters()`, `effect_parameters()` |
| `fl.automation[channel]` | `list()`, `add_beats(time,value,tension)`, `add_ticks()`, `delete(index)` |

All records are immutable snapshots; index handles refer to positions in the
current project's collections. Opening projects, switching arrangements, deleting
items, or editing elsewhere can invalidate those references. Requery after such
changes. Find-by-name requires exactly one match; duplicate names raise `LookupError`.

## Add a mixer insert

`fl.mixer.list()` returns typed `MixerTrackInfo(index, name, kind)` snapshots of
Master (`kind="master"`) and active ordinary inserts (`kind="insert"`). Iteration,
`find()` and `len(fl.mixer)` use this set. Current is a special physical track
(501 in the verified native layout), not `count - 1`, and is excluded together
with dormant slots. The low-level `get_mixer_track_count()` retains native
cardinality including Current; do not turn that count into a range of track IDs.

`fl.mixer.add()` appends one ordinary insert. `fl.mixer.add("New insert", after=3)`
inserts after track 3; `after=0` inserts immediately after Master. Omit `after` to
append; explicit negative indices and booleans are rejected. The returned
`MixerTrack.index` identifies the newly added insert. This operation does not load
an effect or configure a channel route or send.

Insertion shifts subsequent track indices. Requery tracks and channel routes and
reacquire retained `MixerTrack`, effect-slot and parameter handles before further
edits. Optional naming is a second native operation: if naming or a later step
fails, the insert remains. Inspect current state before retrying rather than
creating another insert blindly. Verified native mixer support is required;
`fl.capabilities()` reports unavailable operations.

## Query snapshots

`query_notes`, `query_clips`, and `query_plugin_parameters` return typed pages, described below.

## Linked automation and complete envelopes

```python
from fruitylink import AutomationPointSpec, AutomationTarget

created = fl.automation.create(AutomationTarget.channel_volume(3), track=1,
                               start_tick=0, length_tick=1536, name="Volume curve")
fl.automation[created.channel].set_points([
    AutomationPointSpec(0, 0.2), AutomationPointSpec(8, 0.8), AutomationPointSpec(16, 0.4)])
result = created  # channel and clipIndex
# Reuse that automation generator in another playlist location:
# result = fl.automation[created.channel].add_clip(2, 1536, 1536)
```

Target factories are `channel_volume`, `channel_pan`, `channel_pitch`,
`mixer_volume`, `mixer_pan`, and `plugin_parameter(index, parameter, slot=-1)`.
For plugin parameters, slot -1 means a generator channel; slot 0..9 means a mixer
effect. Plugin parameter indices come from that plugin's parameter API. Native
target identifiers are not part of the public API. Initial creation links the
target; attaching additional targets to an existing automation channel is not
supported by these helpers.

Playlist tracks are **one-based 1..500**; channels and returned playlist clip
indices are zero-based. Placement uses ticks, while automation point times use
beats. Whole envelopes require 2..4000 points, first time zero, strictly increasing
times, values 0..1, tension -1..1, and linear curve 0. Clip placement length and
envelope duration are separate; supply the desired point extent explicitly.
Use `fl.capabilities()` before relying on native automation support. Creation can
partially complete before an error, so inspect channels and clips before retrying.

## Offline audio and note measurements

`fl.analysis.wav(path, start_seconds=..., end_seconds=...)` and
`fl.analysis.pcm([left, right], sample_rate)` return local analysis objects.
Return `.summary()`, `.windows(...)`, `.spectral()` or `.spectral_windows(...)`
to MCP. These operations never edit FL or capture live audio. Amplitude metrics,
energy, occupancy, optional gated LUFS/true-peak estimate/PSR, and source/frame
provenance are described in [audio analysis](../../docs/python-audio-analysis.md).
`fl.analysis.note_density(...)` is separate pattern-local symbolic analysis;
it does not infer repeated playlist playback or audio loudness.

## Paginated query snapshots

`query_notes`, `query_clips`, and `query_plugin_parameters` return typed `Page`
objects with `items`, `next_offset`, and `total`. Offsets are raw collection slots;
filtered pages can contain no items while still advancing. High-level `list()`
follows all pages. Avoid concurrent structural edits during enumeration; pages are
not an atomic project snapshot. Optional query operations require a backend that
advertises them; unsupported errors are not silently converted to parsed text.

## Browse large plugin parameter sets

Plugin wrappers can expose thousands of parameters. Return one page from a script,
then follow its `next_offset` in a later request. `limit` is 1..512 and defaults to
64; it bounds raw slots scanned, so a filtered page can be empty even when more
matches exist later. Retain the same filter and use the returned continuation,
never `offset + len(page.items)`. Filters are case-insensitive name substrings.

```python
parameters = fl.channels[0].parameters
page = parameters.page(filter="cutoff", offset=0, limit=32)
result = page  # items, nextOffset, total; SDK records serialize automatically.
# In the next request, pass the returned nextOffset if it is not None:
# result = parameters.page(filter="cutoff", offset=32, limit=32)
```

`for parameter in parameters` and `parameters.iter(filter="cutoff")` fetch pages
on demand; stop iteration to avoid further remote reads. `list()` still returns
the complete tuple without truncation. `find(name)` uses native filtering but
requires an exact case-sensitive unique name, scanning later pages to prove
uniqueness and stopping as soon as a second exact match proves ambiguity.

Embedded and worker script results remain limited to 512 KiB (streams 64 KiB each,
complete response 1 MiB). Page size limits record count, not bytes; unusually long
names/values can still require smaller pages or returning only selected fields.
Do not return multiple complete parameter lists after edits: serialization failure
does not roll those edits back. Inspect a bounded readback before retrying mutations.

Use `NoteSpec`, `NoteEdit`, `NoteRef`, `ClipMove`, `ClipResize`, and `PatternClipSpec`
for batch operations. The library serializes their fields to the wire contract.
Single native bulk operations refresh FL once; general batches sequence multiple
independent operations and expose partial errors. They provide no transaction or undo.

Properties read/write remotely immediately. No setter executes during construction,
connection, enumeration, or capability lookup. Opening an export dialog does not
mean a render completed. Higher-level session ownership and verified rendering
belong to the application using this library, including the separate MCP adapter.
