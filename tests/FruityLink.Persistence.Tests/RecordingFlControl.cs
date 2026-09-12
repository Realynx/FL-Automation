using System.Collections.Generic;
using System.IO;
using FruityLink.Core.Abstractions;

namespace FruityLink.Persistence.Tests;

/// <summary>
/// A recording <see cref="INativeFlControl"/> for version-control tests. <see cref="SaveCopyAsync"/> actually
/// writes the backup file (so restore's <c>File.Exists</c> check passes), <see cref="OpenProjectAsync"/> is
/// recorded (to prove the <c>.flp</c> fallback), and channel volume is a settable field so the granular
/// undo/redo replay can be observed writing the OLD/NEW value back.
/// </summary>
internal sealed class RecordingFlControl : INativeFlControl
{
    public List<string> Calls { get; } = new();
    public bool Available { get; set; } = true;
    public int ChannelVolume { get; set; } = 10000;

    private Task Rec(string call) { Calls.Add(call); return Task.CompletedTask; }
    private Task<T> Rec<T>(string call, T value) { Calls.Add(call); return Task.FromResult(value); }

    public Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.FromResult(Available);

    public Task SaveCopyAsync(string path, CancellationToken ct = default)
    {
        Calls.Add($"SaveCopyAsync({path})");
        File.WriteAllText(path, "flp");   // real file so RestoreCore's File.Exists(FlpBackupPath) passes
        return Task.CompletedTask;
    }

    public Task OpenProjectAsync(string path, CancellationToken ct = default) => Rec($"OpenProjectAsync({path})");
    public Task<string> GetProjectInfoAsync(CancellationToken ct = default) => Rec("GetProjectInfoAsync()", "Untitled (never saved)");

    // The op the granular tests exercise.
    public Task SetChannelVolumeAsync(int channel, int value, CancellationToken ct = default)
    {
        ChannelVolume = value;
        return Rec($"SetChannelVolumeAsync({channel},{value})");
    }
    public Task<long> GetChannelVolumeAsync(int channel, CancellationToken ct = default) => Task.FromResult((long)ChannelVolume);

