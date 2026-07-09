# FL control API

`INativeFlControl` (in `FruityLink.Core.Abstractions`) is the **only** way a plugin touches FL Studio.
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

`IsAvailableAsync` returns true when the injected bridge is loaded and responding.

## Value conventions

FL uses native integer scales. The important ones:

| Quantity | Range | Notes |
| --- | --- | --- |
| Volume (master / mixer / channel) | `0..12800` | `10000` = 100%. Master ≈ `7624` ≈ 0 dB; channel default `10000` (≈78% law). |
| Pan (mixer / channel) | `0..12800` | `6400` = center. |
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
(`AddSampleChannelAsync`, `ReplaceChannelSampleAsync`, `ListSamplesAsync`).

**Patterns.** `GetCurrentPatternAsync`, `SelectPatternAsync`, `CreatePatternAsync` (selects the first
empty pattern), `ClearPatternAsync`, `Get/SetPatternNameAsync`, `ListPatternsAsync`,
`ClonePatternAsync` (deep-copy a pattern's notes into a new one).

**Piano-roll notes.** Author with `AddNoteAsync` or the batched `AddNotesAsync(pattern, notes)` (one
refresh for the whole set — use it for chords/melodies/multi-channel grids). Read with `GetNotesAsync`
(paged via `offset`). Edit surgically without clearing the pattern: `EditNotesAsync` and
`DeleteNotesAsync`. Notes are identified by the `(Channel, Key, StartTick)` triple (`NoteRef`), which is
stable — two notes can't share all three on one channel — so you target a note without a fragile array
index. Batch authoring uses `NoteSpec`; edits use `NoteEdit` (nullable "new" fields; null = leave
unchanged).

**Mixer.** `Get/SetMixerVolumeAsync`, `Get/SetMixerPanAsync`, `Get/SetMixerTrackMutedAsync`
(track 0 = master), `GetMixerTrackCountAsync`, `Get/SetMixerTrackNameAsync`, `ListMixerTracksAsync`
(name→index resolution), sends/EQ (`SetMixerSendAsync`, `SetMixerEqGainAsync`), and FX slots:
`ListMixerEffectsAsync`, `AddMixerEffectAsync`, `RemoveMixerEffectAsync`, `CloneMixerEffectAsync`,
`SetMixerFxParamAsync`.

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

**Plugin parameters.** `ListPluginParamsAsync(channelOrTrack, slot, filter)` and
`SetPluginParamAsync(channelOrTrack, slot, paramIndex, value)` — `slot < 0` targets a channel generator,
otherwise a mixer track + FX slot; values are normalized `0..1`. `ListAvailablePluginsAsync(effects)`
lists installed generators (`effects=false`) or effects (`effects=true`).

**Automation clips.** For a channel hosting the Automation Clip generator:
`ListAutomationPointsAsync`, `AddAutomationPointAsync(channel, timeBeats, value, tension)`,
`DeleteAutomationPointAsync`.

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
}
```

Feature-detect and **fail open**: only the real injected bridge implements this — mocks/tests do not — so
when the cast fails or `GetSymbolStatusAsync` returns null, assume everything is available. Resolution is
computed once after FL loads and never changes for the process.

## Error behavior and the trust boundary

- Methods **throw** if the bridge isn't injected/ready. Guard with `IsAvailableAsync` and/or try-catch;
  never let the exception reach the host.
- The context exposes only this typed surface plus the menu/toolbar registrars. The bridge's raw
  peek/poke/call primitives are **internal and capability-locked** — no address ever crosses into a
  plugin. This is by design (a third-party plugin can't reach FL's internals or DRM).
