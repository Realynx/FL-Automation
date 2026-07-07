namespace FruityLink.Agent.Versioning;

/// <summary>One parsed row of <c>INativeFlControl.ListClipsAsync</c> output.</summary>
/// <param name="Slot">The live slot index (volatile — shifts as clips are added/removed).</param>
/// <param name="Track">Playlist track (1-based).</param>
/// <param name="Start">Start tick (PPQ).</param>
/// <param name="Length">Length in ticks.</param>
/// <param name="Pattern">Source pattern (1-based) for a pattern clip, or -1 for audio/automation clips.</param>
/// <param name="Muted">True when the row carried the trailing "muted" marker (clip+0x13 bit 0x20).</param>
public readonly record struct ClipInfo(int Slot, int Track, int Start, int Length, int Pattern, bool Muted = false);

/// <summary>
/// Parses the <c>ListClipsAsync</c> string ("<c>[i] track T start=S len=L pattern N</c>") so clip moves/
/// resizes can be addressed by STABLE IDENTITY (pattern+track+start) instead of a volatile slot index:
/// the slot is resolved from the identity against LIVE state at capture and again at replay.
/// </summary>
public static class ClipList
{
    /// <summary>Parse every "<c>[i] …</c>" row; non-clip lines (headers, "(no clips)", continuation
    /// hints) are ignored. Tolerant: an unparsable row is skipped, never throws.</summary>
    public static List<ClipInfo> Parse(string? listing)
    {
        var result = new List<ClipInfo>();
        if (string.IsNullOrWhiteSpace(listing)) return result;
        foreach (var raw in listing.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (raw.Length == 0 || raw[0] != '[') continue;
            int close = raw.IndexOf(']');
            if (close < 2 || !int.TryParse(raw.AsSpan(1, close - 1), out int slot)) continue;

            int? track = FieldAfter(raw, "track ");
            int? start = FieldAfter(raw, "start=");
            int? len = FieldAfter(raw, "len=");
            int? pattern = FieldAfter(raw, "pattern ");   // absent for channel/audio clips
            bool muted = raw.Contains("muted", StringComparison.Ordinal);   // trailing marker (clip+0x13 bit 0x20)
            if (track is null || start is null || len is null) continue;
            result.Add(new ClipInfo(slot, track.Value, start.Value, len.Value, pattern ?? -1, muted));
        }
        return result;
    }

    /// <summary>The slot of the clip matching (<paramref name="pattern"/>, <paramref name="start"/>,
    /// <paramref name="track"/>), or -1 if none. Pattern clips only (a -1 pattern never matches).</summary>
    public static int FindSlot(IReadOnlyList<ClipInfo> clips, int pattern, int start, int track)
    {
        for (int i = 0; i < clips.Count; i++)
        {
            var c = clips[i];
            if (c.Pattern == pattern && c.Start == start && c.Track == track) return c.Slot;
        }
        return -1;
    }

    /// <summary>The parsed clip at <paramref name="slot"/>, or null.</summary>
    public static ClipInfo? AtSlot(IReadOnlyList<ClipInfo> clips, int slot)
    {
        foreach (var c in clips)
            if (c.Slot == slot) return c;
        return null;
    }

    /// <summary>Read the signed integer immediately following <paramref name="token"/> in a clip line
    /// (e.g. "start=" → 384, "pattern " → 1), or null when absent / not numeric.</summary>
    private static int? FieldAfter(string line, string token)
    {
        int i = line.IndexOf(token, StringComparison.Ordinal);
        if (i < 0) return null;
        int j = i + token.Length, k = j;
        if (k < line.Length && (line[k] == '-' || line[k] == '+')) k++;
        while (k < line.Length && char.IsDigit(line[k])) k++;
        return int.TryParse(line.AsSpan(j, k - j), out int v) ? v : null;
    }
}
