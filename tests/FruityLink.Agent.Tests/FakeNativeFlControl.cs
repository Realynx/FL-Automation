using System.Text;
using FruityLink.Core.Abstractions;

namespace FruityLink.Agent.Tests;

/// <summary>
/// Recording <see cref="INativeFlControl"/> fake: every call is appended to <see cref="Calls"/> as
/// "Method(arg,arg,…)" (invariant formatting, so assertions are culture-stable) and reads return
/// small canned values. The recorder is the guard tests' oracle: a plugin guard that rejects an
/// argument must leave <see cref="Calls"/> EMPTY — proving the bad value never reached the (real)
/// bridge, where it would poke raw FL memory at a wrong address.
/// </summary>
internal sealed class FakeNativeFlControl : INativeFlControl
{
    /// <summary>Bridge calls observed, in order, as "Method(arg,arg,…)".</summary>
    public List<string> Calls { get; } = new();

    /// <summary>Canned <see cref="IsAvailableAsync"/> answer; flip to false to simulate no bridge.</summary>
    public bool Available { get; set; } = true;

    private Task Record(FormattableString call)
    {
        Calls.Add(FormattableString.Invariant(call));
        return Task.CompletedTask;
    }

    private Task<T> Record<T>(FormattableString call, T result)
    {
        Calls.Add(FormattableString.Invariant(call));
        return Task.FromResult(result);
    }

    public Task<bool> IsAvailableAsync(CancellationToken ct = default) =>
        Record($"IsAvailableAsync()", Available);

    // --- global / master ---
    public Task SetTempoAsync(double bpm, CancellationToken ct = default) => Record($"SetTempoAsync({bpm})");
    public Task<double> GetTempoAsync(CancellationToken ct = default) => Record($"GetTempoAsync()", 128.0);
    public Task SetMasterVolumeAsync(int value, CancellationToken ct = default) => Record($"SetMasterVolumeAsync({value})");
    public Task SetMasterPitchAsync(int cents, CancellationToken ct = default) => Record($"SetMasterPitchAsync({cents})");
    public Task SetShuffleAsync(int value, CancellationToken ct = default) => Record($"SetShuffleAsync({value})");

    // --- mixer ---
    public Task<int> GetMixerTrackCountAsync(CancellationToken ct = default) => Record($"GetMixerTrackCountAsync()", 127);
    public Task<int> AddMixerTrackAsync(int afterTrack = -1, CancellationToken ct = default) =>
        Record($"AddMixerTrackAsync({afterTrack})", afterTrack < 0 ? 126 : afterTrack + 1);
    public Task<string> GetMixerTrackNameAsync(int track, CancellationToken ct = default) => Record($"GetMixerTrackNameAsync({track})", track == 0 ? "Master" : $"Insert {track}");
    public Task<string> ListMixerTracksAsync(CancellationToken ct = default) => Record($"ListMixerTracksAsync()", "0: Master");
    public Task SetMixerVolumeAsync(int track, int value, CancellationToken ct = default) => Record($"SetMixerVolumeAsync({track},{value})");
    public Task SetMixerPanAsync(int track, int value, CancellationToken ct = default) => Record($"SetMixerPanAsync({track},{value})");
    public Task SetMixerTrackMutedAsync(int track, bool muted, CancellationToken ct = default) => Record($"SetMixerTrackMutedAsync({track},{muted})");
    public Task SetMixerFxParamAsync(int track, int slot, int paramIndex, long value, CancellationToken ct = default) =>
        Record($"SetMixerFxParamAsync({track},{slot},{paramIndex},{value})");

