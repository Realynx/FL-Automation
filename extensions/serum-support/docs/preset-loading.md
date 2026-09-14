# Serum preset loading design note

The `fruitylink-serum` extension currently discovers preset metadata and
describes caller-supplied audition audio. It does not load a preset into Serum.
Preset loading must remain unavailable until the host operation below passes the
live verification gate on every supported FL Studio build.

## What the host can and cannot select

Serum 2 exposes 4,240 automatable parameters through the hosted-plugin
interface on the currently tested installation. That interface supports the
existing query, set, and read-back sound-design loop, but it does not expose a
preset filename or Serum browser selection parameter.

FL Studio's generic program bank is also insufficient for Serum library
selection. A live probe returned 128 generic names (`Prog 1` through
`Prog 128`); it did not enumerate the names in Serum's browser. Program changes
therefore cannot implement selection of an arbitrary preset by catalog name.

The local Serum 2 library contains 912 native `.SerumPreset` files: 626 under
`Factory`, 284 under `Splice`, and 2 under `User`. The `S1 Presets` directory
under the Serum 2 root points at/imports the separate legacy Serum library and
contains 998 `.fxp` programs. Those 998 legacy files are not Serum 2 native
presets and must not be added to the 912 native count. A recursive total that
follows both roots can also count the same legacy content twice.

FL Studio does not treat `.SerumPreset` as a host preset format. The portable
candidate is an FL `.fst` saved after loading a sound in Serum. That file embeds
the hosted plugin state and can later be applied to another instance of the same
plugin. There are currently no Serum `.fst` files in this machine's user or
factory FL plugin-preset directories, so the convert-once workflow has no
artifact available to test yet.

## Candidate generic host operation

Reverse engineering of the tested FL engine found a same-plugin state-load
operation with these semantics:

1. Resolve the channel generator or mixer-slot host object.
2. Place an absolute `.fst` path in bridge-owned scratch memory as a
   NUL-terminated UTF-8 byte string.
3. On FL's main thread, dispatch the host's "load state from file" operation to
   the already loaded plugin instance.
4. Keep the plugin instance in place and apply the state stored in the `.fst`.

The observed operation number and engine addresses are reverse-engineering
evidence for the tested binary only. They are not a stable ABI and must not be
copied into the Serum extension or advertised as a supported address. A future
implementation belongs in the version-checked FruityLink host bridge as a
generic `LoadPluginPresetFile` capability shared by native, VST2, and VST3
plugins. The Serum extension may add catalog-to-captured-`.fst` mapping only
after that capability is supported.

Direct `.fxp` loading is not the first implementation target. The installed
Serum 2 instance is VST3, while the 998 `.fxp` files belong to the imported
legacy Serum library. FL's VST2 loader does not establish that those files can be
applied safely or faithfully to the active Serum 2 VST3 instance.

## Live verification gate

Use a disposable FL project and a deliberately recognizable Serum patch. Save
that patch through FL as an `.fst`, move Serum to a different patch, and then
exercise the candidate generic operation. Shipping support requires all of the
following evidence:

- The target is positively identified as Serum 2 before the write.
- The plugin instance pointer is unchanged by the load.
- Several independent parameters and the audible result return to the captured
  patch, including state that is not represented by the obvious macro controls.
- A missing file, a non-`.fst` file, and an `.fst` for a different plugin fail
  without changing the song.
- Unicode and long absolute paths work within an explicit scratch-buffer bound.
- Immediate parameter queries after the load are stable, with any required host
  settling interval measured rather than guessed.
- Saving, closing, and reopening the FL project preserves the complete loaded
  state.
- Undo behavior is understood and tested.
- The test passes independently on each FL Studio build declared compatible by
  FruityLink's symbol/version gate.

Until this gate passes, normal song work can add Serum 2 and use the verified
parameter workflow: query a narrowly filtered portion of the 4,240-parameter
list, set normalized values by the live indices, and read them back. Sound design
starts from the patch already loaded in Serum; catalog metadata must not be
reported as proof that a catalog preset was applied.

## Status 2026-09-13: implemented and live-verified

The generic host operations described above now exist as `LoadChannelPluginStateAsync`
and `LoadMixerEffectStateAsync` (`src/FruityLink.FlStudio/Inject/FlInjectBridge.PluginState.cs`),
exposed to Python as `fl.channels[i].load_state(path)` and
`fl.mixer[t].effects[s].load_state(path)`. The extension gained the `XferJson`/CBOR container
codec (`xfer.py`, `cbor.py`), a `.vstpreset` writer (`vstpreset.py`) and the navigation /
generation / loading API in `loading.py` (`list_folders`, `find_presets`, `resolve_preset`,
`read_preset`, `parameters`, `build_preset`, `to_vstpreset`, `load_preset`).

Format facts verified on local files: `.SerumPreset` and FL's saved Serum 2 VST3 state share
one container (JSON metadata + zstd-compressed CBOR map of named `plainParams`); the
metadata `hash` is the MD5 of the compressed payload; a decoded factory preset re-encodes
to a byte-identical size with an equal state map. Serum omits default-valued parameters.

Live gate (FL 26.1.3.5570, Serum 2 VST3, Ember Tides v006 channel 4, dispatcher route):

- `.vstpreset` with class id `56534558667350736572756D20320000` (Serum's plugin-database GUID
  string, braces/dashes removed) loaded the captured chord state: A Level 75%, unison 7, Sub
  Saw, Filter 1 226 Hz; same instance, channel name unchanged. The same file with the FUID
  memory-order id (`58455356...`) was silently ignored, so `SERUM2_CLASS_ID` and
  `class_id_from_guid` use the string order.
