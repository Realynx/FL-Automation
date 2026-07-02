namespace FruityLink.Core.Abstractions;

/// <summary>
/// Direct native control of FL Studio via the injected DLL bridge (command-bus param protocol).
/// These operations are executed natively inside the FL process and cover the full parameter
/// surface (master, mixer, channels).
/// All values use FL's native integer scales (documented per method). Implementations are
/// best-effort and throw if the bridge isn't injected; callers surface failures to the user.
/// </summary>
public interface INativeFlControl
{
    /// <summary>True if the injected bridge is loaded in FL and responding.</summary>
    Task<bool> IsAvailableAsync(CancellationToken ct = default);

    // --- global / master (live-verified) ---
    Task SetTempoAsync(double bpm, CancellationToken ct = default);
    Task<double> GetTempoAsync(CancellationToken ct = default);
    /// <summary>Master volume, 0..12800 (≈7624 ≈ 0 dB).</summary>
    Task SetMasterVolumeAsync(int value, CancellationToken ct = default);
    /// <summary>Master pitch in cents, -1200..+1200.</summary>
    Task SetMasterPitchAsync(int cents, CancellationToken ct = default);
    /// <summary>Global shuffle/swing, 0..128.</summary>
    Task SetShuffleAsync(int value, CancellationToken ct = default);

    // --- mixer (live-verified track volume; same protocol for pan/FX) ---
    /// <summary>Mixer track volume 0..12800 (track 0 = master).</summary>
    Task SetMixerVolumeAsync(int track, int value, CancellationToken ct = default);
    /// <summary>Mixer track pan 0..12800 (6400 = center).</summary>
    Task SetMixerPanAsync(int track, int value, CancellationToken ct = default);
    /// <summary>A mixer FX-slot plugin parameter (normalized fixed-point value).</summary>
    Task SetMixerFxParamAsync(int track, int slot, int paramIndex, long value, CancellationToken ct = default);

    // --- channel rack (live-verified) ---
    /// <summary>Channel volume 0..12800 (10000 = default 78%).</summary>
    Task SetChannelVolumeAsync(int channel, int value, CancellationToken ct = default);
    /// <summary>Channel pan 0..12800 (6400 = center).</summary>
    Task SetChannelPanAsync(int channel, int value, CancellationToken ct = default);
    /// <summary>Channel pitch in cents (0 = center).</summary>
    Task SetChannelPitchAsync(int channel, int cents, CancellationToken ct = default);
    /// <summary>Mute/unmute a channel.</summary>
    Task SetChannelMutedAsync(int channel, bool muted, CancellationToken ct = default);
    /// <summary>Route a channel to a mixer track (0..125).</summary>
    Task SetChannelFxRouteAsync(int channel, int mixerTrack, CancellationToken ct = default);

    // --- piano roll (current pattern) ---
    /// <summary>Add a note to a pattern's piano roll for a channel (pattern: 1-based, or &lt;=0 = current).
    /// key = MIDI 0..131 (60 = middle C), startTick/lengthTick in PPQ ticks, velocity 0..127.</summary>
    Task AddNoteAsync(int pattern, int channel, int key, int startTick, int lengthTick, int velocity, CancellationToken ct = default);

    /// <summary>Add many notes to a pattern's piano roll in one batch — resolves the pattern and refreshes
    /// the editor once for the whole set, far faster than repeated <see cref="AddNoteAsync"/>. Each note
    /// carries its own channel, so a single call can author chords, melodies, or multi-channel drum grids.</summary>
    Task AddNotesAsync(int pattern, IReadOnlyList<NoteSpec> notes, CancellationToken ct = default);

    /// <summary>Project timebase: ticks per quarter note (PPQ).</summary>
    Task<int> GetPpqAsync(CancellationToken ct = default);

    // --- patterns ---
    Task<int> GetCurrentPatternAsync(CancellationToken ct = default);
    Task SelectPatternAsync(int index, CancellationToken ct = default);
    /// <summary>Selects the first empty pattern; returns its index.</summary>
    Task<int> CreatePatternAsync(CancellationToken ct = default);
    Task ClearPatternAsync(int index, CancellationToken ct = default);
    Task<string> GetPatternNameAsync(int index, CancellationToken ct = default);
    Task<string> ListPatternsAsync(CancellationToken ct = default);

    // --- channel rack ---
    Task<int> GetChannelCountAsync(CancellationToken ct = default);
    /// <summary>Exclusively select a channel (so the piano roll edits it).</summary>
    Task SelectChannelAsync(int index, CancellationToken ct = default);
    Task<string> GetChannelNameAsync(int index, CancellationToken ct = default);
    Task<string> ListChannelsAsync(CancellationToken ct = default);

