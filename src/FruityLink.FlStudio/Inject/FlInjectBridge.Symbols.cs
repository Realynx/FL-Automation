using System.Text.Json;
using FruityLink.Core.Abstractions;

namespace FruityLink.FlStudio.Inject;

// Signature-scan symbol resolution: query the bridge's `syms` diagnostic so the managed side can hide
// tools whose native code the bridge couldn't locate on THIS FL version (multi-version portability).
// Partial of the FlInjectBridge god-class split; see FlInjectBridge.cs for the class doc.
public sealed partial class FlInjectBridge : IFlSymbolResolution
{
    // A pipe can reconnect to a different FL process/version. Only the in-process transport has a
    // stable lifetime, so cache its answer and associate it with the transport that produced it.
    private sealed record SymbolCache(Func<string, int, CancellationToken, Task<string>> Transport, FlSymbolStatus Status);
    private static volatile SymbolCache? _cachedSymbolStatus;
    private static readonly SemaphoreSlim _symbolQueryGate = new(1, 1);

    /// <summary>
    /// <see cref="IFlSymbolResolution.GetSymbolStatusAsync"/>: send <c>syms</c>, parse the JSON
    /// (<c>{"ver":N,"ok":N,"fail":M,"unresolved":[{"name","why"}],"complete":true}</c>), and
    /// cache the first authoritative answer. Explicit completion is authoritative even when every
    /// symbol failed or no scanner supports the engine. Explicit pending responses are re-queried;
    /// older diagnostics without completion metadata require at least one resolved symbol. This
    /// prevents both caching startup as failure and treating completed unsupported scans as unknown.
    /// Any transport/parse failure also
    /// returns null so a diagnostic hiccup can't break tool advertisement. Caller cancellation
    /// propagates. Pipe answers are queried again because a reconnect may reach another FL version.
    /// </summary>
    public async Task<FlSymbolStatus?> GetSymbolStatusAsync(CancellationToken ct = default)
    {
        await _symbolQueryGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var transport = Transport;
            var cached = _cachedSymbolStatus;
            if (transport is not null && cached?.Transport == transport) return cached.Status;

            string json;
            try { json = await RawAsync("syms", 4000, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { return null; }   // bridge down / FL busy / timeout → unknown, fail open

            FlSymbolStatus? status = ParseSyms(json);

            if (status is { Complete: true } or { Complete: null, Resolved: > 0 })
            {
                if (Transport != transport) return null;
                if (transport is not null) _cachedSymbolStatus = new SymbolCache(transport, status);
                return status;
            }
            return null;
        }
        finally { _symbolQueryGate.Release(); }
    }

    /// <summary>Parse the <c>syms</c> JSON into a <see cref="FlSymbolStatus"/>; null on any malformed
    /// / non-JSON response (e.g. an <c>err:*</c> line). Legacy missing fields default to 0/empty;
    /// absent optional metadata stays null. Present metadata must have its declared JSON type.</summary>
    internal static FlSymbolStatus? ParseSyms(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json.Trim());
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            int ver  = ReadInt(root, "ver");
            int ok   = ReadInt(root, "ok");
            int fail = ReadInt(root, "fail");

            return new FlSymbolStatus(ver, ok, fail, ReadUnresolved(root))
            {
                FileVersion = ReadOptionalString(root, "fileVersion"),
                Scanner = ReadOptionalString(root, "scanner"),
                Supported = ReadOptionalBoolean(root, "supported"),
                Complete = ReadOptionalBoolean(root, "complete"),
                MixerTrackStride = ReadMixerTrackStride(root, ver),
                MixerLayout = ReadMixerLayout(root),
                TimelineLayout = ReadTimelineLayout(root),
                AutomationClips = ReadOptionalBoolean(root, "automationClips")
            };
        }
        catch (JsonException) { return null; }
    }

    private static int ReadInt(JsonElement obj, string prop) =>
        obj.TryGetProperty(prop, out JsonElement e) && e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out int v)
            ? v : 0;

    private static IReadOnlySet<string> ReadUnresolved(JsonElement root)
    {
        var unresolved = new HashSet<string>(StringComparer.Ordinal);
        if (!root.TryGetProperty("unresolved", out JsonElement arr) || arr.ValueKind != JsonValueKind.Array)
            return unresolved;

        foreach (JsonElement entry in arr.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object ||
                !entry.TryGetProperty("name", out JsonElement name) || name.ValueKind != JsonValueKind.String)
                continue;
            string? value = name.GetString();
            if (!string.IsNullOrEmpty(value)) unresolved.Add(value);
        }
        return unresolved;
    }

    private static string? ReadOptionalString(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.String) throw new JsonException($"Expected string metadata: {property}.");
        return value.GetString();
    }

    private static bool? ReadOptionalBoolean(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new JsonException($"Expected Boolean metadata: {property}.")
        };
    }

    private static int? ReadMixerTrackStride(JsonElement root, int legacyVersion)
    {
        // Compatibility for bridges predating scanner metadata; explicit null never inherits a layout.
        if (!root.TryGetProperty("mixerTrackStride", out var value))
            return legacyVersion switch { 1 => 0x1474, 2 => 0x1478, _ => null };
        if (value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int stride) && stride > 0)
            return stride;
        throw new JsonException("Expected positive mixer-track stride or null.");
    }

    private static FlMixerLayout? ReadMixerLayout(JsonElement root)
    {
        if (!root.TryGetProperty("mixerLayout", out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.Object) throw new JsonException("Expected a complete mixer layout object or null.");
        var layout = new FlMixerLayout(
            ReadLayoutInt(value, "trackStride", 1),
            ReadLayoutInt(value, "nameOffset"),
            ReadLayoutInt(value, "typeOffset"),
            ReadLayoutInt(value, "enabledOffset"),
            ReadLayoutInt(value, "soloOffset"),
            ReadLayoutInt(value, "sendTableOffset"),
            ReadLayoutInt(value, "effectSlotsOffset"),
            ReadLayoutInt(value, "sendStride", 1),
            ReadLayoutInt(value, "sendLevelOffset"),
            ReadLayoutInt(value, "sendActiveOffset"),
            ReadLayoutInt(value, "effectSlotStride", 8),
            ReadLayoutInt(value, "effectIndexOffset"),
            ReadLayoutInt(value, "effectNameOffset"),
            ReadLayoutInt(value, "effectLoadVtableOffset"));
        ValidateMixerLayout(layout);
        return layout;
    }

    private static FlTimelineLayout? ReadTimelineLayout(JsonElement root)
    {
        if (!root.TryGetProperty("timelineLayout", out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.Object) throw new JsonException("Expected a complete timeline layout object or null.");
        var layout = new FlTimelineLayout(
            ReadLayoutInt(value, "markerManagerOffset", 8), ReadLayoutInt(value, "markerStride", 12),
            ReadLayoutInt(value, "markerTickOffset"), ReadLayoutInt(value, "markerNameOffset"));
        if (layout.MarkerManagerOffset > 65536 || layout.MarkerStride > 4096)
            throw new JsonException("Timeline layout exceeds supported bounds.");
        RequireFieldFits(layout.MarkerTickOffset, 4, layout.MarkerStride, "markerTickOffset");
        RequireFieldFits(layout.MarkerNameOffset, 8, layout.MarkerStride, "markerNameOffset");
        return layout;
    }

    private static int ReadLayoutInt(JsonElement root, string property, int minimum = 0)
    {
        if (root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number &&
            value.TryGetInt32(out int number) && number >= minimum) return number;
        throw new JsonException($"Missing or invalid native layout field: {property}.");
    }

    private static void ValidateMixerLayout(FlMixerLayout layout)
    {
        RequireFieldFits(layout.NameOffset, 8, layout.TrackStride, "nameOffset");
        RequireFieldFits(layout.TypeOffset, 4, layout.TrackStride, "typeOffset");
        RequireFieldFits(layout.EnabledOffset, 1, layout.TrackStride, "enabledOffset");
        RequireFieldFits(layout.SoloOffset, 1, layout.TrackStride, "soloOffset");
        RequireFieldFits(layout.SendTableOffset, layout.SendStride, layout.TrackStride, "sendTableOffset");
        RequireFieldFits(layout.EffectSlotsOffset, 9L * layout.EffectSlotStride + 8, layout.TrackStride, "effectSlotsOffset");
        RequireFieldFits(layout.SendLevelOffset, 4, layout.SendStride, "sendLevelOffset");
        RequireFieldFits(layout.SendActiveOffset, 1, layout.SendStride, "sendActiveOffset");
    }

    private static void RequireFieldFits(int offset, long width, int capacity, string field)
    {
        if ((long)offset + width > capacity) throw new JsonException($"Native layout field exceeds its containing structure: {field}.");
    }
}
