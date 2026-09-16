# Live audio capture

Measuring audio today means `fl_project_render`: FL's command-line renderer renders the *saved*
project offline and the managed session is closed and reopened around it. For a whole song that is
the right tool. For an 8-bar section it costs a snapshot, a renderer launch (every plugin loads
again), the renderer's exit and a session reopen; and no render can show what one mixer insert (say
Serum after its FX chain) is producing. This page evaluates the routes to per-insert (and master)
audio while FL plays a range, picks one, and documents what is implemented, what is stubbed and
what must be checked live.

Status (2026-09-14): design complete; the Python layer (`fl.audio`), the C# contract, the bridge
operations and the native catalogue entries are implemented and unit-tested offline. The two native
arm symbols (`FLmx_SetTrackArmed`, `MixerTrackArmedOffset`) were harvested read-only from FL's own
scripting API on both installed engines (see [Harvest evidence](#harvest-evidence)) and decode on
25.2.5.5319 and 26.1.3.5570. Nothing on this page has run against a live FL yet; every step marked
**needs live check** has a snippet in the [live-check plan](#live-check-plan).

## Decision table

| Route | What the bridge/FL expose today | What must be added | Alignment / accuracy | FL stability risk | Effort |
| --- | --- | --- | --- | --- | --- |
| **a. FL disk recording** (arm inserts, record + play a range, read the WAVs) | Transport play/stop/record toggle (`FLgl_GlobalCommandDispatch` ops 10/11/12, live-verified), `Seek` (`FLtr_SeekToSongTick`), loop set/clear, song-state readback (play flag, playhead tick, play range), mixer track query/names, project path. FL's own scripting API registers `mixer.isTrackArmed` / `mixer.armTrack` (engine strings at `0x965612`), FL auto-names recordings with a `%s_%d_%s` template into `…\Image-Line\FL Studio\Audio\Recorded` (folder present on this machine) and offers 16-bit int / 32-bit float recording. | Two native symbols harvested (`FLmx_SetTrackArmed` setter thunk and the `MixerTrackArmedOffset` byte, both decoded from FL's own armTrack callback), two typed operations (done), the Python driver (done). Optional: the `AutoCreateClip` / `AutoUnarm` global settings. | Real time: the section plays once. Sample 0 of each WAV is the seek position (FL applies its own latency compensation). Playhead readback drifts 14-20 ticks (friction log), so the *stop* is late by that much and a tail; the file is trimmed to the bar grid from its known start, not from the readback. All inserts are recorded by the same engine pass, so they are mutually sample-aligned. | Low: only FL's own recording path runs. Risks are UI-level: the recording filter dialog, `AutoCreateClip` placing audio clips (and sample channels) in the project, a record LED left on. | Small (native harvest is the gating step) |
| b. FruityLink tap effect (VST3 in an FX slot streaming blocks to shared memory) | `AddMixerEffectAsync` loads any installed VST3 by name; VST3 SDK at `C:\Users\poofi\source\repos\VST_SDK\vst3sdk`; the user already ships VST3s (`PoofyPlugins`); embedded Python runs in FL's process, so the ring buffer could be read with `mmap` without a pipe. | A new native plugin project (processor writes float blocks + host sample position to a named shared-memory ring; a lock-free header), a reader (Python `mmap` or the host), and RMS/LUFS/spectrum over the stream. Slot management (insert into slot 9 or after the last used slot, remove afterwards; the project is dirtied). | Block-accurate and continuous; the plugin sees the host's `ProcessContext.projectTimeSamples`, so bars align exactly and a live rolling waveform is possible. | Medium: a plugin that crashes takes FL down; FL's plugin scanner must see it; the project is modified while measuring (an extra FX slot). | Large (new native project, packaging, scanner registration, live testing) |
| c. Hook the mixer's per-insert buffers in-process | Nothing: no mixer audio buffer symbol is catalogued; the mixer layout covers names/state/sends/slots only. | Reverse-engineer the audio thread's per-track buffer and its callback, install an audio-thread hook (detour) from the injected host. | Sample-accurate but on the audio thread; any stall drops audio. | High: an audio-thread detour in a Delphi engine with no symbols; wrong offsets are uncatchable faults. Explicitly outside the bridge's fail-safe philosophy. | Large, and unverifiable without live crashes |
| d. WASAPI loopback of the default output | Nothing needed from FL. The host or Python (`ctypes`/`pyaudiowpatch`) can open the render endpoint in loopback mode. | A capture helper and alignment to the transport (start-of-play detection). | Master only, mixed with every other application, after the OS mixer (Windows volume), device sample rate, unknown latency; alignment by transient detection or a leading silence gap. | None to FL. | Small |
| e. Edison capture | `AddMixerEffectAsync("Edison")` loads Edison into a slot; its parameters are reachable through the plugin-parameter interface. Recording is armed via its own UI buttons; the buffer lives inside the plugin. | Drive Edison's record mode by parameter/host messages, then export its buffer (menu-driven, "Save as"). No typed operation exists for either. | Sample-aligned ("on play" record mode) but the export requires Edison's UI. | Medium: UI automation in a plugin window on FL's main thread. | Medium, mostly UI automation |

### Recommendation

**Primary: route a, FL disk recording.** It needs no audio code, records every armed insert
post-FX in one pass, produces plain WAV files that every existing helper (and the upcoming
`analysis.describe_audio(path)`) takes directly, and the only native work is resolving two engine
routines FL already exposes to its own scripting API. Its weaknesses (real-time cost, no continuous
stream) are exactly the cases the policy hands to an offline render.

**Fallback: route b, a FruityLink tap VST3.** When a live rolling waveform or a measurement without
a transport pass is needed, a tap plugin in an FX slot is the only route that is block-accurate and
independent of FL's recording options. The toolchain (VST3 SDK, MSVC, a plugin the user already
builds) is on this machine, so it is a planning question, not a research one. Route d (WASAPI
loopback) is worth a one-day helper as a master-only stopgap if the arm harvest slips; routes c
and e are documented as rejected.

## Chosen architecture

```
MCP tool (fl_audio_capture / fl_section_measure)
   │  fl_execute_python
   ▼
fruitylink.capture.Audio  (fl.audio)          ── measure_section: CapturePolicy → live | render
   │ typed ops over the scripting endpoint       (render is never done here; RenderRequired)
   ▼
FlInjectBridge  set/get_mixer_track_armed  ── peek trackStruct+MixerTrackArmedOffset / call sym:FLmx_SetTrackArmed(trackStruct, armed, 0)
                transport_toggle_record / transport_play / transport_stop / seek / set_loop_region / get_song_state
   ▼
FL Studio records every armed insert to  <user data>\Audio\Recorded\<project>_<n>_<track>.wav
   ▲
fruitylink.capture  snapshot folder → wait for stable new WAVs → match by track name → measure_wav / envelope
```

Everything the agent receives is a plain WAV path per insert plus JSON-safe dictionaries.

### Prerequisites in FL (live findings, FL 26.1.3)

- The **recording filter must include Audio**: right-click the record button > Recording filter >
  Audio. The setting lives in the registry at
  `HKCU\Software\Image-Line\FL Studio 26\General\FruityLoopsMainForm`, value `RecordingFilter2`
  (it read `3` = Audio off on the test machine). With Audio off, FL records nothing and the capture
  fails with an error naming this setting.
- Mixer menu > Disk recording > **Auto-create audio clip: off**. Even then FL adds one sample
  channel (named like `lc-001_2026-09-14 17-53-26_Lead`) and one playlist clip per recording; the
  capture deletes the clips and retires the channels (below).
- FL records **32-bit float at the project rate** (48 kHz on the test project); the file starts at
  the seek position (sample 0 = `start_tick`) and runs until the stop, so a 12 s + 1.2 s tail
  request gave a 13.44 s file.
- **Arm-refresh quirk**: a pass that arms only inserts other than the mixer's currently selected
  track writes no file (solo `[5]` and `[3]` failed three times; `[5, 1]`, `[5, 0]`, `[1]` (selected)
  and `[1, 0]` recorded; arming and disarming Master right before a solo `[3]` pass made it
  record). FL registers the recording set only after a *second* arm-state change, so the capture
  arms and disarms one non-requested insert (Master unless Master is requested, else the first
  unarmed non-requested insert) after arming the requested ones, verifies both readbacks, and
  notes the workaround in `warnings`. `arm_refresh=False` disables it.

### Sequence of calls (`fl.audio.capture([5, 0], 33, 40, tail_beats=2)`)

Bars are one-based and **`end_bar` is inclusive**: `33, 40` plays eight bars, `33, 37` five (12.0 s
at 100 bpm). The MCP tools `fl_audio_capture` / `fl_section_measure` use the same convention.

1. `fl.mixer.list()` — the inserts must be addressable (Master and active ordinary inserts).
2. `fl.timebase` + `fl.transport.tempo` → `CapturePlan`: `start_tick = bar_start(33)`,
   `end_tick = bar_start(41)`, `tail_ticks = ticks(2)`, `seconds`, `ticks_per_second`. One constant
   tempo is assumed; tempo automation is not read (as for every bar-grid helper).
3. `resolve_recorded_folder()` and `snapshot_folder()` — name/mtime/size of every WAV already there.
4. `parse_state(fl.transport.state_text())` — refuses if `playing=yes`; remembers the play range.
5. Song mode on, loop cleared (both restored afterwards). **needs live check** (a time selection
   makes play start at the selection).
6. `fl.mixer[t].armed = True` for each insert not already armed (`set_mixer_track_armed` reads the
   armed byte, calls FL's setter only when it differs, re-reads it). **needs live check** (the setter
   names the recording: auto-name, or a dialog if FL's settings ask for one; a cancelled dialog leaves
   the byte clear and the operation reports it).
6b. Arm-refresh: arm and disarm Master (or the first unarmed non-requested insert), verifying the
   readback each time, so FL registers the recording set (see prerequisites).
7. `fl.transport.seek_ticks(start_tick)`; `fl.transport.toggle_record()`; `fl.transport.play()`.
   **needs live check** (record before play starts a recording; no "recording filter" prompt; the
   recording starts at the seek position).
8. Poll `state_text()` every 250 ms until `tick >= end_tick + tail_ticks` or `playing=no` (FL
   reached its song end) or the deadline (`seconds + tail + 10 s`) lapses, then `stop()`.
   **needs live check** (record LED off after stop; otherwise a second toggle is needed).
9. Disarm what was armed here, restore the loop selection and song mode.
10. Wait (up to `file_timeout`, default 15 s) for one *stable* new WAV per insert (size unchanged
    between polls, header parses), pair them with the inserts by the `…_<track name>` suffix, or
    by elimination when exactly one is left. Inserts armed before the call also record; their files
    are ignored by name. **needs live check** (exact auto-name template, 32-bit float format).
10b. With `name`, copy each recording to `<name>-<track>.wav`, verify the copy's header and delete
    FL's auto-named original (`keep_originals=True` keeps it; with `name=None` FL's files are the
    result). Then compare the clip and channel lists with the snapshot taken before the pass:
    new clips are deleted (`fl.clips.delete`), new channels retired (`Channel.retire`: muted,
    routed to Master, renamed `(unused) ...`) and reported as `deleted_clips` / `retired_channels`
    (`cleanup=False` skips this).
