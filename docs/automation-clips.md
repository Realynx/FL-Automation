# Linked automation clips

The shared SDK creates Automation Clip channels, links their initial destination, places them in the playlist, and reads or edits their envelopes. Python and MCP use these same operations; no separate MCP-only creation path exists.

```python
from fruitylink import AutomationTarget, AutomationPointSpec

created = fl.automation.create(
    AutomationTarget.mixer_volume(1),
    track=2, start_tick=0, length_tick=16 * fl.timebase.ppq,
    name="Insert 1 volume",
)
curve = fl.automation[created.channel]
curve.set_points([
    AutomationPointSpec(0, 0.7),
    AutomationPointSpec(7.99, 0.7),
    AutomationPointSpec(8, 0),
    AutomationPointSpec(16, 0),
])
print(curve.list())
```

Playlist tracks are **1..500**; channel and clip collection indices are zero-based. Placement uses ticks; envelope points use quarter-note beats. Read the project's PPQ to convert between them. Collection indices are snapshots and may change after editing the project.

Supported destinations are channel volume, pan and pitch; mixer volume and pan; and a hosted generator or mixer-effect parameter. Targets identify objects and parameter indices rather than exposing native event IDs. Channel targets resolve the channel's persistent event ID, including after channel reordering. Mixer targets use the verified dynamic mixer bounds. An ordinary Sampler can receive channel-volume automation, but its internal sample/envelope settings remain outside the hosted-plugin parameter interface.

`CreateAutomationClipAsync` creates and links one destination, initializes the requested duration, and places its first clip. `AddAutomationClipAsync` places an existing Automation Clip channel again without creating another generator or changing its destination. `SetAutomationPointsAsync` replaces the full envelope with 2..4000 finite, strictly ordered points starting at beat zero. Newly supplied curves support linear type 0; normalized values are 0..1 and tension is -1..1. Point insertion preserves existing point metadata. Point deletion protects the first and last endpoints; use full replacement to change endpoints.

Creation and subsequent naming, envelope setup and placement are separate native steps. A failure after creation reports the created channel when known. A lost or unsuccessful native mutation acknowledgement reports that the project may have changed. Inspect the project before retrying; these APIs promise neither automatic rollback nor a single undo transaction. Native envelope validation and its write execute together on FL's main thread.

## Verified implementation

The native profile enables automation only for **25.2.5.5319** and **26.1.3.5570**. Older 26.1.0 recordings, unknown patches, 2024 and future major versions do not inherit offsets. The `automationClips` diagnostic and `automation_clips` catalogue requirement distinguish this capability from general scanner support. Required native functions must also resolve.

| Recipe | FL 25.2.5.5319 preferred VA | FL 26.1.3.5570 preferred VA |
|---|---:|---:|
| Create clip for parameter event | `0x108A1A0` | `0x118EDB0` |
| Initialize default envelope line | `0xF00C50` | `0xFE6500` |
| Native point insertion, inspected for layout | `0xB30780` | `0xB98BD0` |
| Native point deletion, inspected for layout | `0xB30AD0` | `0xB98F80` |
| Point-array RTTI | `0xB2C678` | `0xB94A78` |

The creator has five arguments: event ID, placement using current UI selection, optional target descriptor, linked-track arrangement, and undo flag. The SDK passes `(event, 0, 0, 0, 1)`. FL loads its own `AutoTrack.fst`, creates the channel, and inserts the target link. The SDK reuses the canonical playlist insertion path instead of depending on current UI selection. Source identity is the channel's event ID plus `0x5000`.

Channel kind **5** is required before dereferencing an automation container. The verified kind/container offsets are `0x190/0x390` in 2025 and `0x160/0x360` in 2026. Both containers hold their main envelope at `+0x10`; envelope mode `+0x58` must be 4. Points are a Delphi array at `+0x28`, with a signed 64-bit count at array-minus-eight and 32-byte records. Times are stored as floating-point deltas. Invalid or excessive counts are errors and are never reset to zero. The original implementation's unconditional 2025 container offset and destructive invalid-count fallback have been removed.

Array replacement uses the verified Delphi allocator and the envelope's engine-owned recompute method. Surviving records retain their additional fields during insertion/deletion; full replacement initializes new records. The target-link registry is the list at `*(registryRoot + 8)`. The adjacent transport global is not an alternative registry.

Envelope `+0x68` is its **maximum permitted time**, not the current curve length. Both inspected constructors initialize it to `2147483647.0` beats (`0xB2CFF0` in 2025; `0xB953F0` in 2026). The initial four-beat duration comes from the second default point. The recompute methods (`0xB352A0` / `0xB9D740`) do not change the maximum. Replacing a four-beat default curve with sixteen beats therefore needs no write to an internal length field. This corrects the earlier ambiguous `totalLength` label in historical RE notes.

The old additional-link helper (`0xE8DEB0` in 2025) takes an automation editor **form**, not a channel pointer. Linking further destinations to an existing clip is therefore not exposed by this implementation. Native creator linkage for the initial destination is verified independently in both engines.

Static evidence was obtained by reading/decompiling the installed engine files without loading them into the test process. Native fixtures cover both layouts, malformed 64-bit counts, ordinary-channel rejection, point metadata, bounds and mutation acknowledgement. Managed and scripting tests cover typed contracts, culture-independent numbers, persistent target IDs and capability propagation. Actual playback and saved-project verification remain separate live checks; static and synthetic tests do not establish audible behavior.

## Live verification

Installer 0.1.21 was tested through the real MCP embedded interpreter on FL
26.1.3.5570 in a disposable project. Creation linked mixer insert 1 volume to a
sixteen-beat curve; whole-curve replacement, interior point insertion/deletion,
and placing an existing Automation Clip succeeded. A normal Sampler was refused
as an envelope-edit target. Saving and reopening preserved every point, clip and
target link.

The eight-second stereo 48 kHz float render contained repeated kicks before the
automation reached zero at beat eight. RMS measured over 0.5–3.5 seconds was
0.066129852; over 4.5–7.5 seconds it was exactly zero. This verifies the initial
parameter link and beat timing affected rendered audio. The same measurements
also passed through the embedded SDK. FL 2025 and the other destination kinds
retain static/automated coverage rather than a live verification claim.
