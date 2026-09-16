---
search:
  boost: 2
---

# Python recipes

The connected recipes below assume `fl` is already supplied by FLMCP or the IDE. In an external program, import `connect` and indent a recipe inside `with connect() as fl:`. They edit the active project immediately; use a saved scratch project while learning.

## Compose a pattern

Create a generator, add a chord to a new pattern and place four beats of that pattern on Playlist track 1. This recipe requires **FL Keys** to be available in the installed FL Studio edition.

```python
from fruitylink import NoteSpec

piano = fl.channels.add("FL Keys", name="Piano")
pattern = fl.patterns.create("Verse chords")
timebase = fl.timebase
pattern.notes.add([
    NoteSpec(piano.index, key, timebase.ticks(0), timebase.ticks(4), 90)
    for key in (60, 64, 67)
])
fl.playlist[1].name = "Piano"
fl.playlist.add_pattern(
    pattern.index, track=1, start_beats=0, length_beats=4
)
result = pattern.notes.list(channel=piano.index)
```

The channel is zero-based; the pattern and Playlist track are one-based. The note records use ticks, while `add_pattern` accepts beats. The existing [standalone composition example](../../python/examples/compose.py) includes the connection wrapper.

For one note, use `pattern.notes.add_beats(channel=piano.index, key=60, start=4, length=1, velocity=90)`.

## Route a channel through an effect

This recipe assumes the composition recipe created exactly one channel named **Piano**, and that **Fruity Parametric EQ 2** is installed.

```python
piano = fl.channels.find("Piano")
bus = fl.mixer.add("Piano bus")
piano.mixer_track = bus.index
effect = bus.effects[0]
effect.load("Fruity Parametric EQ 2")
result = effect.parameters.page(limit=32)
```

`add()` returns the new insert's index. Requery any older mixer handles after insertion. Effect slot 0 means the first slot. Use returned parameter indices and each parameter's meaning before setting a normalized value; names and parameter layout vary by plugin.

## Feed drums into a bus and prove the routing

```python
from fruitylink import SEND_UNITY

fl.mixer.ensure_inserts(17)                   # the template has 16; insert 17 must exist before it can be named
bus = fl.mixer[17]
bus.name = "Drum Bus"
for insert in range(8, 15):                   # kick .. perc
    fl.mixer[insert].send_to(17, SEND_UNITY)  # 0.8 = 0 dB; 1.0 would be about +4.05 dB
    fl.mixer[insert].send_to(0, 0.0)          # Master route stays connected but silent
for send in fl.mixer.routes():
    if 8 <= send.source <= 14:
        assert (send.destination, send.level) in {(17, 0.8), (0, 0.0)}, send
print(fl.mixer[8].sends())                    # (MixerSendInfo(source=8, destination=0, ..., level=0.0), ... 17 ... 0.8)
```

`disconnect(0)` removes the Master route instead of silencing it; it calls FL's route-active core with enable 0 and is not yet live-verified, so use it in an attended session first.

## Set levels in decibels

```python
from fruitylink.levels import channel_volume_to_db, mixer_volume_from_db

fl.channels[18].volume_db = -0.43            # raw 10000, FL's default level
fl.channels[18].set_volume(db=-6)             # raw 7358
print(channel_volume_to_db(5000))             # about -13: why the vocal loop vanished at 5000
fl.mixer[6].set_volume(db=-3)                 # raw 10850 on the 0..16000 mixer scale
fl.mixer[17].volume = mixer_volume_from_db(0) # 12800
fl.ops.set_channel_volume(channel=15, volume=8000)   # volume= is an alias of the canonical value=
```

The curve is the SDK's model of FL's fader law (0.8 = 0 dB, exponent 2.09 from the 2026-09-14 render calibration, so halving a fader costs about 12.6 dB); confirm large moves with a render and pass `exponent=` to the `fruitylink.levels` helpers if a calibration disagrees.

## Park the template's empty Sampler channel

```python
fl.channels.retire(0)                         # muted, routed to Master, renamed "(unused) Sampler"
```

FL has no channel-delete call the bridge can make; this keeps the rack honest until you delete it in the GUI.

## Browse parameters with a continuation

Run one bounded read against a hosted generator. Replace channel 0 with the desired channel index:

```python
result = fl.channels[0].parameters.page(
    filter="cutoff", offset=0, limit=32
)
```

For a later request, pass the returned `nextOffset` as `offset` with the same filter. An empty `items` array does not mean you reached the end when `nextOffset` is present. Avoid returning complete lists from several large plugins in one script.