- A generated preset (`build_preset` overrides `Oscillator0/plainParams/kParamUnison=3`,
  `kParamDetune=0.35`, `to_vstpreset` without a controller chunk) read back unison "3" and
  detune "0.35". The controller chunk is not required.
- A factory preset (`LD - Analog Glow`, 146 named parameters) wrapped as-is did NOT apply even with
  the correct class id. After `normalize_processor_state` (drop `Osc`, `WTOsc`, `Filter`, `SerumGUI`,
  `SpectralOsc`, `GranularOsc`, `MultiSampleOsc`, `ClipPlayer`, `arpBankDisplayName`,
  `clipBankDisplayName`, `fileType`, `presetName`, `presetAuthor`, `presetDescription`; stamp
  `component=processor`, `product=Serum2`, `productVersion=2.1.4`, `version=10.0`) it loaded and
  read back A Level 87%, detune 0.01, blend 70, WT pos 57, sub off, Env 1 15 ms / 2.46 s. The
  minimal identity subset was not isolated; `to_vstpreset` applies the whole tested recipe.
- Raw `.SerumPreset` via the dispatcher: no change. The synthesised `serum-ch1-state.fst`
  (plugin-stub events plus the project's wrapper state event): no change, so `.fst`
  synthesis is unsupported; only FL-saved `.fst` files are candidates for that route.
- Channel-loader route (`use_channel_loader=True`): `.vstpreset` and `.SerumPreset` applied no
  state but renamed the channel to the file's base name, and FL's transport started playing
  during those loads. The host now refuses that route for anything but `.fst`; the dispatcher
  route is the default everywhere.
- The verification line no longer samples parameter values (live, neither indices 0..11 nor 48
  spread indices moved on loads that changed the sound); it compares the wrapper's plugin-data
  record from a project snapshot before and after the load and reports
  "state record changed/unchanged (N bytes)".

Live-verified later the same day (third deploy): save/close/reopen persistence after a
`.vstpreset` load (factory state survived reopen), `SerumPatch(...).load(fl, 4)` (25 changes, all
displays as intended, FXRack0 = [Reverb, Delay]), `snapshot_preset` (1,631-byte file that reloaded),
`load_preset(fl, 4, "LD - Analog Glow")` through the shipped normalisation (WT Pos 57, 87%,
detune 0.01), `Channel.get_state()` (13,663 bytes) and a 13.714 s isolated render of the built
patch (L/R RMS -28.66/-28.82 dBFS). Still open: mixer-slot loads. Test files and steps: `sdk/artifacts/serum-preset-loading/README.md`.

### Load safety (2026-09-14)

`load_preset` (and `SerumPatch.load`) now returns a `LoadResult`: still the host's
verification string (equality, `json.dumps` and every existing caller are unchanged) with
`warnings`, `automation`, `signal_path`, `path` and `to_dict()` added. The load is never
blocked. Before replacing the state it records:

1. always: the whole plugin state is replaced, so globals such as the bend range
   (`kParamBendRangeUp/Dn`), mono/legato and macros reset to the preset's values (Ember
   Tides v011: a `Bend Down` written just before the load was undone);
2. `automation_links(fl, channel)`: every FL automation clip (channels whose
   `get_channel_plugin` text reads `"<clip>: automation clip -> <targets>"`) whose target
   names this channel (`certain`) or matches the exact name of one of its plugin parameters
   (`possible`; another instance could own the link). Live (2026-09-14) FL names no
   plugin-parameter target at all and prints `event 0x...`; those ids are decoded with the
   SDK's `AutomationTarget.from_event_id` (`(channel << 16) | 0x8000 | index` for a generator
   parameter: `0x180cd` = channel 1, parameter 205 "Filter 1 Freq"; `channel << 16` = channel
   volume; `0x70401fc0` = mixer track 1 volume), so a decoded parameter of this channel is
   `certain`, its name is read from the live parameter list (`parameter_name`) and the
   warning reads "channel 1 automates plugin parameter 205 'Filter 1 Freq' (clip ... on
   channel 13); the preset replaces the state that link drives". Other channels, built-in
   controls and mixer targets are `other` (with `decoded`); ids outside the known forms stay
   `unknown`. Mixer-slot loads skip this scan; a failing scan becomes a warning;
3. the decoded preset's `signal_path_notes`: all enabled oscillator direct levels at 0,
   FX-bus sends or direct/master routing, no enabled voice filter, a filter with `kParamWet`
   0, and no mod row using the Velocity source (Ember Tides v007: "KY - Smart Future" has
   A/B/C/Noise at level 0 and only the sub audible, so `Filter 1 Freq` automation did
   nothing; v016: a sine-sub patch without velocity modulation ignored note velocity).
   Factory presets store untouched sub-sections as the string `"default"`
   (`SubOsc4: {"plainParams": "default"}` in "KY - Smart Future" and "PD - Airy Chant");
   the describer treats any non-mapping section as all defaults instead of raising.

### Read state (offline, 2026-09-13)

`read_state` / `snapshot_preset` (extension) and `get_state()` (SDK channel/effect-slot helpers)
read the wrapper's plugin-data record through a temporary project copy written by FL's own
serializer and parsed by `FlpPluginStateReader`; no new engine symbol. Live verification and the
probe script are listed in `artifacts/serum-preset-loading/README.md` under "Read state".