11. `measure_wav()` per file over bars 33..40 (the tail stays in the file but outside the record):
    peak dBFS, RMS dBFS, integrated and short-term LUFS, band levels, and per-bar level / peak /
    crest / centroid / correlation / bands / LUFS. `captured_end_tick` comes from the shortest file.
    Every `measurements[track]` is a dict; a file that cannot be analysed carries an `error` key.
12. `CaptureResult.envelope(track, slices_per_bar=8)` gives the compact waveform readout: one RMS
    and one peak value per eighth note plus band levels per bar.

### Section measurement policy (`fl.audio.measure_section`)

`CapturePolicy` estimates both paths and picks the cheaper one:

- live cost = `seconds + 8 s` (arming, transport, file finalisation, analysis);
- render cost = `45 s + seconds / 8` (snapshot save, renderer FL launch with plugin loading,
  renderer exit, session reopen; then offline throughput).

With the defaults the break-even is about 42 s of music (16 bars at 90 bpm, 21 bars at 120 bpm);
anything longer than `max_live_seconds` (120 s) always renders; a per-insert request always
captures live because a render only yields the master. The render figures are reasoned from the
Parking Lot Moon records (five full renders of a 259 s song plus reopens dominated phases 4 and 5;
no wall times were logged) and are meant to be tuned once `fl_project_render` reports its elapsed
time. `prefer="live"|"render"` overrides the estimate.

