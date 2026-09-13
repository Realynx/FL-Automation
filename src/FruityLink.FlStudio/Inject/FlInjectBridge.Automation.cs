using System.Globalization;
using System.Text;
using System.Text.Json;
using FruityLink.Core.Abstractions;

namespace FruityLink.FlStudio.Inject;

public sealed partial class FlInjectBridge
{
    private sealed record AutomationSnapshot(int Channel, uint SourceEventId, IReadOnlyList<FlAutomationPointInfo> Points);
    private static readonly JsonSerializerOptions AutomationJson = new(JsonSerializerDefaults.Web);

    private async Task RequireAutomationAsync(CancellationToken ct)
    {
        var status = await GetSymbolStatusAsync(ct);
        if (status?.AutomationClips != true)
            throw new NotSupportedException("Automation clips require a verified layout for this exact FL Studio build.");
    }

    private async Task<AutomationSnapshot> AutomationCommandAsync(string command, CancellationToken ct)
    {
        await RequireAutomationAsync(ct);
        string response = await RawAsync(command, 30000, ct);
        try { return ParseAutomationResponse(response); }
        catch (JsonException error)
        {
            throw new InvalidOperationException("Automation returned an unreadable response. Inspect the project before retrying an edit.", error);
        }
    }

    private static AutomationSnapshot ParseAutomationResponse(string response)
    {
        using var document = JsonDocument.Parse(response);
        var root = document.RootElement;
        if (ReadInsertionInt(root, "ok") != 1) ThrowAutomationFailure(root);
        int channel = ReadInsertionInt(root, "channel");
        var (source, pointValues) = AutomationMetadata(root);
        var points = pointValues.Deserialize<List<FlAutomationPointInfo>>(AutomationJson)
            ?? throw new JsonException("Missing points.");
        if (channel < 0 || points.Count > 4000) throw new JsonException("Invalid automation result.");
        ValidateAutomationReadback(points);
        return new(channel, source, points);
    }

    private static (uint Source, JsonElement Points) AutomationMetadata(JsonElement root)
    {
        if (!root.TryGetProperty("sourceEventId", out var sourceValue) || sourceValue.ValueKind != JsonValueKind.Number ||
            !sourceValue.TryGetUInt32(out uint source) || source >= 0x10000000 || (source & 0xffff) != 0 ||
            !root.TryGetProperty("points", out var pointValues) || pointValues.ValueKind != JsonValueKind.Array)
            throw new JsonException("Invalid automation metadata.");
        return (source, pointValues);
    }

    private static void ValidateAutomationReadback(IReadOnlyList<FlAutomationPointInfo> points)
    {
        double previous = 0;
        for (int i = 0; i < points.Count; i++)
        {
            var point = points[i];
            if (point is null || point.Index != i || !ValidAutomationPoint(point.TimeBeats, point.Value, point.Tension) ||
                point.TimeBeats < previous || point.Curve is < 0 or > 255)
                throw new JsonException("Invalid automation point.");
            previous = point.TimeBeats;
        }
    }

    private static void ThrowAutomationFailure(JsonElement root)
    {
        string reason = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("reason", out var error)
            && error.ValueKind == JsonValueKind.String ? error.GetString() ?? "unknown" : "invalid-response";
        bool changed = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("mayHaveChanged", out var value)
            && value.ValueKind == JsonValueKind.True;
        throw new InvalidOperationException($"Automation operation refused: {reason}." +
            (changed ? " The project may have changed; inspect it before retrying." : ""));
    }

    private Task<AutomationSnapshot> ReadAutomationAsync(int channel, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(channel);
        return AutomationCommandAsync($"automation_read {channel}", ct);
    }

    /// <inheritdoc />
    public async Task<string> ListAutomationPointsAsync(int channel, CancellationToken ct = default)
    {
        var snapshot = await ReadAutomationAsync(channel, ct);
        ulong channelObject = await ChannelObjAsync(channel, ct);
        string? targets = await DescribeAutomationTargetsAsync(channelObject, ct);
        var text = new StringBuilder();
        if (targets is not null) text.AppendLine("automates: " + (targets.Length == 0 ? "nothing (no target link yet)" : targets));
        text.AppendLine($"{snapshot.Points.Count} points (time in beats):");
        foreach (var point in snapshot.Points)
            text.AppendLine(FormattableString.Invariant($"  [{point.Index}] t={point.TimeBeats:0.###} value={point.Value:0.###} tension={point.Tension:0.###} curve={point.Curve}"));
        return text.ToString().TrimEnd();
    }

    private static bool ValidAutomationPoint(double time, double value, double tension) =>
        double.IsFinite(time) && time >= 0 && time <= float.MaxValue &&
        double.IsFinite(value) && value is >= 0 and <= 1 &&
        double.IsFinite(tension) && tension is >= -1 and <= 1;

    private static string PointArguments(double time, double value, double tension, int curve) =>
        string.Join(" ", time.ToString("R", CultureInfo.InvariantCulture), value.ToString("R", CultureInfo.InvariantCulture),
            tension.ToString("R", CultureInfo.InvariantCulture), curve.ToString(CultureInfo.InvariantCulture));

    /// <inheritdoc />
    public async Task AddAutomationPointAsync(int channel, double timeBeats, double value, double tension, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(channel);
        if (!ValidAutomationPoint(timeBeats, value, tension)) throw new ArgumentOutOfRangeException(nameof(timeBeats), "Point values must be finite and within the documented ranges.");
        await AutomationCommandAsync($"automation_add {channel} " + PointArguments(timeBeats, value, tension, 0), ct);
        await RefreshRackAsync(ct);
    }

    /// <inheritdoc />
    public async Task DeleteAutomationPointAsync(int channel, int index, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(channel);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(index);
        await AutomationCommandAsync($"automation_delete {channel} {index}", ct);
        await RefreshRackAsync(ct);
    }

    /// <inheritdoc />
    public async Task SetAutomationPointsAsync(int channel, IReadOnlyList<FlAutomationPointSpec> points, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(channel);
        ValidateAutomationPoints(points);
        string arguments = string.Join(" ", points.Select(p => PointArguments(p.TimeBeats, p.Value, p.Tension, p.Curve)));
        await AutomationCommandAsync($"automation_set {channel} {points.Count} {arguments}", ct);
        await RefreshRackAsync(ct);
        await RecomputeSongLengthAsync(ct);
        await RepaintPlaylistAsync(ct);
    }

    private static void ValidateAutomationPoints(IReadOnlyList<FlAutomationPointSpec> points)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (points.Count is < 2 or > 4000 || points[0].TimeBeats != 0)
            throw new ArgumentException("Use 2..4000 points, starting at beat zero.", nameof(points));
        double previous = -1;
        foreach (var point in points)
        {
            if (!ValidAutomationPoint(point.TimeBeats, point.Value, point.Tension) ||
                point.Curve != 0 || point.TimeBeats <= previous)
                throw new ArgumentException("Points must be finite, strictly ordered, and use linear curve 0.", nameof(points));
            previous = point.TimeBeats;
        }
    }
}
