# Build a Serum 2 patch from a musical description

`fruitylink_serum.SerumPatch` turns a description such as "square lead with some reverb"
into a loadable preset in a handful of calls. It writes the same processor state that
`to_vstpreset` wraps and FL's wrapper accepts (live-verified on FL 26.1.3), so nothing
touches FL until `.load(fl, channel)`. Names, units and effect layouts come from the
schema data in `fruitylink_serum/data` (see [state-schema.md](state-schema.md)); the
builder raises a `PatchError` listing the valid choices instead of guessing a key.

Status (2026-09-13): loading builder output into a running Serum 2 is live-verified for
oscillator, sub, filter, envelope and master keys read back from project states. Frame
labels, effect units, LFO/macro keys and the osc B/C names are schema-derived and still
need the live checklist at the end of this page. Added 2026-09-14 (not yet live-checked):
`bend_range()`, `velocity()` / `modulate()` (mod-matrix rows, see the confidence note
below), `describe_state()` / `describe_preset()` summaries, and the `warnings` that
`load_preset` / `patch.load` now return.

## Square lead with some reverb

Type this into `fl_execute_python` with a Serum 2 already on channel 4:

```python
from fruitylink_serum import SerumPatch

patch = (SerumPatch(name="Ember square lead")
         .osc_a("square", unison=3, detune=0.08, width=60, level=0.6)   # Default Shapes, square frame
         .sub("square", octave=-1, level=0.5)                          # weight an octave below
         .filter("lp24", cutoff_hz=2200, resonance=12, drive=10)
         .amp_env(attack_ms=5, decay_ms=400, sustain_db=-6, release_ms=250)
         .mono(True, porta_ms=40, legato=True)
         .fx.reverb(mix=22, type="hall", size=55, predelay_ms=15, width=100)
         .master_volume(db=-9))
result = {"load": patch.load(fl, 4), "changes": patch.changes()}
```

Read the displays back in the **next** request (displays lag inside the request that wrote):

```python
p = fl.channels[4].parameters
result = {name: p.read(i).display_value for name, i in
          {"A Level": 21, "A Unison": 44, "A Uni Detune": 46, "A WT Pos": 59,
           "Sub Shape": 199, "Sub Octave": 196, "Filter 1 Type": 204, "Filter 1 Freq": 205,
           "Env 1 Attack": 224, "Env 1 Release": 228, "Main Vol": 0}.items()}
```

## Wide supersaw chord stack with a saw sub

```python
chords = (SerumPatch(name="Ember chords")
          .osc_a("saw", unison=7, detune=0.12, blend=82, width=100, level=0.75)
          .osc_b("saw", unison=5, detune=0.08, octave=1, level=0.35)     # bright octave layer
          .sub("saw", octave=-1, level=0.45)
          .filter("lp12", cutoff_hz=1800, resonance=10)
          .amp_env(attack_ms=8, decay_ms=330, sustain_db=-5.7, release_ms=150)
          .fx.chorus(mix=30, rate=0.4, depth=8)
          .fx.reverb(mix=18, type="space", size=40)
          .master_volume(knob=0.55))
result = {"load": chords.load(fl, 1)}
```

Derive from something that exists instead of starting from init:

```python
SerumPatch(name="Glow, longer", base="LD - Analog Glow").amp_env(release_ms=800).fx.delay(mix=12, time_ms=250, feedback=30)
```

## API surface