When the decision is *render*, Python does **not** render: `measure_section` calls the `render`
callback it was given, or raises `RenderRequired` with `suggested_tool_call =
{"tool": "fl_project_render", "startBar", "endBar", "tailBeats"}`. The MCP tool layer owns renders
because a render closes the managed session; `fl_section_measure` (below) is where that callback
lives. Both paths return a `SectionMeasurement` whose `measurements[track]` is the same
`measure_wav` record, so an agent compares a live insert capture with a rendered master directly.

## Python API

```python
result = fl.audio.capture([5, 0], 33, 40, tail_beats=2, name="chorus-a")   # inserts 5 and Master, bars 33..40 inclusive
result.paths                    # {5: Path(".../chorus-a-5.wav"), 0: Path(".../chorus-a-master.wav")}
result.deleted_clips, result.retired_channels, result.removed_originals      # FL litter undone (see prerequisites)
result.captured_start_tick, result.captured_end_tick, result.complete
result.measurements[5]["rms_dbfs"], result.measurements[5]["bars"][0]["lufs"]
result.envelope(5, slices_per_bar=8)   # {"rms_db": [...], "peak_db": [...], "bands_per_bar": [...]}

section = fl.audio.measure_section(33, 40)                 # short: live capture of the master
section = fl.audio.measure_section(1, 108)                 # long: raises RenderRequired (no callback)
section = fl.audio.measure_section(33, 40, inserts=[5])    # per-insert: always live

from fruitylink import measure_wav, envelope, CapturePolicy
record = measure_wav(r"C:\...\section.wav", bpm=100, start_bar=33, end_bar=40)   # a render, offset 0
fl.audio.policy = CapturePolicy(render_overhead_seconds=60)
fl.audio.recorded_folder = r"D:\FL\Audio\Recorded"                               # override discovery
```