    // ── everything else: inert defaults (not exercised by these tests) ──────────────────────────
    public Task SetTempoAsync(double bpm, CancellationToken ct = default) => Task.CompletedTask;
    public Task<double> GetTempoAsync(CancellationToken ct = default) => Task.FromResult(128.0);
    public Task SetMasterVolumeAsync(int value, CancellationToken ct = default) => Task.CompletedTask;
    public Task<int> GetMasterVolumeAsync(CancellationToken ct = default) => Task.FromResult(7624);
    public Task SetMasterPitchAsync(int cents, CancellationToken ct = default) => Task.CompletedTask;
    public Task<int> GetMasterPitchAsync(CancellationToken ct = default) => Task.FromResult(0);
    public Task SetShuffleAsync(int value, CancellationToken ct = default) => Task.CompletedTask;
    public Task<int> GetShuffleAsync(CancellationToken ct = default) => Task.FromResult(0);
    public Task SetMixerVolumeAsync(int track, int value, CancellationToken ct = default) => Task.CompletedTask;
    public Task<long> GetMixerVolumeAsync(int track, CancellationToken ct = default) => Task.FromResult(10000L);
    public Task<int> GetMixerTrackCountAsync(CancellationToken ct = default) => Task.FromResult(127);
    public Task<string> GetMixerTrackNameAsync(int track, CancellationToken ct = default) => Task.FromResult(track == 0 ? "Master" : $"Insert {track}");
    public Task<string> ListMixerTracksAsync(CancellationToken ct = default) => Task.FromResult("0: Master");
    public Task SetMixerPanAsync(int track, int value, CancellationToken ct = default) => Task.CompletedTask;
    public Task<int> GetMixerPanAsync(int track, CancellationToken ct = default) => Task.FromResult(6400);
    public Task SetMixerTrackMutedAsync(int track, bool muted, CancellationToken ct = default) => Task.CompletedTask;
    public Task<bool> GetMixerTrackMutedAsync(int track, CancellationToken ct = default) => Task.FromResult(false);
    public Task SetMixerFxParamAsync(int track, int slot, int paramIndex, long value, CancellationToken ct = default) => Task.CompletedTask;
    public Task SetChannelPanAsync(int channel, int value, CancellationToken ct = default) => Task.CompletedTask;
    public Task<int> GetChannelPanAsync(int channel, CancellationToken ct = default) => Task.FromResult(6400);
    public Task SetChannelPitchAsync(int channel, int cents, CancellationToken ct = default) => Task.CompletedTask;
    public Task<int> GetChannelPitchAsync(int channel, CancellationToken ct = default) => Task.FromResult(0);
    public Task SetChannelMutedAsync(int channel, bool muted, CancellationToken ct = default) => Task.CompletedTask;
    public Task<bool> GetChannelMutedAsync(int channel, CancellationToken ct = default) => Task.FromResult(false);
    public Task SetChannelFxRouteAsync(int channel, int mixerTrack, CancellationToken ct = default) => Task.CompletedTask;
    public Task<int> GetChannelFxRouteAsync(int channel, CancellationToken ct = default) => Task.FromResult(0);
    public Task AddNoteAsync(int pattern, int channel, int key, int startTick, int lengthTick, int velocity, CancellationToken ct = default) => Task.CompletedTask;
    public Task AddNotesAsync(int pattern, IReadOnlyList<NoteSpec> notes, CancellationToken ct = default) => Task.CompletedTask;
    public Task<int> EditNotesAsync(int pattern, IReadOnlyList<NoteEdit> edits, CancellationToken ct = default) => Task.FromResult(edits.Count);
    public Task<int> DeleteNotesAsync(int pattern, IReadOnlyList<NoteRef> targets, CancellationToken ct = default) => Task.FromResult(targets.Count);
    public Task<int> ClonePatternAsync(int sourcePattern, CancellationToken ct = default) => Task.FromResult(sourcePattern + 1);
    public Task SetPatternNameAsync(int index, string name, CancellationToken ct = default) => Task.CompletedTask;
    public Task SetChannelNameAsync(int index, string name, CancellationToken ct = default) => Task.CompletedTask;
    public Task SetChannelSoloAsync(int index, CancellationToken ct = default) => Task.CompletedTask;
    public Task SetMixerTrackNameAsync(int track, string name, CancellationToken ct = default) => Task.CompletedTask;
    public Task SetTrackSoloAsync(int track, CancellationToken ct = default) => Task.CompletedTask;
    public Task SetLoopRegionAsync(int startTick, int endTick, CancellationToken ct = default) => Task.CompletedTask;
    public Task<int> GetPpqAsync(CancellationToken ct = default) => Task.FromResult(96);
    public Task<int> GetCurrentPatternAsync(CancellationToken ct = default) => Task.FromResult(1);
    public Task SelectPatternAsync(int index, CancellationToken ct = default) => Task.CompletedTask;
    public Task<int> CreatePatternAsync(CancellationToken ct = default) => Task.FromResult(1);
    public Task ClearPatternAsync(int index, CancellationToken ct = default) => Task.CompletedTask;
    public Task<string> GetPatternNameAsync(int index, CancellationToken ct = default) => Task.FromResult("Pattern");
    public Task<string> ListPatternsAsync(CancellationToken ct = default) => Task.FromResult(string.Empty);
    public Task<int> GetChannelCountAsync(CancellationToken ct = default) => Task.FromResult(0);
    public Task SelectChannelAsync(int index, CancellationToken ct = default) => Task.CompletedTask;
    public Task<string> GetChannelNameAsync(int index, CancellationToken ct = default) => Task.FromResult("Channel");
    public Task<string> ListChannelsAsync(CancellationToken ct = default) => Task.FromResult(string.Empty);
    public Task SetMixerSendAsync(int srcTrack, int dstTrack, double level, CancellationToken ct = default) => Task.CompletedTask;
    public Task SetMixerEqGainAsync(int track, int band, int value, CancellationToken ct = default) => Task.CompletedTask;
    public Task TransportPlayAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task TransportStopAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task TransportToggleRecordAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task<string> ListAvailablePluginsAsync(bool effects, CancellationToken ct = default) => Task.FromResult(string.Empty);
    public Task<string> GetChannelPluginAsync(int channel, CancellationToken ct = default) => Task.FromResult(string.Empty);
    public Task<int> AddChannelAsync(string pluginName, CancellationToken ct = default) => Task.FromResult(0);
    public Task<string> ListMixerEffectsAsync(int track, CancellationToken ct = default) => Task.FromResult(string.Empty);
    public Task AddMixerEffectAsync(int track, int slot, string pluginName, CancellationToken ct = default) => Task.CompletedTask;
    public Task RemoveMixerEffectAsync(int track, int slot, CancellationToken ct = default) => Task.CompletedTask;
    public Task CloneMixerEffectAsync(int track, int fromSlot, int toSlot, CancellationToken ct = default) => Task.CompletedTask;
    public Task<string> ListPluginParamsAsync(int channelOrTrack, int slot, string? filter, CancellationToken ct = default) => Task.FromResult(string.Empty);
    public Task SetPluginParamAsync(int channelOrTrack, int slot, int paramIndex, double value, CancellationToken ct = default) => Task.CompletedTask;
    public Task<string> ListSamplesAsync(string? filter, CancellationToken ct = default) => Task.FromResult(string.Empty);
    public Task<int> AddSampleChannelAsync(string samplePath, CancellationToken ct = default) => Task.FromResult(0);
    public Task ReplaceChannelSampleAsync(int channel, string samplePath, CancellationToken ct = default) => Task.CompletedTask;
    public Task<string> GetNotesAsync(int pattern, int channel, int offset = 0, CancellationToken ct = default) => Task.FromResult(string.Empty);
    public Task<string> ListPlaylistTracksAsync(CancellationToken ct = default) => Task.FromResult(string.Empty);
    public Task SetTrackNameAsync(int track, string name, CancellationToken ct = default) => Task.CompletedTask;
    public Task<string> GetTrackNameAsync(int track, CancellationToken ct = default) => Task.FromResult(string.Empty);
    public Task SetTrackColorAsync(int track, int rgb, CancellationToken ct = default) => Task.CompletedTask;
    public Task<int> GetTrackColorAsync(int track, CancellationToken ct = default) => Task.FromResult(0);
    public Task SetTrackMuteAsync(int track, bool muted, CancellationToken ct = default) => Task.CompletedTask;
    public Task<bool> GetTrackMuteAsync(int track, CancellationToken ct = default) => Task.FromResult(false);
    public Task SetTrackCollapsedAsync(int track, bool collapsed, CancellationToken ct = default) => Task.CompletedTask;
    public Task<bool> GetTrackCollapsedAsync(int track, CancellationToken ct = default) => Task.FromResult(false);
    public Task SelectTrackAsync(int track, CancellationToken ct = default) => Task.CompletedTask;
    public Task<string> ListClipsAsync(int offset = 0, int track = -1, CancellationToken ct = default) => Task.FromResult(string.Empty);
    public Task AddPatternClipAsync(int pattern, int track, int startTick, int lengthTick, CancellationToken ct = default) => Task.CompletedTask;
    public Task MoveClipAsync(int clipIndex, int startTick, int track, CancellationToken ct = default) => Task.CompletedTask;
    public Task ResizeClipAsync(int clipIndex, int lengthTick, CancellationToken ct = default) => Task.CompletedTask;
    public Task DeleteClipAsync(int clipIndex, CancellationToken ct = default) => Task.CompletedTask;
    public Task SetClipMutedAsync(int clipIndex, bool muted, CancellationToken ct = default) => Task.CompletedTask;
    public Task<bool> GetClipMutedAsync(int clipIndex, CancellationToken ct = default) => Task.FromResult(false);
    public Task DeleteClipsAsync(IReadOnlyList<int> clipIndices, CancellationToken ct = default) => Task.CompletedTask;
    public Task MoveClipsAsync(IReadOnlyList<ClipMove> moves, CancellationToken ct = default) => Task.CompletedTask;
    public Task AddPatternClipsAsync(IReadOnlyList<PatternClipSpec> clips, CancellationToken ct = default) => Task.CompletedTask;
    public Task ResizeClipsAsync(IReadOnlyList<ClipResize> resizes, CancellationToken ct = default) => Task.CompletedTask;
    public Task SetClipsMutedAsync(IReadOnlyList<int> clipIndices, bool muted, CancellationToken ct = default) => Task.CompletedTask;
    public Task SliceClipAsync(int clipIndex, int tick, CancellationToken ct = default) => Task.CompletedTask;
    public Task DuplicateClipAsync(int clipIndex, CancellationToken ct = default) => Task.CompletedTask;
    public Task<string> GetSongStateAsync(CancellationToken ct = default) => Task.FromResult(string.Empty);
    public Task<string> GetStatusAsync(CancellationToken ct = default) => Task.FromResult(string.Empty);
    public Task SetSongModeAsync(bool song, CancellationToken ct = default) => Task.CompletedTask;
    public Task<bool> GetSongModeAsync(CancellationToken ct = default) => Task.FromResult(false);
    public Task SeekAsync(int tick, CancellationToken ct = default) => Task.CompletedTask;
    public Task<string> ListMarkersAsync(CancellationToken ct = default) => Task.FromResult(string.Empty);
    public Task AddMarkerAsync(int tick, string name, CancellationToken ct = default) => Task.CompletedTask;
    public Task SaveProjectAsync(string path, CancellationToken ct = default) => Task.CompletedTask;
    public Task NewProjectAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task SaveProjectAsAsync(string path, CancellationToken ct = default) => Task.CompletedTask;
    public Task SaveNewVersionAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task<string> ListRecentProjectsAsync(CancellationToken ct = default) => Task.FromResult(string.Empty);
    public Task<string> ListArrangementsAsync(CancellationToken ct = default) => Task.FromResult(string.Empty);
    public Task<int> AddArrangementAsync(string? name, CancellationToken ct = default) => Task.FromResult(0);
    public Task<int> CloneArrangementAsync(int srcIdx, string? name, CancellationToken ct = default) => Task.FromResult(0);
    public Task RenameArrangementAsync(int idx, string name, CancellationToken ct = default) => Task.CompletedTask;
    public Task<string> GetArrangementNameAsync(int idx, CancellationToken ct = default) => Task.FromResult(string.Empty);
    public Task DeleteArrangementAsync(int idx, CancellationToken ct = default) => Task.CompletedTask;
    public Task SelectArrangementAsync(int idx, CancellationToken ct = default) => Task.CompletedTask;
    public Task<string> ListAutomationPointsAsync(int channel, CancellationToken ct = default) => Task.FromResult(string.Empty);
    public Task AddAutomationPointAsync(int channel, double timeBeats, double value, double tension, CancellationToken ct = default) => Task.CompletedTask;
    public Task DeleteAutomationPointAsync(int channel, int index, CancellationToken ct = default) => Task.CompletedTask;
    public Task OpenExportDialogAsync(int formatIndex = 0, CancellationToken ct = default) => Task.CompletedTask;
    public Task OpenChatTabAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task CloseChatTabAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task<string> ChatPollAsync(CancellationToken ct = default) => Task.FromResult(string.Empty);
    public Task ChatSayAsync(string text, CancellationToken ct = default) => Task.CompletedTask;
}
