# FruityLink Python

Typed Python automation for FL Studio, supporting the host's embedded interpreter
and external Python clients. FLMCP runs this library inside FL with bundled CPython
3.14.6; users do not install Python for that workflow. See the
[embedded runtime guide](../docs/embedded-python.md).

The standalone client below requires Python 3.11 or later, Windows, and an enabled
scripting plugin inside FL Studio. The package uses only the standard library.

The same package also includes [offline audio analysis](../docs/python-audio-analysis.md):
RMS, energy, crest, occupancy, optional gated loudness and true-peak/PSR estimates,
and [spectral summaries](../docs/python-spectral-analysis.md) for explicit WAV/PCM
ranges. These utilities require no DAW connection. The
[API guide](docs/api.md) covers typed automation creation, complete curve replacement,
and mixer insertion as well as the existing composition helpers.

```console
python -m pip install ./python
```

```python
import fruitylink

with fruitylink.connect() as fl:
    print(fl.project.info)
    fl.transport.tempo = 120
    pattern = fl.patterns.create()
    pattern.name = "Verse"
    pattern.notes.add_beats(channel=0, key=60, start=0, length=1, velocity=100)
    fl.playlist.add_pattern(pattern.index, track=1, start_beats=0, length_beats=4)
```

`connect()` requires exactly one discoverable, responding instance. Use
`connect(pid=1234)` when several FL Studio processes are running. Discovery files
contain credentials: do not print, commit, or share them. A live handshake checks
the advertised process and instance identity; there is no fallback to another instance.

`fl.ops` contains generated, concrete methods for the complete native contract,
with editor completion and type hints. Operation names and Python keyword arguments
use snake_case; the library maps them to the native catalogue's wire names:

```python
fl.ops.add_note(pattern=1, channel=0, key=60, start_tick=0, length_tick=96, velocity=100)
print(fl.catalog())
print(fl.capabilities())
```

High-level APIs use snake_case. Channels, mixer tracks, effect slots and clip
indices are zero-based; patterns and playlist tracks are one-based. Clip indices
can change after deletion. `fl.timebase` queries the current project PPQ; a retained
`Timebase` is a snapshot. Automation point times are beats. Native note and clip
positions are ticks. Integer mixer/channel values retain FL's documented scales.

`fl.mixer.add(name="New insert")` adds one mixer insert; use `after=3` to insert
after a specific track. It returns the new `MixerTrack`. Requery track indices and
channel routes after insertion. Naming is a separate edit, so a naming failure
does not undo creation. See [mixer insertion](docs/api.md#add-a-mixer-insert).

Legacy list methods return human-readable **text**, exposed as `*_text` helpers.
The library does not parse arbitrary track/plugin names out of that text.
Structured query support is capability-dependent and is surfaced separately.

For large plugins, return `fl.channels[0].parameters.page(limit=32)` and follow
its `next_offset` in subsequent requests. Parameter iteration is lazy; `list()`
deliberately still gathers every page. Script results are limited to 512 KiB,
and exceeding that bound does not roll back edits already made. See
[bounded parameter browsing](docs/api.md#browse-large-plugin-parameter-sets).

`fl.channels[index].parameters` browses a hosted generator's parameters, such as 3xOsc or
a VST. Built-in Sampler envelopes and sample settings are not exposed through this API;
an unavailable-interface error does not mean its sample failed to load. Use the channel's
volume, pan, mute, routing and sample operations for supported controls. Use
`fl.mixer.list()` for typed Master/active-insert snapshots and their real indices;
iteration and `len(fl.mixer)` exclude the special Current track and dormant slots.
The low-level native count includes Current and is not a physical-index bound.

See [API guide](docs/api.md), [protocol](docs/protocol.md), and [worker](docs/worker.md).
Examples under `examples/` show composition, routing, and batch edits.

## Development

```console
uv sync --directory python
uv run --directory python python tools/check.py
```

The check runs contract generation verification, Ruff (including complexity ≤15
and import checks), strict mypy, tests, and package build. Regenerate operations
after changing the C# contract with `python tools/generate_operations.py`.
The commands above run from the FL-Automation repository root; run the generator
from its `python` directory.

Operations affect the currently connected project. Batches execute sequentially;
they are not transactions and do not provide automatic rollback. The separate
Python worker and embedded runtime execute trusted user code with ordinary OS
permissions; neither is a security sandbox. Unsupported native capabilities fail explicitly.

Licensed under the repository's MIT license.