`fl.mixer[t].armed` reads/writes the arm state directly. `CaptureResult.to_dict()` and
`SectionMeasurement.to_dict()` are JSON-safe for an MCP response; the WAV paths are what a
descriptor such as `analysis.describe_audio(path)` takes.

## MCP tool contract (for Fl-MCP; not implemented here)

- `fl_audio_capture(inserts: int[] | "master", startBar, endBar, tailBeats=0, name=null)` (bars
  one-based, `endBar` inclusive) →
  `CaptureResult.to_dict()`. Runs `fl.audio.capture(...)` through the managed session's Python
  endpoint. Requires an attached or managed session that is *not* playing; leaves the session open.
  Errors: `CaptureError` (transport busy, folder missing, files not found, arm refused).
- `fl_section_measure(startBar, endBar, inserts=null, prefer="auto", tailBeats=0)` (`endBar`
  inclusive) → 
  `SectionMeasurement.to_dict()`. Calls `fl.audio.decide(...)` first; on *live* it runs
  `measure_section` in the session; on *render* the tool itself performs the existing
  `fl_project_render(startBar, endBar, tailBeats)` flow (snapshot, close, render, reopen from the
  `-full.flp`) and then `measure_wav(path, bpm, startBar, endBar)`; the response carries the same
  record plus `decision`. The render branch is the only place a session is closed, and the tool
  says so in its result (`session_reopened_from`).
- Both tools should surface `warnings` verbatim (early stop, deadline, disarm failure) and the
  `recorded_folder`.

## What is implemented (offline)

- `src/FruityLink.Core/Abstractions/INativeFlControl.cs`: `SetMixerTrackArmedAsync`,
  `GetMixerTrackArmedAsync` (the shared contract; Python `Operations` regenerated).
