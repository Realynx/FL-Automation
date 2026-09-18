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
    /// <summary>Set tempo in beats per minute (10..522).</summary>
    Task SetTempoAsync(double bpm, CancellationToken ct = default);
    /// <summary>Read tempo in beats per minute.</summary>
    Task<double> GetTempoAsync(CancellationToken ct = default);
    /// <summary>Master volume as a raw native integer, 0..12800. No dB conversion is defined.</summary>
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
    /// <summary>Mixer track volume as a raw native integer on FL's fader scale 0..16000 (track 0 = master): 12800 (= 0.8, every track's default) is 0 dB, 16000 is the fader top (about +4.05 dB on the render-calibrated curve; FL's own hint says +5.6 dB) and 0 is silence. FL's fader law is not linear in dB: the SDK models it as dB = 20 * 2.09 * log10(raw / 12800), the exponent measured by render calibration 2026-09-14 (so 6400 is about -12.6 dB, measured -12.70, and 3200 about -25 dB); use the Python helpers fruitylink.levels.mixer_volume_from_db / MixerTrack.volume_db for dB values. Values above 16000 are clamped.</summary>
    Task SetMixerVolumeAsync(int track, int value, CancellationToken ct = default);
    /// <summary>Read a mixer track volume 0..16000 (12800 = 0 dB; symmetric with <see cref="SetMixerVolumeAsync"/>).</summary>
    Task<long> GetMixerVolumeAsync(int track, CancellationToken ct = default);
    /// <summary>Mixer track pan as a SIGNED native integer, -6400..6400: 0 = center, negative = left, 6400 = hard right. This scale differs from channel pan (0..12800, 6400 = center); a mixer value of 6400 or more is fully right. Live-verified by isolated renders (Ember Tides v006).</summary>
    Task SetMixerPanAsync(int track, int value, CancellationToken ct = default);
    /// <summary>Read a mixer track pan -6400..6400 (0 = center; symmetric with <see cref="SetMixerPanAsync"/>). Untouched tracks read 0.</summary>
    Task<int> GetMixerPanAsync(int track, CancellationToken ct = default);
    /// <summary>Mute/unmute a mixer track (the enabled flag; solo state untouched).</summary>
    Task SetMixerTrackMutedAsync(int track, bool muted, CancellationToken ct = default);
    /// <summary>Read a mixer track's mute state (symmetric with <see cref="SetMixerTrackMutedAsync"/>).</summary>
    Task<bool> GetMixerTrackMutedAsync(int track, CancellationToken ct = default);
    /// <summary>Arm or disarm a mixer track's disk recording (the disc button; track 0 = Master). While FL's transport records, every armed track writes one WAV of its post-FX output into FL's recorded-audio folder, which is how live per-insert capture works. Idempotent: FL's own setter (the routine behind its scripting armTrack) is only invoked when the state differs, and the state is re-read afterwards. Requires the verified mixer layout plus the harvested FLmx_SetTrackArmed/MixerTrackArmedOffset symbols; refused otherwise.</summary>
    Task SetMixerTrackArmedAsync(int track, bool armed, CancellationToken ct = default);
    /// <summary>Read a mixer track's disk-recording arm state (symmetric with <see cref="SetMixerTrackArmedAsync"/>): the byte FL's own isTrackArmed reads. Refused until the mixer layout and the armed-byte offset resolve on the running build.</summary>
    Task<bool> GetMixerTrackArmedAsync(int track, CancellationToken ct = default);
    /// <summary>A mixer FX-slot plugin parameter (normalized fixed-point value).</summary>
    Task SetMixerFxParamAsync(int track, int slot, int paramIndex, long value, CancellationToken ct = default);

    // --- channel rack (live-verified) ---
    /// <summary>Channel volume as a raw native integer, 0..12800 (FL's default is 10000 = 78 %). The scale is a power curve, not linear dB: the SDK models FL's fader law as dB = 20 * 2.09 * log10(raw / 10240) (10240 = 0 dB, 12800 = about +4.05 dB, 10000 = about -0.4 dB, 6400 = about -8.5 dB, 5000 = about -13 dB, 3200 = about -21 dB), so halving the raw value costs about 12.6 dB (render-measured 12.54). Use fruitylink.levels.channel_volume_from_db / Channel.volume_db for dB values; keep channel volumes near 10000 and trim with plugin gains for large changes.</summary>
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
    /// <summary>Route a channel to Master (0) or an active ordinary mixer insert (within 1..500). Query mixer tracks for current indices; Current and dormant slots are unavailable.</summary>
    Task SetChannelFxRouteAsync(int channel, int mixerTrack, CancellationToken ct = default);
    /// <summary>Read a channel's mixer-track route (symmetric with <see cref="SetChannelFxRouteAsync"/>).</summary>
    Task<int> GetChannelFxRouteAsync(int channel, CancellationToken ct = default);
    /// <summary>Read a built-in channel control by its FL REC_Chan event index (the same command-bus namespace as volume 0, pan 1, pitch 4, mute 7 and mixer route 8, which are live-verified) as a raw native integer. Other entries follow the FL SDK REC_Chan table and are NOT live-verified yet: 2 filter cutoff, 3 filter resonance, 13 Sampler sample start offset, 14 Sampler time-stretch time; every value is FL's raw unit for that control, so read the current value first, write, and confirm in the Channel settings window. Index must be 0..8191 (below the hosted-plugin parameter block, which SetPluginParamAsync covers).</summary>
    Task<long> GetChannelControlAsync(int channel, int control, CancellationToken ct = default);
    /// <summary>Write a built-in channel control by REC_Chan event index with a raw native integer (see <see cref="GetChannelControlAsync"/> for the index table and its verification status). Built-in Sampler settings that are not REC events (reverse, fade in/out, trim/sample end, stretch mode) have no command-bus path; pre-process the audio file and ReplaceChannelSampleAsync instead.</summary>
    Task SetChannelControlAsync(int channel, int control, long value, CancellationToken ct = default);

    // --- piano roll (current pattern) ---
    /// <summary>Add a note to a pattern's piano roll for a channel (pattern: 1-based, or &lt;=0 = current).
    /// channel must be an existing zero-based channel rack index; query channels after adding or removing one.
    /// key = MIDI 0..131 (60 = middle C), startTick &gt;= 0, lengthTick &gt; 0 in PPQ ticks, velocity 0..127.
    /// Invalid values and startTick + lengthTick above int.MaxValue are rejected, not clamped.</summary>
    Task AddNoteAsync(int pattern, int channel, int key, int startTick, int lengthTick, int velocity, CancellationToken ct = default);

    /// <summary>Add many notes to a pattern's piano roll in one batch — resolves the pattern and refreshes
    /// the editor once for the whole set, far faster than repeated <see cref="AddNoteAsync"/>. Each note
    /// carries its own channel, so a single call can author chords, melodies, or multi-channel drum grids.
    /// Every channel must exist in the current channel rack. Invalid channel references are rejected before
    /// any note in the batch is added; channel indices are never wrapped into another channel.
    /// Note values use the same strict ranges as AddNoteAsync. Cancellation may leave a completed prefix
    /// of the batch, but never an unfinished note-on waiting for its length.</summary>
    Task AddNotesAsync(int pattern, IReadOnlyList<NoteSpec> notes, CancellationToken ct = default);

    /// <summary>Project timebase: ticks per quarter note (PPQ).</summary>
    Task<int> GetPpqAsync(CancellationToken ct = default);

    // --- patterns ---
    /// <summary>Read the selected one-based pattern index.</summary>
    Task<int> GetCurrentPatternAsync(CancellationToken ct = default);
    /// <summary>Select a one-based pattern index.</summary>
    Task SelectPatternAsync(int index, CancellationToken ct = default);
    /// <summary>Selects the first empty pattern; returns its index.</summary>
    Task<int> CreatePatternAsync(CancellationToken ct = default);
    /// <summary>Remove all notes from the specified one-based pattern.</summary>
    Task ClearPatternAsync(int index, CancellationToken ct = default);
    /// <summary>Read the name of a one-based pattern.</summary>
    Task<string> GetPatternNameAsync(int index, CancellationToken ct = default);
    /// <summary>List patterns and their names.</summary>
    Task<string> ListPatternsAsync(CancellationToken ct = default);

    // --- channel rack ---
    /// <summary>Read the number of channels in the rack.</summary>
    Task<int> GetChannelCountAsync(CancellationToken ct = default);
    /// <summary>Exclusively select a zero-based channel (so the piano roll edits it).</summary>
    Task SelectChannelAsync(int channel, CancellationToken ct = default);
    /// <summary>Read the name of a zero-based channel.</summary>
    Task<string> GetChannelNameAsync(int channel, CancellationToken ct = default);
    /// <summary>List channel indices and names.</summary>
    Task<string> ListChannelsAsync(CancellationToken ct = default);
    /// <summary>Rename a channel (persists across save/reload) so the model's own name→index lookups keep
    /// working on channels it created.</summary>
    Task SetChannelNameAsync(int channel, string name, CancellationToken ct = default);
    /// <summary>Toggle exclusive SOLO on a channel (solo again = un-solo) — hear one part without muting
    /// every other channel by hand. The channel is zero-based, like every other channel operation.</summary>
    Task SetChannelSoloAsync(int channel, CancellationToken ct = default);

    // --- mixer tracks (identity: resolve a bus/track NAME to its index) ---
    /// <summary>Native mixer cardinality: Master + active ordinary inserts + Current. Current has a special physical index, not count-1; use IFlStructuredQuery.QueryMixerTracksAsync to enumerate addressable Master/insert tracks.</summary>
    Task<int> GetMixerTrackCountAsync(CancellationToken ct = default);
    /// <summary>Add an ordinary mixer insert after afterTrack (0 = Master), or append after the last ordinary insert when -1. Returns the new track index; requery track indices and routing after this structural edit. Unsupported native builds fail without adding a track.</summary>
    Task<int> AddMixerTrackAsync(int afterTrack = -1, CancellationToken ct = default);
    /// <summary>Effective mixer track name (custom if set, else default by type: Master/Insert n/Current).</summary>
    Task<string> GetMixerTrackNameAsync(int track, CancellationToken ct = default);
    /// <summary>Custom-named mixer tracks (+ Master) as "index: name", for name→index resolution.</summary>
    Task<string> ListMixerTracksAsync(CancellationToken ct = default);
    /// <summary>Rename a mixer track/bus (persists) so a bus the model creates is resolvable by name later.</summary>
    Task SetMixerTrackNameAsync(int track, string name, CancellationToken ct = default);

    // --- mixer sends / EQ ---
    /// <summary>Set a mixer send srcTrack-&gt;dstTrack. level uses FL's send scale where 0.8 = unity (0 dB; the level every insert's default Master route reads back), 1.0 = the knob top (about +4.05 dB) and 0 = silent but still connected; the same fader law as mixer volume (native int = level * 16000). With active=false the route is disconnected instead (FL's route-active core with enable 0; FL may show a "Disable routing?" confirmation when the destination is used as a plugin sidechain, so for unattended runs prefer level 0 on a route you cannot confirm). There is no sidechain flag: FL's "Sidechain to this track" is a differently flagged route whose location is not in the verified mixer layout, so a send always sums audio into the destination. Read routes back with IFlStructuredQuery.QueryMixerSendsAsync or the "sends:" line of ListMixerEffectsAsync.</summary>
    Task SetMixerSendAsync(int srcTrack, int dstTrack, double level, bool active = true, CancellationToken ct = default);
    /// <summary>Mixer track EQ band gain (band 0=low,1=mid,2=high; value 0..0x40000000, ~0x20000000 = 0 dB).</summary>
    Task SetMixerEqGainAsync(int track, int band, int value, CancellationToken ct = default);

    // --- transport ---
    /// <summary>Start playback.</summary>
    Task TransportPlayAsync(CancellationToken ct = default);
    /// <summary>Stop playback.</summary>
    Task TransportStopAsync(CancellationToken ct = default);
    /// <summary>Toggle recording.</summary>
    Task TransportToggleRecordAsync(CancellationToken ct = default);
    /// <summary>Set the song loop / time-selection region to [startTick, endTick), with an exclusive end
    /// (for example, 0..1536 spans four bars at 96 PPQ). endTick &lt;= the nonnegative start clears the loop.</summary>
    Task SetLoopRegionAsync(int startTick, int endTick, CancellationToken ct = default);

    // --- plugins / inserts ---
    /// <summary>List installed plugins of a kind (effects=true → mixer effects, false → channel generators).</summary>
    Task<string> ListAvailablePluginsAsync(bool effects, CancellationToken ct = default);
    /// <summary>Describe a channel's loaded generator plugin.</summary>
    Task<string> GetChannelPluginAsync(int channel, CancellationToken ct = default);
    /// <summary>Add a new channel hosting the named generator plugin; returns its index. Instantiating a
    /// plugin runs its constructor on FL's UI thread, so this call is guarded for 20 seconds instead of the
    /// ordinary bridge budget (the FIRST plugin load of a session is the slow one: a cold VST scan/instantiate
    /// can hold FL's UI thread for many seconds). If even that expires, the channel's plugin name is re-read
    /// once after a short settle: when the generator did load the call succeeds and the recovery is recorded in
    /// the op log, and only a channel that still reports no generator raises a timeout saying the plugin is
    /// still initialising or waiting on a dialog.</summary>
    Task<int> AddChannelAsync(string pluginName, CancellationToken ct = default);
    /// <summary>List the effects loaded in a mixer track's FX slots.</summary>
    Task<string> ListMixerEffectsAsync(int track, CancellationToken ct = default);
    /// <summary>Load/replace the named effect into a mixer track's FX slot (0-9) and return a verification
    /// line: the slot plus the effect name the slot reports after the load. Instantiating a plugin runs its
    /// constructor on FL's UI thread, so this call is guarded for 20 seconds instead of the ordinary bridge
    /// budget (the FIRST plugin load of a session is the slow one: a cold VST scan/instantiate can hold FL's UI
    /// thread for many seconds). If even that expires, the slot is re-read once after a short settle: when the
    /// effect did load the call succeeds and the verification line carries "loaded after N ms; FL's UI was
    /// blocked while the plugin initialised", and only a slot that is still empty raises a timeout saying the
    /// plugin is still initialising or waiting on a dialog.</summary>
    Task<string> AddMixerEffectAsync(int track, int slot, string pluginName, CancellationToken ct = default);
    /// <summary>Clear a mixer track's FX slot.</summary>
    Task RemoveMixerEffectAsync(int track, int slot, CancellationToken ct = default);
    /// <summary>Copy the effect type from one FX slot to another (type only, not parameter state).</summary>
    Task CloneMixerEffectAsync(int track, int fromSlot, int toSlot, CancellationToken ct = default);

    /// <summary>List a plugin's parameters ("index: name"). slot &lt; 0 = channel generator; else mixer track+slot. The name filter is OPTIONAL: omit it (or pass null) to list every parameter.</summary>
    Task<string> ListPluginParamsAsync(int channelOrTrack, int slot, string? filter = null, CancellationToken ct = default);
    /// <summary>Set a plugin parameter to a normalized value 0..1. slot &lt; 0 = channel generator; else mixer track+slot.</summary>
    Task SetPluginParamAsync(int channelOrTrack, int slot, int paramIndex, double value, CancellationToken ct = default);

    // --- samples ---
    /// <summary>List available audio samples from every configured search root, optionally filtered by name (a case-insensitive substring of the path). The filter is OPTIONAL: omit it (or pass null) to browse. Entries are root-tagged relative paths with a legend line: [P] = FL's factory packs, [U] = the user's Image-Line documents content, [B1], [B2], ... = the folders FL's browser searches in addition to those (its "extra search folders"), which is where a user's own sample library normally lives. Pass an entry back verbatim to add_sample_channel / replace_channel_sample.</summary>
    Task<string> ListSamplesAsync(string? filter = null, CancellationToken ct = default);
    /// <summary>Add a new channel that plays the given audio sample file (drum/one-shot/loop); returns its index.</summary>
    Task<int> AddSampleChannelAsync(string samplePath, CancellationToken ct = default);
    /// <summary>Replace an existing channel's sample with a new audio file.</summary>
    Task ReplaceChannelSampleAsync(int channel, string samplePath, CancellationToken ct = default);

    // --- plugin state / preset files (live-verified with Serum 2 on FL 26.1.3) ---
    /// <summary>Load a plugin state or preset file into the generator ALREADY hosted by a channel, without
    /// replacing the plugin instance. Uses the wrapper's own state-file loader (dispatcher opcode 0x12).
    /// Live-verified on FL 26.1.3: a VST3 <c>.vstpreset</c> whose class id is the plugin's GUID string with
    /// braces/dashes removed loads into Serum 2 and changes the state in place, and a native plugin's own
    /// preset format can load too (GMS <c>.gmsynth</c> from <c>Data/Patches/Plugin presets/Generators/GMS</c>
    /// applied in place: 226 differing state bytes, the pad became audible), while a third-party proprietary
    /// preset (<c>.SerumPreset</c>) is silently ignored. Load a factory preset BEFORE authoring a native synth by
    /// parameter: a fresh GMS has no oscillator waveforms (chosen in the GUI, not parameters) and renders silence.
    /// An FL <c>.fst</c> preset for one of FL's OWN generators (Sytrus, Harmor, ... — a channel whose plugin is not
    /// the "Fruity Wrapper" VST host) is routed through FL's channel file loader automatically, because the
    /// dispatcher is a silent no-op for those files (FL 26.1.3.5570: a Sytrus factory preset left the state record
    /// byte-identical, the channel loader changed 99% of it). That loader also mutes the channel and renames it to
    /// the preset's base name, so the SDK snapshots the channel's name, mute state and mixer route before the load
    /// and restores all three after it; the verification line names the route used and what was restored.
    /// Set <paramref name="useChannelLoader"/> to force that route for a wrapped plugin's <c>.fst</c> as well; it is
    /// refused for other formats because live it applied no state and renamed the channel.
    /// Refuses channels without a hosted plugin and, for <c>.fst</c> files, files that do not name the channel's
    /// current plugin (the name is matched both as UTF-16, how wrapped plugins store it, and as the single-byte
    /// string FL's own generators store). Returns a verification line: plugin name, the route used, same-instance
    /// check, parameter count and a comparison of the plugin's wrapper state record before and after the load
    /// (sizes, short hashes and the number of differing bytes), or an explicit "unavailable" note when no snapshot
    /// could be taken. Confirm the sound with parameter displays in a separate request or an isolated render.</summary>
    Task<string> LoadChannelPluginStateAsync(int channel, string path, bool useChannelLoader = false, CancellationToken ct = default);
    /// <summary>Load a plugin state or preset file into the effect ALREADY loaded in a mixer FX slot (0-9)
    /// through the wrapper's state-file loader (dispatcher opcode 0x12). Same format and identity rules as
    /// <see cref="LoadChannelPluginStateAsync"/>. Refuses empty slots.</summary>
    Task<string> LoadMixerEffectStateAsync(int track, int slot, string path, CancellationToken ct = default);
    /// <summary>Read the CURRENT state of the generator hosted by a channel as base64 of its FL wrapper
    /// plugin-data record: the same bytes an FL project stores for the plugin (for a VST3 such as Serum 2 this
    /// embeds the processor and controller component states). Implemented through FL's own serializer: a
    /// temporary project copy is written with the direct writer used by SaveCopyAsync and the channel's record
    /// is extracted, so project note validation applies and the live project's path, title and dirty flag do
    /// not change. Pair with <see cref="LoadChannelPluginStateAsync"/> to learn parameter mappings by
    /// set-then-read, or to snapshot a patch without saving the project. Refuses channels without a hosted
    /// plugin; built-in Sampler channels store no wrapper record.</summary>
    Task<string> GetChannelPluginStateAsync(int channel, CancellationToken ct = default);
    /// <summary>Read the CURRENT state of the effect in a mixer FX slot (0-9; track 0 = Master) as base64 of
    /// its FL wrapper plugin-data record, through the same temporary project copy as
    /// <see cref="GetChannelPluginStateAsync"/>. Refuses empty slots.</summary>
    Task<string> GetMixerEffectStateAsync(int track, int slot, CancellationToken ct = default);

    // --- notes (read) ---
    /// <summary>Read piano-roll notes of a pattern (1-based, or &lt;=0 = current); channel&lt;0 = all.
    /// Paged: offset skips the first N notes (raw index); the output's continuation hint feeds it back in.</summary>
    Task<string> GetNotesAsync(int pattern, int channel, int offset = 0, CancellationToken ct = default);

    // --- notes (surgical edit) ---
    /// <summary>Edit EXISTING piano-roll notes in place, WITHOUT clearing the pattern (every other note is
    /// untouched, including fields the read tool doesn't surface — pan, fine pitch, release, cut, res). Each
    /// <see cref="NoteEdit"/> identifies a note by the (channel, key, startTick) triple <see cref="GetNotesAsync"/>
    /// shows, optionally narrowed by its current lengthTick, and applies whichever new fields it carries.
    /// FL allows several notes with the same triple (stacked duplicates), so an edit that matches more than
    /// one note is refused before anything is written unless <paramref name="allowMultiple"/> is true, in which
    /// case every matching note receives the edit. Returns the number of notes changed. Playlist clips of the
    /// pattern keep their lengths (FL would otherwise re-derive them from the edited notes).</summary>
    Task<int> EditNotesAsync(int pattern, IReadOnlyList<NoteEdit> edits, bool allowMultiple = false, CancellationToken ct = default);

    /// <summary>Delete SPECIFIC existing piano-roll notes (matched by the (channel, key, startTick) triple,
    /// optionally narrowed by lengthTick), leaving the rest of the pattern intact — the surgical counterpart to
    /// <see cref="ClearPatternAsync"/>. A target that matches several stacked duplicates is refused before
    /// anything is deleted unless <paramref name="allowMultiple"/> is true, which deletes all of them.
    /// Returns the number of notes deleted. Playlist clips of the pattern keep their lengths (FL would
    /// otherwise shrink them to the remaining notes); resize clips explicitly with ResizeClips.</summary>
    Task<int> DeleteNotesAsync(int pattern, IReadOnlyList<NoteRef> targets, bool allowMultiple = false, CancellationToken ct = default);

    // --- patterns (clone) ---
    /// <summary>Duplicate a pattern's notes into a new empty pattern; returns the new pattern's 1-based index
    /// (0 if the source has nothing to clone). The full 24-byte note structs are copied, so pan/fine-pitch/
    /// mute/etc. survive — a "make a variation of this part" without hand-recreating every note.</summary>
    Task<int> ClonePatternAsync(int sourcePattern, CancellationToken ct = default);

    /// <summary>Rename a pattern (1-based; persists across save/reload) so the model can label its verse/
    /// chorus/drop parts instead of leaving "Pattern N" — which its own list_patterns navigation relies on.</summary>
    Task SetPatternNameAsync(int index, string name, CancellationToken ct = default);

    // --- playlist tracks ---
    /// <summary>List customized playlist tracks and summarize default tracks.</summary>
    Task<string> ListPlaylistTracksAsync(CancellationToken ct = default);
    /// <summary>Rename a one-based playlist track.</summary>
    Task SetTrackNameAsync(int track, string name, CancellationToken ct = default);
    /// <summary>Read a playlist track's name ("" when default; symmetric with <see cref="SetTrackNameAsync"/>).</summary>
    Task<string> GetTrackNameAsync(int track, CancellationToken ct = default);
    /// <summary>Set a playlist track color as packed RGB.</summary>
    Task SetTrackColorAsync(int track, int rgb, CancellationToken ct = default);
    /// <summary>Read a playlist track's RGB color (symmetric with <see cref="SetTrackColorAsync"/>).</summary>
    Task<int> GetTrackColorAsync(int track, CancellationToken ct = default);
    /// <summary>Mute or unmute a playlist track.</summary>
    Task SetTrackMuteAsync(int track, bool muted, CancellationToken ct = default);
    /// <summary>Read a playlist track's mute state (symmetric with <see cref="SetTrackMuteAsync"/>).</summary>
    Task<bool> GetTrackMuteAsync(int track, CancellationToken ct = default);
    /// <summary>Toggle exclusive SOLO on a playlist track (solo again = un-solo).</summary>
    Task SetTrackSoloAsync(int track, CancellationToken ct = default);
    /// <summary>Collapse or expand a playlist track.</summary>
    Task SetTrackCollapsedAsync(int track, bool collapsed, CancellationToken ct = default);
    /// <summary>Read a playlist track's collapsed state (symmetric with <see cref="SetTrackCollapsedAsync"/>).</summary>
    Task<bool> GetTrackCollapsedAsync(int track, CancellationToken ct = default);
    /// <summary>Select a playlist track.</summary>
    Task SelectTrackAsync(int track, CancellationToken ct = default);

    // --- playlist clips (arrangement) ---
    /// <summary>List active playlist clips, paged: offset skips the first N matching clips;
    /// track&gt;0 filters to one playlist track (&lt;=0 = all).</summary>
    Task<string> ListClipsAsync(int offset = 0, int track = -1, CancellationToken ct = default);
    /// <summary>Add a pattern clip (pattern 1-based, matching notes/patterns; 0 or out-of-range throws) to a track at startTick; lengthTick&lt;=0 = pattern length. A PATTERN CLIP DOES NOT LOOP: FL plays the pattern once from the clip start and the rest of the clip is silent (live-verified 2026-09-18, FL 26.1.3.5570: a 1-bar pattern in a 4-bar clip sounded in bar 1 only, bars 2-4 measured silent at the master), so a span that should repeat needs one clip per repetition - keep lengthTick at most the pattern's own length, or use the Python helper fl.playlist.tile_pattern, which places the whole run in one pass.</summary>
    Task AddPatternClipAsync(int pattern, int track, int startTick, int lengthTick, CancellationToken ct = default);
    /// <summary>Move a playlist clip to a tick position and track.</summary>
    Task MoveClipAsync(int clipIndex, int startTick, int track, CancellationToken ct = default);
    /// <summary>Set a playlist clip duration in ticks.</summary>
    Task ResizeClipAsync(int clipIndex, int lengthTick, CancellationToken ct = default);
    /// <summary>Remove a playlist clip by its collection index.</summary>
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
    /// refresh/repaint. Clips are addressed by (pattern,track,start), so add-order index shifts don't matter.
    /// A positive lengthTick is pinned (as ResizeClips does), so the clip keeps that length even when the
    /// pattern's own content is longer or shorter; lengthTick &lt;= 0 takes the pattern length and follows
    /// it. A pinned length is NOT a loop: FL plays the pattern once from the clip start and the rest of the
    /// clip is silent, so a repeating span is one spec per repetition (the Python helper
    /// fl.playlist.add_patterns(..., repeat=True) expands them for you).</summary>
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
    /// <summary>Describe playback state, mode, and song position.</summary>
    Task<string> GetSongStateAsync(CancellationToken ct = default);
    /// <summary>Read song mode (true) vs pattern mode (false) — symmetric with <see cref="SetSongModeAsync"/>.</summary>
    Task<bool> GetSongModeAsync(CancellationToken ct = default);
    /// <summary>
    /// FL's current status/hint bar text (the name/tooltip of whatever is under the mouse + current-operation
    /// messages, e.g. "Opening: Fruity Wrapper" at load), cleaned of FL's internal "tooltip|status" split and
    /// '^' markup. Empty when there is no active hint. Read-only; safe to poll.
    /// </summary>
    Task<string> GetStatusAsync(CancellationToken ct = default);
    /// <summary>Select song playback when true, or pattern playback when false.</summary>
    Task SetSongModeAsync(bool song, CancellationToken ct = default);
    /// <summary>Read FL's metronome click. It is mixed into what FL plays, so it also lands in a live per-insert capture; capture switches it off for the pass and restores it. Symmetric with <see cref="SetMetronomeAsync"/>; refused until the toggle's symbols resolve on the running build.</summary>
    Task<bool> GetMetronomeAsync(CancellationToken ct = default);
    /// <summary>Set FL's metronome click. It is mixed into what FL plays, so it also lands in a live per-insert capture; capture switches it off for the pass and restores it. The change goes through the very setter FL's own toggle Action calls, on FL's main thread, so FL repaints and reacts exactly as it does for a click; the value is re-read afterwards and a refusal is reported instead of assumed. Refused until the toggle's symbols resolve on the running build.</summary>
    Task SetMetronomeAsync(bool on, CancellationToken ct = default);
    /// <summary>Read FL's countdown before recording (the "precount" toolbar toggle). With it on, a record+play pass spends a bar counting in before FL records anything, which silently shortens or empties a captured span; capture switches it off for the pass and restores it. Symmetric with <see cref="SetCountdownAsync"/>; refused until the toggle's symbols resolve on the running build.</summary>
    Task<bool> GetCountdownAsync(CancellationToken ct = default);
    /// <summary>Set FL's countdown before recording (the "precount" toolbar toggle). With it on, a record+play pass spends a bar counting in before FL records anything, which silently shortens or empties a captured span; capture switches it off for the pass and restores it. The change goes through the very setter FL's own toggle Action calls, on FL's main thread, so FL repaints and reacts exactly as it does for a click; the value is re-read afterwards and a refusal is reported instead of assumed. Refused until the toggle's symbols resolve on the running build.</summary>
    Task SetCountdownAsync(bool on, CancellationToken ct = default);
    /// <summary>Read FL's "wait for input to start playing" toggle. With it on, play does not start until FL sees note or audio input, so an automated record+play pass hangs until its deadline with nothing recorded; capture switches it off for the pass and restores it. Symmetric with <see cref="SetWaitForInputAsync"/>; refused until the toggle's symbols resolve on the running build.</summary>
    Task<bool> GetWaitForInputAsync(CancellationToken ct = default);
    /// <summary>Set FL's "wait for input to start playing" toggle. With it on, play does not start until FL sees note or audio input, so an automated record+play pass hangs until its deadline with nothing recorded; capture switches it off for the pass and restores it. The change goes through the very setter FL's own toggle Action calls, on FL's main thread, so FL repaints and reacts exactly as it does for a click; the value is re-read afterwards and a refusal is reported instead of assumed. Refused until the toggle's symbols resolve on the running build.</summary>
    Task SetWaitForInputAsync(bool on, CancellationToken ct = default);
    /// <summary>Read FL's loop-recording toggle. With it on, a pass over a looped range keeps every take instead of one recording, which changes what a capture writes and what the project ends up holding; capture switches it off for the pass and restores it. Symmetric with <see cref="SetLoopRecordAsync"/>; refused until the toggle's symbols resolve on the running build.</summary>
    Task<bool> GetLoopRecordAsync(CancellationToken ct = default);
    /// <summary>Set FL's loop-recording toggle. With it on, a pass over a looped range keeps every take instead of one recording, which changes what a capture writes and what the project ends up holding; capture switches it off for the pass and restores it. The change goes through the very setter FL's own toggle Action calls, on FL's main thread, so FL repaints and reacts exactly as it does for a click; the value is re-read afterwards and a refusal is reported instead of assumed. Refused until the toggle's symbols resolve on the running build.</summary>
    Task SetLoopRecordAsync(bool on, CancellationToken ct = default);
    /// <summary>Read FL's "blend recorded notes" (overdub) toggle: recorded notes are merged into the existing pattern instead of replacing it. Irrelevant to audio capture, exposed because note recording needs it. Symmetric with <see cref="SetBlendRecordedNotesAsync"/>; refused until the toggle's symbols resolve on the running build.</summary>
    Task<bool> GetBlendRecordedNotesAsync(CancellationToken ct = default);
    /// <summary>Set FL's "blend recorded notes" (overdub) toggle: recorded notes are merged into the existing pattern instead of replacing it. Irrelevant to audio capture, exposed because note recording needs it. The change goes through the very setter FL's own toggle Action calls, on FL's main thread, so FL repaints and reacts exactly as it does for a click; the value is re-read afterwards and a refusal is reported instead of assumed. Refused until the toggle's symbols resolve on the running build.</summary>
    Task SetBlendRecordedNotesAsync(bool on, CancellationToken ct = default);
    /// <summary>Read whether FL's audio engine currently has a recording pass running, independently of the toolbar record button's paint state. This is the counter FL's own apply-recording-filter routine checks before it computes the effective filter, so it is true while the engine is actually recording and false otherwise; use it to verify that a record+play pass really started when the button byte is in doubt. Refused until the recording-state symbol resolves on the running build.</summary>
    Task<bool> GetRecordingActiveAsync(CancellationToken ct = default);
    /// <summary>Read whether FL's transport record button is engaged (the toolbar toggle FL's own scripting reports as ui.isRecording): true means the next play records. TransportToggleRecordAsync only flips it, and FL leaves the button engaged after a recording pass, so a caller that needs recording ON must read this first and toggle only when it differs -- a blind toggle before a second pass switches recording OFF and that pass records nothing. Refused until the record-button symbols resolve on the running build.</summary>
    Task<bool> GetRecordPressedAsync(CancellationToken ct = default);
    /// <summary>Read FL's global recording filter — the bitmask behind the record button's right-click "Recording filter" submenu, which decides what a recording pass is allowed to capture. Bits (FL's own menu-item tags): 1 = Automation, 2 = Notes, 4 = Audio, 8 = Clips; FL 2025 has no Clips item, so bit 8 is unused there. Bit 4 must be set or FL silently writes no WAV for an armed mixer insert, which is the single most common reason live audio capture produces nothing. FL keeps this value only in memory while it runs (its registry home, RecordingFilter2 under HKCU > Software > Image-Line > FL Studio 26 > General > FruityLoopsMainForm, is read at startup and written at exit), so it must be read and set through the running engine. Refused until the recording-filter symbols resolve on the running build.</summary>
    Task<int> GetRecordingFilterAsync(CancellationToken ct = default);
    /// <summary>Set FL's global recording filter bitmask (see <see cref="GetRecordingFilterAsync"/> for the bits) by invoking the very routine FL's own "Recording filter" menu items call, on FL's main thread, so the menu checkmarks and the record button follow. The value is re-read afterwards and a mismatch is reported instead of assumed. Only the flags given are kept, so read first and combine when preserving the user's other choices; the value is restored to what it was by callers that changed it for one pass. Refused until the recording-filter symbols resolve on the running build.</summary>
    Task SetRecordingFilterAsync(int flags, CancellationToken ct = default);
    /// <summary>Move the song playhead to an absolute tick (PPQ).</summary>
    Task SeekAsync(int tick, CancellationToken ct = default);
    /// <summary>List song time markers.</summary>
    Task<string> ListMarkersAsync(CancellationToken ct = default);
    /// <summary>Add a named song marker at a tick position.</summary>
    Task AddMarkerAsync(int tick, string name, CancellationToken ct = default);
    /// <summary>Delete a song time marker by its zero-based index in <see cref="ListMarkersAsync"/> order. Refuses a missing index without changing the project. FL extends renders and the play range to the last marker, so remove trailing markers to shorten an audition.</summary>
    Task DeleteMarkerAsync(int index, CancellationToken ct = default);

    // --- project lifecycle ---
    /// <summary>Open the project at the specified path.</summary>
    Task OpenProjectAsync(string path, CancellationToken ct = default);
    /// <summary>Save the project using the specified path. Rejects orphan note channel references before invoking FL's serializer; inspect and repair the reported note before retrying.</summary>
    Task SaveProjectAsync(string path, CancellationToken ct = default);
    /// <summary>Create a new project through FL Studio.</summary>
    Task NewProjectAsync(CancellationToken ct = default);
    /// <summary>Read project identity and metadata.</summary>
    Task<string> GetProjectInfoAsync(CancellationToken ct = default);
    /// <summary>Save As: write to a new path and make it the current project (updates title + recent files). Orphan note validation runs before changing project identity.</summary>
    Task SaveProjectAsAsync(string path, CancellationToken ct = default);
    /// <summary>Save a full <c>.flp</c> copy of the live project to a path WITHOUT changing the current
    /// project path/title. Supports UNTITLED projects through FL's low-level direct writer.
    /// Orphan note channel references are rejected before writing; no notes are deleted automatically.</summary>
    Task SaveCopyAsync(string path, CancellationToken ct = default);
    /// <summary>Save an auto-incremented new version and make it current. Orphan note validation runs before changing project identity.</summary>
    Task SaveNewVersionAsync(CancellationToken ct = default);
    /// <summary>List recently opened project paths.</summary>
    Task<string> ListRecentProjectsAsync(CancellationToken ct = default);

    // --- arrangements ---
    /// <summary>List arrangement indices and names.</summary>
    Task<string> ListArrangementsAsync(CancellationToken ct = default);
    /// <summary>Add a new (empty) arrangement and switch to it; returns its index.</summary>
    Task<int> AddArrangementAsync(string? name, CancellationToken ct = default);
    /// <summary>Clone an arrangement (deep copy incl. clips); srcIdx&lt;0 = current. Returns the new index.</summary>
    Task<int> CloneArrangementAsync(int srcIdx, string? name, CancellationToken ct = default);
    /// <summary>Rename an arrangement by index.</summary>
    Task RenameArrangementAsync(int idx, string name, CancellationToken ct = default);
    /// <summary>Read an arrangement's name ("" when unnamed; symmetric with <see cref="RenameArrangementAsync"/>).</summary>
    Task<string> GetArrangementNameAsync(int idx, CancellationToken ct = default);
    /// <summary>Delete an arrangement by index.</summary>
    Task DeleteArrangementAsync(int idx, CancellationToken ct = default);
    /// <summary>Switch to an arrangement by index.</summary>
    Task SelectArrangementAsync(int idx, CancellationToken ct = default);

    // --- automation clips (channel must host the Automation Clip generator) ---
    /// <summary>Create a linked automation channel and place its clip on one-based playlist track 1..500. Times are ticks, length positive. Initial linking is part of native creation; failures may leave the channel created, so inspect before retrying.</summary>
    Task<FlAutomationClipResult> CreateAutomationClipAsync(FlAutomationTarget target, int track, int startTick, int lengthTick, string? name = null, CancellationToken ct = default);
    /// <summary>Place an existing Automation Clip generator on one-based playlist track 1..500. Returns the new playlist clip index; startTick is nonnegative and lengthTick positive.</summary>
    Task<int> AddAutomationClipAsync(int channel, int track, int startTick, int lengthTick, CancellationToken ct = default);
    /// <summary>Replace an automation envelope with 2..4000 linear points. Times are beats, first time zero, later times strictly increasing; values 0..1, tension -1..1, curve must be zero.</summary>
    Task SetAutomationPointsAsync(int channel, IReadOnlyList<FlAutomationPointSpec> points, CancellationToken ct = default);
    /// <summary>List an automation channel curve with times, values, and tension.</summary>
    Task<string> ListAutomationPointsAsync(int channel, CancellationToken ct = default);
    /// <summary>Add an automation point: time in beats, value 0..1, tension -1..1 (inserts in time order).</summary>
    Task AddAutomationPointAsync(int channel, double timeBeats, double value, double tension, CancellationToken ct = default);
    /// <summary>Delete an automation point by index and recompute its curve. The first and last points are protected endpoints and cannot be deleted; edit them with <see cref="SetAutomationPointAsync"/>.</summary>
    Task DeleteAutomationPointAsync(int channel, int index, CancellationToken ct = default);
    /// <summary>Change one existing automation point's value 0..1 and tension -1..1 in place, keeping its time. Works for the protected first and last points. Refuses a missing index and curves that contain non-linear points (replace those with <see cref="SetAutomationPointsAsync"/>).</summary>
    Task SetAutomationPointAsync(int channel, int index, double value, double tension, CancellationToken ct = default);

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

/// <summary>Identifies an existing note by the (Channel, Key, StartTick) triple that
/// <see cref="INativeFlControl.GetNotesAsync"/> reports, so the model can target a note without a fragile array
/// index. FL does allow stacked duplicates that share the triple; set <see cref="LengthTick"/> (the note's current
/// length) to pick one of them, or pass allowMultiple to the operation to address all of them.</summary>
public readonly record struct NoteRef(int Channel, int Key, int StartTick, int? LengthTick = null);

/// <summary>An edit to ONE existing note: <see cref="Channel"/>/<see cref="Key"/>/<see cref="StartTick"/>
/// (and optionally the current <see cref="LengthTick"/>) locate it (original values), and each nullable "New"
/// field, when set, is the note's NEW value (null = leave unchanged). Changing <see cref="NewKey"/>/
/// <see cref="NewStartTick"/> moves the note; the others don't.</summary>
public readonly record struct NoteEdit(
    int Channel, int Key, int StartTick,
    int? NewKey = null, int? NewStartTick = null, int? NewLength = null, int? NewVelocity = null, bool? Muted = null,
    int? LengthTick = null);