| Method | Accepts | State keys |
| --- | --- | --- |
| `SerumPatch(name, base=None \| preset name \| path \| XferContainer, schema=None, root=None)` | `base=None` starts from the init state (identity plus top-level scalars, no sections; nothing embedded) | |
| `osc_a / osc_b / osc_c(shape, table, frame, level, level_db, unison, detune, blend, width, octave, semi, fine, pan, enable)` | `shape`: `sine`, `triangle`, `saw`, `square`, `pulse` (frame labels of `S2 Tables/Default Shapes.wav`, or of `table=`); `frame`: 1-based frame of a known table, else the raw 0..256 position | `OscillatorN/plainParams/kParamEnable, kParamVolume, kParamUnison, kParamDetune, kParamDetuneWid, kParamUnisonStereo, kParamOctave, kParamPitch, kParamFine, kParamPan`; `OscillatorN/WTOscN/relativePathToWT, plainParams/kParamTablePos` |
| `sub(shape, octave, level, level_db, enable)` | `sine` (default, key left unset), `roundrect`, `triangle`, `saw`, `square`, `pulse` | `Oscillator4/plainParams/...`, `Oscillator4/SubOsc4/plainParams/kParamShape` |
| `noise(level, level_db, enable)` | | `Oscillator3/plainParams/...` |
| `filter(type, cutoff_hz, resonance, drive, index, enable)` | `type`: short names `lp6/lp12/lp18/lp24` (Moog-style, `lp12` = default), `clean_lp12..24`, `hp6..24`, `bp12/bp24`, `notch12/notch24`, `ladder`, `acid`, `dirty`, `comb`, `formant`, `vowel`, `diffuser`; FL display names (`MG Low 24`); or Serum values (`MgL24`) | `VoiceFilterN/plainParams/kParamType, kParamFreq, kParamReso, kParamDrive, kParamEnable` |
| `amp_env(...)` / `env(index, attack_ms, hold_ms, decay_ms, sustain, sustain_db, release_ms)` | | `EnvN/plainParams/kParamAttack, kParamHold, kParamDecay, kParamSustain, kParamRelease` |
| `lfo(index, rate, beat_sync)` | numeric rate only (division encoding unresolved); drawn shapes are not built | `LFON/plainParams/kParamRate, kParamBeatSync` |
| `macro(index, value)` | percent | `MacroN/plainParams/kParamValue` |
| `mono(enabled, porta_ms, porta_always, legato)`, `transpose(semitones)` | | `Global0/plainParams/kParamMonoToggle, kParamPortamentoTime, kParamPortaAlways, kParamLegato, kParamTranspose` |
| `master_volume(knob=... \| db=...)` | | `Global0/plainParams/kParamMasterVolume` |
| `bend_range(up_semitones=None, down_semitones=None)` | semitones; down may be given positive (stored negative, as Serum writes `-12.0`) | `Global0/plainParams/kParamBendRangeUp, kParamBendRangeDn` |
| `velocity(amount=1.0, *, target="amp", bipolar=None, slot=None)` | `amount` 0..1 (100 % = full range); `target`: `amp` (`Global0 kParamVoiceAmp`, what factory presets modulate with velocity), `filter`/`filter2`, `osc_a`..`osc_c`, `sub`, `noise`, or any builder name | one `ModSlot{n}` row: `source [16, 0]`, `destModuleTypeString`/`destModuleID`/`destModuleParamName`/`destModuleParamID`, `plainParams/kParamAmount` |
| `modulate(source, target, amount, *, aux=None, bipolar=None, slot=None)` | `source`/`aux`: `velocity`, `note`, `mod wheel`, `env 1`, `lfo 1..10`, `macro 1..8` or a raw id; `target`: builder name or raw `Section{n}/plainParams/kParam` path; `amount` percent -100..100 | same row layout (`slot` defaults to the first empty `ModSlot`) |
| `fx.<effect>(rack=0, **params)` | effects: `reverb`, `delay`, `chorus`, `distortion`, `eq`, `compressor`, `filter`, `phaser`, `flanger`, `hyper`/`dimension`, `bode`, `convolve`, `utility`; params: `mix`, `level`, `type`, `mode`, `size`, `predelay_ms`, `width`, `feedback`, `rate`, `depth`, `drive`, `time_ms`, `time_l_ms`, `time_r_ms`, `cutoff_hz`, `freq_hz`, `resonance`, `attack_ms`, `release_ms`, `ratio`, `threshold`, `makeup`, `unison`, `detune`, `damping`, `tone`, `decay_s`, `beat_sync`, `stages`, `poles`, `shift`, `balance`, `hpf_hz`, `lpf_hz`, `delay_ms`, or any raw `kParam*` key the effect lists; enum values by name (`type="hall"` → `kHall`) | appends `{"type": n, "FX<Name>": {"plainParams": {...}}, "kUIParamMixOrGain": 0.0}` to `FXRack<rack>/FX` |
| `set(key, value)`, `unset(key)`, `get(key)` | escape hatches for any state path | |
| `changes()`, `to_state()`, `container()`, `save(path)`, `to_vstpreset(path)`, `load(fl, channel, slot=None)` | outputs | |