For a local program that wants to process all matches incrementally:

```python
for parameter in fl.channels[0].parameters.iter(filter="cutoff", page_size=32):
    print(parameter.index, parameter.name, parameter.display_value)
```

## Create linked automation

This recipe assumes there is exactly one channel named **Piano** and that the connected host advertises automation support. It creates an eight-beat automation clip on Playlist track 2 and explicitly sets an eight-beat envelope.

```python
from fruitylink import AutomationPointSpec, AutomationTarget

piano = fl.channels.find("Piano")
timebase = fl.timebase
created = fl.automation.create(
    AutomationTarget.channel_volume(piano.index),
    track=2,
    start_tick=0,
    length_tick=timebase.ticks(8),
    name="Piano volume",
)
curve = fl.automation[created.channel]
curve.set_points([
    AutomationPointSpec(0, 0.2),
    AutomationPointSpec(4, 0.8),
    AutomationPointSpec(8, 0.4),
])
result = created
```

The result includes the automation channel and clip index. Point times are beats even though placement uses ticks. To place this same envelope again:

```python
result = curve.add_clip(
    track=2, start_tick=timebase.ticks(8), length_tick=timebase.ticks(8)
)
```

Run this continuation in the same script, or reacquire the curve using the returned channel index in a later run. Embedded runs do not share script globals. Inspect the project after a partial failure before retrying creation.

## Compose into the rests

List the rests of at least one beat on a pattern, then the same across bars 9..16 of the arrangement:

```python
pattern = fl.patterns.find("Verse chords")
rests = pattern.gaps(min_beats=1)
arranged = fl.playlist.gaps(9, 16, channel=fl.channels.find("Piano").index, min_beats=2)
result = [(g.channel, g.start_beat, g.length_beats) for g in rests + arranged]
```

Each `Gap` is `[start_tick, end_tick)` with beat conversions at the project PPQ. Pattern gaps are pattern-relative; playlist gaps are absolute. Muted notes count as silence, and a note that starts inside a clip sounds for its full length even past the clip end.

## Add a sidechain pump

Duck a pad channel's volume once per beat across bars 33..40, on the first playlist track free in that range:

```python
from fruitylink import AutomationTarget

timebase = fl.timebase
start, end = timebase.bar_start(33), timebase.bar_start(41)
track = fl.playlist.first_free_track(start, end, above=10)
pumped = fl.automation.pump(
    AutomationTarget.channel_volume(fl.channels.find("Pad").index),
    start, end - start, track=track,
    depth=0.6, recovery_beats=0.75, ceiling=0.78, name="Pad pump",
)
result = {"track": track, "channel": pumped.clip.channel, "points": pumped.point_count}
```

`ceiling=0.78` keeps the recovered level near FL's default channel volume; 1.0 would be the 12800 maximum. `first_free_track` also sees automation and audio clips, so the new clip never lands on an occupied track. The default `tension=0.5` recovers fast and eases in (positive = fast start, slow finish); pass a negative value for a recovery that starts slowly and accelerates.

## Follow the kick with one duck envelope

Automation clips cannot loop or offset, so a kick pattern that alternates per bar (beats 1 + 2.5 on odd bars, 1 + 3 on even bars, fills in the chorus) cannot be followed by re-placing a one-bar clip. Read the kick hits through the arrangement and write ONE envelope on the bass insert:

```python
from fruitylink import AutomationTarget

timebase = fl.timebase
kick = fl.channels.find("Kick").index
start, end = timebase.bar_start(9), timebase.bar_start(57)
hits = fl.playlist.onsets(kick, start, end)
track = fl.playlist.first_free_track(start, end, above=19)
ducked = fl.automation.duck(
    AutomationTarget.mixer_volume(6), hits, start, end - start, track=track,
    ceiling=0.8, depth=0.11, recovery_beats=0.25, tension=0.5, name="Bass duck (insert 6)",
)
result = {"hits": len(hits), "points": ducked.point_count, "channel": ducked.clip.channel}
```

`ceiling=0.8` is the insert's unity level (12800 on the mixer volume scale is 1.0). `recovery_beats=0.25` at 100 BPM is 150 ms. The envelope holds the ceiling until one tick before each hit, so the drop is vertical. Over 4000 points is refused before anything is written; split long songs into two clips. For a regular shape shifted per bar use `fl.automation.tile(target, shape, start, length, track=..., period_beats=4, offsets_beats=[0, 0.5])`.

## Inventory the automation