- `src/FruityLink.FlStudio/Inject/FlInjectBridge.MixerRecording.cs`: get = the armed byte at
  `trackStruct + MixerTrackArmedOffset` (bounds-checked against the verified track stride); set =
  `sym:FLmx_SetTrackArmed(trackStruct, armed, 0)` only when the byte differs, then re-read.
- `src/FruityLink.Scripting/OperationAvailability.cs`: both operations require
  `mixer_arm_symbols`; refused while the mixer layout or either symbol is unresolved.
- `native/bridge/sigscan.cpp`: catalogue entries `FLmx_SetTrackArmed` (SK_DataRef, CALL rel32) and
  `MixerTrackArmedOffset` (SK_VtableSlot, CMP disp32) over one 68-byte anchor;
  `native/bridge/tests/sigscan_tests.cpp` `testArmTrackSymbols` decodes both from a fixture;
  `native/bridge/analysis/verified-symbols-arm-2026-09-14.json` records the evidence.
- `python/src/fruitylink/capture.py` (`fl.audio`): planning, transport-state parsing, folder
  snapshot/diff, name matching, the live driver, `measure_wav`, `envelope`, `CapturePolicy`,
  `measure_section`, `RenderRequired`; `python/tests/test_capture.py` covers all of it against a
  fake FL (24 tests).

## Live results (FL 26.1.3.5570, disposable copy of Parking Lot Moon, 2026-09-14)

- The harvested symbols resolve and the arm readback / round trip works on every insert (0..24).
- `fl.audio.capture` records once the prerequisites above hold; the captured master over bars
  33-36 measured **-13.83 dBFS RMS / -1.0 dBFS peak**, matching the offline master render
  (-13.8 LUFS, -1.0 dBTP): the live path and the render path agree.
- Known FL limitation (residual litter): every recording leaves one sample channel in the rack.
  FL has no channel-delete call, so the capture retires it (muted, routed to Master, renamed
  `(unused) ...`) and reports the indices; delete them by hand from the channel rack when tidying.

## What remains

1. **Global recording options** could be read/cleared natively (`AutoCreateClip`, `AutoUnarm`,
   `RecordingFilter2`; engine settings keys at `0xd9f910`) instead of asked of the user.
2. **Render cost logging**: have `fl_project_render` report wall time so `CapturePolicy` defaults
   stop being estimates.
3. **Fallback b** (tap VST3) once a continuous waveform is wanted; **d** (WASAPI) as a stopgap.

## Live-check plan

Run in order in a disposable copy of a project (Parking Lot Moon v015 is fine) with the transport
stopped; each step names its pass condition. Use `fl_execute_python`.

1. Symbols: the `syms` diagnostic must **not** list `FLmx_SetTrackArmed` or
   `MixerTrackArmedOffset` as unresolved (expected on 26.1.3.5570: thunk `0x12c3980` rebased,
   offset `0x1470`); `fl.mixer[5].armed` reads `False` on an unarmed insert without error.
2. Arm round trip: `fl.mixer[5].armed = True; fl.mixer[5].armed` → `True`; the disc button on
   insert 5 is lit; note whether a recording-name dialog opened (if so, enable auto-naming in FL's
   recording settings and repeat); `= False` clears it and the button.
3. Folder: `from fruitylink.capture import default_recorded_folders; [p.is_dir() for p in default_recorded_folders()]`
   → the first is `True`. Mixer menu ▸ Disk recording: note the format (prefer 32-bit float) and
   turn *Auto-create audio clips* off for the pass.
4. Transport pass by hand: `fl.transport.seek_ticks(fl.timebase.bar_start(33)); fl.transport.toggle_record(); fl.transport.play()`
   then `fl.transport.state_text()` → `playing=yes`, tick advancing from ~bar 33; no "recording
   filter" prompt. `fl.transport.stop()` → the record LED is off (if it stays lit, add a
   `toggle_record()` after stop in `_record_pass` and re-check).
5. Files: a new `<project>_<n>_<track>.wav` appeared in the folder for each armed insert; print
   `fruitylink.analysis.load_wav(path, end_seconds=0.01).provenance()` — 32-bit float, the project
   sample rate; length ≈ (stop tick − start tick) / ticks per second. Confirm the name template and
   adjust `match_recordings` if it differs.
