# SDK backlog

## Mixer insert creation through Python and MCP

- [x] Add a supported operation to create ordinary mixer inserts, exposed through
  `INativeFlControl`, the shared scripting contract, and the Python mixer API. MCP
  should call that shared Python API rather than implement another native path.

Requested while setting up a parallel compression send in Glass Satellites. The SDK
could load effects and create sends between existing tracks, but could not add
an insert. `fl.mixer.add(name, after=...)` now uses FL's native insertion operation.
`fl.mixer.list()` returns typed Master/ordinary inserts and excludes Current.

### Acceptance criteria

- Verify the native creation function and argument contract independently for
  supported FL Studio 2025 and 2026 builds. Use the version scanner/layout abstraction;
  refuse unsupported builds before mutation. Do not write the mixer count directly.
- Support appending an insert and inserting after an explicitly selected ordinary
  track. Return the actual created track identity/index and refreshed track count.
- Identify Master, ordinary inserts, and Current using verified native metadata.
  Validate capacity and placement; do not treat Current as a spare effect return.
- Preserve existing channel assignments, mixer sends, names, effects, and levels.
  Document index changes and require callers to refresh references after insertion.
- Cover the operation in generated API discovery, Python documentation, regression
  tests, and installation/package checks.
- Live-test creation, naming, effect loading, routing, save/reopen persistence, and
  end-to-end rendering on each supported version available for testing.

### First end-to-end scenario

Create a **parallel compression send/return** with the installed FabFilter Pro-C.
Send selected drums (initially kick, snare, and clap) to it, preserve their existing
dry routes to Master, and blend the fully wet compressed return underneath them.
Do not turn this into a serial bus for all drums. Verify both routing and audio
after saving/reopening and rendering.

Status: **implemented; live verified on FL 26.1.3.5570 with installer 0.1.20**.
Append, insertion after an ordinary track, and insertion after Master preserved
channel assignments, sends, effect parameters and names. The disposable fixture
saved, reopened, and rendered an eight-second stereo 48 kHz WAV successfully.
The FL 2025 native profile has exact-binary analysis and automated coverage;
FL 2025 live insertion/rendering remains to be verified.

The musical compression return remains deferred at the user's request. No such
channel has been created in Glass Satellites.

## Automation and measurement work

- [x] Create/link Automation Clips and replace validated curves through the shared
  native API and Python, with independently verified 2025/2026 layouts.
- [x] Analyze supplied WAV/PCM over explicit ranges and bounded windows: RMS,
  crest factor, normalized energy, occupancy and standards-defined loudness/PSR.
- [x] Return compact spectral summaries for samples: frequency-band power,
  spectral centroid/rolloff, dominant bins and time-window pages.
- [ ] Add a verified per-mixer audio capture/stem workflow. Existing untimed peak
  meters are insufficient for RMS, PSR or spectral analysis; file measurements
  must identify their actual source. Progress 2026-09-14: FL disk recording chosen
  (see [live audio capture](live-audio-capture.md)); `SetMixerTrackArmedAsync` /
  `GetMixerTrackArmedAsync` over the harvested `FLmx_SetTrackArmed`, and
  `fl.audio.capture` / `decide` / `measure_section`; live verification pending.
- [x] Describe audio for an agent: `fruitylink.analysis.describe_audio`,
  `compare_audio`, `describe_samples` (also `fl.analysis.describe` and
  `fl.samples.describe`); numpy ships as the optional `analysis-support` extension.

## Known gaps carried from Parking Lot Moon (2026-09-14)

Each is documented with its workaround in [API gaps](api-gaps.md) and the Python API.

- [ ] Channel delete: UI-only in FL. Workaround `fl.channels.retire(index)`.
- [ ] Sidechain send flag: not in the verified mixer layout (+0x12A4 table unprofiled).
  Workaround `fl.automation.duck` / `pump` on `mixer_volume(insert)`, or the GUI.
- [ ] Sampler reverse / fades / trim / stretch mode: not REC events. Workaround an
  offline WAV edit plus `Channel.replace_sample`; `stretch_time` / `sample_offset`
  ids 13/14 need live confirmation.
- [ ] Mixer send-level automation target: no event id known. Workaround the return
  insert's `mixer_volume` or the send effect's wet parameter
  (`AutomationTarget.effect_parameter`).
- [ ] Serum 2 FX proxy slot names: `proxyParams = null` in every preset. Workaround
  state-based `SerumPatch.fx` plus `describe.fx_slot_names`.
- [ ] Seek readback / automation-pass signal: none in the bridge; `seek_settled` and
  `read_at` wait client-side.
- [ ] `get_channel_sample_path`: FL exposes no Sampler-file query, so
  `fl.samples.describe(channel)` relies on session history or `path=`.

## Optional synth preset selection and auditioning

- [x] Add a separately installable `fruitylink-serum` Python wheel for read-only
  preset inventory, local Serum 2 metadata queries, and bounded descriptions of
  supplied audition WAV/PCM. Reuse the SDK's existing audio analysis.
- [x] Load a specific preset into a verified Serum instance through a supported
  host operation. `load_channel_plugin_state` / `load_mixer_effect_state` (wrapper
  state-file loader, same instance) plus `fruitylink_serum.load_preset` /
  `build_preset` / `to_vstpreset`. Live-verified 2026-09-13 on FL 26.1.3.5570: a
  `.vstpreset` with the GUID-string class id loaded a captured chord state (A Level
  75%, unison 7, Sub Saw) and a generated preset read back unison 3 / detune 0.35;
  raw `.SerumPreset` and synthesised `.fst` files are ignored by the wrapper. See
  `artifacts/serum-preset-loading/README.md`.
- [ ] Render comparable isolated auditions with explicit note/chord, velocity,
  gate length, tempo, tail length, and effects policy. Record those conditions
  with the preset identity and audio measurements.
- [ ] Detect embedded clip/arpeggiator playback and custom tuning before choosing
  a preset for a supplied melody. Check project save/reopen persistence after
  applying the preset, including its complete internal state.

This gap became concrete during Ember Tides v005: preset descriptions provided
useful future-bass candidates, but metadata did not establish the resulting sound.
The extension describes supplied audio; it does not yet load or audition presets.
Any Serum-specific native implementation and its dependencies should remain
optional, outside the default SDK/MCP payload. See
[optional Serum support](python/serum-support.md).
