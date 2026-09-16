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
| `fl.transport` | `tempo`, `song_mode`, `play()`, `stop()`, `toggle_record()`, `seek_beats()`, `seek_ticks(tick, settle=False)`, `seek_settled(tick)`, `position_tick`, `read_at(tick, reader)`, `loop_beats()`, `loop_ticks()`, `clear_loop()`, `markers_text()`, `markers()`, `add_marker_beats()`, `add_marker_ticks()`, `delete_marker(index_or_name)`, `set_song_end(bar=None, tick=None, name="End")` |
| `fl.channels` | `list()`, `find(name)`, `add(plugin, name=None)`, `add_sample(path, name=None)`, `retire(index, name=None)`, `[index]` |
| Channel | `name`, `volume`, `volume_db`, `set_volume(value=None, db=None)`, `pan`, `pitch`, `muted`, `mixer_track`, `parameters`, `control(index)`, `set_control(index, value)`, `stretch_time`, `sample_offset`, `select()`, `toggle_solo()`, `replace_sample(path)`, `retire(name=None)`, `load_state(path)`, `get_state()` |
| `fl.patterns` | `list()`, `find(name)`, `current`, `create(name=None)`, `[index]` |
| Pattern | `name`, `notes`, `select()`, `clear()`, `clone()`, `gaps(channel=None, min_ticks=None, min_beats=1.0)`; clone can return `None` for an empty source |
| Notes | `list(channel=-1)`, `add(records)`, `add_beats(...)`, `add_ticks(...)`, `edit(edits, allow_multiple=False, preserve_clips=False)`, `delete(refs, allow_multiple=False, preserve_clips=False)` |
| `fl.playlist` | `list()`, `[track]`, `add_pattern(...)`, `add_pattern_ticks(...)`, `add_patterns(records, enforce_lengths=False)`, `first_free_track(start_tick, end_tick, above=1)`, `gaps(start_bar, end_bar, channel=None, min_beats=1.0)`, `onsets(channel, start_tick, end_tick)` |
| PlaylistTrack | `name`, `color`, `muted`, `collapsed`, `select()`, `toggle_solo()` |
| `fl.clips` | `list(track=-1)`, `[index]`, bulk `move(records)` / `move(index, start_tick, track)`, `resize(records)` / `resize(index, length_tick)`, `delete(indices_or_index)`, `set_muted(indices_or_index, muted)`; records may be `ClipMove`/`ClipResize` or plain tuples |
| Clip | `muted`, `move_beats()`, `move_ticks()`, `resize_beats()`, `resize_ticks()`, `slice_beats()`, `slice_ticks()`, `duplicate()`, `delete()` |
| `fl.arrangements` | `list()`, `current`, `add(name)`, `[index]`; a handle has `name`, `select()`, `clone()`, `delete()` |
| `fl.mixer` | `[track]`, `master`, `list()`, `insert_count`, `capacity`, `ensure_inserts(count)`, `add(name=None, after=None)`, `find(name)`, `routes()`, `list_text()` |
| MixerTrack | `name`, `volume` (0..16000, 12800 = 0 dB), `volume_db`, `set_volume(value=None, db=None)`, `pan` (signed, 0 = center), `muted`, `send_to(destination, level=1.0, db=None, active=True)`, `disconnect(destination)`, `sends()`, `send_level(destination)`, `set_eq_gain(band, value)`, `effects[slot]` |
| EffectSlot | `load(plugin)` (tolerant name: "FabFilter Pro-R 2" loads "Pro-R 2"; ambiguous names are refused with the candidates), `clear()`, `clone_type_to(slot)`, `parameters`, `load_state(path)`, `get_state()` |
| Parameters | `page(filter=None, offset=0, limit=64)`, lazy `iter(filter=None, page_size=64)`, `list(filter=None)`, `read(index)`, `read_at(index, tick, settle=0.3)`, `display_at(index, tick)`, `find(name)`, `set(index, value)`, `set_named(name, value)`, `set_verified(index_or_name, value, attempts=6, delay=0.05, settle_display=True)` |
| `fl.plugins` | `available_text(effects=False)`, `samples_text()`, `channel_parameters()`, `effect_parameters()` |
| `fl.automation` | `list(with_points=True)`, `describe()`, `create(target, track, start_tick, length_tick, name=None)`, `pump(target, start_tick, length_tick, track=..., depth=0.5, recovery_beats=0.75, beats_per_hit=1, floor=None, name=None)`, `duck(target, hits_ticks, start_tick, length_tick, track=..., ...)`, `tile(target, shape, start_tick, length_tick, track=..., period_beats=..., offsets_beats=(0,))`, `[channel]` |
| AutomationCurve | `list()`, `add_beats(time, value, tension=0)`, `add_ticks(tick, value, tension=0)`, `delete(index)`, `set_point(index, value, tension=0)`, `set_points(points)`, `add_clip(track, start_tick, length_tick)` |
| `fl.analysis` | `wav(path, ...)`, `pcm(channels, sample_rate, ...)`, `describe(source, bpm=None, ppq=None, start_bar=None, beats_per_bar=4, detail="normal")`, `compare(a, b, **options)`, `describe_samples(paths, cache_dir=None, detail="brief", bpm=None)`, `compare_bands(...)`, `masking_report(...)`, `transition(...)`, `note_density(...)`, `tick_range_seconds(...)` |
| AudioAnalysis | `summary(...)`, `windows(...)`, `spectral(...)`, `spectral_windows(...)`, `scan_bars(bpm, ...)`, `band_energy(...)`, `describe_sections(bpm, sections, ...)`, `pitch_track(...)`, `compare_bands(other, ...)`, `masking_report(other, ...)`, `transition(other, bpm, bar, ...)`, `describe(bpm=None, ppq=None, start_bar=None, detail="normal")` |
| AudioDescription / AudioComparison / SampleTable | `.text` (hand to a model), `.data` (JSON-safe), `.tags`; a table also has `.rows` and `.descriptions` |
| `fl.samples` | `describe(channel, path=None, **options)`, `compare(a, b, **options)` (channel indices or paths), `browse(paths, cache_dir=None, detail="brief", bpm=None)`, `register(channel, path)`, `path_of(channel)`, `known()`; paths are remembered from `add_sample`/`replace_sample` because FL exposes no Sampler file query |

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