```python
result = fl.automation.describe()
```

prints one line per linked automation clip: channel, name, the host's target text, the decoded target, the point count and every playlist placement. For records use `fl.automation.list()`; each item has `channel`, `name`, `event_ids`, `targets`, `target`, `point_count` and `clips`.

## Verify an automated value at a bar

A read right after a seek shows the previous position's automated value, and a stopped seek lands 14-20 ticks late while FL runs its automation pass. Settle first:

```python
timebase = fl.timebase
output_level = fl.mixer[1].effects[0].parameters
result = {bar: output_level.display_at(556, timebase.bar_start(bar)) for bar in (5, 9, 20)}
```

`display_at` (and `read_at`) seek, wait until the playhead stops moving, wait 300 ms more, then read until two consecutive readings agree. For any other reader use `fl.transport.read_at(tick, lambda: fl.mixer[6].volume)`. Envelopes shorter than a beat (a 24-tick duck) cannot be sampled this way; verify those from `fl.automation[channel].list()`.

## Place clips at an exact length and keep it

```python
from fruitylink import PatternClipSpec

bar = fl.timebase.bar_start
specs = [PatternClipSpec(36, 9, bar(1), bar(9) - bar(1)), PatternClipSpec(36, 9, bar(9), bar(17) - bar(9))]
fixed = fl.playlist.add_patterns(specs, enforce_lengths=True)
result = {"resized_after_placement": fixed, "lengths": [c.length_tick for c in fl.clips.list(track=9)]}
```

A positive `length_tick` is pinned by the host, so the clips stay 8 bars even when a humanised hit or a legato pad overhangs the bar; `enforce_lengths=True` re-checks and resizes anything that still drifted. Deleting or editing notes afterwards leaves those clips at their lengths (`notes.delete(..., preserve_clips=True)` re-checks from the client); change a clip length deliberately with `fl.clips.resize(index, length_tick)`.

## Set the song end

```python
marker = fl.transport.set_song_end(121)          # marker at the start of bar 121
# marker = fl.transport.set_song_end(tick=46080)  # or an absolute tick
result = (marker.index, marker.name, marker.tick)
```

FL extends the song and the render to the last time marker, so the marker adds silence for tails after the final note. Calling it again moves the marker: any existing marker with the same name is deleted first.

## Shorten an audition by removing trailing markers

FL extends the song length, play range and full-song renders to the last time marker, even when the playlist ends earlier. Remove trailing markers in a disposable copy before rendering a short isolated audition:

```python
kept = 48 * 4 * fl.timebase.ppq  # keep markers within the first 48 bars
for marker in reversed(fl.transport.markers()):
    if marker.tick >= kept:
        fl.transport.delete_marker(marker.index)
result = fl.transport.markers_text()
```

`delete_marker()` also accepts an exact, unique marker name. Deleting by index from the end keeps earlier indices stable.

## Verify a plugin parameter write

```python
lead = fl.channels.find("Lantern - square and saw lead").parameters
written = lead.set_verified("A Level", 0.55)
result = {"index": written.index, "display": written.display_after, "verified": written.verified,
          "display_changed": written.display_changed, "normalized": written.normalized_after}
```

`verified` compares normalized values (the raw readback decoded as a float, within 2^-20), never display strings. When it is False the host still reported the previous raw value after every readback. When `display_changed` is False the plugin's display string had not caught up with the write within the readback budget (or the new value shares the old label); read the slot again in a later request before quoting the display.

## Write a stock effect in display units

Stock effects report names with FL hint codes (`^b^aWet level`) and some repeat a name; the
readback strips the codes, refuses ambiguous names and can convert display units where the
scale is known:

```python
from fruitylink.plugins import scale_for, unique_names

verb = fl.mixer[0].effects[0].parameters                       # Fruity Reeverb 2 on the Master
names = [(p.index, p.name, p.display_value) for p in unique_names(verb.all())]
low_cut = scale_for("Fruity Reeverb 2", "Low cut")             # measured table, Hz
verb.set_verified("Low cut", low_cut.to_normalized(300))
verb.set_verified("Wet", scale_for("Fruity Reeverb 2", "Wet").to_normalized(3))   # 3 %

delay = fl.mixer[4].effects[3].parameters                      # Fruity Delay 3: three "Distortion" slots
try:
    delay.set_named("Distortion", 0.2)
except LookupError as error:
    ambiguous = str(error)                                     # lists indices 18, 19, 20
delay.set_named("Distortion [18]", 0.2)
result = {"names": names, "ambiguous": ambiguous, "low_cut_hz": low_cut.to_display(verb.find("Low cut").normalized or 0)}
```

