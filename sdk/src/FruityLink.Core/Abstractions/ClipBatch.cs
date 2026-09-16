namespace FruityLink.Core.Abstractions;

/// <summary>One playlist-clip move for a batch: <paramref name="Index"/> is the clip's slot index
/// (from ListClips), <paramref name="StartTick"/> the new absolute start in PPQ ticks, and
/// <paramref name="Track"/> the destination playlist track (1-based); a Track &lt; 0 keeps the
/// clip on its current track.</summary>
public readonly record struct ClipMove(int Index, int StartTick, int Track);

/// <summary>One playlist-clip resize for a batch: set the clip at <paramref name="Index"/> to
/// <paramref name="LengthTick"/> PPQ ticks.</summary>
public readonly record struct ClipResize(int Index, int LengthTick);

/// <summary>One pattern-clip placement for a batch: place <paramref name="Pattern"/> (1-based) on
/// playlist track <paramref name="Track"/> (1-based) at <paramref name="StartTick"/> for
/// <paramref name="LengthTick"/> ticks (&lt;= 0 = the pattern's own length).</summary>
public readonly record struct PatternClipSpec(int Pattern, int Track, int StartTick, int LengthTick);
