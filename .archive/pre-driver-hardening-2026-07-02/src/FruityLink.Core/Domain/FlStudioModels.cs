namespace FruityLink.Core.Domain;

// Lightweight DTOs describing FL Studio state as read back over the bridge. These
// mirror what the FL Python API exposes; fields the current FL version can't provide
// are left at sensible defaults.

/// <summary>A channel in the channel rack.</summary>
public sealed record ChannelInfo(
    int Index,
    string Name,
    string Type,
    bool Muted,
    float Volume,
    float Pan,
    int TargetMixerTrack);

/// <summary>A mixer track and the effects loaded on it.</summary>
public sealed record MixerTrackInfo(
    int Index,
    string Name,
    bool Muted,
    bool Solo,
    float Volume,
    float Pan,
    IReadOnlyList<EffectSlotInfo> Effects);

/// <summary>One effect slot on a mixer track.</summary>
public sealed record EffectSlotInfo(int Slot, string PluginName, bool Enabled);

/// <summary>A plugin parameter (channel instrument or mixer effect).</summary>
public sealed record PluginParamInfo(int Index, string Name, float NormalizedValue, string DisplayValue);

/// <summary>A pattern in the project.</summary>
public sealed record PatternInfo(int Index, string Name, int Color, bool Selected, int LengthTicks);

/// <summary>
/// Step-sequencer steps to program for one channel. <see cref="Channel"/> is a channel name
/// (matched against the rack) or <c>index:N</c>; <see cref="Steps"/> is one bool per 16th-note
/// step (true = hit). Written directly into the current pattern — no piano roll.
/// </summary>
public sealed record StepTrack(string Channel, IReadOnlyList<bool> Steps);

/// <summary>A playlist track.</summary>
public sealed record PlaylistTrackInfo(int Index, string Name, bool Muted, bool Selected);

/// <summary>Transport / clock state.</summary>
public sealed record TransportState(bool Playing, bool Recording, double Tempo, int SongPositionTicks, int LoopMode);

/// <summary>Top-level project snapshot for situational awareness.</summary>
public sealed record ProjectInfo(
    string Title,
    double Tempo,
    int Ppq,
    int CurrentPattern,
    int PatternCount,
    int ChannelCount,
    int MixerTrackCount);
