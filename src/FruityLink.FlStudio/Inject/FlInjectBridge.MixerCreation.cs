using System.Text.Json;
using FruityLink.Core.Abstractions;

namespace FruityLink.FlStudio.Inject;

public sealed partial class FlInjectBridge
{
    /// <inheritdoc />
    public async Task<int> AddMixerTrackAsync(int afterTrack = -1, CancellationToken ct = default)
    {
        if (afterTrack is < -1 or > 500) throw new ArgumentOutOfRangeException(nameof(afterTrack));
        await MixerLayoutAsync(ct);
        LogOp("AddMixerTrack", $"after={afterTrack}");
        // Count validation, insertion and result verification execute together on FL's UI thread.
        // A failed/ambiguous native call is never retried: insertion can shift every later index.
        string response = await RawAsync($"mixer_add {afterTrack}", 30000, ct);
        return ReadMixerInsertionResult(response);
    }

    private static int ReadMixerInsertionResult(string response)
    {
        try { return ParseMixerInsertionResult(response); }
        catch (JsonException error)
        {
            throw new InvalidOperationException("Mixer track insertion returned an unreadable response; inspect the mixer before another insertion.", error);
        }
    }

    private static int ParseMixerInsertionResult(string response)
    {
        using var document = JsonDocument.Parse(response);
        var result = document.RootElement;
        if (ReadInsertionInt(result, "ok") != 1) ThrowMixerInsertionFailure(result);
        int index = ReadInsertionInt(result, "index"), count = ReadInsertionInt(result, "count");
        if (index is < 1 or > 500 || count is < 3 or > 502 || index > count - 2)
            throw new InvalidOperationException("Mixer track insertion returned an invalid result; inspect the mixer before another insertion.");
        return index;
    }

    private static int ReadInsertionInt(JsonElement value, string property)
        => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(property, out var element)
            && element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out int number) ? number : -1;

    private static void ThrowMixerInsertionFailure(JsonElement result)
    {
        string reason = result.ValueKind == JsonValueKind.Object && result.TryGetProperty("reason", out var error)
            && error.ValueKind == JsonValueKind.String ? error.GetString() ?? "unknown" : "unknown";
        bool ambiguous = result.ValueKind == JsonValueKind.Object && result.TryGetProperty("mayHaveChanged", out var changed)
            && changed.ValueKind == JsonValueKind.True;
        throw new InvalidOperationException($"Mixer track insertion failed: {reason}."
            + (ambiguous ? " Native state may have changed; inspect the mixer before another insertion." : ""));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<FlMixerTrackInfo>> QueryMixerTracksAsync(CancellationToken ct = default)
    {
        var layout = await MixerLayoutAsync(ct);
        int count = await GetMixerTrackCountAsync(ct);
        ulong array = await GPtrAsync("14a7eb0", ct);
        if (array == 0) throw new InvalidOperationException("Mixer track array is unavailable.");
        var tracks = new List<FlMixerTrackInfo>(count - 1);
        for (int index = 0; index <= count - 2; index++)
        {
            ct.ThrowIfCancellationRequested();
            tracks.Add(await ReadMixerTrackInfoAsync(index, MixerTrackAddress(array, index, layout), layout, ct));
        }
        return tracks;
    }

    private async Task<FlMixerTrackInfo> ReadMixerTrackInfoAsync(int track, ulong address, FlMixerLayout layout, CancellationToken ct)
    {
        int type = await AI32Async(address + (ulong)layout.TypeOffset, ct);
        if (type != (track == 0 ? 0 : 1)) throw new InvalidOperationException("Mixer track topology does not match an addressable Master/ordinary insert.");
        ulong name = await APtrAsync(address + (ulong)layout.NameOffset, ct);
        string custom = name == 0 ? "" : await ReadDelphiStringAsync(name, ct);
        string label = string.IsNullOrEmpty(custom) ? (track == 0 ? "Master" : $"Insert {track}") : custom;
        return new(track, label, track == 0 ? "master" : "insert");
    }

    private async Task ValidateAddressableMixerTrackAsync(int track, CancellationToken ct)
        => ValidateMixerTrack(track, await GetMixerTrackCountAsync(ct));
}