    // --- channel rack ---
    public Task SetChannelVolumeAsync(int channel, int value, CancellationToken ct = default) => Record($"SetChannelVolumeAsync({channel},{value})");
    public Task SetChannelPanAsync(int channel, int value, CancellationToken ct = default) => Record($"SetChannelPanAsync({channel},{value})");
    public Task SetChannelPitchAsync(int channel, int cents, CancellationToken ct = default) => Record($"SetChannelPitchAsync({channel},{cents})");
    public Task SetChannelMutedAsync(int channel, bool muted, CancellationToken ct = default) => Record($"SetChannelMutedAsync({channel},{muted})");
    public Task SetChannelFxRouteAsync(int channel, int mixerTrack, CancellationToken ct = default) => Record($"SetChannelFxRouteAsync({channel},{mixerTrack})");

    // --- piano roll ---
    public Task AddNoteAsync(int pattern, int channel, int key, int startTick, int lengthTick, int velocity, CancellationToken ct = default) =>
        Record($"AddNoteAsync({pattern},{channel},{key},{startTick},{lengthTick},{velocity})");
    public Task AddNotesAsync(int pattern, IReadOnlyList<NoteSpec> notes, CancellationToken ct = default) =>
        Record($"AddNotesAsync({pattern},[{notes.Count} notes])");
    public Task<int> EditNotesAsync(int pattern, IReadOnlyList<NoteEdit> edits, CancellationToken ct = default) =>
        Record($"EditNotesAsync({pattern},[{edits.Count} edits])", edits.Count);
    public Task<int> DeleteNotesAsync(int pattern, IReadOnlyList<NoteRef> targets, CancellationToken ct = default) =>
        Record($"DeleteNotesAsync({pattern},[{targets.Count} targets])", targets.Count);
    public Task<int> ClonePatternAsync(int sourcePattern, CancellationToken ct = default) =>
        Record($"ClonePatternAsync({sourcePattern})", 6);
    public Task SetPatternNameAsync(int index, string name, CancellationToken ct = default) =>
        Record($"SetPatternNameAsync({index},{name})");
    public Task SetChannelNameAsync(int index, string name, CancellationToken ct = default) =>
        Record($"SetChannelNameAsync({index},{name})");
    public Task SetChannelSoloAsync(int index, CancellationToken ct = default) =>
        Record($"SetChannelSoloAsync({index})");
    public Task SetMixerTrackNameAsync(int track, string name, CancellationToken ct = default) =>
        Record($"SetMixerTrackNameAsync({track},{name})");
    public Task SetTrackSoloAsync(int track, CancellationToken ct = default) =>
        Record($"SetTrackSoloAsync({track})");
    public Task SetLoopRegionAsync(int startTick, int endTick, CancellationToken ct = default) =>
        Record($"SetLoopRegionAsync({startTick},{endTick})");
    public Task<int> GetPpqAsync(CancellationToken ct = default) => Record($"GetPpqAsync()", 96);

    // --- patterns ---
    public Task<int> GetCurrentPatternAsync(CancellationToken ct = default) => Record($"GetCurrentPatternAsync()", 1);
    public Task SelectPatternAsync(int index, CancellationToken ct = default) => Record($"SelectPatternAsync({index})");
    public Task<int> CreatePatternAsync(CancellationToken ct = default) => Record($"CreatePatternAsync()", 5);
    public Task ClearPatternAsync(int index, CancellationToken ct = default) => Record($"ClearPatternAsync({index})");
    public Task<string> GetPatternNameAsync(int index, CancellationToken ct = default) => Record($"GetPatternNameAsync({index})", $"Pattern {index}");
    public Task<string> ListPatternsAsync(CancellationToken ct = default) => Record($"ListPatternsAsync()", "1: Intro\n2: Drop");

    // --- channel rack (read) ---
    public Task<int> GetChannelCountAsync(CancellationToken ct = default) => Record($"GetChannelCountAsync()", 2);
    public Task SelectChannelAsync(int index, CancellationToken ct = default) => Record($"SelectChannelAsync({index})");
    public Task<string> GetChannelNameAsync(int index, CancellationToken ct = default) => Record($"GetChannelNameAsync({index})", $"Channel {index}");
    public Task<string> ListChannelsAsync(CancellationToken ct = default) => Record($"ListChannelsAsync()", "0: Kick\n1: Snare");

