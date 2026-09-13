# Python SDK

FruityLink lets Python scripts read and edit an FL Studio project through a typed `Studio` object, conventionally named `fl`. Use it for composition, arrangement, routing, automation, and offline audio analysis.

[Start with your first script](getting-started.md) · [Browse the API](api.md) · [Copy a recipe](examples.md)

## Build the mental model

**FL Studio holds the project. `fl` is a connection to it.** Reading a property asks FL for its current value; assigning a property changes the project immediately. A Python object such as `fl.channels[0]` is an index handle, not a separate copy of a channel.

| FL Studio concept | Python entry point | What it represents |
| --- | --- | --- |
| Project and playback | `fl.project`, `fl.transport` | Project information, save operations, tempo, playback and loop points |
| Channel Rack | `fl.channels` | Instruments, samples, channel controls and routing |
| Patterns | `fl.patterns` | Reusable note content for channels |
| Playlist | `fl.playlist`, `fl.clips`, `fl.arrangements` | Tracks and placements of pattern or automation content |
| Mixer | `fl.mixer` | Master, active inserts, effects and sends |
| Automation | `fl.automation` | Linked automation channels, playlist placement and envelopes |
| Measurements | `fl.analysis` | Local WAV/PCM and symbolic note analysis |

A pattern contains notes; placing that pattern in the Playlist makes an instance of that content on the song timeline. A channel selects the instrument; its mixer route selects where that instrument's audio goes.

## Choose where Python runs

| Workflow | Where your script runs | How to get `fl` |
| --- | --- | --- |
| FLMCP Python tool | Inside FL Studio, using the bundled runtime | The tool supplies `fl` |
| FL Python IDE, under development | Inside FL Studio, using the same bundled runtime | The editor supplies `fl` |
| Your own `.py` program | An external Python 3.11+ process on Windows | Install the package, enable FL Python, call `connect()` |
| Offline analysis | Your own Python process | Import `Analysis`; no FL connection required |

Embedded users do not need a system Python installation. External users need the **FL Python** endpoint plugin; FLMCP's embedded workflow does not. Read [execution and session behavior](execution.md) before building a runner or integrating another application.

## Find the right level of API

Start with domain objects such as `fl.patterns.create("Verse")`. Use typed records such as `NoteSpec` for many edits of one kind. Use `fl.batch()` to sequence different operations. Reach for `fl.ops` when you need a concrete generated method from the complete native contract, and inspect `fl.catalog()` and `fl.capabilities()` for the connected host's schemas and availability.

The same Python package works across supported native profiles. Actual operation support depends on the installed FL build and host; a method's presence in Python does not guarantee native support.

## Continue learning

- [Getting started](getting-started.md): connect, inspect, make one edit, and read it back.
- [API guide](api.md): objects, indices, units, queries, batches and errors.
- [Examples](examples.md): compose notes, route audio, browse parameters and create automation.
- [Execution](execution.md): embedded scripts, external clients, cancellation and results.
- [Audio analysis](../python-audio-analysis.md) and [spectral analysis](../python-spectral-analysis.md): measure explicit files and PCM ranges.
- [Validation evidence](../python-validation.md): what has been checked and its limits.
