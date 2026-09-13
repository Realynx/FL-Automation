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
  must identify their actual source.