    // --- mixer sends / EQ ---
    public Task SetMixerSendAsync(int srcTrack, int dstTrack, double level, CancellationToken ct = default) =>
        Record($"SetMixerSendAsync({srcTrack},{dstTrack},{level})");
    public Task SetMixerEqGainAsync(int track, int band, int value, CancellationToken ct = default) =>
        Record($"SetMixerEqGainAsync({track},{band},{value})");

    // --- transport ---
    public Task TransportPlayAsync(CancellationToken ct = default) => Record($"TransportPlayAsync()");
    public Task TransportStopAsync(CancellationToken ct = default) => Record($"TransportStopAsync()");
    public Task TransportToggleRecordAsync(CancellationToken ct = default) => Record($"TransportToggleRecordAsync()");

    // --- plugins / inserts ---
    public Task<string> ListAvailablePluginsAsync(bool effects, CancellationToken ct = default) =>
        Record($"ListAvailablePluginsAsync({effects})", effects ? "Fruity Reeverb 2\nFruity Limiter" : "Sytrus\nFLEX");
    public Task<string> GetChannelPluginAsync(int channel, CancellationToken ct = default) =>
        Record($"GetChannelPluginAsync({channel})", $"channel {channel}: Sytrus");
    public Task<int> AddChannelAsync(string pluginName, CancellationToken ct = default) => Record($"AddChannelAsync({pluginName})", 3);
    public Task<string> ListMixerEffectsAsync(int track, CancellationToken ct = default) =>
        Record($"ListMixerEffectsAsync({track})", "slot 0: Fruity Reeverb 2");
    public Task AddMixerEffectAsync(int track, int slot, string pluginName, CancellationToken ct = default) =>
        Record($"AddMixerEffectAsync({track},{slot},{pluginName})");
    public Task RemoveMixerEffectAsync(int track, int slot, CancellationToken ct = default) => Record($"RemoveMixerEffectAsync({track},{slot})");
    public Task CloneMixerEffectAsync(int track, int fromSlot, int toSlot, CancellationToken ct = default) =>
        Record($"CloneMixerEffectAsync({track},{fromSlot},{toSlot})");

    public Task<string> ListPluginParamsAsync(int channelOrTrack, int slot, string? filter, CancellationToken ct = default) =>
        Record($"ListPluginParamsAsync({channelOrTrack},{slot},{filter})", "0: Cutoff = 0.5");
    public Task SetPluginParamAsync(int channelOrTrack, int slot, int paramIndex, double value, CancellationToken ct = default) =>
        Record($"SetPluginParamAsync({channelOrTrack},{slot},{paramIndex},{value})");

    // --- samples ---
    public Task<string> ListSamplesAsync(string? filter, CancellationToken ct = default) =>
        Record($"ListSamplesAsync({filter})", "[Packs] Drums/Kick 01.wav");
    public Task<int> AddSampleChannelAsync(string samplePath, CancellationToken ct = default) => Record($"AddSampleChannelAsync({samplePath})", 4);
    public Task ReplaceChannelSampleAsync(int channel, string samplePath, CancellationToken ct = default) =>
        Record($"ReplaceChannelSampleAsync({channel},{samplePath})");

    // --- notes (read) ---
    public Task<string> GetNotesAsync(int pattern, int channel, int offset = 0, CancellationToken ct = default) =>
        Record($"GetNotesAsync({pattern},{channel},{offset})", "chan 0 key 60 @0 len 96 vel 100");

