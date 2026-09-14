# FL control API

`INativeFlControl` (in `FruityLink.Core.Abstractions`) is the supported typed surface for controlling FL Studio.
You get it from `IPluginContext.Fl`. It executes operations natively inside the FL process via the
bridge; the raw memory/call primitives are *not* exposed to plugins (see the trust-boundary note below
and [native-bridge.md](native-bridge.md)).

Every method is `async` and takes an optional `CancellationToken`. Implementations are best-effort and
throw if the bridge isn't available, so guard calls and surface failures to the user rather than letting
them escape into the host.

See also: [Menus and toolbar](menus-and-toolbar.md) · [Plugin lifecycle](plugin-lifecycle.md).

## Availability

```csharp
if (!await ctx.Fl.IsAvailableAsync()) { ctx.Log("bridge not ready"); return; }
```

`IsAvailableAsync` checks that the bridge is available and FL has initialized its
state and main window. It does not establish every operation's capability.

## Value conventions

FL uses native integer scales. The important ones:

| Quantity | Range | Notes |
| --- | --- | --- |
| Volume (master / mixer / channel) | `0..12800` | Raw native integer. The API defines no dB conversion; query the current value before changing it. Untouched mixer tracks and Master have been observed at `12800`, while channel defaults have been observed at `10000`. |
| Channel pan | `0..12800` | `6400` = center. |
| Mixer track pan | `-6400..6400` | **Signed**: `0` = center, negative = left, `6400` = hard right. Untouched tracks read `0`. A value written on the channel scale (for example `6800`) pans fully right; this caused the Ember Tides v005 right-sided lead. |
| Master pitch | `-1200..+1200` cents | |
| Channel / note pitch | cents | `0` = center. |
| Shuffle / swing | `0..128` | |
| MIDI key | `0..131` | `60` = middle C. |
| Velocity | `0..127` | |
| Tick times | PPQ ticks | Query the project timebase with `GetPpqAsync()`. |
| Mixer EQ band gain | `0..0x40000000` | `~0x20000000` = 0 dB; band `0`=low, `1`=mid, `2`=high. |
| Mixer send level | `double` | `1.0` ≈ unity. |
| Plugin parameter (`SetPluginParamAsync`) | `0..1` normalized | |
| Automation point value | `0..1`; tension `-1..1` | time in beats. |

Ticks are PPQ-relative: always resolve a musical position through `GetPpqAsync()` rather than assuming a
fixed resolution.

## The surface, by capability

Grouped roughly as the interface is. Names below are the actual method names; read
[`INativeFlControl.cs`](../src/FruityLink.Core/Abstractions/INativeFlControl.cs) for full signatures.

**Global / master.** `Get/SetTempoAsync`, `Get/SetMasterVolumeAsync`, `Get/SetMasterPitchAsync`,
`Get/SetShuffleAsync`.

**Channel rack.** `GetChannelCountAsync`, `ListChannelsAsync`, `GetChannelNameAsync`,
`SetChannelNameAsync`, `SelectChannelAsync`, `Get/SetChannelVolumeAsync`, `Get/SetChannelPanAsync`,
`Get/SetChannelPitchAsync`, `Get/SetChannelMutedAsync`, `SetChannelSoloAsync`,
`Get/SetChannelFxRouteAsync` (route a channel to a mixer track), plus generator hosting:
`AddChannelAsync(pluginName)`, `GetChannelPluginAsync`, and sample channels
(`AddSampleChannelAsync`, `ReplaceChannelSampleAsync`, `ListSamplesAsync`). Every channel operation
names its zero-based channel argument `channel` (wire name `channel`); `select_channel` and
`set_channel_solo` still accept the older `index` wire name when `channel` is absent.

