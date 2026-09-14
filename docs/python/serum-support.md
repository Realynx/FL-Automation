# Optional Serum sound discovery

## Quick start: build a patch

Live-verified 2026-09-13 (FL 26.1.3, Serum 2 on channel 4, disposable copy of Ember Tides
v006): this exact call produced 25 state changes and every display read back as intended
in the following request (A WT Pos 4 = square frame, unison 4, detune 0.20, width 60,
B On +1 oct 30%, Sub Triangle -1 oct 50%, MG Low 24 at 500 Hz / 20 %, Env 50 ms / 400 ms,
Macro 1 40, Main 50% [-9.0 dB]); the plugin state showed FXRack0 = [Reverb, Delay], and an
8-bar isolated render (13.714 s) measured L/R RMS -28.66/-28.82 dBFS, peak -15.6 dBFS.

```python
from fruitylink_serum import SerumPatch

patch = (SerumPatch(name="square-lead")
         .osc_a("square", unison=4, detune=0.2, width=60)
         .osc_b("saw", octave=1, level=0.3)
         .sub("triangle", octave=-1, level=0.5)
         .filter("lp24", cutoff_hz=500, resonance=20)
         .amp_env(attack_ms=50, release_ms=400)
         .macro(0, 40)
         .fx.reverb(mix=25, type="hall", size=50)
         .fx.delay(mix=10, time_ms=250, feedback=30)
         .master_volume(db=-9))
result = patch.load(fl, 4)          # Serum 2 must already be on channel 4
# next request: fl.channels[4].parameters.read(59).display_value == "4"
```

`patch.save(path)` writes a `.SerumPreset`, `snapshot_preset(fl, 4, "name")` captures the
current sound as one, and `load_preset(fl, 4, "LD - Analog Glow")` loads a library preset
(live: WT Pos 57, A Level 87%, detune 0.01). See [patch-builder.md](../../extensions/serum-support/docs/patch-builder.md).

### Added 2026-09-14 (unit-tested, not yet live-checked)

```python
from fruitylink_serum import SerumPatch, load_preset, describe_state, format_description, audition_candidates

patch = (SerumPatch(name="soft sub").sub("sine", level=0.8)
         .velocity(0.9)                 # Velocity -> Global kParamVoiceAmp; without it velocity is ignored
         .bend_range(2, 12))            # baked in: a preset load resets globals set through FL
result = load_preset(fl, 1, "KY - Smart Future")
result.warnings                         # state replaced; FL automation clips driving this channel;
                                        # "preset: all enabled oscillator direct levels ... are 0 ..." etc.
print(format_description(describe_state(fl, 1)))   # wavetable path/name + frame label, levels, filters, FX order, mod rows
layout = audition_candidates(fl, source_pattern=3, mixer_track=5,
                             candidates=["LD - Analog Glow", patch, 4], accompaniment_patterns=[1], gap_bars=1)
# then render once and measure each layout["candidates"][i]["start_seconds"..."end_seconds"]
```

`load_preset` still returns the host's verification string (a `LoadResult` subclass with
`warnings`, `automation`, `signal_path` and `to_dict()`); it never blocks the load.
`describe_preset(path)` summarises a file the same way. The Velocity source id and the
oscillator/filter enable defaults are inferred from library statistics (see
[state-schema.md](../../extensions/serum-support/docs/state-schema.md)); the live checklist
in `patch-builder.md` and `tools/harvest_live.py` (two-request set-then-read harvest) turn
them into verified rows. Records (`PresetFile`, `IndexedPreset`, `LoadResult`) expose
`.name` and `.to_dict()`; `vars()` does not work on them.

`fruitylink-serum` is a separately built Python extension for inspecting a local
Serum preset library and describing explicitly supplied audition audio. Its code
lives in `extensions/serum-support`; it is not included in the core Python wheel.
The framework installer selects this separate extension by default.

The extension reuses the SDK's [audio analysis](../python-audio-analysis.md) and
[spectral analysis](../python-spectral-analysis.md). No Serum binary, preset bank,
sample pack, or additional Python interpreter is distributed with it.

## Search your local preset library

```python
from dataclasses import asdict
from fruitylink_serum import discover_serum_roots, query_index

roots = discover_serum_roots()
result = [asdict(p) for p in query_index(roots[0], text="future bass", limit=12)]
```

