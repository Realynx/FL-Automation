namespace FruityLink.Agent.Versioning;

/// <summary>
/// Reads the current-arrangement index out of <c>INativeFlControl.ListArrangementsAsync</c> output
/// ("<c>* [i] name</c>" marks the current one). Needed so <c>native_clone_arrangement</c> can capture a
/// CONCRETE source index at commit time: a redo must re-clone from the same source, and passing "-1 =
/// current" would re-resolve against whatever is current at replay (which the undo just changed).
/// </summary>
public static class ArrangementList
{
    /// <summary>The index marked current (row beginning with '*'), or -1 when none is marked / unparsable.</summary>
    public static int CurrentIndex(string? listing)
    {
        if (string.IsNullOrWhiteSpace(listing)) return -1;
        foreach (var raw in listing.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (raw.Length == 0 || raw[0] != '*') continue;
            int open = raw.IndexOf('[');
            int close = open >= 0 ? raw.IndexOf(']', open + 1) : -1;
            if (open >= 0 && close > open + 1 && int.TryParse(raw.AsSpan(open + 1, close - open - 1), out int idx))
                return idx;
        }
        return -1;
    }
}