**Patterns.** `GetCurrentPatternAsync`, `SelectPatternAsync`, `CreatePatternAsync` (selects the first
empty pattern), `ClearPatternAsync`, `Get/SetPatternNameAsync`, `ListPatternsAsync`,
`ClonePatternAsync` (deep-copy a pattern's notes into a new one).

**Piano-roll notes.** Author with `AddNoteAsync` or the batched `AddNotesAsync(pattern, notes)` (one
refresh for the whole set — use it for chords/melodies/multi-channel grids). Read with `GetNotesAsync`
(paged via `offset`). Edit surgically without clearing the pattern: `EditNotesAsync` and
`DeleteNotesAsync`. Notes are identified by the `(Channel, Key, StartTick)` triple (`NoteRef`), so you
target a note without a fragile array index. FL does allow stacked duplicates that share the triple:
both records carry an optional `LengthTick` (the note's current length) to pick one of them, and a
target that still matches several notes is refused before anything is written unless the operation's
`allowMultiple` flag is set, which addresses all of them (identical duplicates can only be addressed
together). Batch authoring uses `NoteSpec`; edits use `NoteEdit` (nullable "new" fields; null = leave
unchanged).

**Mixer.** `Get/SetMixerVolumeAsync`, `Get/SetMixerPanAsync`, `Get/SetMixerTrackMutedAsync`
(track 0 = master), `GetMixerTrackCountAsync`, `Get/SetMixerTrackNameAsync`, `ListMixerTracksAsync`
(name→index resolution), `AddMixerTrackAsync` (append or insert an ordinary track),
sends/EQ (`SetMixerSendAsync`, `SetMixerEqGainAsync`), and FX slots:
`ListMixerEffectsAsync`, `AddMixerEffectAsync`, `RemoveMixerEffectAsync`, `CloneMixerEffectAsync`,
`SetMixerFxParamAsync`. Plugin names passed to `AddMixerEffectAsync` and `AddChannelAsync` are
resolved tolerantly against the plugin database file names (`ListAvailablePluginsAsync`): exact
name first, then case/punctuation-insensitive, then a unique containment such as a vendor prefix
("FabFilter Pro-R 2" loads "Pro-R 2"). A name that matches several plugins ("Pro") is refused with
every candidate listed; a miss lists the closest installed names.

**Transport.** `TransportPlayAsync`, `TransportStopAsync`, `TransportToggleRecordAsync`,
`SetLoopRegionAsync` (set/clear the loop/time-selection span), `SeekAsync` (move the playhead to a
tick), plus `GetSongStateAsync`, `Get/SetSongModeAsync` (song vs pattern mode), and `GetStatusAsync`
(FL's status/hint bar text; read-only, safe to poll).

**Playlist tracks.** `ListPlaylistTracksAsync`, `Get/SetTrackNameAsync`, `Get/SetTrackColorAsync`,
`Get/SetTrackMuteAsync`, `SetTrackSoloAsync`, `Get/SetTrackCollapsedAsync`, `SelectTrackAsync`.

**Playlist clips (arrangement).** `ListClipsAsync` (paged, filterable by track), `AddPatternClipAsync`,
`MoveClipAsync`, `ResizeClipAsync`, `DeleteClipAsync`, `Get/SetClipMutedAsync`, `SliceClipAsync`,
`DuplicateClipAsync`. Bulk variants apply a whole batch with a single repaint at the end
(`AddPatternClipsAsync`, `MoveClipsAsync`, `ResizeClipsAsync`, `DeleteClipsAsync`, `SetClipsMutedAsync`)
using `PatternClipSpec` / `ClipMove` / `ClipResize`. The singular methods delegate to these, so use
whichever is convenient — prefer the bulk form for many edits.

**Markers.** `ListMarkersAsync`, `AddMarkerAsync(tick, name)`.

**Arrangements.** `ListArrangementsAsync`, `AddArrangementAsync`, `CloneArrangementAsync`
(deep copy incl. clips), `RenameArrangementAsync`, `GetArrangementNameAsync`, `DeleteArrangementAsync`,
`SelectArrangementAsync`.

**Project lifecycle.** `NewProjectAsync`, `OpenProjectAsync`, `SaveProjectAsync`, `SaveProjectAsAsync`,
`SaveCopyAsync` (modal-free copy, safe on untitled projects), `SaveNewVersionAsync`, `GetProjectInfoAsync`,
`ListRecentProjectsAsync`.

**Plugin state.** `LoadChannelPluginStateAsync(channel, path, useChannelLoader=false)` and
`LoadMixerEffectStateAsync(track, slot, path)` load a preset/state file into the plugin already in
the slot through the wrapper's state-file dispatcher (live-verified with a Serum 2 `.vstpreset`;
proprietary formats such as `.SerumPreset` are ignored by the wrapper). `GetChannelPluginStateAsync(channel)`
and `GetMixerEffectStateAsync(track, slot)` return the plugin's current wrapper state (base64 of the FLP
plugin-data record) by writing a temporary project copy through the same direct writer as
`SaveCopyAsync` and extracting that record; the live project's path, title and dirty flag are
unchanged, and save-time note validation applies. Python: `channel.load_state()`, `channel.get_state()`,
`effect_slot.load_state()`, `effect_slot.get_state()`. The load operations return a verification line
that compares that wrapper record before and after the load (sizes, short SHA-256 of each side, and
the number of differing bytes); it says "unavailable ... nothing was compared" when a snapshot could
not be taken, and never claims "no change" from sampled parameter values (those missed live
parameters on Serum 2). A handful of differing bytes can be serializer noise; a large fraction is a
loaded state. Parameter displays read in the same request can still lag the load (see below).

**Plugin parameters.** `ListPluginParamsAsync(channelOrTrack, slot, filter)` and
`SetPluginParamAsync(channelOrTrack, slot, paramIndex, value)` — `slot < 0` targets a channel generator,
otherwise a mixer track + FX slot; values are normalized `0..1`. `ListAvailablePluginsAsync(effects)`
lists installed generators (`effects=false`) or effects (`effects=true`).

Readback timing: a write goes through FL's command bus, and the raw value (`rawValue`, the wrapper's
own `getParamValue`) reflects it at once. The display string (`displayValue`) is produced by the plugin
instance, which only sees the change once FL has delivered it (audio-thread/idle sync); read in the
same request right after a write or a preset load it can still show the previous value, while the next
request is always correct (live: Serum 2 and Pro-L 2, FL 26.1.3). The bridge knows no wrapper call that
forces that delivery, so the host does not attempt one. Treat the raw value as the oracle and, when a
display string matters within one request, re-read after a short delay (`Parameters.set_verified`
does both).

