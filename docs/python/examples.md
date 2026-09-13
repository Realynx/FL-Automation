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
