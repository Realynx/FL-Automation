namespace FruityLink.Core.Abstractions;

/// <summary>Optional structured inspection for scripting clients. Indices describe the current project;
/// edits can invalidate them. Queries are read-only but are not atomic snapshots of the DAW.</summary>
public interface IFlStructuredQuery
{
    /// <summary>Read the channel rack, using zero-based channel indices.</summary>
    Task<IReadOnlyList<FlChannelInfo>> QueryChannelsAsync(CancellationToken ct = default);
    /// <summary>Read nonempty patterns, using one-based pattern indices.</summary>
    Task<IReadOnlyList<FlPatternInfo>> QueryPatternsAsync(CancellationToken ct = default);
    /// <summary>Read a bounded page of raw note slots. Negative channel means all channels.
    /// Offset and total count raw slots, so filtered pages can be empty with a continuation.</summary>
    Task<FlQueryPage<FlNoteInfo>> QueryNotesAsync(int pattern, int channel = -1, int offset = 0, int limit = 512, CancellationToken ct = default);
    /// <summary>Read all 500 playlist tracks, including default tracks, using one-based indices.</summary>
    Task<IReadOnlyList<FlPlaylistTrackInfo>> QueryPlaylistTracksAsync(CancellationToken ct = default);
    /// <summary>Read a bounded page of raw clip slots. Negative track means all tracks.
    /// Offset and total count raw slots, not filter matches.</summary>
    Task<FlQueryPage<FlClipInfo>> QueryClipsAsync(int track = -1, int offset = 0, int limit = 512, CancellationToken ct = default);
    /// <summary>Read arrangements, using zero-based indices.</summary>
    Task<IReadOnlyList<FlArrangementInfo>> QueryArrangementsAsync(CancellationToken ct = default);
    /// <summary>Read Master and active ordinary mixer inserts with their physical indices. Excludes the special Current track and dormant slots. Requery after insertion because indices can shift.</summary>
    Task<IReadOnlyList<FlMixerTrackInfo>> QueryMixerTracksAsync(CancellationToken ct = default);
    /// <summary>Read one mixer track's ACTIVE sends from the native send table: destination index and effective name, the send level on FL's scale (native int / 16000: 0.8 = unity/0 dB, the level an untouched insert's default Master route reads; 1.0 = knob top, 0 = connected but silent) and the active flag. Inactive (disconnected) destinations are omitted, so a route set to level 0 still appears with level 0 while a disconnected one does not. Sidechain-flagged routes are not distinguishable from plain sends in the verified layout. This is the typed readback for SetMixerSendAsync; requery after mixer insertion because indices shift.</summary>
    Task<IReadOnlyList<FlMixerSendInfo>> QueryMixerSendsAsync(int track, CancellationToken ct = default);
    /// <summary>Read plugin parameters without guessing normalized values from native integer bits.
    /// Negative slot selects a channel generator; otherwise select a mixer track and slot.
    /// Offset and total count unfiltered parameter slots.</summary>
    Task<FlQueryPage<FlPluginParameterInfo>> QueryPluginParametersAsync(int channelOrTrack, int slot = -1, string? filter = null, int offset = 0, int limit = 512, CancellationToken ct = default);
    /// <summary>Read the exact project title and path. Untitled does not indicate unsaved edits.</summary>
    Task<FlProjectInfo> QueryProjectAsync(CancellationToken ct = default);
    /// <summary>Read automation points with absolute times in quarter-note beats.</summary>
    Task<IReadOnlyList<FlAutomationPointInfo>> QueryAutomationPointsAsync(int channel, CancellationToken ct = default);
}

/// <summary>A page over raw collection slots. Follow NextOffset even when filtering returns no Items.</summary>
/// <param name="Items">Matching records in this page.</param>
/// <param name="NextOffset">Next raw slot, or null at the end.</param>
/// <param name="Total">Total raw slots before filtering.</param>
public sealed record FlQueryPage<T>(IReadOnlyList<T> Items, int? NextOffset, int Total);

/// <summary>A channel snapshot; volume and pan retain FL's integer control units.</summary>
/// <param name="Index">Zero-based channel index.</param><param name="Name">Display name.</param>
/// <param name="MixerTrack">Mixer route, zero means master.</param><param name="Muted">Mute state.</param>
/// <param name="Volume">Volume in native units.</param><param name="Pan">Pan in native units.</param>
public sealed record FlChannelInfo(int Index, string Name, int MixerTrack, bool Muted, long Volume, int Pan);