These methods and `QueryPluginParametersAsync` require a hosted-plugin parameter interface,
as provided by generators such as 3xOsc and hosted VST plugins. The built-in Sampler does not
expose its envelopes or sample settings through this SDK interface. A Sampler channel can
still contain and play a sample when parameter enumeration reports that the interface is
unavailable. Use the channel volume, pan, mute, mixer-routing and sample-loading methods for
those supported controls; the SDK does not substitute them for plugin parameter indices.
The text list returns an explanatory message; structured queries and writes fail with that
same diagnostic. An empty mixer effect slot receives a separate slot-specific diagnostic.

Use `IFlStructuredQuery.QueryMixerTracksAsync()` to enumerate addressable Master and
active ordinary inserts. `GetMixerTrackCountAsync()` includes Master and the special
Current track, whose physical index is 501 rather than `count - 1`. It is not a bound
from which to construct an addressable index range. Requery after insertion.

**Automation clips.** `CreateAutomationClipAsync` creates and links an automation channel
and places its first clip; `AddAutomationClipAsync` places an existing one again.
`SetAutomationPointsAsync` replaces its full envelope. For a channel hosting the
Automation Clip generator, `ListAutomationPointsAsync`,
`AddAutomationPointAsync(channel, timeBeats, value, tension)`, and
`DeleteAutomationPointAsync` read or edit points. See [linked automation clips](automation-clips.md)
for point validation, exact-build requirements, and partial-failure behavior.

