using System.Globalization;

namespace FruityLink.Agent.Versioning;

/// <summary>One parsed row of <c>INativeFlControl.ListAutomationPointsAsync</c> output.</summary>
/// <param name="Index">The live point index (volatile — shifts as points are added/removed).</param>
/// <param name="Time">Absolute time in beats.</param>
/// <param name="Value">Point value 0..1.</param>
/// <param name="Tension">Point tension -1..1.</param>
/// <param name="Curve">Curve type byte (0 = the only shape <c>AddAutomationPointAsync</c> can reproduce).</param>
public readonly record struct AutoPointInfo(int Index, double Time, double Value, double Tension, int Curve);

/// <summary>
/// Parses <c>ListAutomationPointsAsync</c> rows ("<c>[i] t=T value=V tension=X curve=C</c>") so automation
/// points can be addressed by STABLE IDENTITY (channel + time-in-beats) rather than a volatile index: the
/// index is resolved from the identity against LIVE state at replay (add/delete both shift indices).
/// </summary>
public static class AutomationPointList
{
    /// <summary>Parse every "<c>[i] t=… value=… tension=… curve=…</c>" row; header / "(no points)" lines are
    /// ignored. Tolerant: an unparsable row is skipped, never throws.</summary>
    public static List<AutoPointInfo> Parse(string? listing)
    {
        var result = new List<AutoPointInfo>();
        if (string.IsNullOrWhiteSpace(listing)) return result;
        foreach (var raw in listing.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int open = raw.IndexOf('[');
            if (open < 0) continue;
            int close = raw.IndexOf(']', open + 1);
            if (close <= open + 1 || !int.TryParse(raw.AsSpan(open + 1, close - open - 1), out int idx)) continue;

            double? t = DoubleAfter(raw, "t=");
            double? v = DoubleAfter(raw, "value=");
            if (t is null || v is null) continue;
            double ten = DoubleAfter(raw, "tension=") ?? 0;
            int cv = (int)(DoubleAfter(raw, "curve=") ?? 0);
            result.Add(new AutoPointInfo(idx, t.Value, v.Value, ten, cv));
        }
        return result;
    }

    /// <summary>The index of the point whose time matches <paramref name="timeBeats"/> most closely (within
    /// <paramref name="epsilon"/> beats), or -1 if none is close enough. Time is the clip's stable identity;
    /// index resolution picks the nearest so a tiny formatting/round-trip drift still hits the right point.</summary>
    public static int FindIndex(IReadOnlyList<AutoPointInfo> points, double timeBeats, double epsilon = 0.01)
    {
        int best = -1;
        double bestDelta = double.MaxValue;
        for (int i = 0; i < points.Count; i++)
        {
            double delta = Math.Abs(points[i].Time - timeBeats);
            if (delta < bestDelta) { bestDelta = delta; best = points[i].Index; }
        }
        return bestDelta <= epsilon ? best : -1;
    }

    /// <summary>Read the signed decimal immediately following <paramref name="token"/> (e.g. "t=" → 4.0), or
    /// null when absent / not numeric. Invariant culture so a comma-decimal locale can't misparse.</summary>
    private static double? DoubleAfter(string line, string token)
    {
        int i = line.IndexOf(token, StringComparison.Ordinal);
        if (i < 0) return null;
        int j = i + token.Length, k = j;
        if (k < line.Length && (line[k] == '-' || line[k] == '+')) k++;
        while (k < line.Length && (char.IsDigit(line[k]) || line[k] == '.')) k++;
        return double.TryParse(line.AsSpan(j, k - j), NumberStyles.Float, CultureInfo.InvariantCulture, out double val)
            ? val : null;
    }
}