`describe_parameters(container)` groups any state by section with the musical value of
every key the schema knows; `flatten_state(state)` lists every scalar as a path.
`describe_state(fl, channel)` / `describe_preset(path)` / `describe_container(container)`
return a structured summary (oscillators with wavetable path, display name, frame and
frame label, level; sub shape; filters; envelopes; LFOs; FX rack order; mod rows; macros;
globals; `signal_path` notes) and `format_description(d)` renders it as text. The wavetable
identity comes from `Oscillator{n}/WTOsc{n}/relativePathToWT` (+ `tableDisplayName`,
`embeddedWTData` for custom tables) in the state, which FL's parameter list never shows.

## Defaults implied by absence

Serum omits every parameter that sits at its default, so an absent key means the default
below. `describe_*` prints `default` for values it does not know.

| Absent key | Meaning | Evidence |
| --- | --- | --- |
| `Oscillator0 kParamEnable` | oscillator A **on** | factory presets leave it absent on a sounding osc A (inferred) |
| `Oscillator1..4 kParamEnable` | B, C, noise, sub **off** | `kParamEnable` is written as 1.0 in 2074 of 2083 occurrences (inferred) |
| `VoiceFilter{n} kParamEnable` | filter **off** | only 1.0 ever written (inferred) |
| `VoiceFilter{n} kParamWet` | filter wet **100 %** | Ember Tides v007: a preset with explicit 0.0 was bypassed; after an override to 100 the reopened state omits the key |
| `VoiceFilter{n} kParamType` | `MgL12` ("MG Low 12") | verified live |
| `SubOsc4 kParamShape` | `kSine` | verified live |
| `FX unit kParamEnable` | unit **enabled** (0.0 = bypassed) | library statistics |
| `FX unit kParamWet` | mix **100 %** | library statistics (inferred) |
| `RoutingSlot{n} kParamRoutingDest` | routed to the voice filter (inferred; `kRoutingDestFilter` is also written explicitly by converted presets) | enum in `Serum2.vst3`: `kRoutingDestFilter = 0, kRoutingDestMaster, kRoutingDestDirect, kRoutingDestNone` |
| `Global0 kParamBendRangeUp/Dn` | Serum's own default (±2 semitones, unverified) | `bend_range()` always writes both |
| `ModSlot{n}` | empty row | 64 rows; empty ones are written as `{"plainParams": "default"}` or omitted |

A preset load replaces the whole plugin state, including these globals: a bend range set
through FL's parameter list before `load_preset` is lost (Ember Tides v011). Bake it in
with `bend_range()`.

## Mod matrix and velocity (confidence: inferred)

`velocity()` / `modulate()` write `ModSlot{n}` rows exactly as factory presets store them.
The destination ids (`destModuleParamID`) come from `data/mod-matrix.json`: the library
majority per `(type, kParam)` pair, cross-checked against the parameter enums found as
declaration strings in `Serum2.vst3` (`Global kParamVoiceAmp = 2`, `Oscillator kParamVolume = 1`,
`RoutingSlot`, `MultiSampleOsc`, `FXConv`: marked `binary-enum`; the rest `library-majority`,
no conflicts). The **source ids are numeric in files and their names are inferred**:
16 = Velocity (61 of the 92 factory rows that modulate `Global kParamVoiceAmp` use it, and it
is the usual "via" source for envelopes), 17 = Note, 18 = Mod Wheel, 6..15 = LFO 1..10
(monotone usage decay), 25..32 = Macro 1..8 (eight ids with ~1000+ uses each), 1 = Env 1,
3/4/5 = probably Env 2/3/4, 2 = unresolved. Until the live checklist below confirms it,
treat a `velocity()` row as "what a factory preset would contain", not as verified.

```python
patch = (SerumPatch(name="Soft sub")
         .sub("sine", level=0.8)
         .velocity(0.9)                      # Velocity -> Global kParamVoiceAmp 90 %
         .bend_range(2, 12)                  # kParamBendRangeUp 2, kParamBendRangeDn -12
         .modulate("lfo 1", "Oscillator0.fine", 15, aux="mod wheel"))
```

## Unit conversions (single reference)