    // --- playlist tracks ---
    public Task<string> ListPlaylistTracksAsync(CancellationToken ct = default) => Record($"ListPlaylistTracksAsync()", "1: Drums");
    public Task SetTrackNameAsync(int track, string name, CancellationToken ct = default) => Record($"SetTrackNameAsync({track},{name})");
    public Task SetTrackColorAsync(int track, int rgb, CancellationToken ct = default) => Record($"SetTrackColorAsync({track},{rgb})");
    public Task SetTrackMuteAsync(int track, bool muted, CancellationToken ct = default) => Record($"SetTrackMuteAsync({track},{muted})");
    public Task SetTrackCollapsedAsync(int track, bool collapsed, CancellationToken ct = default) => Record($"SetTrackCollapsedAsync({track},{collapsed})");
    public Task SelectTrackAsync(int track, CancellationToken ct = default) => Record($"SelectTrackAsync({track})");

    // --- playlist clips (model-backed so add/delete/duplicate/mute round-trip; identity = pattern+track+start) ---
    private sealed class FakeClip { public int Track, Start, Length, Pattern, Channel; public bool Muted; }
    private readonly List<FakeClip> _clips = new()
    {
        new FakeClip { Track = 1, Start = 0,   Length = 384, Pattern = 1 },
        new FakeClip { Track = 2, Start = 768, Length = 384, Pattern = 2 },
    };

    /// <summary>Stage an extra clip (tests use this for muted / audio (pattern &lt; 0) clips before an op).</summary>
    public void SeedClip(int track, int start, int length, int pattern, bool muted = false, int channel = 0) =>
        _clips.Add(new FakeClip { Track = track, Start = start, Length = length, Pattern = pattern, Muted = muted, Channel = channel });

    public Task<string> ListClipsAsync(int offset = 0, int track = -1, CancellationToken ct = default)
    {
        Calls.Add(FormattableString.Invariant($"ListClipsAsync({offset},{track})"));
        var sb = new StringBuilder();
        int matched = 0, shown = 0;
        for (int i = 0; i < _clips.Count; i++)
        {
            var c = _clips[i];
            if (track > 0 && c.Track != track) continue;
            matched++;
            if (matched <= offset) continue;
            string src = c.Pattern >= 0 ? $"pattern {c.Pattern}" : $"channel {c.Channel}";
            sb.Append(FormattableString.Invariant($"[{i}] track {c.Track} start={c.Start} len={c.Length} {src}{(c.Muted ? " muted" : "")}\n"));
            shown++;
        }
        return Task.FromResult(shown == 0 ? "(no clips)" : $"{shown} clips:\n" + sb.ToString().TrimEnd());
    }

    public Task AddPatternClipAsync(int pattern, int track, int startTick, int lengthTick, CancellationToken ct = default)
    {
        Calls.Add(FormattableString.Invariant($"AddPatternClipAsync({pattern},{track},{startTick},{lengthTick})"));
        _clips.Add(new FakeClip { Track = track, Start = startTick, Length = lengthTick, Pattern = pattern });
        return Task.CompletedTask;
    }
    public Task MoveClipAsync(int clipIndex, int startTick, int track, CancellationToken ct = default) =>
        Record($"MoveClipAsync({clipIndex},{startTick},{track})");
    public Task ResizeClipAsync(int clipIndex, int lengthTick, CancellationToken ct = default) => Record($"ResizeClipAsync({clipIndex},{lengthTick})");
    public Task DeleteClipAsync(int clipIndex, CancellationToken ct = default) => DeleteClipsAsync(new[] { clipIndex }, ct);
    public Task SetClipMutedAsync(int clipIndex, bool muted, CancellationToken ct = default)
    {
        Calls.Add(FormattableString.Invariant($"SetClipMutedAsync({clipIndex},{muted})"));
        if (clipIndex >= 0 && clipIndex < _clips.Count) _clips[clipIndex].Muted = muted;
        return Task.CompletedTask;
    }
    public Task<bool> GetClipMutedAsync(int clipIndex, CancellationToken ct = default)
    {
        Calls.Add(FormattableString.Invariant($"GetClipMutedAsync({clipIndex})"));
        return Task.FromResult(clipIndex >= 0 && clipIndex < _clips.Count && _clips[clipIndex].Muted);
    }
    public Task SliceClipAsync(int clipIndex, int tick, CancellationToken ct = default) => Record($"SliceClipAsync({clipIndex},{tick})");
    public Task DuplicateClipAsync(int clipIndex, CancellationToken ct = default)
    {
        Calls.Add(FormattableString.Invariant($"DuplicateClipAsync({clipIndex})"));
        if (clipIndex >= 0 && clipIndex < _clips.Count)
        {
            var s = _clips[clipIndex];
            // FL's duplicate copies right after the source on the same track (mute state included).
            _clips.Insert(clipIndex + 1, new FakeClip
            { Track = s.Track, Start = s.Start + s.Length, Length = s.Length, Pattern = s.Pattern, Channel = s.Channel, Muted = s.Muted });
        }
        return Task.CompletedTask;
    }