## Start a native synth from a factory preset

A fresh GMS (and other native synths whose waveforms are chosen in the GUI) renders
silence when authored by parameter alone; load a factory preset first, then trim:

```python
gms = fl.channels.add("GMS", name="Pad")
report = gms.load_preset(r"C:\Program Files\Image-Line\FL Studio 2026\Data\Patches\Plugin presets\Generators\GMS\Pads & Textures\Smooth & Warm TE.gmsynth")
gms.parameters.set_verified("Filter Cutoff", 0.42)
result = {"load": report, "cutoff": gms.parameters.find("Filter Cutoff").display_value}
```

The report line says `same instance` and how many state bytes changed; confirm the voice with an isolated section render before mixing on it. A Sampler channel has no plugin state: `get_state()` / `load_preset()` explain that and point at the channel controls and `replace_sample`.

## Edit several notes at once

Raise note velocity in the current pattern, capped at 127, using one native bulk operation:

```python
from fruitylink import NoteEdit

notes = fl.patterns.current.notes
edits = [
    NoteEdit(
        note.channel, note.key, note.start_tick,
        new_velocity=min(note.velocity + 5, 127),
    )
    for note in notes.list()
]
result = {"changed": notes.edit(edits)} if edits else {"changed": 0}
```

Read the [batch semantics](api.md#batches-and-bulk-edits) before grouping different types of operations. The [standalone batch example](../../python/examples/batch_edit.py) combines transport edits with note editing.

## Analyze a WAV without FL Studio

Run this as a normal Python program after installing the package. Replace the path with an existing WAV:

```python
from fruitylink.analysis import Analysis

audio = Analysis.wav(r"C:\Audio\mix.wav", start_seconds=0, end_seconds=10)
print(audio.summary())
print(audio.spectral())
```

This measures the explicit ten-second file range. It neither captures FL output nor starts a render. In an embedded script, `fl.analysis` exposes the same methods; assign a summary to `result` rather than returning the analysis object. See [audio measurements](../python-audio-analysis.md) for format support, bounds and interpretation.

## Choose between two snare samples, then check Serum through its FX chain

An agent cannot listen, so describe the candidates and read the tags and the comparison. Browse the folder first, then A/B the two survivors:

```python
import glob

candidates = glob.glob(r"C:\Users\me\Documents\Splice\Samples\packs\**\Snare\*.wav", recursive=True)
table = fl.analysis.describe_samples(candidates, detail="brief")     # cached by content hash
result = table.text            # one line per file: dur, peak, rms, root, width, tags
# Keep the ones without "harsh 2-4 kHz" or "wide AND bright", then compare the best two:
delta = fl.analysis.compare(candidates[3], candidates[7])
result = delta.text            # "verdict: b is smoother in 2-4 kHz; b has a longer tail"
# Load the winner and describe it once more from the channel:
channel = fl.channels.add_sample(candidates[7], name="Snare")
result = fl.samples.describe(channel.index, bpm=fl.transport.tempo).text
```

To see what Serum sounds like *after* the insert's effects, render the section (the render is the mixer output, so the chain is included), then describe the dry and processed renders of the same bars and compare them:

```python
# Serum on channel 2 routed to insert 5; render bars 49-56 with and without the chain.
# fl_project_render(outputPath=".../bars-49-56-dry.wav", startBar=49, endBar=56, tailBeats=8) with the
# insert's slots bypassed, then again as ".../bars-49-56-fx.wav" with the chain active.
dry = fl.analysis.describe(r"C:\Renders\bars-49-56-dry.wav", bpm=100, start_bar=49)
wet = fl.analysis.describe(r"C:\Renders\bars-49-56-fx.wav", bpm=100, start_bar=49)
delta = fl.analysis.compare(dry, wet)      # descriptions are reused, nothing is re-analyzed
result = {"wet": wet.text, "delta": delta.text}
# Read delta: "bands (absolute dB): ... pres +4.1 ..." and "verdict: b is harsher in 2-4 kHz" mean the
# chain (a saturator, an exciter) added presence the taste rules reject; "b is wider" with "bright" in
# wet.tags is the "wide AND bright" case. Fix the chain and compare again.
```

Per-insert capture files from a live-capture API can be passed to `describe`/`compare` unchanged; they are ordinary WAVs. The description names the file and the segment spans in bars, so quoting `wet.text` back in a request keeps the discussion anchored to the render.
