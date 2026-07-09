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
    /// <summary>Read master volume, 0..12800 (symmetric with <see cref="SetMasterVolumeAsync"/>).</summary>
    Task<int> GetMasterVolumeAsync(CancellationToken ct = default);
    /// <summary>Master pitch in cents, -1200..+1200.</summary>
    Task SetMasterPitchAsync(int cents, CancellationToken ct = default);
    /// <summary>Read master pitch in cents (symmetric with <see cref="SetMasterPitchAsync"/>).</summary>
    Task<int> GetMasterPitchAsync(CancellationToken ct = default);
    /// <summary>Global shuffle/swing, 0..128.</summary>
    Task SetShuffleAsync(int value, CancellationToken ct = default);
    /// <summary>Read global shuffle/swing, 0..128 (symmetric with <see cref="SetShuffleAsync"/>).</summary>
    Task<int> GetShuffleAsync(CancellationToken ct = default);

    // --- mixer (live-verified track volume; same protocol for pan/FX) ---
    /// <summary>Mixer track volume 0..12800 (track 0 = master).</summary>
    Task SetMixerVolumeAsync(int track, int value, CancellationToken ct = default);
    /// <summary>Read a mixer track volume 0..12800 (symmetric with <see cref="SetMixerVolumeAsync"/>).</summary>
    Task<long> GetMixerVolumeAsync(int track, CancellationToken ct = default);
    /// <summary>Mixer track pan 0..12800 (6400 = center).</summary>
    Task SetMixerPanAsync(int track, int value, CancellationToken ct = default);
    /// <summary>Read a mixer track pan 0..12800 (symmetric with <see cref="SetMixerPanAsync"/>).</summary>
    Task<int> GetMixerPanAsync(int track, CancellationToken ct = default);
    /// <summary>Mute/unmute a mixer track (the enabled flag; solo state untouched).</summary>
    Task SetMixerTrackMutedAsync(int track, bool muted, CancellationToken ct = default);
    /// <summary>Read a mixer track's mute state (symmetric with <see cref="SetMixerTrackMutedAsync"/>).</summary>
    Task<bool> GetMixerTrackMutedAsync(int track, CancellationToken ct = default);
    /// <summary>A mixer FX-slot plugin parameter (normalized fixed-point value).</summary>
    Task SetMixerFxParamAsync(int track, int slot, int paramIndex, long value, CancellationToken ct = default);

    // --- channel rack (live-verified) ---
    /// <summary>Channel volume 0..12800 (10000 = default 78%).</summary>
    Task SetChannelVolumeAsync(int channel, int value, CancellationToken ct = default);
    /// <summary>Read channel volume 0..12800 (symmetric with <see cref="SetChannelVolumeAsync"/>).</summary>
    Task<long> GetChannelVolumeAsync(int channel, CancellationToken ct = default);
    /// <summary>Channel pan 0..12800 (6400 = center).</summary>
    Task SetChannelPanAsync(int channel, int value, CancellationToken ct = default);
    /// <summary>Read channel pan 0..12800 (symmetric with <see cref="SetChannelPanAsync"/>).</summary>
    Task<int> GetChannelPanAsync(int channel, CancellationToken ct = default);
    /// <summary>Channel pitch in cents (0 = center).</summary>
    Task SetChannelPitchAsync(int channel, int cents, CancellationToken ct = default);
    /// <summary>Read channel pitch in cents (symmetric with <see cref="SetChannelPitchAsync"/>).</summary>
    Task<int> GetChannelPitchAsync(int channel, CancellationToken ct = default);
    /// <summary>Mute/unmute a channel.</summary>
    Task SetChannelMutedAsync(int channel, bool muted, CancellationToken ct = default);
    /// <summary>Read a channel's mute state (symmetric with <see cref="SetChannelMutedAsync"/>).</summary>
    Task<bool> GetChannelMutedAsync(int channel, CancellationToken ct = default);
    /// <summary>Route a channel to a mixer track (0..125).</summary>
    Task SetChannelFxRouteAsync(int channel, int mixerTrack, CancellationToken ct = default);
    /// <summary>Read a channel's mixer-track route (symmetric with <see cref="SetChannelFxRouteAsync"/>).</summary>
    Task<int> GetChannelFxRouteAsync(int channel, CancellationToken ct = default);

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
    /// <summary>Rename a channel (persists across save/reload) so the model's own name→index lookups keep
    /// working on channels it created.</summary>
    Task SetChannelNameAsync(int index, string name, CancellationToken ct = default);
    /// <summary>Toggle exclusive SOLO on a channel (solo again = un-solo) — hear one part without muting
    /// every other channel by hand.</summary>
    Task SetChannelSoloAsync(int index, CancellationToken ct = default);

    // --- mixer tracks (identity: resolve a bus/track NAME to its index) ---
    /// <summary>Number of mixer tracks (127 at rest: master + 125 inserts + current).</summary>
    Task<int> GetMixerTrackCountAsync(CancellationToken ct = default);
    /// <summary>Effective mixer track name (custom if set, else default by type: Master/Insert n/Current).</summary>
    Task<string> GetMixerTrackNameAsync(int track, CancellationToken ct = default);
    /// <summary>Custom-named mixer tracks (+ Master) as "index: name", for name→index resolution.</summary>
    Task<string> ListMixerTracksAsync(CancellationToken ct = default);
    /// <summary>Rename a mixer track/bus (persists) so a bus the model creates is resolvable by name later.</summary>
    Task SetMixerTrackNameAsync(int track, string name, CancellationToken ct = default);

    // --- mixer sends / EQ ---
    /// <summary>Set a mixer send srcTrack-&gt;dstTrack at level (1.0 ≈ unity).</summary>
    Task SetMixerSendAsync(int srcTrack, int dstTrack, double level, CancellationToken ct = default);
    /// <summary>Mixer track EQ band gain (band 0=low,1=mid,2=high; value 0..0x40000000, ~0x20000000 = 0 dB).</summary>
    Task SetMixerEqGainAsync(int track, int band, int value, CancellationToken ct = default);

    // --- transport ---
    Task TransportPlayAsync(CancellationToken ct = default);
    Task TransportStopAsync(CancellationToken ct = default);
    Task TransportToggleRecordAsync(CancellationToken ct = default);
    /// <summary>Set the song loop / time-selection region to [startTick, endTick] so the transport loops just
    /// that span (e.g. loop the drop while working on it). endTick &lt; 0 (or &lt;= startTick) CLEARS the loop.</summary>
    Task SetLoopRegionAsync(int startTick, int endTick, CancellationToken ct = default);

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
    /// <summary>Read piano-roll notes of a pattern (1-based, or &lt;=0 = current); channel&lt;0 = all.
    /// Paged: offset skips the first N notes (raw index); the output's continuation hint feeds it back in.</summary>
    Task<string> GetNotesAsync(int pattern, int channel, int offset = 0, CancellationToken ct = default);

    // --- notes (surgical edit) ---
    /// <summary>Edit EXISTING piano-roll notes in place, WITHOUT clearing the pattern (every other note is
    /// untouched, including fields the read tool doesn't surface — pan, fine pitch, release, cut, res). Each
    /// <see cref="NoteEdit"/> identifies a note by the (channel, key, startTick) triple <see cref="GetNotesAsync"/>
    /// shows and applies whichever new fields it carries. Returns the number of notes changed.</summary>
    Task<int> EditNotesAsync(int pattern, IReadOnlyList<NoteEdit> edits, CancellationToken ct = default);

    /// <summary>Delete SPECIFIC existing piano-roll notes (matched by the (channel, key, startTick) triple),
    /// leaving the rest of the pattern intact — the surgical counterpart to <see cref="ClearPatternAsync"/>.
    /// Returns the number of notes deleted.</summary>
    Task<int> DeleteNotesAsync(int pattern, IReadOnlyList<NoteRef> targets, CancellationToken ct = default);

    // --- patterns (clone) ---
    /// <summary>Duplicate a pattern's notes into a new empty pattern; returns the new pattern's 1-based index
    /// (0 if the source has nothing to clone). The full 24-byte note structs are copied, so pan/fine-pitch/
    /// mute/etc. survive — a "make a variation of this part" without hand-recreating every note.</summary>
    Task<int> ClonePatternAsync(int sourcePattern, CancellationToken ct = default);

    /// <summary>Rename a pattern (1-based; persists across save/reload) so the model can label its verse/
    /// chorus/drop parts instead of leaving "Pattern N" — which its own list_patterns navigation relies on.</summary>
    Task SetPatternNameAsync(int index, string name, CancellationToken ct = default);

    // --- playlist tracks ---
    Task<string> ListPlaylistTracksAsync(CancellationToken ct = default);
    Task SetTrackNameAsync(int track, string name, CancellationToken ct = default);
    /// <summary>Read a playlist track's name ("" when default; symmetric with <see cref="SetTrackNameAsync"/>).</summary>
    Task<string> GetTrackNameAsync(int track, CancellationToken ct = default);
    Task SetTrackColorAsync(int track, int rgb, CancellationToken ct = default);
    /// <summary>Read a playlist track's RGB color (symmetric with <see cref="SetTrackColorAsync"/>).</summary>
    Task<int> GetTrackColorAsync(int track, CancellationToken ct = default);
    Task SetTrackMuteAsync(int track, bool muted, CancellationToken ct = default);
    /// <summary>Read a playlist track's mute state (symmetric with <see cref="SetTrackMuteAsync"/>).</summary>
    Task<bool> GetTrackMuteAsync(int track, CancellationToken ct = default);
    /// <summary>Toggle exclusive SOLO on a playlist track (solo again = un-solo).</summary>
    Task SetTrackSoloAsync(int track, CancellationToken ct = default);
    Task SetTrackCollapsedAsync(int track, bool collapsed, CancellationToken ct = default);
    /// <summary>Read a playlist track's collapsed state (symmetric with <see cref="SetTrackCollapsedAsync"/>).</summary>
    Task<bool> GetTrackCollapsedAsync(int track, CancellationToken ct = default);
    Task SelectTrackAsync(int track, CancellationToken ct = default);

    // --- playlist clips (arrangement) ---
    /// <summary>List active playlist clips, paged: offset skips the first N matching clips;
    /// track&gt;0 filters to one playlist track (&lt;=0 = all).</summary>
    Task<string> ListClipsAsync(int offset = 0, int track = -1, CancellationToken ct = default);
    /// <summary>Add a pattern clip (pattern 1-based, matching notes/patterns; 0 or out-of-range throws) to a track at startTick; lengthTick&lt;=0 = pattern length.</summary>
    Task AddPatternClipAsync(int pattern, int track, int startTick, int lengthTick, CancellationToken ct = default);
    Task MoveClipAsync(int clipIndex, int startTick, int track, CancellationToken ct = default);
    Task ResizeClipAsync(int clipIndex, int lengthTick, CancellationToken ct = default);
    Task DeleteClipAsync(int clipIndex, CancellationToken ct = default);
    /// <summary>Mute/unmute a playlist clip.</summary>
    Task SetClipMutedAsync(int clipIndex, bool muted, CancellationToken ct = default);
    /// <summary>Read a playlist clip's mute state (clip+0x13 bit 0x20), symmetric with
    /// <see cref="SetClipMutedAsync"/> — the read-before-write for granular clip-mute undo.</summary>
    Task<bool> GetClipMutedAsync(int clipIndex, CancellationToken ct = default);

    // --- playlist clips: BULK (single call, single refresh/repaint at the end) ---
    // Each of these applies the whole batch and refreshes/repaints ONCE. The singular methods above
    // delegate to these (one implementation), so callers can use whichever is convenient.
    /// <summary>Delete many playlist clips in one pass. Indices are DEDUPED and removed high→low so the
    /// TList shift from an earlier removal never invalidates a later index; one recount + one repaint.</summary>
    Task DeleteClipsAsync(IReadOnlyList<int> clipIndices, CancellationToken ct = default);
    /// <summary>Move many playlist clips in one pass (per-clip start/track poke), then one repaint. Moves
    /// don't reorder the collection, so all indices stay valid within the call.</summary>
    Task MoveClipsAsync(IReadOnlyList<ClipMove> moves, CancellationToken ct = default);
    /// <summary>Place many pattern clips in one pass (each realized + inserted atomically), then one
    /// refresh/repaint. Clips are addressed by (pattern,track,start), so add-order index shifts don't matter.</summary>
    Task AddPatternClipsAsync(IReadOnlyList<PatternClipSpec> clips, CancellationToken ct = default);
    /// <summary>Resize many playlist clips in one pass (per-clip length poke), then one repaint.</summary>
    Task ResizeClipsAsync(IReadOnlyList<ClipResize> resizes, CancellationToken ct = default);
    /// <summary>Mute/unmute many playlist clips in one pass, then one repaint.</summary>
    Task SetClipsMutedAsync(IReadOnlyList<int> clipIndices, bool muted, CancellationToken ct = default);
    /// <summary>Slice/chop a clip into two at an absolute tick (audio stays continuous).</summary>
    Task SliceClipAsync(int clipIndex, int tick, CancellationToken ct = default);
    /// <summary>Duplicate a clip right after itself on the same track.</summary>
    Task DuplicateClipAsync(int clipIndex, CancellationToken ct = default);

    // --- song / transport state ---
    Task<string> GetSongStateAsync(CancellationToken ct = default);
    /// <summary>Read song mode (true) vs pattern mode (false) — symmetric with <see cref="SetSongModeAsync"/>.</summary>
    Task<bool> GetSongModeAsync(CancellationToken ct = default);
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
    /// <summary>Save a full <c>.flp</c> copy of the live project to a path WITHOUT changing the current
    /// project path/title. Modal-free and safe on UNTITLED projects too (uses FL's low-level direct
    /// writer, not the save wrapper that pops a blocking dialog on untitled projects).</summary>
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
    /// <summary>Read an arrangement's name ("" when unnamed; symmetric with <see cref="RenameArrangementAsync"/>).</summary>
    Task<string> GetArrangementNameAsync(int idx, CancellationToken ct = default);
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

/// <summary>Identifies ONE existing note by the (Channel, Key, StartTick) triple that
/// <see cref="INativeFlControl.GetNotesAsync"/> reports — stable (two notes can't share all three on one
/// channel), so the model can target a note without a fragile array index.</summary>
public readonly record struct NoteRef(int Channel, int Key, int StartTick);

/// <summary>An edit to ONE existing note: <see cref="Channel"/>/<see cref="Key"/>/<see cref="StartTick"/>
/// locate it (original values), and each nullable field, when set, is the note's NEW value (null = leave
/// unchanged). Changing <see cref="NewKey"/>/<see cref="NewStartTick"/> moves the note; the others don't.</summary>
public readonly record struct NoteEdit(
    int Channel, int Key, int StartTick,
    int? NewKey = null, int? NewStartTick = null, int? NewLength = null, int? NewVelocity = null, bool? Muted = null);
