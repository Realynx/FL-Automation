# Serum 2 state schema (derived)

This page documents what the `fruitylink_serum/data/*.json` files contain, how they were
produced, and which facts are verified versus inferred. Load them with
`fruitylink_serum.schema` (`parameter()`, `parameters_in()`, `fl_anchor()`, `fx_effect()`,
`filter_types()`, `wavetable()`, `find_frames()`, `table_pos_for_frame()`).

The files hold **derived statistics only**: parameter names, observed ranges, enum values,
occurrence counts, unit rules and wavetable frame labels. No preset payload, curve data or
audio is stored, and none of Xfer's proprietary files are redistributed.

## How the data was derived

- 1,125 local files were decoded with `fruitylink_serum.xfer` (897 factory/user
  `.SerumPreset`, plus `.SerumFX` / `.SerumFXRack` effect chains and this project's
  FL-saved processor states). Nine factory `.SerumFX` files use a container variant whose
  metadata is not UTF-8 JSON and were skipped.
- Every `plainParams` leaf was aggregated by path with numeric suffixes normalised to `{n}`
  (`Oscillator0` → `Oscillator{n}`), recording type, min, max, occurrence and file counts and
  up to 64 distinct values for enums.
- Unit and scale rules were attached where the FL wrapper's parameter displays gave evidence
  during the Ember Tides sessions (2026-09-13); each row carries a `confidence`:
  `verified` (a live FL readback matched), `inferred` (name/range reasoning), or
  `observed-only` (range recorded, unit unknown).
- Wavetable frames were classified from the factory `.wav` tables (float32 mono, 2048-sample
  frames per the `clm ` chunk) using the first 32 harmonics of each frame.

## State layout

The CBOR state map is a flat dictionary of named sections. Numeric suffixes index instances.

| Section | Meaning | Notes |
| --- | --- | --- |
| `Oscillator0..2` | Oscillators A, B, C | `plainParams` holds level, unison, detune, octave/semi/fine, pan; `WTOsc{n}` holds the wavetable path (`relativePathToWT`), `numFrames` (total samples), `kParamTablePos`, phase; `kParamType` absent = wavetable, else `kOsc_Sample`, `kOsc_MultiSample`, `kOsc_Granular`, `kOsc_Spectral` with matching sub-sections |
| `Oscillator3` | Noise | `SampleOsc3` for the noise sample |
| `Oscillator4` | Sub oscillator | `SubOsc4/plainParams/kParamShape` selects the shape |
| `VoiceFilter0..1` | Filter 1 and 2 | `kParamType` enum (see `filters.json`), `kParamFreq` normalised cutoff, `kParamReso`, `kParamDrive` percent |
| `Env0..3` | Envelopes 1–4 (Env 1 = amplitude) | seconds for A/H/D/R, `kParamSustain` 0..1, `kParamCurve1..3` percent |
| `LFO0..9` | LFOs 1–10 | `curveData` = drawn shape, `plainParams` rate/mode/smooth/rise/delay |
| `ModSlot0..63` | Modulation matrix | `source: [main_id, aux_id]`, `destModuleTypeString`, `destModuleID`, `destModuleParamName`, `destModuleParamID`, `plainParams.kParamAmount` (-100..100), optional `kParamBipolar`/`kParamBypass`/curves; see `mod-matrix.json` below |
| `Macro0..7` | Macros | `name`, `plainParams.kParamValue` percent |
| `Global0` | Global | master volume, mono/legato/portamento, bend ranges, bus levels, oversampling |
| `FXRack0..2` | FX chains | see below |
| `RoutingSlot`, `VoicePanel`, `Arp*`, `*Clip*`, `PitchQuantizer`, `scalars` | routing, arp/clip player, note/velocity curves | recorded but not annotated |
| `component`, `product`, `productVersion`, `version` | processor identity | required by the VST3 state (`loading.normalize_processor_state`) |

Parameters at their default values are **omitted** from files. A builder must therefore start
from a base state and only write the keys it changes.

## Units and scales (anchors verified against FL displays)