`query_notes`, `query_clips` and `query_plugin_parameters` return `Page` objects with `items`, `next_offset` and `total`. Offsets refer to raw collection slots. A filtered page can be empty and still have a continuation. Follow `next_offset` instead of adding the item count yourself. High-level `list()` follows all pages; enumeration is not an atomic snapshot, so avoid concurrent structural edits.

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

`get_state()` / `load_state()` on a Sampler channel now say so: `Cannot read the plugin state of channel 8: it is a built-in Sampler channel (or an audio clip / layer) ...` and point at the channel controls, `replace_sample` and the sample operations; automation clips are named with their targets. If a state read reports that the project snapshot could not be framed (FL wrote an event the reader does not know), the message names the event, offset and FL build: save the project or close it to a new version and reopen, then retry (the FL 26.1.3.5570 tagged event 0xAC is already handled).

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

## Linked automation and complete envelopes

Use `AutomationTarget.channel_volume`, `channel_pan`, `channel_pitch`, `mixer_volume`, `mixer_pan`, or a plugin parameter target with `fl.automation.create(...)`.

Plugin parameter targets accept both argument orders: `plugin_parameter(index, parameter, slot=-1)` (the SDK order) and `plugin_parameter(track, slot, parameter)` (the `track/slot/param` order automation records use, so a record line `1/0/556` is `plugin_parameter(1, 0, 556)`), plus keywords `track=`/`channel=`, `slot=`, `parameter=`/`param=`. `generator_parameter(channel, parameter)` and `effect_parameter(track, slot, parameter)` are the unambiguous spellings, and `from_record("1/0/556")`, `from_record((1, 0, 556))` or `from_record({"track": 1, "slot": 0, "param": 556})` build the same target. `slot=-1` selects a generator channel; slots 0..9 select mixer effects. Parameter indices come from the plugin parameter API. Native target identifiers are not public API, but `AutomationTarget.from_event_id` decodes the `event 0x...` ids the host prints. Creation links its initial target; these helpers do not attach additional targets to an existing automation channel.