**Render / export.** `OpenExportDialogAsync` opens FL's audio Export dialog for the user to finish.

## Name-vs-index resolution

Channels, mixer tracks, patterns, and playlist tracks are addressed by **index**, but their names are
what a user (or an AI) reasons about. The list methods return `"index: name"` mappings so you can
resolve a name to an index yourself:

- `ListChannelsAsync` / `ListMixerTracksAsync` / `ListPatternsAsync` / `ListPlaylistTracksAsync`.

The `Set…NameAsync` methods **persist** across save/reload, so a channel/bus/pattern your plugin creates
and names stays resolvable by that name later. `ListMixerTracksAsync` in particular exists to map a bus
name to its index (rather than scanning every track).

## Version gating: `IFlSymbolResolution`

The bridge resolves FL's reverse-engineered entry points per FL version. Some may not resolve on a given
build — in which case the tools that depend on them should be hidden rather than fired (a wrong address
is an uncatchable crash). An `INativeFlControl` **may** also implement `IFlSymbolResolution`:

```csharp
if (ctx.Fl is IFlSymbolResolution res)
{
    FlSymbolStatus? status = await res.GetSymbolStatusAsync();
    // status?.Unresolved names the symbols that did NOT resolve on this FL build.
    // status is null => unknown; treat as "gate nothing" (fail OPEN).
    // status is { Complete: true, Supported: false } => completed unsupported scan;
    // honor its unresolved symbols instead of treating zero resolved symbols as unknown.
}
```

Feature-detect and **fail open**: only the real injected bridge implements this — mocks/tests do not — so
when the cast fails or `GetSymbolStatusAsync` returns null, assume everything is available. Resolution is
computed once after FL loads and never changes for the process.

Newer bridges report optional metadata on `FlSymbolStatus`: `FileVersion` contains the full engine
file version, `Scanner` identifies the selected scanner profile, `Supported` indicates whether a
profile supports scanning that engine, and `Complete` distinguishes finished scanning from startup.
`Supported=true` does not mean every symbol resolved or every object layout is verified.

An explicitly completed scan is authoritative even with `Resolved=0`, including an unsupported engine
whose symbols all fail. `Complete=false` is pending and is re-queried. Legacy responses without the
completion field retain the earlier rule: at least one resolved symbol is needed for an authoritative
result. Completed results are cached only for their in-process transport; pipe queries are never cached
because reconnecting can reach a different FL process. Malformed metadata or unavailable transport
returns unknown, and caller cancellation still propagates.

`MixerLayout` is an optional complete `FlMixerLayout` record supplied by the native profile. It carries
the track stride and the field/table offsets for names, types, state, sends, and effects. All 14 fields
are required together, with positive strides, nonnegative offsets, and checked containment for fixed
track/send fields. Missing, partial, or null layout metadata cannot authorize raw mixer operations;
the bridge must be upgraded when an older diagnostic lacks this object. Consumers also validate dynamic
track/send indices before accessing memory.

`MixerTrackStride` remains an optional diagnostic value, separate from the full layout. For old bridge
responses that omit this scalar field entirely, the central parser adapts exact legacy version 1 to
`0x1474` and version 2 to `0x1478`. An explicitly null scalar never inherits a stride. Neither this
compatibility scalar nor a version/year can create a missing `MixerLayout`. The original four-argument
`FlSymbolStatus` constructor and deconstruction remain available.

## Error behavior and the trust boundary

- Methods **throw** if the bridge isn't injected/ready. Guard with `IsAvailableAsync` and/or try-catch;
  never let the exception reach the host.
- The context exposes only this typed surface plus the menu/toolbar registrars. The bridge's raw
  peek/poke/call primitives are **internal and capability-locked** — no address ever crosses into a
  plugin through these interfaces. This narrows the supported API; it is not a security
  sandbox. In-process plugins still execute with FL Studio's permissions.