Choose an existing Serum 2 root with a `System/presets.db` index. Discovery checks
explicit `extra_roots`, `SERUM_PRESETS_PATH`, and standard Documents/Xfer locations;
pass your actual root when Documents is redirected or your library is elsewhere.
`iter_presets(root)` inventories preset file paths without opening their payloads.
Legacy Serum libraries may have preset files without a Serum 2 index.

Index queries expose names, categories, descriptions, authors, tags, and ratings.
The index is undocumented local metadata and can be absent, stale, or incompatible
with a future Serum version. A catalog description such as “future bass chords”
is a search hint, not an acoustic measurement or confirmation that a preset has
been auditioned. The extension does not modify the index or proprietary presets.

## Measure a sound

Render a short, isolated audition with a known note or chord, velocity, gate
length, tempo, and effects policy. Compare presets using the same audition
conditions. A whole-song render cannot identify which instrument contributed its
frequency content.

```python
from fruitylink_serum import describe_wav

result = describe_wav(r"C:\auditions\lead-F4.wav", end_seconds=4)
```

The result contains provenance, amplitude and frequency-band measurements,
envelope and stereo evidence, and explicitly defined descriptor rules. These
measurements help compare brightness, energy distribution, and temporal behavior.
They do not establish an oscillator's waveform or a sound's suitability for a
genre. A filtered square, a processed saw, and a layered patch can produce similar
spectra.

`describe_audition(audio)` accepts an existing `AudioAnalysis` object. Auditions
are limited to 15 seconds and two million scalar samples (frames times channels).
Select a shorter range if either limit is exceeded.

## Import the installed extension in embedded Python

Build the extension independently:

```powershell
uv run --project python python -m build extensions/serum-support
```

Run that command from the SDK repository root. For development installs, place
the resulting wheel under
`FruityLink/python/extensions/serum-support/`. The normal installer does this by
default. FLMCP and the Python IDE share an embedded runtime that discovers one
wheel from each installed extension directory, so scripts use a normal import:

```python
from fruitylink_serum import describe_wav

result = describe_wav(r"C:\auditions\lead-F4.wav", end_seconds=4)
```

This uses FL Studio's already bundled interpreter and SDK without reading global
`site-packages` or `PYTHONPATH`. External Python programs can install the same
wheel with pip alongside `fruitylink-python`. Remove the installed wheel to
uninstall the extension; restart FL to clear already imported Python modules.

## Load a preset file into a Serum 2 channel (live-verified on FL 26.1.3)

The host now exposes two generic state-file operations, usable from embedded
Python without any MCP tool:

```python
fl.channels[4].load_state(r"C:\presets\Captured.fst")          # generator already on channel 4
fl.mixer[3].effects[0].load_state(r"C:\presets\Pro-R 2 hall.fst")  # effect already in the slot
```

They route the file to the wrapper's own "load state from file" entry for the plugin
that is already loaded (no re-instantiation), refuse empty slots, and refuse `.fst`
files that do not name the hosted plugin. The return value is a verification line
(plugin name, same-instance check, and whether the wrapper's plugin-state record changed,
compared through a project snapshot before and after the load); it is not proof of the
sound, so read parameter displays in the following request or render an isolated audition.
`load_state(path, use_channel_loader=True)` instead routes an FL `.fst` through FL's
channel file loader (the drag-and-drop path, which may replace the generator); it is
refused for other formats because live that route applied no state and renamed the channel.

The optional extension adds library navigation and file generation on top:

```python
from fruitylink_serum import find_presets, list_folders, load_preset, build_preset, read_preset, parameters

list_folders()                                   # "Factory/Lead", "Splice/Bass", ...
find_presets(folder="Factory/Lead", text="analog", limit=10)
load_preset(fl, 4, "LD - Analog Glow")           # resolve by unique name or relative path, then load

base = read_preset(find_presets(text="Analog Glow")[0].path)
parameters(base)                                 # {"Oscillator0/plainParams/kParamUnison": 7.0, ...}
generated = build_preset(base, {"Oscillator0/plainParams/kParamUnison": 4,
                                "VoiceFilter0/plainParams/kParamCutoff": 0.8}, name="Glow narrow")
load_preset(fl, 4, generated)
```

`.SerumPreset` files are an `XferJson` container: a JSON metadata header and a
zstd-compressed CBOR map of named sections whose `plainParams` hold named floats.
`build_preset` decodes that map, applies overrides (a section must already exist; a
`kParam*` name may be introduced inside an existing `plainParams` map because Serum
omits parameters at their defaults), recomputes the payload hash and writes a new
`.SerumPreset`. `to_vstpreset` normalises the state to a processor component and wraps it as a VST3 `.vstpreset`; `load_preset`
converts `.SerumPreset` files that way by default (`format="direct"` disables this).
zstd needs Python 3.14 (`compression.zstd`, the FL embedded runtime) or the
`zstandard` package.