**Mixer send levels have no target.** FL's per-send event ids are not known to the bridge (`set_mixer_send` writes the send table directly), so `AutomationTarget` cannot address "Insert 4 -> Insert 24 level". The official recipe: automate the return insert's own level with `AutomationTarget.mixer_volume(return_insert)` (moves every source feeding it together, and 1.0 is 12800 = 0 dB, so base the curve on the insert's current level), or, per source, the send effect's own wet/mix parameter with `AutomationTarget.effect_parameter(track, slot, index)` (for example Fruity Delay 3 "Output wet"). Send levels read back through `fl.mixer[track].effects.list_text()` (`sends:` line, 0.8 = unity).

Every point's tension belongs to the segment that ends at that point. Positive tension moves fast first and eases into the point (`TENSION_EASE_OUT`; a compressor-style recovery, and what `pump` uses on its recoveries); negative tension starts slowly and accelerates into the point (`TENSION_EASE_IN`; a filter that opens into a downbeat). Zero is a straight line.

The first and last points of a curve are protected endpoints: `delete(index)` refuses them. `set_point(index, value, tension=0)` changes any point in place, including both endpoints, and refuses curves that contain non-linear points.

`set_points()` replaces the whole envelope. Supply 2..4000 `AutomationPointSpec` records: first time zero, strictly increasing times, values 0..1, tension -1..1, and linear curve 0. Point times are beats. Clip placement uses ticks, and clip length does not automatically set the envelope's last point. [The automation recipe](examples.md#create-linked-automation) shows both spans explicitly.

Creation can partially complete before an error. Inspect channels and playlist clips before retrying. `add_clip()` reuses an existing automation generator at another playlist position; each placement plays the same envelope from its own start (automation clips have no loop or offset), so a shape that differs per bar needs one envelope across the range (`duck`, `tile`).

`fl.automation.list()` is the inventory: one `AutomationChannelInfo` per linked automation clip channel with `name`, the host's `targets_text` (`"Insert 24 volume"` or `"event 0x71008030"`), `event_ids`, decoded `targets` (`target` is the first decoded one), `point_count` and the playlist `clips` that play it (`ClipInfo` records). `describe()` renders the same as one line per clip. Both read every channel's plugin description and, unless `with_points=False`, each envelope once.

## Composition helpers

These build on the queries above and compute locally; they issue no new native operations.

| Helper | Behaviour |
| --- | --- |
| `pattern.gaps(channel=None, min_ticks=None, min_beats=1.0, *, end_tick=None)` | Rest regions from the pattern's note snapshot as `Gap` records (`channel`, `start_tick`, `end_tick`, `length_tick`, `start_beat`, `length_beats`), sorted by time. A note occupies `[start, start + length)`; muted notes are silent. The span ends at the later of the host-reported pattern length and the last note end unless `end_tick` is given. `channel=None` covers every channel with notes; a named channel without notes yields one gap over the whole span. `min_ticks` overrides `min_beats` (converted at the project PPQ). |
| `fl.playlist.gaps(start_bar, end_bar, channel=None, min_beats=1.0, *, beats_per_bar=4)` | The same over an arrangement range, bars `start_bar..end_bar` inclusive (one-based), in absolute ticks. Reads unmuted pattern clips overlapping the range and each pattern's notes once. A note starting inside its clip sounds for its full length; notes starting after the clip end do not. Clips are treated as starting at their pattern's beginning (sliced clips with an offset cannot be distinguished). Automation and audio clips are ignored. |
| `fl.playlist.first_free_track(start_tick, end_tick, *, above=1)` | Lowest one-based track `>= above` with no clip of any kind (pattern, audio, automation; muted included) overlapping `[start_tick, end_tick)`. Raises `LookupError` when tracks up to 500 are all used. |
| `fl.automation.pump(target, start_tick, length_tick, *, track, depth=0.5, recovery_beats=0.75, beats_per_hit=1, floor=None, ceiling=1.0, tension=0.5, name=None)` | Sidechain-style curve: creates and places the clip with `create(...)`, then `set_points(...)`. Each hit drops to `floor` (default `ceiling - depth`), recovers to `ceiling` over `recovery_beats` (`tension` shapes that segment), holds, and drops one tick before the next hit; the last point sits at the clip end. Returns `PumpResult(clip, point_count)`. Over 4000 points is refused before any write; split long spans. |
| `fl.playlist.onsets(channel, start_tick, end_tick)` | Sorted absolute note-on ticks of one channel inside `[start_tick, end_tick)`, read through every unmuted pattern clip with the same rules as `gaps` (muted notes silent, notes past their clip end silent, clips assumed to start at the pattern's beginning). The input for `duck`. |
| `fl.automation.duck(target, hits_ticks, start_tick, length_tick, *, track, depth=0.5, recovery_beats=0.25, floor=None, ceiling=1.0, tension=0.5, name=None)` | `pump` for an irregular grid: ONE envelope that holds `ceiling` until one tick before each absolute tick in `hits_ticks` inside the clip, drops to the dip (`floor`, default `ceiling - depth`) at the hit and recovers over `recovery_beats` (cut short by a closer hit). Follows alternating kick bars, fills and pre-chorus variants that no repeated 1-bar clip can. Returns `PumpResult`; over 4000 points is refused before any write (split the span). |
| `fl.automation.tile(target, shape, start_tick, length_tick, *, track, period_beats, offsets_beats=(0,), name=None)` | ONE envelope that repeats a clip-relative `shape` (2+ points from beat 0, fitting one period) every `period_beats`; repeat `i` is shifted by `offsets_beats[i % n]` (`[0, 0.5]` alternates bars). The last value holds between repeats; a partial last repeat is cut at the clip end. |
| `fl.transport.set_song_end(bar=None, *, tick=None, name="End", beats_per_bar=4)` | Places a single end marker at a one-based bar start or an absolute tick, deleting any marker with the same name first, and returns the `Marker` as the host lists it afterwards. It relies on FL extending the song length, play range and render to the last time marker, so a marker past the final note adds silence or room for tails. |
| `fl.transport.seek_settled(tick, *, attempts=20, delay=0.05)` | Seeks, then polls `position_tick` until two consecutive reads agree; returns `SeekResult(requested_tick, position_tick, settled, reads)`. `seek_ticks(tick, settle=True)` is the same call. |
| `fl.transport.read_at(tick, reader, *, settle=0.3, attempts=6, delay=0.1)` | Settled seek, `settle` seconds for the host's automation pass, then calls `reader()` until two consecutive values are equal. `parameters.read_at(index, tick)` / `display_at(index, tick)` wrap it for a plugin parameter. |

`pump` on `AutomationTarget.channel_volume` recovers to `ceiling`, and 1.0 there is the 12800 channel maximum, above FL's default level. Pass `ceiling` (0.78 is roughly the default) or automate `mixer_volume` instead. The pure builders `fruitylink.automation.pump_points(length_beats, ppq=...)`, `duck_points(hits_beats, length_beats, ppq=...)` and `tile_points(shape, period_beats, length_beats, ppq=...)` return envelopes without a connection.

## Batches and bulk edits

Use `NoteSpec`, `NoteEdit`, `NoteRef`, `ClipMove`, `ClipResize` and `PatternClipSpec` for native bulk operations. A single bulk operation refreshes FL once. `fl.clips.resize` and `fl.clips.move` also take plain tuples or a single clip (`fl.clips.resize(1, 3072)`, `fl.clips.move(4, 768, 3)`), and `delete` / `set_muted` take one index or a sequence. `PatternClipSpec(pattern, track, start_tick, length_tick=0)`: a positive length is pinned by the host, so an 8-bar clip stays 8 bars even when the pattern's notes overhang; 0 follows the pattern length. `fl.playlist.add_patterns(specs, enforce_lengths=True)` re-lists the clips afterwards and resizes any that still differ (returns how many). `NoteRef` and `NoteEdit` address a note by `(channel, key, start_tick)`; FL allows stacked duplicates sharing that triple, so both carry an optional `length_tick` (the note's current length) to pick one, and `notes.edit(...)` / `notes.delete(...)` refuse a target that still matches several notes unless `allow_multiple=True`, which addresses all of them (nothing is written on a refusal). A general batch sequences multiple independent operations, up to 256:

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