    // --- playlist clips (bulk) ---
    public Task DeleteClipsAsync(IReadOnlyList<int> clipIndices, CancellationToken ct = default)
    {
        Calls.Add(FormattableString.Invariant($"DeleteClipsAsync([{string.Join(",", clipIndices)}])"));
        foreach (int idx in clipIndices.Distinct().OrderByDescending(i => i))
            if (idx >= 0 && idx < _clips.Count) _clips.RemoveAt(idx);
        return Task.CompletedTask;
    }
    public Task MoveClipsAsync(IReadOnlyList<ClipMove> moves, CancellationToken ct = default) =>
        Record($"MoveClipsAsync([{moves.Count} moves])");
    public Task AddPatternClipsAsync(IReadOnlyList<PatternClipSpec> clips, CancellationToken ct = default)
    {
        Calls.Add(FormattableString.Invariant($"AddPatternClipsAsync([{clips.Count} clips])"));
        foreach (var s in clips)
            _clips.Add(new FakeClip { Track = s.Track, Start = s.StartTick, Length = s.LengthTick, Pattern = s.Pattern });
        return Task.CompletedTask;
    }
    public Task ResizeClipsAsync(IReadOnlyList<ClipResize> resizes, CancellationToken ct = default) =>
        Record($"ResizeClipsAsync([{resizes.Count} resizes])");
    public Task SetClipsMutedAsync(IReadOnlyList<int> clipIndices, bool muted, CancellationToken ct = default)
    {
        Calls.Add(FormattableString.Invariant($"SetClipsMutedAsync([{string.Join(",", clipIndices)}],{muted})"));
        foreach (int idx in clipIndices) if (idx >= 0 && idx < _clips.Count) _clips[idx].Muted = muted;
        return Task.CompletedTask;
    }

    // --- song / transport state ---
    public Task<string> GetSongStateAsync(CancellationToken ct = default) => Record($"GetSongStateAsync()", "stopped @0, pattern mode");
    public Task<string> GetStatusAsync(CancellationToken ct = default) => Record($"GetStatusAsync()", string.Empty);
    public Task SetSongModeAsync(bool song, CancellationToken ct = default) => Record($"SetSongModeAsync({song})");
    public Task SeekAsync(int tick, CancellationToken ct = default) => Record($"SeekAsync({tick})");
    public Task<string> ListMarkersAsync(CancellationToken ct = default) => Record($"ListMarkersAsync()", "(no markers)");
    public Task AddMarkerAsync(int tick, string name, CancellationToken ct = default) => Record($"AddMarkerAsync({tick},{name})");

    // --- project lifecycle ---
    public Task OpenProjectAsync(string path, CancellationToken ct = default) => Record($"OpenProjectAsync({path})");
    public Task SaveProjectAsync(string path, CancellationToken ct = default) => Record($"SaveProjectAsync({path})");
    public Task NewProjectAsync(CancellationToken ct = default) => Record($"NewProjectAsync()");
    public Task<string> GetProjectInfoAsync(CancellationToken ct = default) => Record($"GetProjectInfoAsync()", "Untitled (never saved)");
    public Task SaveProjectAsAsync(string path, CancellationToken ct = default) => Record($"SaveProjectAsAsync({path})");
    public Task SaveCopyAsync(string path, CancellationToken ct = default) => Record($"SaveCopyAsync({path})");
    public Task SaveNewVersionAsync(CancellationToken ct = default) => Record($"SaveNewVersionAsync()");
    public Task<string> ListRecentProjectsAsync(CancellationToken ct = default) => Record($"ListRecentProjectsAsync()", "C:\\songs\\demo.flp");