**Verification status (FL 26.1.3.5570, Serum 2 VST3, 2026-09-13).** Live on channel 4 of
Ember Tides v006 through `load_state` (dispatcher route): a `.vstpreset` whose class id is
Serum's GUID string with braces and dashes removed (`56534558667350736572756D20320000`)
loaded the captured chord state (A Level 75%, unison 7, Sub Saw, Filter 1 at 226 Hz) into
the same instance with the channel name unchanged; the byte-swapped FUID order was silently
ignored. A generated preset (`build_preset` overrides unison 3, detune 0.35, wrapped by
`to_vstpreset` without a controller chunk) read back unison "3" and detune "0.35", so the
controller chunk is optional. A factory preset wrapped as-is was ignored; after `normalize_processor_state` (drop the
UI/file-only sections `Osc`, `WTOsc`, `Filter`, `SerumGUI`, `SpectralOsc`, `GranularOsc`,
`MultiSampleOsc`, `ClipPlayer`, `arpBankDisplayName`, `clipBankDisplayName`, `fileType`,
`presetName`, `presetAuthor`, `presetDescription`, and stamp `component=processor`,
`product=Serum2`, `productVersion=2.1.4`, `version=10.0`) the same factory preset loaded and
read back A Level 87%, detune 0.01, blend 70, wavetable position 57, sub off, Env 1 attack
15 ms / decay 2.46 s. `to_vstpreset` applies that normalisation by default; which of the
identity fields Serum strictly requires was not isolated. Raw `.SerumPreset` files and an `.fst` synthesised from the plugin stub
were ignored by the dispatcher route; through the channel-loader route they applied no state
and renamed the channel, and FL's transport started playing during those loads, so `use_channel_loader=True` is
refused for anything but FL `.fst` and the dispatcher route is the default everywhere. Persistence across save/close/reopen has not been re-measured after this change;
confirm every load by reading parameter displays in a separate request. Serum's browser
preview sequences are unrelated to this path; see
[Xfer's preview documentation](https://support.xferrecords.com/article/52-serum-2-preset-previews).

## Read the live state and learn parameter mappings

`read_state(fl, channel, slot=None, component="processor")` decodes the Serum 2 state
currently in a channel or FX slot (through the host's `get_channel_plugin_state` /
`get_mixer_effect_state` operations, which write a temporary project copy and extract the
wrapper record; the live project is unchanged). `parameters(read_state(...))` gives the
flattened named values, so one FL parameter change followed by a read shows which internal
name and unit it maps to. `snapshot_preset(fl, channel, name, output_dir=...)` writes the
live state as a `.SerumPreset` that `build_preset` can edit and `load_preset` can reload.
These two functions are unit-tested on synthetic containers and not yet live-verified.

## Build a patch from a musical description

`SerumPatch` describes a Serum 2 patch in musical units and produces the loadable file:

```python
from fruitylink_serum import SerumPatch

patch = (SerumPatch(name="Square lead")
         .osc_a("square", unison=3, detune=0.08, width=60, level=0.6)
         .sub("square", octave=-1, level=0.5)
         .filter("lp24", cutoff_hz=2200, resonance=12)
         .amp_env(attack_ms=5, decay_ms=400, sustain_db=-6, release_ms=250)
         .fx.reverb(mix=22, type="hall", size=55)
         .master_volume(db=-9))
patch.load(fl, channel=4)      # into the Serum 2 already on channel 4; read displays back next request
```

Shape names, filter types, effect units, LFO and macro keys come from the schema data in
`fruitylink_serum/data` (`parameter-map.json`, `wavetables.json`, `fx-schema.json`,
`filters.json`; see `extensions/serum-support/docs/state-schema.md`). Conversions (gain =
knob², envelope seconds, sustain dB = 40·log10(knob), cutoff Hz from Serum's normalised
value) come from readbacks of real project states. Unknown names raise a `PatchError` that
lists the valid choices; `set(key, value)` writes any state path directly.
`read_channel_state(fl, channel)` reads a channel's state back through a snapshot copy, and
`diff_states` lists what changed. Method table, unit reference and the live checklist:
`extensions/serum-support/docs/patch-builder.md`.