| State key | Scale | Evidence |
| --- | --- | --- |
| `Oscillator{n}/plainParams/kParamVolume`, `Global0 kParamMasterVolume` | linear gain 0..1; FL % = √value·100; dB = 20·log10(value) (Main Vol shows +3 dB) | 0.7586 → "87% [-2.4 dB]", 0.3025 → "55% [-7.4 dB]" |
| `kParamUnison` | voice count 1..16; FL normalised = (n-1)/15 | 3 ↔ 0.1333, 7 ↔ 0.4 |
| `kParamDetune` | 0..1 = FL normalised; FL display = value² | 0.316 → "0.10" |
| `kParamDetuneWid` | percent = FL "Uni Blend" | 81.99 → "82" |
| `VoiceFilter kParamFreq` | 0..1; Hz = 8.1846·2698.7^value (two-point fit used by the builder; the rounder 8·2756^v is within ~1 %) | 0.42 → "226 Hz", 0.60 → "937 Hz" |
| `VoiceFilter kParamReso` | percent | 10 → "10 %" |
| `Env kParamAttack/Decay/Release` | seconds | 0.015 → "15 ms", 2.46 → "2.46 s" |
| `Env kParamSustain` | 0..1; FL dB = 20·log10(value²) | 0.72 → "-5.7 dB" |
| `Oscillator4 kParamOctave` | integer octaves | -1 → "-1 oct" |
| `SubOsc4 kParamShape` | enum `kSine` (default, absent), `kRoundRect`, `kTriangle`, `kSaw`, `kSquare`, `kPulse`; FL normalised 0.0/0.2/0.4/0.6/0.8/1.0 | live probe (kRoundRect position inferred) |
| `WTOsc kParamTablePos` | 0..256 spanning the table; frame = round(value/256·numFrames) | 88.29 on a 166-frame table displayed "57"; library max 256 |

Everything else in `parameter-map.json` is either `inferred` from names and observed ranges
(percent 0..100, semitones, degrees) or `observed-only`.

## Effects (`fx-schema.json`)

`FXRack0` is the main chain; `FXRack1`/`FXRack2` are the two buses fed by
`Global0 kParamFXBus1Vol/kParamFXBus2Vol`. Each rack is `{"FX": [unit, ...], "displayName": ""}`.
A unit is `{"type": <int>, "<FXName>": {"plainParams": {...}}, "kUIParamMixOrGain": 0.0}` and
the list order is the processing order.

| type | section | label |
| ---: | --- | --- |
| 0 | FXDistortion | Distortion (`kParamMode` enum: kOverdrive, kSoftClip, kTapeSat, kDiode1, kDownsample, …) |
| 1 | FXFlanger | Flanger |
| 2 | FXPhaser | Phaser |
| 3 | FXChorus | Chorus |
| 4 | FXDelay | Delay (`kParamTimeL/R` seconds when unsynced, `kParamMode` 1/2) |
| 5 | FXComp | Compressor (multiband fields) |
| 6 | FXReverb | Reverb (`kParamType` kHall/kVintage/kAbyss/kSpace; `kParamSize`, `kParamWet`, `kParamWidth` percent; `kParamPreDelay` seconds) |
| 7 | FXEQ | Equalizer (`kParamFreq1/2` Hz, `kParamGain1/2` dB, `kParamType1/2`) |
| 8 | FXFilter | Filter (same type enum as the voice filters) |
| 9 | FXHyperD | Hyper / Dimension |
| 10 | FXBode | Bode frequency shifter |
| 11 | FXConv | Convolution reverb (`relativePathToIR`) |
| 12 | FXUtils | Utility |
| 13–15 | FXSplit, FXSplit3, FXSplitMS | Splitters |

`kParamWet` is the unit mix in percent; `kParamEnable` is absent when enabled and `0.0`
when bypassed. Per-effect parameter keys with observed ranges are in the file.

## Filters (`filters.json`)

`VoiceFilter{n} kParamType` and `FXFilter kParamType` share one string enum (83 observed
values). Only the default is verified against FL: an absent type displays "MG Low 12"
(`MgL12`). Other display names are pattern guesses (`MgL24` → "MG Low 24", `H12` →
"High 12") marked `pattern`; exotic types (Diffuser, Reverb1, Combs, FormantONE…) carry
no guess.

## Wavetables (`wavetables.json`)

Factory tables live under `Documents/Xfer/Serum 2 Presets/Tables`; `relativePathToWT` is
relative to that folder (`S2 Tables/Default Shapes.wav`, `Analog/Basic Shapes.wav`; a leading
`/` also occurs in files). Frames are 2048 samples; `numFrames` in the state is the total
sample count (frames × 2048).

Derived frame labels for the default table `S2 Tables/Default Shapes.wav` (9 frames):
1 saw, 2 sine, 3 triangle, 4–5 square, 6–8 pulse, 9 other. `Analog/Basic Shapes.wav`:
1 sine, 2 saw, 3 triangle, 4 square, 5–6 pulse, 7 square. Select a frame with
`schema.table_pos_for_frame(frame, num_frames)`.