| Musical argument | State value | Evidence |
| --- | --- | --- |
| `level` (knob 0..1) | `kParamVolume = knob²` (linear gain; FL shows `sqrt(state)` as the percent, dB = `20·log10(state)`) | 0.45 → "45% [-13.9 dB]", 0.7586 → "87% [-2.4 dB]" |
| `level_db` | `10^(dB/20)` | |
| `master_volume(db=)` | `10^((dB-3)/20)` (FL adds +3 dB to the master display) | 0.3025 → "55% [-7.4 dB]" |
| `cutoff_hz` (voice filter, FX filter) | `v = log(hz/8.1846) / log(2698.7)` | 0.42 → 226 Hz, 0.60 → 937 Hz |
| `attack_ms`, `decay_ms`, `release_ms`, `hold_ms`, `porta_ms` | seconds | 0.015 → "15 ms", 2.46 → "2.46 s" |
| `sustain` / `sustain_db` | knob 0..1; `knob = 10^(dB/40)` (FL dB = `40·log10(knob)`) | 0.72 → "-5.7 dB" |
| `unison` | voice count (FL normalised `(n-1)/15`) | 5 → "5" |
| `detune` | the 0..1 value FL displays (FL normalised value² is the display) | 0.316 (FL) → "0.10" |
| `blend`, `width`, `resonance`, `drive`, `macro` | percent | |
| `shape` | table path + `kParamTablePos = 256·frame/num_frames` | 88.29 on 166 frames → "57" |
| effect `predelay_ms`, `time_ms` | seconds for reverb pre-delay, delay times, convolve; milliseconds for compressor attack/release and reverb `delay_ms` | observed ranges in `fx-schema.json` |
| effect `cutoff_hz` | normalised when the key's observed range is 0..1 (FX filter), Hz otherwise (delay/phaser/EQ) | |

## Reading a channel's state back

```python
from fruitylink_serum import read_channel_state, diff_states

before = read_channel_state(fl, 4)            # saves a snapshot copy next to the project, parses it, deletes it
fl.channels[4].parameters.set_named("A Unison", 4 / 15)
after = read_channel_state(fl, 4)
diff_states(before, after)                    # {'Oscillator0/plainParams/kParamUnison': (5.0, 4.0)}
```

`read_state(fl, channel)` (host operation `get_channel_plugin_state`) and
`snapshot_preset(fl, channel, name)` do the same through the bridge; `snapshot_preset` writes
a `.SerumPreset` you can use as `base=` (pass a bare name plus `output_dir=`, or a full
`.SerumPreset` path as the name to write exactly there).

## Live checklist (run once per FL build, disposable project, Serum 2 on channel 4)

Request 1:

```python
from fruitylink_serum import SerumPatch, read_channel_state
before = read_channel_state(fl, 4)
patch = (SerumPatch(name="gate").osc_a("square", unison=4, detune=0.2, width=60).osc_b("saw", octave=1, level=0.3)
         .sub("triangle", octave=-1, level=0.5).filter("lp24", cutoff_hz=500, resonance=20)
         .amp_env(attack_ms=50, release_ms=400).macro(0, 40).fx.reverb(mix=25, type="hall", size=50)
         .fx.delay(mix=10, time_ms=250, feedback=30).master_volume(db=-9))
result = {"load": patch.load(fl, 4), "changes": list(patch.changes())}
```

Request 2 (expected displays): A WT Pos "4" of 9 and the table name in Serum's UI, A Unison
"4", A Uni Detune "0.20", A Uni Width "60", B Enable "On", B Octave "1 oct", Sub Shape
"Triangle", Sub Octave "-1 oct", Filter 1 Type "MG Low 24", Filter 1 Freq ≈ "500 Hz",
Env 1 Attack "50 ms", Env 1 Release "400 ms", Macro 1 "40", Main Vol "50% [-9.0 dB]".
Then confirm the FX rack in Serum's UI shows Reverb (Hall) and Delay in that order, and
`diff_states(before, read_channel_state(fl, 4))` contains every key in `changes()`.

Request 3 (mod matrix and globals, added 2026-09-14): load
`SerumPatch(name="vel").osc_a("saw", level=0.7).velocity(1.0).bend_range(2, 12)` and check
in Serum's matrix that row 1 reads **Velocity -> Voice Amp** (or "Amp"); if the source shows
another name, that name is what id 16 means and `mod-matrix.json` must be corrected. Read
`Bend Up` "2" and `Bend Down` "-12" (or "12") from FL's parameter list. Render two notes at
velocity 30 and 127 on that channel: the RMS must differ by well over 6 dB. Then
`describe_state(fl, 4)["mod_slots"]` must list the row and `["oscillators"][0]["wavetable"]`
must name `Default Shapes` frame 1 (saw). Finally run `tools/harvest_live.py`
(`write_phase`, then `read_phase` in a new request) and apply the result.
