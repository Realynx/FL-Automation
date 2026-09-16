---
search:
  boost: 2
---

# C# recipes

These methods can live in a plugin's helper class. They use the `IPluginContext`
received during activation and a caller-owned cancellation token. Call them from
asynchronous plugin work; do not block an FL menu or toolbar callback while waiting.
Catch and report exceptions at your plugin's operation boundary.

Use the read recipe first. The editing recipes change the live project, so use a
scratch project and inspect it before rerunning them.

## Inspect channels and mixer tracks

`IFlStructuredQuery` is an optional companion to `INativeFlControl`. It returns typed
records instead of human-readable lists. Check for the interface before using it:

```csharp
using FruityLink.Core.Abstractions;
using FruityLink.Plugins.Abstractions;

public static class ProjectInspector
{
    public static async Task InspectAsync(IPluginContext context, CancellationToken ct)
    {
        if (context.Fl is not IFlStructuredQuery query)
        {
            context.Log("Structured queries are unavailable in this host.");
            return;
        }

        foreach (FlChannelInfo channel in await query.QueryChannelsAsync(ct))
            context.Log($"Channel {channel.Index}: {channel.Name}, mixer {channel.MixerTrack}");

        foreach (FlMixerTrackInfo track in await query.QueryMixerTracksAsync(ct))
            context.Log($"Mixer {track.Index}: {track.Name} ({track.Kind})");
    }
}
```

Mixer queries return Master and active ordinary inserts, excluding the special
Current track and dormant slots. The native track count includes Current and is
not a list of addressable physical indices. Requery after insertion or reordering.

## Create a four-note phrase

Choose a channel index from the inspection above. The method selects the first empty
pattern, names it, adds four quarter notes, and places a four-beat clip on playlist
track 1 in the current arrangement. It does not start playback or save the project.

```csharp
using FruityLink.Core.Abstractions;
using FruityLink.Plugins.Abstractions;

public static class PhraseWriter
{
    public static async Task CreateAsync(IPluginContext context, int channel, CancellationToken ct)
    {
        INativeFlControl fl = context.Fl;
        int channelCount = await fl.GetChannelCountAsync(ct);
        if (channel < 0 || channel >= channelCount)
            throw new ArgumentOutOfRangeException(nameof(channel));

        int ppq = await fl.GetPpqAsync(ct);
        int pattern = await fl.CreatePatternAsync(ct);
        await fl.SetPatternNameAsync(pattern, "SDK phrase", ct);
        await fl.AddNotesAsync(pattern, new NoteSpec[]
        {
            new(channel, 60, 0,       ppq, 100),
            new(channel, 64, ppq,     ppq, 100),
            new(channel, 67, 2 * ppq, ppq, 100),
            new(channel, 72, 3 * ppq, ppq, 100),
        }, ct);
        await fl.AddPatternClipAsync(pattern, track: 1, startTick: 0, lengthTick: 4 * ppq, ct: ct);
        context.Log($"Created phrase in pattern {pattern}.");
    }
}
```

MIDI keys are integers, note velocity is `0..127`, and note/clip timing uses project
PPQ ticks. `AddNotesAsync` batches note insertion with one refresh. The full workflow
has multiple edits and no automatic rollback: a failed clip placement may leave a
successfully written pattern. Inspect the project before retrying.

## Create a mixer insert and route a channel

This appends an ordinary mixer insert and routes the selected channel to it.
The channel must exist when the operation runs. Repeating the method creates another
insert; use a query when you want to reuse an existing one.

```csharp
using FruityLink.Core.Abstractions;
using FruityLink.Plugins.Abstractions;

public static class MixerRouter
{
    public static async Task CreateRouteAsync(IPluginContext context, int channel, CancellationToken ct)
    {
        INativeFlControl fl = context.Fl;
        int channelCount = await fl.GetChannelCountAsync(ct);
        if (channel < 0 || channel >= channelCount)
            throw new ArgumentOutOfRangeException(nameof(channel));

        int track = await fl.AddMixerTrackAsync(afterTrack: -1, ct: ct);
        context.Log($"Created mixer insert {track}; configuring route.");
        await fl.SetMixerTrackNameAsync(track, "SDK bus", ct);
        await fl.SetChannelFxRouteAsync(channel, track, ct);
        context.Log($"Channel {channel} now routes to mixer insert {track}.");
    }
}
```

Mixer creation requires the matching native layout and insertion capability. A
failure can occur after creation, so the first log entry records the new index before
later steps. Refresh structured mixer queries afterward.

## Go further

- [Control API](../fl-control-api.md) lists the available operations and value scales.
- [Automation clips](../automation-clips.md) explains linked creation and envelope rules.
- [Menus and toolbar](../menus-and-toolbar.md) explains how to expose an operation as a command.
- [Plugin lifecycle](../plugin-lifecycle.md) covers cancellation, cleanup, and reload.
- [Python SDK](../python/index.md) provides another route to the same project model.
