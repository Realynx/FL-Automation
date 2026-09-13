namespace FruityLink.Core.Abstractions;

/// <summary>A verified automation destination. Kind is channel_volume, channel_pan, channel_pitch,
/// mixer_volume, mixer_pan, or plugin_parameter. Index is the source channel or mixer index.
/// For plugin_parameter, Slot=-1 denotes a generator; Slot=0..9 denotes a mixer effect.
/// Parameter is the plugin parameter index. Other kinds require Slot=Parameter=-1.</summary>
public sealed record FlAutomationTarget(string Kind, int Index, int Slot = -1, int Parameter = -1);

/// <summary>One point in a complete automation envelope. Times are beats; Curve=0 is linear.</summary>
public readonly record struct FlAutomationPointSpec(double TimeBeats, double Value, double Tension = 0, int Curve = 0);

/// <summary>The created automation generator channel and its placed playlist clip collection index.</summary>
public sealed record FlAutomationClipResult(int Channel, int ClipIndex);