    // --- arrangements ---
    public Task<string> ListArrangementsAsync(CancellationToken ct = default) => Record($"ListArrangementsAsync()", "* [0] Arrangement");
    public Task<int> AddArrangementAsync(string? name, CancellationToken ct = default) => Record($"AddArrangementAsync({name})", 1);
    public Task<int> CloneArrangementAsync(int srcIdx, string? name, CancellationToken ct = default) =>
        Record($"CloneArrangementAsync({srcIdx},{name})", 2);
    public Task RenameArrangementAsync(int idx, string name, CancellationToken ct = default) => Record($"RenameArrangementAsync({idx},{name})");
    public Task DeleteArrangementAsync(int idx, CancellationToken ct = default) => Record($"DeleteArrangementAsync({idx})");
    public Task SelectArrangementAsync(int idx, CancellationToken ct = default) => Record($"SelectArrangementAsync({idx})");

    // --- automation clips (model-backed, real ListAutomationPoints format so identity parsing is realistic) ---
    private sealed class FakePoint { public double Time, Value, Tension; public int Curve; }
    private readonly Dictionary<int, List<FakePoint>> _autos = new();

    /// <summary>Seed an automation point (delete-capture reads it; a non-zero curve forces a taint/.flp).</summary>
    public void SeedAutomationPoint(int channel, double time, double value, double tension = 0, int curve = 0)
    {
        if (!_autos.TryGetValue(channel, out var list)) _autos[channel] = list = new();
        list.Add(new FakePoint { Time = time, Value = value, Tension = tension, Curve = curve });
        list.Sort((a, b) => a.Time.CompareTo(b.Time));
    }

    public Task<FlAutomationClipResult> CreateAutomationClipAsync(FlAutomationTarget target, int track, int startTick, int lengthTick, string? name = null, CancellationToken ct = default) =>
        Record($"CreateAutomationClipAsync({target},{track},{startTick},{lengthTick},{name})", new FlAutomationClipResult(3, 2));

    public Task<int> AddAutomationClipAsync(int channel, int track, int startTick, int lengthTick, CancellationToken ct = default) =>
        Record($"AddAutomationClipAsync({channel},{track},{startTick},{lengthTick})", 2);

    public Task SetAutomationPointsAsync(int channel, IReadOnlyList<FlAutomationPointSpec> points, CancellationToken ct = default)
    {
        Calls.Add(FormattableString.Invariant($"SetAutomationPointsAsync({channel},[{points.Count} points])"));
        _autos[channel] = points.Select(p => new FakePoint { Time = p.TimeBeats, Value = p.Value, Tension = p.Tension, Curve = p.Curve }).ToList();
        return Task.CompletedTask;
    }

    public Task<string> ListAutomationPointsAsync(int channel, CancellationToken ct = default)
    {
        Calls.Add(FormattableString.Invariant($"ListAutomationPointsAsync({channel})"));
        if (!_autos.TryGetValue(channel, out var list) || list.Count == 0) return Task.FromResult("(no points)");
        var sb = new StringBuilder(FormattableString.Invariant($"{list.Count} points (time in beats):\n"));
        for (int i = 0; i < list.Count; i++)
            sb.Append(FormattableString.Invariant(
                $"  [{i}] t={list[i].Time:0.###} value={list[i].Value:0.###} tension={list[i].Tension:0.###} curve={list[i].Curve}\n"));
        return Task.FromResult(sb.ToString().TrimEnd());
    }