/// <summary>A nonempty pattern snapshot.</summary>
/// <param name="Index">One-based index.</param><param name="Name">Display name.</param>
/// <param name="LengthTick">Length in ticks, null when unavailable.</param><param name="NoteCount">Note count, null when unavailable.</param>
/// <param name="Current">Whether selected.</param>
public sealed record FlPatternInfo(int Index, string Name, int? LengthTick, int? NoteCount, bool Current);

/// <summary>A piano-roll note snapshot. Index is a transient raw slot, not a durable note ID.</summary>
/// <param name="Index">Raw slot.</param><param name="Channel">Zero-based channel.</param><param name="Key">MIDI key.</param>
/// <param name="StartTick">Start tick.</param><param name="LengthTick">Duration ticks.</param>
/// <param name="Velocity">Velocity.</param><param name="Muted">Mute state.</param>
public sealed record FlNoteInfo(int Index, int Channel, int Key, int StartTick, int LengthTick, int Velocity, bool Muted);

/// <summary>A playlist track snapshot.</summary>
/// <param name="Index">One-based track.</param><param name="Name">Display name.</param><param name="Color">RGB color.</param>
/// <param name="Muted">Mute state.</param><param name="Collapsed">Collapsed state.</param><param name="Selected">Selection state.</param>
/// <param name="Mode">Native track mode: zero normal, one audio, three instrument.</param>
public sealed record FlPlaylistTrackInfo(int Index, string Name, uint Color, bool Muted, bool Collapsed, bool Selected, int Mode);

/// <summary>A playlist clip snapshot. Index is invalidated by collection edits.</summary>
/// <param name="Index">Raw slot.</param><param name="Track">One-based playlist track.</param>
/// <param name="StartTick">Start tick.</param><param name="LengthTick">Duration ticks.</param>
/// <param name="SourceKind">Pattern or channel.</param><param name="SourceIndex">Source pattern or channel index.</param>
/// <param name="Muted">Mute state.</param>
public sealed record FlClipInfo(int Index, int Track, int StartTick, int LengthTick, string SourceKind, int SourceIndex, bool Muted);

/// <summary>An arrangement snapshot.</summary>
/// <param name="Index">Zero-based index.</param><param name="Name">Display name.</param><param name="Current">Whether selected.</param>
public sealed record FlArrangementInfo(int Index, string Name, bool Current);

/// <summary>An addressable mixer track snapshot; excludes FL's special Current track.</summary>
/// <param name="Index">Physical index: zero for Master, positive for active ordinary inserts.</param>
/// <param name="Name">Effective display name.</param><param name="Kind">Either master or insert.</param>
public sealed record FlMixerTrackInfo(int Index, string Name, string Kind);

/// <summary>One active mixer send read from a track's native send table.</summary>
/// <param name="Source">Physical index of the sending track.</param>
/// <param name="Destination">Physical index of the receiving track (0 = Master).</param>
/// <param name="DestinationName">Effective display name of the destination.</param>
/// <param name="Level">Send level on FL's scale: native int / 16000, so 0.8 is unity (0 dB) and 1.0 the knob top.</param>
/// <param name="Active">Always true for listed sends; disconnected destinations are omitted.</param>
public sealed record FlMixerSendInfo(int Source, int Destination, string DestinationName, double Level, bool Active);

/// <summary>A plugin parameter snapshot. RawValue is plugin-specific and is not a normalized float.</summary>
/// <param name="Index">Parameter index.</param><param name="Name">Parameter name.</param>
/// <param name="RawValue">Native integer representation.</param><param name="DisplayValue">Plugin-provided display text, possibly empty.</param>
public sealed record FlPluginParameterInfo(int Index, string Name, int RawValue, string DisplayValue);

/// <summary>Exact project identity fields, including names containing newlines or punctuation.</summary>
/// <param name="Title">Project title.</param><param name="Path">Project path.</param>
/// <param name="Untitled">Whether the project has no assigned filename; not an unsaved-edits flag.</param>
public sealed record FlProjectInfo(string Title, string Path, bool Untitled);

/// <summary>An automation point with an absolute time.</summary>
/// <param name="Index">Point index.</param><param name="TimeBeats">Quarter-note beats.</param>
/// <param name="Value">Value.</param><param name="Tension">Curve tension.</param><param name="Curve">Native curve type.</param>
public sealed record FlAutomationPointInfo(int Index, double TimeBeats, double Value, double Tension, int Curve);