## Mod matrix (`mod-matrix.json`, added 2026-09-14)

Generated by `tools/schema/mod_matrix.py` from 912 library presets. A row is

```json
{"source": [16, 0], "destModuleTypeString": "Global", "destModuleID": 0,
 "destModuleParamName": "kParamVoiceAmp", "destModuleParamID": 2, "plainParams": {"kParamAmount": 80.0}}
```

- `destModuleTypeString` is the section family (`Oscillator`, `WTOsc`, `VoiceFilter`, `Env`,
  `LFO`, `Global`, `Macro`, `RoutingSlot`, `FX*`), `destModuleID` the instance index, and
  `destModuleParamID` the index of the parameter in that module's enum. The `destinations`
  table lists the library-majority id per `(type, param)`; rows marked `binary-enum` were
  confirmed against parameter enums found as declaration strings in `Serum2.vst3` 2.1.4
  (`kParamMasterVolume = 0, kParamMasterTuning, kParamVoiceAmp, kParamPortamentoTime, ...`;
  `kParamEnable = 0, kParamVolume, kParamPan, kParamOctave, kParamPitch, kParamFine,
  kParamCoarsePit, ...`; `kParamFilterBalance = 0, kParamFXBus1Level, kParamFXBus2Level`).
  No conflicts were found; the rare ids 10/11 for `kParamDetune`/`kParamDetuneWid` come from
  older presets (the 2.1.4 enum says 26/27).
- `source` ids are numeric in files and Serum does not store their names. The `sources`
  table names them by inference: 16 Velocity (61/92 modulations of `Global kParamVoiceAmp`,
  common aux source for envelopes), 17 Note (filter key tracking), 18 Mod Wheel (classic
  LFO "via"), 6..15 LFO 1..10 (monotone usage decay 1231 > 811 > 575 > ...), 25..32 Macro
  1..8 (eight ids with ~1000+ uses each), 1 Env 1, 3/4/5 probably Env 2/3/4, 2 unresolved,
  0 none. The binary's UI enum order (`Env1..4, LFO1..10, VeloCurve, NoteCurve, Macro1..8,
  Wheel`) is consistent with this but does not fix the numbers. Each row carries
  `confidence`; nothing here is live-verified yet (checklist in `patch-builder.md`).
- Also from the binary: `kRoutingDestFilter = 0, kRoutingDestMaster, kRoutingDestDirect,
  kRoutingDestNone` (RoutingSlot), `kSine = 0, kRoundRect, kTriangle, kSaw, kSquare, kPulse`
  (sub shape, matching the FL normalised order), `kPlate = 0, kHall, kVintage, kAbyss,
  kSpace` (reverb), `kOsc_WT = 0, kOsc_MultiSample, kOsc_Sample, kOsc_Granular, kOsc_Spectral,
  kOsc_Noise, kOsc_Sub`.

## Confidence fields and the live harvest

Every row in the five data files carries `confidence` (`verified`, `inferred`,
`observed-only`, `derived-labels`, `pattern`, `unknown`, `binary-enum`, `library-majority`;
each file lists its vocabulary under `confidence_vocabulary`). `tools/harvest_live.py`
upgrades rows to `verified`: `write_phase(fl, channel, out_dir=...)` snapshots the state and
sets every unverified FL anchor to a probe value; `read_phase(fl, channel, manifest=...)`,
run in a **separate** request because displays lag, re-reads the state, attributes each
write to the state key that changed and records the display; `harvest_live.py apply
<result.json>` stamps the data file. `probe_mod_matrix=True` additionally loads a
`velocity()` + `bend_range()` patch so the mod row and bend keys can be checked.

## Uncertain / not yet verified

- Mod-source ids (Velocity = 16 etc.) and the oscillator/filter enable defaults are inferred.
- The `kParamTablePos` rule rests on one live data point plus the library maximum.
- Filter-type display names other than the default; `kParamOctave` mapping for A/B/C in FL
  normalised terms; LFO rate units when synced; envelope FL-normalised-to-seconds curves.
- `kRoundRect` position in the FL Sub Shape enum (0.2 read back "Sine" once; readbacks lag).
- Effect parameter units beyond wet/enable/level are observed ranges only.
- The 9 skipped `.SerumFX` files (non-UTF-8 metadata) may hide additional effect fields.
