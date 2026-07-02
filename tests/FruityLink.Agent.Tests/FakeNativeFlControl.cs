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
    public Task SetMixerVolumeAsync(int track, int value, CancellationToken ct = default) => Record($"SetMixerVolumeAsync({track},{value})");
    public Task SetMixerPanAsync(int track, int value, CancellationToken ct = default) => Record($"SetMixerPanAsync({track},{value})");
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

    // --- playlist clips ---
    public Task<string> ListClipsAsync(int offset = 0, int track = -1, CancellationToken ct = default) =>
        Record($"ListClipsAsync({offset},{track})", "0: pattern 1 @0 len 384 track 1");
    public Task AddPatternClipAsync(int pattern, int track, int startTick, int lengthTick, CancellationToken ct = default) =>
        Record($"AddPatternClipAsync({pattern},{track},{startTick},{lengthTick})");
    public Task MoveClipAsync(int clipIndex, int startTick, int track, CancellationToken ct = default) =>
        Record($"MoveClipAsync({clipIndex},{startTick},{track})");
    public Task ResizeClipAsync(int clipIndex, int lengthTick, CancellationToken ct = default) => Record($"ResizeClipAsync({clipIndex},{lengthTick})");
    public Task DeleteClipAsync(int clipIndex, CancellationToken ct = default) => Record($"DeleteClipAsync({clipIndex})");
    public Task SetClipMutedAsync(int clipIndex, bool muted, CancellationToken ct = default) => Record($"SetClipMutedAsync({clipIndex},{muted})");
    public Task SliceClipAsync(int clipIndex, int tick, CancellationToken ct = default) => Record($"SliceClipAsync({clipIndex},{tick})");
    public Task DuplicateClipAsync(int clipIndex, CancellationToken ct = default) => Record($"DuplicateClipAsync({clipIndex})");

    // --- playlist clips (bulk) ---
    public Task DeleteClipsAsync(IReadOnlyList<int> clipIndices, CancellationToken ct = default) =>
        Record($"DeleteClipsAsync([{string.Join(",", clipIndices)}])");
    public Task MoveClipsAsync(IReadOnlyList<ClipMove> moves, CancellationToken ct = default) =>
        Record($"MoveClipsAsync([{moves.Count} moves])");
    public Task AddPatternClipsAsync(IReadOnlyList<PatternClipSpec> clips, CancellationToken ct = default) =>
        Record($"AddPatternClipsAsync([{clips.Count} clips])");
    public Task ResizeClipsAsync(IReadOnlyList<ClipResize> resizes, CancellationToken ct = default) =>
        Record($"ResizeClipsAsync([{resizes.Count} resizes])");
    public Task SetClipsMutedAsync(IReadOnlyList<int> clipIndices, bool muted, CancellationToken ct = default) =>
        Record($"SetClipsMutedAsync([{string.Join(",", clipIndices)}],{muted})");

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
    public Task<string> ListArrangementsAsync(CancellationToken ct = default) => Record($"ListArrangementsAsync()", "0: Arrangement *");
    public Task<int> AddArrangementAsync(string? name, CancellationToken ct = default) => Record($"AddArrangementAsync({name})", 1);
    public Task<int> CloneArrangementAsync(int srcIdx, string? name, CancellationToken ct = default) =>
        Record($"CloneArrangementAsync({srcIdx},{name})", 2);
    public Task RenameArrangementAsync(int idx, string name, CancellationToken ct = default) => Record($"RenameArrangementAsync({idx},{name})");
    public Task DeleteArrangementAsync(int idx, CancellationToken ct = default) => Record($"DeleteArrangementAsync({idx})");
    public Task SelectArrangementAsync(int idx, CancellationToken ct = default) => Record($"SelectArrangementAsync({idx})");

    // --- automation clips ---
    public Task<string> ListAutomationPointsAsync(int channel, CancellationToken ct = default) =>
        Record($"ListAutomationPointsAsync({channel})", "0: @0 beats value 0.5 tension 0");
    public Task AddAutomationPointAsync(int channel, double timeBeats, double value, double tension, CancellationToken ct = default) =>
        Record($"AddAutomationPointAsync({channel},{timeBeats},{value},{tension})");
    public Task DeleteAutomationPointAsync(int channel, int index, CancellationToken ct = default) =>
        Record($"DeleteAutomationPointAsync({channel},{index})");

    // --- render / export ---
    public Task OpenExportDialogAsync(int formatIndex = 0, CancellationToken ct = default) => Record($"OpenExportDialogAsync({formatIndex})");

    // --- in-FL chat tab ---
    public Task OpenChatTabAsync(CancellationToken ct = default) => Record($"OpenChatTabAsync()");
    public Task CloseChatTabAsync(CancellationToken ct = default) => Record($"CloseChatTabAsync()");
    public Task<string> ChatPollAsync(CancellationToken ct = default) => Record($"ChatPollAsync()", string.Empty);
    public Task ChatSayAsync(string text, CancellationToken ct = default) => Record($"ChatSayAsync({text})");
}