6. Alignment: record bars 33-34 of a kick-only insert; `envelope(path, bpm=..., start_bar=33, end_bar=34, slices_per_bar=16)`
   → the first peak sits in slice 0 (or report the offset in slices; a constant offset becomes an
   `offset_seconds` default).
7. End to end: `r = fl.audio.capture([5, 0], 33, 40, tail_beats=2); r.to_dict()` → two files,
   `complete=True`, `warnings=[]`, `measurements["0"]["bars"]` has eight rows; `fl.clips.list()`
   unchanged; `fl.mixer[5].armed` and `fl.mixer[0].armed` are `False`; the loop selection and song
   mode are as before.
8. Cross-check: render bars 33-40 with `fl_project_render(startBar=33, endBar=40)` and compare
   `measure_wav(render)["rms_dbfs"]` with `r.measurements[0]["rms_dbfs"]` — expect agreement within
   0.5 dB (the live master carries the same FX; a larger gap means latency compensation or
   monitoring differences and goes into the friction log).
9. Policy: `fl.audio.decide(1, 108)` → render; `fl.audio.decide(33, 40)` → live;
   `fl.audio.measure_section(1, 108)` raises `RenderRequired` with the suggested tool call.

## Harvest evidence

Read-only, 2026-09-14, no FL process started: a stdlib pointer scan located the scripting
`PyMethodDef` entries, then Ghidra 12.1.2 headless (`-noanalysis`, `InspectFlFunctions.java`,
disposable project) decompiled the selected entry points on both installed engines
(25.2.5.5319 SHA-256 `1B7E2381…C7AC`, 26.1.3.5570 SHA-256 `BCBA5005…AEAB`). Full addresses are in
`native/bridge/analysis/verified-symbols-arm-2026-09-14.json`.

| Step | 25.2.5.5319 | 26.1.3.5570 | What it shows |
| --- | --- | --- | --- |
| `isTrackArmed` PyMethodDef → wrapper | `0x1407220` → `0xe068d0` | `0x1518f38` → `0xd61040` | Reads `*(byte*)(MixerTrackArray + track*stride + off)` with `0 <= track < MixerTrackCount`; stride `0x1474` (IMUL `0x51D`, scale 4) / `0x1478` (IMUL `0x28F`, scale 8); armed byte `+0x145c` / `+0x1470`. The solo byte at `+0x1a` in the sibling wrapper agrees with the verified mixer layout, and the array global is the one the bridge already resolves as `MixerTrackArray`. |
| `armTrack` PyMethodDef → wrapper → callback | `0x1407240` → `0xe06a70` → `0xe06970` | `0x1518f58` → `0xd611e0` → `0xd610e0` | `armTrack(index, value=-1)` marshals to a main-thread callback: readiness vtable check, bounds check, then `value == -1 ? setter(track, !armed, 0) : (armed != value ? setter(track, value, 0) : nothing)`. |
| Setter thunk (`FLmx_SetTrackArmed`) | `0x11c59d0` | `0x12c3980` | `RCX = trackStruct, DL = armed, R8 = 0`; forwards the recorder object at `trackStruct + 0x1460` / `+0x158` to the inner routine. |
| Inner routine | `0x1174070` | `0x1270a80` | Writes the armed byte, builds the recording name when arming (auto-name or dialog per FL's settings, disarming itself if that fails), refreshes the mixer. |
| Anchor signature (68 bytes, callback tail) | 1 match at `0xe069cf` | 1 match at `0xd6113f` | CALL rel32 (+64, end +68) → thunk; CMP disp32 (+52) → armed offset. `inspect_fl_profiles.py` decodes both on both engines; the fixture test `testArmTrackSymbols` passes (121 native checks). |

The armed offset is decoded from the signature rather than added to the fixed mixer layout so it
cannot be inherited by an uninspected build. The `%s_%d_%s` naming template and the `AutoCreateClip`
/ `AutoUnarm` settings keys remain string-level evidence only.