    public Task AddAutomationPointAsync(int channel, double timeBeats, double value, double tension, CancellationToken ct = default)
    {
        Calls.Add(FormattableString.Invariant($"AddAutomationPointAsync({channel},{timeBeats},{value},{tension})"));
        SeedAutomationPoint(channel, timeBeats, value, tension, 0);   // AddAutomationPoint always writes linear (curve 0)
        return Task.CompletedTask;
    }

    public Task DeleteAutomationPointAsync(int channel, int index, CancellationToken ct = default)
    {
        Calls.Add(FormattableString.Invariant($"DeleteAutomationPointAsync({channel},{index})"));
        if (_autos.TryGetValue(channel, out var list) && index >= 0 && index < list.Count) list.RemoveAt(index);
        return Task.CompletedTask;
    }

    // --- symmetric getters (inverse-journal read-before-write) ---
    public Task<int> GetMasterVolumeAsync(CancellationToken ct = default) => Record($"GetMasterVolumeAsync()", 7624);
    public Task<int> GetMasterPitchAsync(CancellationToken ct = default) => Record($"GetMasterPitchAsync()", -100);
    public Task<int> GetShuffleAsync(CancellationToken ct = default) => Record($"GetShuffleAsync()", 64);
    public Task<long> GetMixerVolumeAsync(int track, CancellationToken ct = default) => Record($"GetMixerVolumeAsync({track})", 10000L);
    public Task<int> GetMixerPanAsync(int track, CancellationToken ct = default) => Record($"GetMixerPanAsync({track})", 6400);
    public Task<bool> GetMixerTrackMutedAsync(int track, CancellationToken ct = default) => Record($"GetMixerTrackMutedAsync({track})", false);
    public Task<long> GetChannelVolumeAsync(int channel, CancellationToken ct = default) => Record($"GetChannelVolumeAsync({channel})", 10000L);
    public Task<int> GetChannelPanAsync(int channel, CancellationToken ct = default) => Record($"GetChannelPanAsync({channel})", 6400);
    public Task<int> GetChannelPitchAsync(int channel, CancellationToken ct = default) => Record($"GetChannelPitchAsync({channel})", -1200);
    public Task<bool> GetChannelMutedAsync(int channel, CancellationToken ct = default) => Record($"GetChannelMutedAsync({channel})", false);
    public Task<int> GetChannelFxRouteAsync(int channel, CancellationToken ct = default) => Record($"GetChannelFxRouteAsync({channel})", 3);
    public Task<string> GetTrackNameAsync(int track, CancellationToken ct = default) => Record($"GetTrackNameAsync({track})", "OldName");
    public Task<int> GetTrackColorAsync(int track, CancellationToken ct = default) => Record($"GetTrackColorAsync({track})", 0x112233);
    public Task<bool> GetTrackMuteAsync(int track, CancellationToken ct = default) => Record($"GetTrackMuteAsync({track})", false);
    public Task<bool> GetTrackCollapsedAsync(int track, CancellationToken ct = default) => Record($"GetTrackCollapsedAsync({track})", false);
    public Task<bool> GetSongModeAsync(CancellationToken ct = default) => Record($"GetSongModeAsync()", false);
    public Task<string> GetArrangementNameAsync(int idx, CancellationToken ct = default) => Record($"GetArrangementNameAsync({idx})", "Arr");

    // --- render / export ---
    public Task OpenExportDialogAsync(int formatIndex = 0, CancellationToken ct = default) => Record($"OpenExportDialogAsync({formatIndex})");

    // --- in-FL chat tab ---
    public Task OpenChatTabAsync(CancellationToken ct = default) => Record($"OpenChatTabAsync()");
    public Task CloseChatTabAsync(CancellationToken ct = default) => Record($"CloseChatTabAsync()");
    public Task<string> ChatPollAsync(CancellationToken ct = default) => Record($"ChatPollAsync()", string.Empty);
    public Task ChatSayAsync(string text, CancellationToken ct = default) => Record($"ChatSayAsync({text})");
}