    // --- mixer sends / EQ ---
    /// <summary>Set a mixer send srcTrack-&gt;dstTrack at level (1.0 ≈ unity).</summary>
    Task SetMixerSendAsync(int srcTrack, int dstTrack, double level, CancellationToken ct = default);
    /// <summary>Mixer track EQ band gain (band 0=low,1=mid,2=high; value 0..0x40000000, ~0x20000000 = 0 dB).</summary>
    Task SetMixerEqGainAsync(int track, int band, int value, CancellationToken ct = default);

    // --- transport ---
    Task TransportPlayAsync(CancellationToken ct = default);
    Task TransportStopAsync(CancellationToken ct = default);
    Task TransportToggleRecordAsync(CancellationToken ct = default);

    // --- plugins / inserts ---
    /// <summary>List installed plugins of a kind (effects=true → mixer effects, false → channel generators).</summary>
    Task<string> ListAvailablePluginsAsync(bool effects, CancellationToken ct = default);
    /// <summary>Describe a channel's loaded generator plugin.</summary>
    Task<string> GetChannelPluginAsync(int channel, CancellationToken ct = default);
    /// <summary>Add a new channel hosting the named generator plugin; returns its index.</summary>
    Task<int> AddChannelAsync(string pluginName, CancellationToken ct = default);
    /// <summary>List the effects loaded in a mixer track's FX slots.</summary>
    Task<string> ListMixerEffectsAsync(int track, CancellationToken ct = default);
    /// <summary>Load/replace the named effect into a mixer track's FX slot (0-9).</summary>
    Task AddMixerEffectAsync(int track, int slot, string pluginName, CancellationToken ct = default);
    /// <summary>Clear a mixer track's FX slot.</summary>
    Task RemoveMixerEffectAsync(int track, int slot, CancellationToken ct = default);
    /// <summary>Copy the effect type from one FX slot to another (type only, not parameter state).</summary>
    Task CloneMixerEffectAsync(int track, int fromSlot, int toSlot, CancellationToken ct = default);

    /// <summary>List a plugin's parameters ("index: name"). slot &lt; 0 = channel generator; else mixer track+slot. Optional name filter.</summary>
    Task<string> ListPluginParamsAsync(int channelOrTrack, int slot, string? filter, CancellationToken ct = default);
    /// <summary>Set a plugin parameter to a normalized value 0..1. slot &lt; 0 = channel generator; else mixer track+slot.</summary>
    Task SetPluginParamAsync(int channelOrTrack, int slot, int paramIndex, double value, CancellationToken ct = default);

    // --- samples ---
    /// <summary>List available audio samples (factory packs + user content), optionally filtered by name.</summary>
    Task<string> ListSamplesAsync(string? filter, CancellationToken ct = default);
    /// <summary>Add a new channel that plays the given audio sample file (drum/one-shot/loop); returns its index.</summary>
    Task<int> AddSampleChannelAsync(string samplePath, CancellationToken ct = default);
    /// <summary>Replace an existing channel's sample with a new audio file.</summary>
    Task ReplaceChannelSampleAsync(int channel, string samplePath, CancellationToken ct = default);

    // --- notes (read) ---
    /// <summary>Read piano-roll notes of a pattern (1-based, or &lt;=0 = current); channel&lt;0 = all.</summary>
    Task<string> GetNotesAsync(int pattern, int channel, CancellationToken ct = default);

    // --- playlist tracks ---
    Task<string> ListPlaylistTracksAsync(CancellationToken ct = default);
    Task SetTrackNameAsync(int track, string name, CancellationToken ct = default);
    Task SetTrackColorAsync(int track, int rgb, CancellationToken ct = default);
    Task SetTrackMuteAsync(int track, bool muted, CancellationToken ct = default);
    Task SetTrackCollapsedAsync(int track, bool collapsed, CancellationToken ct = default);
    Task SelectTrackAsync(int track, CancellationToken ct = default);

    // --- playlist clips (arrangement) ---
    Task<string> ListClipsAsync(CancellationToken ct = default);
    /// <summary>Add a pattern clip (pattern 1-based, matching notes/patterns; 0 or out-of-range throws) to a track at startTick; lengthTick&lt;=0 = pattern length.</summary>
    Task AddPatternClipAsync(int pattern, int track, int startTick, int lengthTick, CancellationToken ct = default);
    Task MoveClipAsync(int clipIndex, int startTick, int track, CancellationToken ct = default);
    Task ResizeClipAsync(int clipIndex, int lengthTick, CancellationToken ct = default);
    Task DeleteClipAsync(int clipIndex, CancellationToken ct = default);
    /// <summary>Mute/unmute a playlist clip.</summary>
    Task SetClipMutedAsync(int clipIndex, bool muted, CancellationToken ct = default);
    /// <summary>Slice/chop a clip into two at an absolute tick (audio stays continuous).</summary>
    Task SliceClipAsync(int clipIndex, int tick, CancellationToken ct = default);
    /// <summary>Duplicate a clip right after itself on the same track.</summary>
    Task DuplicateClipAsync(int clipIndex, CancellationToken ct = default);

