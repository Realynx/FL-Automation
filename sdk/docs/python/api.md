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
| `fl.transport` | `tempo`, `song_mode`, `play()`, `stop()`, `toggle_record()`, `record_pressed`, `ensure_record_pressed(pressed=True)`, `recording_filter`, `recording_filter_parts`, `ensure_recording_filter(audio=True, automation=None, notes=None, clips=None)`, `metronome`, `countdown`, `wait_for_input`, `loop_record`, `blend_recorded_notes`, `recording_active`, `settings()`, `ensure(**flags)`, `seek_beats()`, `seek_ticks(tick, settle=False)`, `seek_settled(tick)`, `position_tick`, `read_at(tick, reader)`, `loop_beats()`, `loop_ticks()`, `clear_loop()`, `markers_text()`, `markers()`, `add_marker_beats()`, `add_marker_ticks()`, `delete_marker(index_or_name)`, `set_song_end(bar=None, after_bar=None, tick=None, name="End")` |
| `fl.channels` | `list()`, `find(name)`, `add(plugin, name=None)`, `add_sample(path, name=None)`, `retire(index, name=None)`, `[index]` |
| Channel | `name`, `volume`, `volume_db`, `set_volume(value=None, db=None)`, `pan`, `pitch`, `muted`, `mixer_track`, `parameters`, `control(index)`, `set_control(index, value)`, `stretch_time`, `sample_offset`, `select()`, `toggle_solo()`, `replace_sample(path)`, `retire(name=None)`, `load_state(path)`, `get_state()` |
| `fl.patterns` | `list()`, `find(name)`, `current`, `create(name=None)`, `[index]` |
| Pattern | `name`, `notes`, `length_tick` (host-reported, the span one clip plays), `select()`, `clear()`, `clone()`, `gaps(channel=None, min_ticks=None, min_beats=1.0)`; clone can return `None` for an empty source |
| Notes | `list(channel=-1)`, `add(records)`, `add_beats(...)`, `add_ticks(...)`, `edit(edits, allow_multiple=False, preserve_clips=False)`, `delete(refs, allow_multiple=False, preserve_clips=False)` |
| `fl.playlist` | `list()`, `[track]`, `pattern_length(pattern)`, `tile_pattern(pattern, track=, start_tick=, length_tick=)`, `tile_pattern_beats(...)`, `add_pattern(..., repeat=False)`, `add_pattern_ticks(..., repeat=False)`, `add_patterns(records, enforce_lengths=False, repeat=False)`, `first_free_track(start_tick, end_tick, above=1)`, `gaps(start_bar, end_bar, channel=None, min_beats=1.0)`, `onsets(channel, start_tick, end_tick)`. **Pattern clips do not repeat**: tile a span instead of placing one long clip (see below) |
| PlaylistTrack | `name`, `color`, `muted`, `collapsed`, `select()`, `toggle_solo()` |
| `fl.clips` | `list(track=-1)`, `[index]`, bulk `move(records)` / `move(index, start_tick, track)`, `resize(records)` / `resize(index, length_tick)`, `delete(indices_or_index)`, `set_muted(indices_or_index, muted)`; records may be `ClipMove`/`ClipResize` or plain tuples |
| Clip | `muted`, `move_beats()`, `move_ticks()`, `resize_beats()`, `resize_ticks()`, `slice_beats()`, `slice_ticks()`, `duplicate()`, `delete()` |
| `fl.arrangements` | `list()`, `current`, `add(name)`, `[index]`; a handle has `name`, `select()`, `clone()`, `delete()` |
| `fl.mixer` | `[track]`, `master`, `list()`, `insert_count`, `capacity`, `ensure_inserts(count)`, `add(name=None, after=None)`, `find(name)`, `routes()`, `list_text()` |
| MixerTrack | `name`, `volume` (0..16000, 12800 = 0 dB), `volume_db`, `set_volume(value=None, db=None)`, `pan` (signed, 0 = center), `muted`, `send_to(destination, level=1.0, db=None, active=True)`, `disconnect(destination)`, `sends()`, `send_level(destination)`, `set_eq_gain(band, value)`, `effects[slot]`, `effects.names()` / `effects.loaded()` (the whole chain in one call) |
| EffectSlot | `plugin_name` (what is loaded, or `None`; one `list_mixer_effects` call, cached on the handle until `refresh()`), `is_empty`, `refresh()`, `repr` -> `EffectSlot(track=64, slot=0, plugin='Pro-Q 4')`, `load(plugin)` -> verification line (tolerant name: "FabFilter Pro-R 2" loads "Pro-R 2"; ambiguous names are refused with the candidates; a cold first load is guarded for 20 s and reports `loaded after N ms` rather than failing), `clear()`, `clone_type_to(slot)`, `parameters`, `load_state(path)`, `get_state()` |
| Parameters | `page(filter=None, offset=0, limit=64)`, lazy `iter(filter=None, page_size=64)`, `list(filter=None)`, `read(index)`, `read_at(index, tick, settle=0.3)`, `display_at(index, tick)`, `find(name)`, `set(index, value)`, `set_named(name, value)`, `set_verified(index_or_name, value, attempts=6, delay=0.05, settle_display=True)` |
| `fl.plugins` | `available_text(effects=False)`, `samples_text()`, `channel_parameters()`, `effect_parameters()` |
| `fl.automation` | `list(with_points=True)`, `describe()`, `create(target, track, start_tick, length_tick, name=None)`, `pump(target, start_tick, length_tick, track=..., depth=0.5, recovery_beats=0.75, beats_per_hit=1, floor=None, name=None)`, `duck(target, hits_ticks, start_tick, length_tick, track=..., ...)`, `tile(target, shape, start_tick, length_tick, track=..., period_beats=..., offsets_beats=(0,))`, `links_to(target)`, `targets_of(kind, index, slot=-1, parameter=-1)`, `refresh()`, `release(channel, value=None)`, `[channel]` |
| AutomationCurve | `list()`, `add_beats(time, value, tension=0)`, `add_ticks(tick, value, tension=0)`, `delete(index)`, `set_point(index, value, tension=0)`, `set_points(points)`, `add_clip(track, start_tick, length_tick)` |
| `fl.analysis` | `wav(path, ...)`, `pcm(channels, sample_rate, ...)`, `describe(source, bpm=None, ppq=None, start_bar=None, beats_per_bar=4, detail="normal")`, `compare(a, b, **options)`, `describe_samples(paths, cache_dir=None, detail="brief", bpm=None)`, `compare_bands(...)`, `masking_report(...)`, `transition(...)`, `note_density(...)`, `tick_range_seconds(...)` |
| AudioAnalysis | `summary(...)`, `windows(...)`, `spectral(...)`, `spectral_windows(...)`, `scan_bars(bpm, ...)`, `band_energy(...)`, `describe_sections(bpm, sections, ...)`, `pitch_track(...)`, `compare_bands(other, ...)`, `masking_report(other, ...)`, `transition(other, bpm, bar, ...)`, `describe(bpm=None, ppq=None, start_bar=None, detail="normal")` |
| AudioDescription / AudioComparison / SampleTable | `.text` (hand to a model), `.data` (JSON-safe), `.tags`; a table also has `.rows` and `.descriptions` |
| `fl.audio` | `capture(inserts="master", start_bar, end_bar, tail_beats=0, name=None, arm_refresh=True, ensure_recording_filter=True, restore_recording_filter=True, cleanup=True)`, `plan(...)`, `decide(...)`, `measure_section(...)`, `resolve_recorded_folder()`; live per-insert capture through FL's own disk recording (see [live audio capture](../live-audio-capture.md)) |
| `fl.samples` | `query(filter=None, offset=0, limit=50)` (structured, paged sample discovery over every [search root](#sample-search-roots): `SampleInfo(entry, root_tag, relative_path, name, extension)`; pass `entry` verbatim to `add_sample`), `describe(channel, path=None, **options)`, `compare(a, b, **options)` (channel indices or paths), `browse(paths, cache_dir=None, detail="brief", bpm=None)`, `register(channel, path)`, `path_of(channel)`, `known()`; paths are remembered from `add_sample`/`replace_sample` because FL exposes no Sampler file query |

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
| Channel volume | Native integers 0..12800 on a power curve: 10240 = 0 dB, FL's default 10000 = about -0.4 dB, 6400 = about -8.5 dB, 5000 = about -13 dB (`Channel.volume_db`, `fruitylink.levels`) |
| Mixer track volume | Native integers 0..16000: 12800 = 0 dB (every track's default), 16000 = about +4.05 dB (FL's hint says +5.6; the renders say +4.05) (`MixerTrack.volume_db`) |
| Mixer send level | Float 0..1 = native 0..16000; 0.8 is unity (0 dB), 1.0 is the knob top (`SEND_UNITY`, `MixerTrack.sends()`) |
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
| Automation point tension | Belongs to the segment that ENDS at the point. Positive = fast start, slow finish (`TENSION_EASE_OUT`, compressor-like recovery; `pump` uses +0.5). Negative = slow start accelerating into the point (`TENSION_EASE_IN`, a swell landing on a downbeat). Live: +0.3 read 89 % of the travel half-way, -0.3 about a third |
| Automation clip placements | Every `add_clip` placement replays the same envelope from its own start; there is no loop or offset flag. A pattern that changes per bar needs one long envelope (`fl.automation.duck` from `fl.playlist.onsets`, or `tile`) |
| Pattern clip length | `PatternClipSpec.length_tick > 0` is pinned: the clip keeps it even when notes overhang the bar; `0` follows the pattern's own length. `add_patterns(..., enforce_lengths=True)` re-checks and resizes drifted clips |
| Pattern clip repeats | There are none. A pattern clip plays its pattern ONCE from the clip start and is silent for the rest of its length (live 2026-09-18). A span that repeats is one clip per repetition: `fl.playlist.tile_pattern(...)` or `repeat=True`; placing one long clip warns with `PatternClipLongerThanPatternWarning` |
| Note edits and clips | `notes.delete` / `notes.edit` leave the pattern's playlist clips at their lengths (FL alone re-derives them from the remaining notes); `preserve_clips=True` re-checks from the client. Adding notes past a clip that follows the pattern still grows it |
| Seek readback | A stopped seek reads 14-20 ticks late for about 100 ms and plugin displays show the previous position's value for up to 300 ms: use `transport.seek_settled`, `transport.read_at` or `parameters.read_at` / `display_at`; verify sub-beat envelopes from the point list |
| Mixer send levels | No automation target (the send-level event ids are unknown to the bridge); automate the return insert's `mixer_volume` or the send effect's wet parameter, see [Linked automation](#linked-automation-and-complete-envelopes) |
| `NoteEdit` fields | `new_key`, `new_start_tick`, `new_length`, `new_velocity`, `muted` (there is no `velocity=`) |
| Notes longer than their clip | Keep sounding past the clip end and extend the song and render length |
| Time markers | Extend the song, play range and render to the last marker (`set_song_end` relies on this; `delete_marker` shortens) |
| Parameter readback | A display read in the same request as a write can show the old value; use `Parameters.set_verified` or read again in the next request |
| `set_verified` on a slot that already holds the value | `verified=True, unchanged=True` after one readback; `display_changed` stays False because nothing had to move |
| Volume and pan setter keywords | The canonical keyword is `value=` (`set_channel_volume(channel=, value=)`, `set_mixer_volume(track=, value=)`); `volume=` and `pan=` are accepted aliases in Python and on the wire |
| Mixer volume in dB | `mixer_volume_to_db` / `mixer_volume_from_db`, `channel_volume_to_db` / `channel_volume_from_db`, `send_level_to_db` / `send_level_from_db` model FL's fader law (0.8 = 0 dB, exponent 2.09 from the render calibration, so halving a fader costs about 12.6 dB); see [Volume in decibels](#volume-in-decibels) |
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

`query_notes`, `query_clips`, `query_plugin_parameters` and `query_samples` return `Page` objects with `items`, `next_offset` and `total`. Offsets refer to raw collection slots. A filtered page can be empty and still have a continuation. Follow `next_offset` instead of adding the item count yourself. High-level `list()` follows all pages; enumeration is not an atomic snapshot, so avoid concurrent structural edits. `query_samples` is the exception on offsets: it counts MATCHED samples rather than raw slots, because its filter is applied while the sample roots are scanned.

### Browse large plugin parameter sets

Return a bounded page when a plugin exposes many parameters:

```python
parameters = fl.channels[0].parameters
page = parameters.page(filter="cutoff", offset=0, limit=32)
result = page
```

In Python, follow `page.next_offset`. Serialized SDK records use the wire field `nextOffset`. Keep the same filter in later requests and stop when the continuation is `None`. `limit` is 1..512 and bounds raw slots scanned, not result bytes. Filters are case-insensitive substrings; `find(name)` and `set_named(name, value)` require an exact, case-sensitive unique match across all pages. `set_named` returns the resolved parameter index, which avoids copying plugin-specific numeric indices into scripts. Name resolution and writing are separate operations, so no atomicity is guaranteed. Query separately when read-back is required, or use `set_verified(index_or_name, value)`, which writes once and re-reads the slot (up to `attempts` times, `delay` seconds apart) until the raw value decodes to the written normalized value (within `NORMALIZED_TOLERANCE`, 2^-20; values are compared, never display strings) or differs from the pre-write value, and then, with `settle_display=True`, keeps re-reading within the same budget until the display string has moved as well. The result's `verified` flag is the raw-value oracle; a slot that already held the value is `verified=True, unchanged=True` after one readback (the write is in place, not failed); `display_changed` is False when the display did not move in time, the new value shares the previous label, or nothing had to change; `normalized_after` is the decoded readback. Why: the raw value is the wrapper's own and reflects a bus write immediately, but the display string comes from the plugin instance, which sees the change only after FL delivers it (audio-thread/idle sync), so a display read in the same request right after a write or preset load can still show the previous value while the next request is always correct (live: Serum 2, Pro-L 2). No bridge call is known that forces that delivery, so the helper waits instead; when `display_changed` is False, read again in a later request before quoting the display.

`page(filter=...)` scans only `limit` raw slots from `offset`; a filter on a parameter beyond that window returns an empty page with a continuation, not a miss. Use `find(name)` or `read(index)` to target one parameter, and `list(filter)` to scan every slot.

Iteration fetches pages lazily. `list()` intentionally fetches everything. Embedded and worker results are limited to 512 KiB; small pages or selected fields may be needed for unusually long names and values. A serialization failure after edits does not reverse those edits.

`channel.parameters` describes a hosted generator such as 3xOsc or a VST. Built-in Sampler envelopes and sample settings are not exposed through this interface; use supported channel controls and sample operations. `effect.parameters.set(index, value)` and `effect.parameters.set_named(name, value)` use a normalized value. They use the same existing setter validation; `set_named` only adds exact-name index resolution.

`effect.clone_type_to(slot)` loads the same plugin type into another slot on that mixer track; it does not copy the plugin's parameter state.

### Read everything, names, duplicates and normalized values

`page(limit=...)` accepts at most `PAGE_LIMIT` = 512 slots (the host's cap; `page(limit=4240)` is refused with an `IndexError` naming it). `parameters.all(filter=None, unique=False)` and `list()` page automatically (Serum 2's 4240 slots are nine requests inside one `fl_execute_python` call); `iter()` streams them.

Every `PluginParameterInfo` now carries:

| Field | Meaning |
| --- | --- |
| `name` | Display name with FL's hint-formatting codes removed: stock effects report `"^b^aWet level"` (Fruity Chorus, Reeverb 2, Delay 3, Parametric EQ 2, ...) and Vintage Chorus band 0 `"^b^a^^(shift-click for I + II) ^Mode"`; these read `"Wet level"` and `"Mode"`. Wrapper (VST) names are unchanged. `clean_parameter_name(raw)` is the function. |
| `raw_name` | The string exactly as the plugin wrote it. `find()` / `set_named()` accept either form. |
| `raw_value` | The host's native integer. For VST/wrapper parameters it is the IEEE-754 bit pattern of the normalized value (`0x3F203A7B` = 0.626), not the value itself. |
| `normalized` | `raw_value` decoded to the 0..1 float (`plugins.normalized_from_raw`), or `None` for native FL integer scales. Compare this with what you wrote; compare `display_value` only in a later request. |

Duplicated names are refused, not guessed: Fruity Delay 3 exposes three parameters named `Distortion` (18, 19, 20), and the plugin gives no section names, so `find("Distortion")` and `set_named("Distortion", v)` raise `LookupError` listing every index without writing anything. Address one of them as `"Distortion [19]"` (accepted by `find`, `set_named` and `set_verified`) or by index; `parameters.all(unique=True)` / `unique_names(rows)` return the list with that suffix on duplicates so listings stay unambiguous.

### Known parameter scales (display units)

Normalized 0..1 writes are the only interface, but the knobs show Hz, ms, dB or percent, so every session probed scales with calibration writes. `fruitylink.plugins.scale_for(plugin, parameter)` returns a `ParameterScale` seeded from the evidence in the friction log (Fruity Reeverb 2 low/high cut, decay and levels; Fruity Delay 3 time, feedback and cutoff; FabFilter Pro-L 2 gain and output level; FabFilter Pro-Q 4 band frequency/gain/Q/shape/slope and output; Super VHS output; Serum 2 filter frequency, envelope times, sustain, unison, detune, width, fine, main volume, sub shape). Data lives in `fruitylink/data/parameter-scales.json`, each entry with a `confidence` (`verified`, `measured`, `inferred`, `rough`) and its session.

```python
from fruitylink.plugins import scale_for, scales_for, known_plugins

gain = scale_for("Pro-L 2", "Gain")             # plugin names match case-insensitively, aliases and containment
gain.to_display(0.6)                             # 18.0 dB
v = gain.to_normalized(12)                       # 0.4
fl.mixer[0].effects[2].parameters.set_verified("Gain", v)
scale_for("Fruity Reeverb 2", "Low cut").to_normalized(300)   # interpolated between the measured points
scale_for("Serum 2", "Sub Shape").to_display(0.25)            # "RoundRect"
[s.describe() for s in scales_for("Fruity Delay 3")]
```

`describe()` states the formula or measured range and the confidence; `inferred`/`rough` entries are a first guess to confirm with `set_verified` and a display read in the next request. Unknown plugins or parameters raise `LookupError` naming what is known; `add_scale(ParameterScale(...))` registers a calibration for the running process.

### Plugin preset files

`fl.channels[n].load_preset(path)` (also `fl.channels.load_preset_file(n, path)`) loads the hosted plugin's own preset file into the generator already on the channel; it is `load_state` under the name the common case needs. Formats seen live on FL 26.1.3: FL `.fst`, VST3 `.vstpreset` (Serum 2; see the Serum guide), and GMS `.gmsynth` from `<FL>\Data\Patches\Plugin presets\Generators\GMS\...`. A raw `.SerumPreset` is ignored. Load a factory preset before authoring a native synth by parameter: a fresh GMS added with `channels.add("GMS")` has no oscillator waveforms (its Synth Waves are chosen in the GUI and are not parameters), its displays are raw fractions (`Filter Cutoff = 0.60 %`), and parameter-only patches render silence until a preset is loaded.

Two routes exist and the SDK picks the working one. A wrapped plugin's preset goes through the wrapper's own state-file loader, which changes the state in place. An **FL-native generator `.fst`** (`<FL>\Data\Patches\Plugin presets\Generators\Sytrus|Harmor|...`) goes through **FL's channel loader** instead, chosen automatically, because that loader is the only route FL honours for those files (FL 26.1.3.5570: a Sytrus factory preset sent to the wrapper loader left the channel's state record byte-identical; the channel loader changed 99% of it, Harmor 47.5%). FL's channel loader treats the file as a channel to build, so it also **mutes the channel and renames it to the preset's base name** — the SDK snapshots the channel's name, mute state and mixer route before the load and restores all three after it, and the returned verification line names the route and what was restored:

```python
fl.channels[3].load_preset(r"C:\Program Files\Image-Line\FL Studio 2026\Data\Patches\Plugin presets\Generators\Sytrus\Lead\Sync Lead.fst")
# channel 3: loaded 'Sync Lead.fst' into 'Sytrus' via FL's channel loader (automatic for an FL-native
# generator .fst: the wrapper dispatcher is a no-op for these; restored name 'Sync Lead' -> 'Sytrus',
# mute -> unmuted) (same instance; params 0->0; state record changed (...)). ...
assert not fl.channels[3].muted and fl.channels[3].name == "Sytrus"
```

`load_state(path, use_channel_loader=True)` forces the channel-loader route for a wrapped plugin's `.fst` as well (state is still preserved); it stays refused for other formats, where that loader applies no state and only renames the channel. The `.fst` identity guard matches the plugin name in both spellings a preset can use — UTF-16 for wrapped plugins, a single-byte string for FL's own generators — so a native preset is no longer refused with `does not name the hosted plugin 'Sytrus'`.

`get_state()` / `load_state()` on a Sampler channel now say so: `Cannot read the plugin state of channel 8: it is a built-in Sampler channel (or an audio clip / layer) ...` and point at the channel controls, `replace_sample` and the sample operations; automation clips are named with their targets. If a state read reports that the project snapshot could not be framed (FL wrote an event the reader does not know), the message names the event, offset and FL build: save the project or close it to a new version and reopen, then retry (the FL 26.1.3.5570 tagged event 0xAC is already handled).

## Sample search roots

`fl.samples.query(...)` and `fl.plugins.samples_text(...)` search, in this order:

| Tag | Folder |
| --- | --- |
| `[B1]`, `[B2]`, ... | Every folder named by `FRUITYLINK_SAMPLE_ROOTS` (semicolon-separated absolute paths), then **FL's browser extra search folders** |
| `[P]` | FL's factory packs, `<FL install>\Data\Patches\Packs` |
| `[U]` | The user's Image-Line content, `Documents\Image-Line\FL Studio` |

Every entry is `<tag><relative path>` and goes back verbatim to `fl.channels.add_sample`,
`Channel.replace_sample` and the MCP `fl_sample_add` tool, which resolve the tag against the same list (a
managed MCP session first copies the file into `<workspace>\staged-samples\<tag>\`, one subfolder per tag).

A user's own library usually lives under neither FL root and reaches FL's browser as an **extra search
folder**. FL 2026 keeps those in the registry, not in a settings file:
`HKCU\Software\Image-Line\FL Studio <major>\Search paths`, one `REG_SZ` per folder named `0`, `1`, ...,
whose value is `<absolute folder>,<display name>`. Every `FL Studio <major>` key is read, newest first, and
duplicates are dropped. Live 2026-09-18: an agent asked to use the user's own drums found `[U]` empty, because
those two roots were all the SDK ever searched.

The user's folders are searched FIRST because a listing is capped (40 entries unfiltered, 150 filtered) and
FL's factory packs are enormous — the user's own samples would otherwise never make the cut. A folder that
does not exist, repeats, or lies inside a root already in the list is dropped, so one file is never listed
twice under two tags. Set `FRUITYLINK_SAMPLE_ROOTS` for a library that is not in FL's browser at all.

## Add a mixer insert

`fl.mixer.list()` returns Master and active ordinary inserts with their real indices and `kind` (`master` or `insert`). Iteration and `len(fl.mixer)` use that set. The special Current track and dormant slots are excluded. Never convert the low-level native mixer count into a range of physical track IDs.

`fl.mixer.add("Bus")` appends an insert. `fl.mixer.add("Bus", after=3)` inserts after track 3; `after=0` inserts after Master. The returned handle identifies the added insert. Naming is a separate edit, so a naming failure leaves the insert created.

Insertion shifts later indices. Requery tracks and routes and reacquire retained mixer, effect and parameter handles before further edits. Creation does not load effects or set routing. Consult capabilities before depending on native insertion support.

### Mixer capacity

The default template has 16 ordinary inserts (`fl.mixer.insert_count == 16`; the native count 18 includes Master and the Current pseudo-track), and FL allows 500 (`fl.mixer.capacity`). Addressing `fl.mixer[17]` on that template is refused with `Mixer track must be 0..16 ... Insert 17 does not exist yet ... call add_mixer_track`; grow the mixer first. `fl.mixer.ensure_inserts(20)` appends inserts until at least 20 exist and returns how many were added; appending never shifts existing indices. Name them afterwards:

```python
names = ["Kick", "Snare", "Hats", "Bass", "Keys", "Vox"]
fl.mixer.ensure_inserts(len(names) + 12)          # 18 inserts, no error at 17
for offset, name in enumerate(names, start=13):
    fl.mixer[offset].name = name
```

### Confirm what an effect slot holds

`EffectSlot.load(...)` returns a verification line, but the handle itself said nothing afterwards: its repr was
`<fruitylink.mixer.EffectSlot object at 0x...>`, so an agent could not confirm what it had loaded (live
2026-09-18). A slot now describes itself:

```python
slot = fl.mixer[64].effects[0]
slot.load("Pro-Q 4")
slot.plugin_name          # 'Pro-Q 4'  (None when the slot is empty)
slot.is_empty             # False
repr(slot)                # "EffectSlot(track=64, slot=0, plugin='Pro-Q 4')"
fl.mixer[64].effects.names()    # {0: 'Pro-Q 4', 3: 'Fruity Reeverb 2'} - the whole chain in ONE call
fl.mixer[64].effects.loaded()   # the non-empty slots, each with its name already cached
```

`plugin_name` costs one `list_mixer_effects` call and is then remembered on that handle; `load`, `load_state`
and `clear` forget it, `refresh()` forgets it on demand, and a fresh `fl.mixer[t].effects[s]` always reads the
live slot. The name is FL's plugin database name, the same string `load` takes, so it round-trips. `repr`
never fails: a host that cannot answer prints `EffectSlot(track=64, slot=0)` rather than a guess.

## Verify sends and bus routing

`fl.mixer[track].sends()` reads the track's native send table as `MixerSendInfo` records (`source`, `destination`, `destination_name`, `level`, `active`, plus the modelled `level_db`); `fl.mixer.routes()` concatenates every track's sends. The level is FL's send scale: **0.8 is unity (0 dB)**, the level every untouched insert's Master route reads back; 1.0 (the historical default of `send_to`) is the knob top, about +4.05 dB. A route set to level 0 stays connected and is listed with level 0; `disconnect(destination)` (or `send_to(destination, 0.0, active=False)`) removes it from the list. Verify after every routing write instead of parsing `effects.list_text()`:

```python
for insert in range(8, 15):                    # kick .. perc -> "Drum Bus" at unity, Master silent
    fl.mixer[insert].send_to(15, 0.8)
    fl.mixer[insert].send_to(0, 0.0)
assert all(s.level == 0.0 for s in fl.mixer.routes() if 8 <= s.source <= 14 and s.destination == 0)
print(fl.mixer[8].send_level(15), fl.mixer[8].sends())
```

Sidechain routes cannot be created or recognised: FL's "Sidechain to this track" is a differently flagged route whose flag lives outside the verified mixer layout (a per-track pointer table the scanner does not profile), and the route-active core can raise a confirmation dialog when a sidechain destination is disabled. A plain send always sums audio into the destination, so a limiter's or Pro-C 3's external input stays silent. Keep the computed duck instead: `fl.automation.pump(AutomationTarget.mixer_volume(bass_insert), ...)` keyed to the kick pattern (see [recipes](examples.md#add-a-sidechain-pump)), or flip the route to sidechain in the FL GUI.

## Volume in decibels

FL's volume controls are raw integers on a curve that is neither linear in gain nor in dB. `fruitylink.levels` models FL's fader law as a **single power curve** anchored at the fader position 0.8 that FL labels 0 dB: `dB = 20 * 2.09 * log10(position / 0.8)`. The exponent is measured, not inferred from FL's fader hint - a render calibration (Parking Lot Moon, 2026-09-14: an isolated held chord, bars 110-111, RMS of the render) and the one full-mix master move fix it by least squares:

| Move | Positions | Measured | Model (2.09) |
| --- | --- | ---: | ---: |
| channel volume 12800 -> 6400 | 1.0 -> 0.5 | -12.54 dB | -12.58 dB |
| mixer volume 12800 -> 6400 | 0.8 -> 0.4 | -12.70 dB | -12.58 dB |
| mixer volume 12800 -> 5769 | 0.8 -> 0.3606 | -14.21 dB | -14.47 dB |
| master volume 6800 -> 12800 (Ember Tides) | 0.425 -> 0.8 | +11.80 LU | +11.48 dB |

One exponent fits every point within 0.33 dB, so no piecewise or table-based mapping is needed and channel volume, mixer volume and sends share the curve. Two cautions: FL's fader hint claims +5.6 dB at the top, where this curve says **+4.05 dB** (`levels.FL_HINT_TOP_DB` keeps FL's number, `levels.FADER_MAX_DB` the measured one) - the renders are what the SDK promises; and every measurement lies between position 0.36 and 0.8, so below about -20 dB the curve is an extrapolation (+-3 dB) - confirm large cuts with a render. Every helper takes `exponent=` for a fresh recalibration. `Channel.volume_db`, `MixerTrack.volume_db`, `set_volume(db=...)` and `send_to(destination, db=...)` apply the model; gains above +4.05 dB are refused, so trim with a plugin gain (Parametric EQ 2 main level, Pro-Q output) for large boosts.

| Raw | Channel volume (0..12800) | Mixer volume (0..16000) |
| ---: | ---: | ---: |
| 16000 | - | +4.1 dB (fader top) |
| 12800 | +4.1 dB (knob top) | 0.0 dB (default) |
| 10240 | 0.0 dB | -4.1 dB |
| 10000 | -0.4 dB (FL default) | -4.5 dB |
| 8000 | -4.5 dB | -8.5 dB |
| 6400 | -8.5 dB | -12.6 dB (measured -12.5 / -12.7) |
| 5769 | -10.4 dB | -14.5 dB (measured -14.2) |
| 5000 | -13.0 dB | -17.1 dB |
| 3200 | -21.1 dB | -25.2 dB |
| 1600 | -33.7 dB | -37.8 dB |
| 0 | -inf | -inf |

Send levels use the mixer law on 0..1 (the native send value is `level * 16000`, so they are not calibrated separately): 1.0 = +4.1 dB, 0.8 = 0 dB, 0.5 = -8.5 dB, 0.25 = -21.1 dB. `channel_volume_from_db(-6)` is 7358, `mixer_volume_from_db(-6)` is 9197, `send_level_from_db(-6)` is 0.575; `levels.volume_table(step)` prints the whole curve.

## Sampler channel settings

Built-in Sampler channels have no hosted-plugin parameter interface. Two Sampler controls are reachable as FL REC_Chan events through the same command bus as volume and pan: `Channel.stretch_time` (time-stretch "Time", event 14) and `Channel.sample_offset` (sample start offset, event 13), with the generic `Channel.control(index)` / `set_control(index, value)` and the `ChannelControl` enum for the rest of the table (filter cutoff 2, resonance 3). These indices come from the FL SDK table and are not live-verified yet: read first, write, and confirm in the Channel settings window; values are FL's raw units. Reverse, fade in/out, trim/sample end and the stretch mode are not REC events and have no path from the bridge (Edison is GUI-only). Pre-process the file and load it instead:

```python
import wave

with wave.open(src, "rb") as source:
    params = source.getparams()
    data = source.readframes(source.getnframes())
frame = params.sampwidth * params.nchannels
keep = int(8 * 4 * 60 / 100 * params.framerate) * frame            # 8 bars at 100 BPM
frames = [data[i:i + frame] for i in range(0, min(keep, len(data)), frame)]
with wave.open(dst, "wb") as target:
    target.setparams(params)
    target.writeframes(b"".join(reversed(frames)))                   # drop reversed() to keep it forward
fl.channels[8].replace_sample(dst)
```

Apply fades by scaling samples (the Parking Lot Moon records carry a pure-Python fade). Time-stretching to the project tempo also stays offline (ffmpeg `atempo`, or a stretched export from the sample's DAW); FL's Python has no numpy.

## Retire a channel

FL exposes channel deletion only through the channel-rack context menu; there is no engine call the bridge can make, so `fl.channels` has no `delete`. `fl.channels[0].retire()` (or `fl.channels.retire(0)`) parks a channel instead: muted, routed to Master (so it holds no bus) and renamed `"(unused) <old name>"` (pass `name=` for another label). Notes and clips are untouched; clear patterns yourself. The template's empty Sampler channel is the usual candidate.

Retiring does **not** clear a sample channel's file reference, and a project saved while a channel points at a file that no longer exists hangs FL's command-line renderer outright (live 26.1.3.5570, 2026-09-17: no render output at a 300 s or 600 s deadline, and no dialog; 7.5 s once the channels were repointed). So when you retire a channel whose sample you are about to delete, point it at a file that exists first: `fl.channels[i].replace_sample(path)`. `fl.audio.capture` does this for every channel it retires, using a tiny silent `fruitylink-retired-placeholder.wav` in FL's recorded-audio folder (`repointed_channels` / `placeholder`).

## Linked automation and complete envelopes

Use `AutomationTarget.channel_volume`, `channel_pan`, `channel_pitch`, `mixer_volume`, `mixer_pan`, or a plugin parameter target with `fl.automation.create(...)`.

Plugin parameter targets accept both argument orders: `plugin_parameter(index, parameter, slot=-1)` (the SDK order) and `plugin_parameter(track, slot, parameter)` (the `track/slot/param` order automation records use, so a record line `1/0/556` is `plugin_parameter(1, 0, 556)`), plus keywords `track=`/`channel=`, `slot=`, `parameter=`/`param=`. `generator_parameter(channel, parameter)` and `effect_parameter(track, slot, parameter)` are the unambiguous spellings, and `from_record("1/0/556")`, `from_record((1, 0, 556))` or `from_record({"track": 1, "slot": 0, "param": 556})` build the same target. `slot=-1` selects a generator channel; slots 0..9 select mixer effects. Parameter indices come from the plugin parameter API. Native target identifiers are not public API, but `AutomationTarget.from_event_id` decodes the `event 0x...` ids the host prints. Creation links its initial target; these helpers do not attach additional targets to an existing automation channel.

**Mixer send levels have no target.** FL's per-send event ids are not known to the bridge (`set_mixer_send` writes the send table directly), so `AutomationTarget` cannot address "Insert 4 -> Insert 24 level". The official recipe: automate the return insert's own level with `AutomationTarget.mixer_volume(return_insert)` (moves every source feeding it together, and 1.0 is 12800 = 0 dB, so base the curve on the insert's current level), or, per source, the send effect's own wet/mix parameter with `AutomationTarget.effect_parameter(track, slot, index)` (for example Fruity Delay 3 "Output wet"). Send levels read back through `fl.mixer[track].effects.list_text()` (`sends:` line, 0.8 = unity).

Every point's tension belongs to the segment that ends at that point. Positive tension moves fast first and eases into the point (`TENSION_EASE_OUT`; a compressor-style recovery, and what `pump` uses on its recoveries); negative tension starts slowly and accelerates into the point (`TENSION_EASE_IN`; a filter that opens into a downbeat). Zero is a straight line.

The first and last points of a curve are protected endpoints: `delete(index)` refuses them. `set_point(index, value, tension=0)` changes any point in place, including both endpoints, and refuses curves that contain non-linear points.

`set_points()` replaces the whole envelope. Supply 2..4000 `AutomationPointSpec` records: first time zero, strictly increasing times, values 0..1, tension -1..1, and linear curve 0. Point times are beats. Clip placement uses ticks, and clip length does not automatically set the envelope's last point. [The automation recipe](examples.md#create-linked-automation) shows both spans explicitly.

Creation can partially complete before an error. Inspect channels and playlist clips before retrying. `add_clip()` reuses an existing automation generator at another playlist position; each placement plays the same envelope from its own start (automation clips have no loop or offset), so a shape that differs per bar needs one envelope across the range (`duck`, `tile`).

**An automated fader cannot be pinned by a plain write, and deleting the clip is not enough.** While an automation channel targets a control, FL keeps re-applying that channel's value over it: a plain `fl.mixer[t].volume = ...` reads back correctly in the same session but is overwritten at the playhead (live 2026-09-14: 5769 written, 15926 — the clip's last value — after a save and reopen). **Deleting the playlist clip does not break the link** (live 2026-09-17): after `fl.clips.delete(...)` of the only clip of automation channel 12, `fl.mixer[5].volume = 6400` still read back 6400 while the master capture measured the same level as 12800, and routing the audio through insert 10 instead gave the expected -12.44 dB. FL has no channel delete, so the automation channel itself survives the clip and keeps pinning its target. To free a fader, the **automation channel itself must be retargeted** or the **audio routed through a different insert** (both verified live); deleting or muting its clips is not enough. Otherwise set the value through the envelope (`set_point` / `set_points`) rather than the fader, or write the fader on an insert no automation channel targets.

See [automation links](../automation-links.md) for the SDK side of this: `fl.automation.links_to(target)` / `targets_of(kind, index, slot, parameter)` name the automation channel that owns a control (one cached rack scan per connection; `fl.automation.refresh()` rescans after GUI edits), plain `volume` / `pan` / `pitch` and plugin-parameter writes warn with `AutomationLinkedWarning` unless you pass `linked="raise"` or `linked="ignore"` (`VerifiedWrite.automation_linked` carries the same text), and `fl.automation.release(channel, value=None)` flattens a curve to one held value so the level FL keeps reapplying is the one you asked for. **A curve is only evaluated through a placed clip**, so `release` places one at tick 0 on the first free track when the channel has none (live 2026-09-17: with the clip deleted, releasing to 0.8 and to 0.4 both measured -19.6 dBFS at the master; with a clip, 0.4 measured -32.18 and 0.8 measured -19.60 dBFS — the 12.58 dB the fader model predicts). Its `ReleaseResult` is the released float plus `placed_clip`, `track`, `start_tick`, `length_tick`, `clip_index`; pass `place_clip=False` to leave the playlist alone. Note that a **readback of the target never shows the automation-applied value** (`fl.mixer[1].volume` read 12800 throughout that run): the getters report the project value, not what the engine is playing, so only `links_to` and a render or capture can tell you.

`fl.automation.list()` is the inventory: one `AutomationChannelInfo` per linked automation clip channel with `name`, the host's `targets_text` (`"Insert 24 volume"` or `"event 0x71008030"`), `event_ids`, decoded `targets` (`target` is the first decoded one), `point_count` and the playlist `clips` that play it (`ClipInfo` records). `describe()` renders the same as one line per clip. Both read every channel's plugin description and, unless `with_points=False`, each envelope once.

## Pattern clips do not repeat

**FL plays a pattern clip's pattern ONCE, from the clip start, and the rest of the clip is silent.** There is
no loop or repeat flag on a playlist clip; a long clip is not four bars of a one-bar pattern, it is one bar of
music followed by three bars of nothing.

Live measurement (FL 26.1.3.5570, 2026-09-18): a 1-bar pattern placed with
`fl.playlist.add_patterns([PatternClipSpec(p, 1, 0, 4 * BAR)], enforce_lengths=True)` produced the clip
`(start 0, length 1536)` — exactly the four bars asked for at PPQ 96 — and a master capture of that span had
audio in **bar 1 only**; bars 2, 3 and 4 measured silent. An agent that had placed 4-bar patterns as 8- and
16-bar clips produced, in the user's words, "huge amounts of empty space", and nothing in the SDK warned.

Place one clip per repetition:

```python
BAR = 4 * fl.ops.get_ppq()

# One clip per pattern length across bars 1-16, the last clip shortened to end exactly on the span.
placed = fl.playlist.tile_pattern(drums, track=1, start_tick=0, length_tick=16 * BAR)
print(int(placed), placed.pattern_length_tick)      # 16 1536  (a 1-bar pattern: sixteen clips)

fl.playlist.add_pattern_ticks(drums, track=1, start=16 * BAR, length=8 * BAR, repeat=True)   # same, inline
fl.playlist.add_patterns([PatternClipSpec(drums, 1, 0, 16 * BAR)], repeat=True)              # same, batched
```

| Call | Behaviour |
| --- | --- |
| `fl.playlist.pattern_length(pattern)` | The host-reported pattern length in ticks (`fl.patterns.list()` `lengthTick`, also `fl.patterns[p].length_tick`): the span one clip actually plays, and the tile width. `LookupError` when the pattern is missing or the host reports no length. |
| `fl.playlist.tile_pattern(pattern, *, track, start_tick, length_tick)` | Fills `[start_tick, start_tick + length_tick)` with one clip per repetition in ONE native pass; the last clip is shortened to fit, so the run ends exactly where you asked. Returns a `PatternPlacement`, which IS the clip count and also carries `pattern_length_tick`, `repeated` and `notice`. Over `MAX_TILED_CLIPS` (1000) clips is refused before any write. |
| `fl.playlist.tile_pattern_beats(pattern, *, track, start_beats, length_beats)` | The same in beats at the project PPQ. |
| `fl.playlist.add_pattern_ticks(pattern, *, track, start, length=0, repeat=False)` | `repeat=True` tiles the span instead of placing one long clip. `repeat=False` with a `length` longer than the pattern places the long clip and warns (below). `length=0` follows the pattern and never needs a lookup. Returns a `PatternPlacement`. |
| `fl.playlist.add_pattern(pattern, *, track, start_beats, length_beats=0, repeat=False)` | The same in beats. |
| `fl.playlist.add_patterns(specs, *, enforce_lengths=False, repeat=False)` | `repeat=True` expands every over-long spec into tiles before the single native pass. The result is still the number of clips RESIZED by `enforce_lengths`, and additionally carries `.placed` (clips written), `.repeated` and `.notices`. |

With `repeat=False` a clip longer than its pattern emits `PatternClipLongerThanPatternWarning`, naming the
pattern, its length, the clip length and how much of the clip will be silent; the same text is in the returned
`PatternPlacement.notice` (or `PatternPlacementBatch.notices`) for callers that do not watch warnings:

```
Pattern 12 is 384 ticks long but the clip asks for 1536 ticks (4.00 pattern lengths): FL does not loop a
pattern clip, so it plays once and the last 1152 ticks of the clip are SILENT. Place one clip per repetition
instead - repeat=True on this call, or fl.playlist.tile_pattern(12, track=..., start_tick=..., length_tick=1536).
```

The advice costs ONE shared `query_patterns` per call (none at all when every length is 0), and a host that
cannot describe its patterns simply gets no advice — the placement is never failed or delayed by the check.

This is also why `fl.playlist.gaps` and `fl.playlist.onsets` count each clip's pattern exactly once: their
"clips do not loop" assumption is FL's real behaviour, verified above, so the silent tail of an over-long clip
is correctly reported as a rest rather than as material.

## Composition helpers

These build on the queries above and compute locally; they issue no new native operations.

| Helper | Behaviour |
| --- | --- |
| `pattern.gaps(channel=None, min_ticks=None, min_beats=1.0, *, end_tick=None)` | Rest regions from the pattern's note snapshot as `Gap` records (`channel`, `start_tick`, `end_tick`, `length_tick`, `start_beat`, `length_beats`), sorted by time. A note occupies `[start, start + length)`; muted notes are silent. The span ends at the later of the host-reported pattern length and the last note end unless `end_tick` is given. `channel=None` covers every channel with notes; a named channel without notes yields one gap over the whole span. `min_ticks` overrides `min_beats` (converted at the project PPQ). |
| `fl.playlist.gaps(start_bar, end_bar, channel=None, min_beats=1.0, *, beats_per_bar=4)` | The same over an arrangement range, bars `start_bar..end_bar` inclusive (one-based), in absolute ticks. Reads unmuted pattern clips overlapping the range and each pattern's notes once. A note starting inside its clip sounds for its full length; notes starting after the clip end do not. Clips are treated as starting at their pattern's beginning (sliced clips with an offset cannot be distinguished), and each clip's pattern is counted ONCE because FL does not loop a pattern clip ([verified](#pattern-clips-do-not-repeat)), so the silent tail of an over-long clip is reported as a rest. Automation and audio clips are ignored. |
| `fl.playlist.first_free_track(start_tick, end_tick, *, above=1)` | Lowest one-based track `>= above` with no clip of any kind (pattern, audio, automation; muted included) overlapping `[start_tick, end_tick)`. Raises `LookupError` when tracks up to 500 are all used. |
| `fl.automation.pump(target, start_tick, length_tick, *, track, depth=0.5, recovery_beats=0.75, beats_per_hit=1, floor=None, ceiling=1.0, tension=0.5, name=None)` | Sidechain-style curve: creates and places the clip with `create(...)`, then `set_points(...)`. Each hit drops to `floor` (default `ceiling - depth`), recovers to `ceiling` over `recovery_beats` (`tension` shapes that segment), holds, and drops one tick before the next hit; the last point sits at the clip end. Returns `PumpResult(clip, point_count)`. Over 4000 points is refused before any write; split long spans. |
| `fl.playlist.onsets(channel, start_tick, end_tick)` | Sorted absolute note-on ticks of one channel inside `[start_tick, end_tick)`, read through every unmuted pattern clip with the same rules as `gaps` (muted notes silent, notes past their clip end silent, clips assumed to start at the pattern's beginning). Each clip contributes its pattern's onsets ONCE — FL does not loop a pattern clip ([verified](#pattern-clips-do-not-repeat)) — so a tiled span yields every repetition and an over-long clip yields only the first. The input for `duck`. |
| `fl.automation.duck(target, hits_ticks, start_tick, length_tick, *, track, depth=0.5, recovery_beats=0.25, floor=None, ceiling=1.0, tension=0.5, name=None)` | `pump` for an irregular grid: ONE envelope that holds `ceiling` until one tick before each absolute tick in `hits_ticks` inside the clip, drops to the dip (`floor`, default `ceiling - depth`) at the hit and recovers over `recovery_beats` (cut short by a closer hit). Follows alternating kick bars, fills and pre-chorus variants that no repeated 1-bar clip can. Returns `PumpResult`; over 4000 points is refused before any write (split the span). |
| `fl.automation.tile(target, shape, start_tick, length_tick, *, track, period_beats, offsets_beats=(0,), name=None)` | ONE envelope that repeats a clip-relative `shape` (2+ points from beat 0, fitting one period) every `period_beats`; repeat `i` is shifted by `offsets_beats[i % n]` (`[0, 0.5]` alternates bars). The last value holds between repeats; a partial last repeat is cut at the clip end. |
| `fl.transport.set_song_end(bar=None, *, after_bar=None, tick=None, name="End", beats_per_bar=4)` | Places a single end marker at a bar start or an absolute tick, deleting any marker with the same name first, and returns the `Marker` as the host lists it afterwards. `bar=N` puts the marker at the start of bar N (the song ends before it, so bar N is NOT played); use `after_bar=N` to keep bar N — the marker then lands at the start of bar N+1, so a 96-bar song wants `after_bar=96` (equivalently `bar=97`) and `bar=96` cuts the last bar. The three arguments are mutually exclusive. It relies on FL extending the song length, play range and render to the last time marker, so a marker past the final note adds silence or room for tails. |
| `fl.transport.seek_settled(tick, *, attempts=20, delay=0.05)` | Seeks, then polls `position_tick` until two consecutive reads agree; returns `SeekResult(requested_tick, position_tick, settled, reads)`. `seek_ticks(tick, settle=True)` is the same call. |
| `fl.transport.read_at(tick, reader, *, settle=0.3, attempts=6, delay=0.1)` | Settled seek, `settle` seconds for the host's automation pass, then calls `reader()` until two consecutive values are equal. `parameters.read_at(index, tick)` / `display_at(index, tick)` wrap it for a plugin parameter. |

`pump` on `AutomationTarget.channel_volume` recovers to `ceiling`, and 1.0 there is the 12800 channel maximum, above FL's default level. Pass `ceiling` (0.78 is roughly the default) or automate `mixer_volume` instead. The pure builders `fruitylink.automation.pump_points(length_beats, ppq=...)`, `duck_points(hits_beats, length_beats, ppq=...)` and `tile_points(shape, period_beats, length_beats, ppq=...)` return envelopes without a connection.

## Batches and bulk edits

Use `NoteSpec`, `NoteEdit`, `NoteRef`, `ClipMove`, `ClipResize` and `PatternClipSpec` for native bulk operations. A single bulk operation refreshes FL once. `fl.clips.resize` and `fl.clips.move` also take plain tuples or a single clip (`fl.clips.resize(1, 3072)`, `fl.clips.move(4, 768, 3)`), and `delete` / `set_muted` take one index or a sequence. `PatternClipSpec(pattern, track, start_tick, length_tick=0)`: a positive length is pinned by the host, so an 8-bar clip stays 8 bars even when the pattern's notes overhang; 0 follows the pattern length. A pinned length is NOT a loop — an 8-bar clip of a 2-bar pattern is silent after bar 2 ([verified](#pattern-clips-do-not-repeat)) — so pass `repeat=True` to expand such specs into one clip per repetition, or accept the `PatternClipLongerThanPatternWarning`. `fl.playlist.add_patterns(specs, enforce_lengths=True)` re-lists the clips afterwards and resizes any that still differ (returns how many). `NoteRef` and `NoteEdit` address a note by `(channel, key, start_tick)`; FL allows stacked duplicates sharing that triple, so both carry an optional `length_tick` (the note's current length) to pick one, and `notes.edit(...)` / `notes.delete(...)` refuse a target that still matches several notes unless `allow_multiple=True`, which addresses all of them (nothing is written on a refusal). A general batch sequences multiple independent operations, up to 256:

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

### Recording state

`fl.transport.recording_filter` is FL's global recording filter, the bitmask behind the record button's
right-click "Recording filter" submenu (1 automation, 2 notes, **4 audio**, 8 clips; FL 2025 has no clips
item). With the audio bit clear FL arms a mixer insert, records, and writes no file at all, so
`fl.audio.capture` sets it for itself and hands the previous value back — nothing has to be clicked in
FL first. Set parts without disturbing the others through
`fl.transport.ensure_recording_filter(audio=True)`, which returns the bitmask it found; the pure helpers
`fruitylink.recording_filter_flags(current, **parts)` and `recording_filter_names(flags)` do the same
arithmetic offline. FL only reads its `RecordingFilter2` registry value at startup and writes it at exit,
so this is the only way to change it in a running FL.

FL's **global transport toggles** are properties too: `metronome`, `countdown` (the toolbar precount),
`wait_for_input` ("wait for input to start playing"), `loop_record` and `blend_recorded_notes` (overdub),
alongside `song_mode`. They are FL-wide settings that FL saves when it exits, so read them, set them, and hand
them back: `previous = fl.transport.ensure(countdown=False, metronome=False)` ... `fl.transport.ensure(**previous)`.
`fl.transport.settings()` returns `{name: bool}` for exactly the toggles the running build exposes — a toggle
whose native symbols did not resolve is left out rather than guessed. Each one is set through the very setter
FL's own toolbar Action calls, so FL repaints and reacts as it does for a click. `countdown` and
`wait_for_input` stop an automated record pass from recording anything, `loop_record` turns one recording into
a pile of takes and `metronome` is mixed into captured audio, which is why `fl.audio.capture` switches all four
off for itself and restores them (reported as `CaptureResult.toggles`).

`fl.transport.record_pressed` reads whether FL's transport record button is engaged. `toggle_record()`
merely flips it and FL leaves it engaged after a recording pass, so code that needs recording on should
use `ensure_record_pressed()` (which returns the previous state) rather than toggling blindly.

`fl.analysis.wav(...)` and `fl.analysis.pcm(...)` return local audio analysis objects; the same API is available without FL through `from fruitylink.analysis import Analysis`. Return `.summary()`, `.windows(...)`, `.spectral()` or `.spectral_windows(...)` from a script.

These APIs read supplied audio; they do not capture live output or render a project. `note_density(...)` measures pattern-local symbolic notes, independently of audio loudness or repeated Playlist playback. See [audio analysis](../python-audio-analysis.md), [spectral analysis](../python-spectral-analysis.md) and [an offline example](examples.md#analyze-a-wav-without-fl-studio).

For an agent that can only read text, `fl.analysis.describe(path_or_pcm, bpm=..., detail=...)` returns an `AudioDescription` whose `.text` is a compact rendering (level, envelope sketch, onsets in bar:beat, decay, spectral balance per segment, tonality and root, width, loop hints, character tags); `fl.analysis.compare(a, b)` gives `b minus a` in the same vocabulary and `fl.analysis.describe_samples(paths)` browses many files with a cache. `fl.samples.describe(channel)` does the same for the sample loaded into a channel through this session. See [describing audio for an agent](../python-audio-analysis.md#describing-audio-for-an-agent).

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