    // --- song / transport state ---
    Task<string> GetSongStateAsync(CancellationToken ct = default);
    /// <summary>
    /// FL's current status/hint bar text (the name/tooltip of whatever is under the mouse + current-operation
    /// messages, e.g. "Opening: Fruity Wrapper" at load), cleaned of FL's internal "tooltip|status" split and
    /// '^' markup. Empty when there is no active hint. Read-only; safe to poll.
    /// </summary>
    Task<string> GetStatusAsync(CancellationToken ct = default);
    Task SetSongModeAsync(bool song, CancellationToken ct = default);
    /// <summary>Move the song playhead to an absolute tick (PPQ).</summary>
    Task SeekAsync(int tick, CancellationToken ct = default);
    Task<string> ListMarkersAsync(CancellationToken ct = default);
    Task AddMarkerAsync(int tick, string name, CancellationToken ct = default);

    // --- project lifecycle ---
    Task OpenProjectAsync(string path, CancellationToken ct = default);
    Task SaveProjectAsync(string path, CancellationToken ct = default);
    Task NewProjectAsync(CancellationToken ct = default);
    Task<string> GetProjectInfoAsync(CancellationToken ct = default);
    /// <summary>Save As: write to a new path and make it the current project (updates title + recent files).</summary>
    Task SaveProjectAsAsync(string path, CancellationToken ct = default);
    /// <summary>Save a copy to a path without changing the current project.</summary>
    Task SaveCopyAsync(string path, CancellationToken ct = default);
    /// <summary>Save an auto-incremented new version and make it current.</summary>
    Task SaveNewVersionAsync(CancellationToken ct = default);
    Task<string> ListRecentProjectsAsync(CancellationToken ct = default);

    // --- arrangements ---
    Task<string> ListArrangementsAsync(CancellationToken ct = default);
    /// <summary>Add a new (empty) arrangement and switch to it; returns its index.</summary>
    Task<int> AddArrangementAsync(string? name, CancellationToken ct = default);
    /// <summary>Clone an arrangement (deep copy incl. clips); srcIdx&lt;0 = current. Returns the new index.</summary>
    Task<int> CloneArrangementAsync(int srcIdx, string? name, CancellationToken ct = default);
    Task RenameArrangementAsync(int idx, string name, CancellationToken ct = default);
    Task DeleteArrangementAsync(int idx, CancellationToken ct = default);
    Task SelectArrangementAsync(int idx, CancellationToken ct = default);

    // --- automation clips (channel must host the Automation Clip generator) ---
    Task<string> ListAutomationPointsAsync(int channel, CancellationToken ct = default);
    /// <summary>Add an automation point: time in beats, value 0..1, tension -1..1 (inserts in time order).</summary>
    Task AddAutomationPointAsync(int channel, double timeBeats, double value, double tension, CancellationToken ct = default);
    Task DeleteAutomationPointAsync(int channel, int index, CancellationToken ct = default);

    // --- render / export ---
    /// <summary>Opens FL's audio Export dialog for the user to finish (format/path/Render).</summary>
    Task OpenExportDialogAsync(int formatIndex = 0, CancellationToken ct = default);

    // --- in-FL chat tab ---
    /// <summary>Open (or focus) the native "FruityLink AI" chat tab in FL's browser.</summary>
    Task OpenChatTabAsync(CancellationToken ct = default);
    /// <summary>Hide the chat tab and restore the browser content hook.</summary>
    Task CloseChatTabAsync(CancellationToken ct = default);
    /// <summary>Return + clear the user's submitted chat message (empty if none pending).</summary>
    Task<string> ChatPollAsync(CancellationToken ct = default);
    /// <summary>Append a line to the chat display (runs on FL's main thread).</summary>
    Task ChatSayAsync(string text, CancellationToken ct = default);
}

/// <summary>One piano-roll note for batch authoring. key = MIDI 0..131 (60 = middle C),
/// startTick/lengthTick in PPQ ticks, velocity 0..127.</summary>
public readonly record struct NoteSpec(int Channel, int Key, int StartTick, int LengthTick, int Velocity);
