using System.Text.Json;
using FruityLink.Core.Abstractions;

namespace FruityLink.FlStudio.Inject;

// Signature-scan symbol resolution: query the bridge's `syms` diagnostic so the managed side can hide
// tools whose native code the bridge couldn't locate on THIS FL version (multi-version portability).
// Partial of the FlInjectBridge god-class split; see FlInjectBridge.cs for the class doc.
public sealed partial class FlInjectBridge : IFlSymbolResolution
{
    // The FIRST authoritative `syms` answer, cached process-wide. Signature resolution runs ONCE on the
    // native side after FL loads and is immutable for the run, so one query serves every kernel build
    // (main agent + every sub-agent). Static so the cache is shared across the DI container's instances.
    private static volatile FlSymbolStatus? _cachedSymbolStatus;
    private static readonly SemaphoreSlim _symbolQueryGate = new(1, 1);

    /// <summary>
    /// <see cref="IFlSymbolResolution.GetSymbolStatusAsync"/>: send <c>syms</c>, parse the JSON
    /// (<c>{"ver":N,"ok":N,"fail":M,"unresolved":[{"name","why"}]}</c>), and cache the first
    /// AUTHORITATIVE answer. "Authoritative" = at least one symbol resolved (<c>ok &gt; 0</c>): before
    /// FL is ready the native resolver is a no-op and reports ver=0/ok=0/all-unresolved, and caching
    /// THAT would wrongly hide every gated tool for the whole run. So a not-yet-resolved response
    /// returns null (fail open) and is re-queried on the next call. Any transport/parse failure also
    /// returns null — never throws — so a diagnostic hiccup can't break tool advertisement.
    /// </summary>
    public async Task<FlSymbolStatus?> GetSymbolStatusAsync(CancellationToken ct = default)
    {
        FlSymbolStatus? cached = _cachedSymbolStatus;
        if (cached is not null) return cached;

        await _symbolQueryGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_cachedSymbolStatus is not null) return _cachedSymbolStatus;

            string json;
            try { json = await RawAsync("syms", 4000, ct).ConfigureAwait(false); }
            catch { return null; }   // bridge down / FL busy / timeout → unknown, fail open

            FlSymbolStatus? status = ParseSyms(json);

            // Only commit a real answer (resolution actually ran). ok==0 means FL wasn't ready when we
            // asked (or a catastrophic total miss) — don't cache it, so a later build re-queries.
            if (status is { Resolved: > 0 })
            {
                _cachedSymbolStatus = status;
                return status;
            }
            return null;
        }
        finally { _symbolQueryGate.Release(); }
    }

    /// <summary>Parse the <c>syms</c> JSON into a <see cref="FlSymbolStatus"/>; null on any malformed
    /// / non-JSON response (e.g. an <c>err:*</c> line). Tolerant: missing fields default to 0/empty.</summary>
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

            var unresolved = new HashSet<string>(StringComparer.Ordinal);
            if (root.TryGetProperty("unresolved", out JsonElement arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement e in arr.EnumerateArray())
                {
                    if (e.ValueKind == JsonValueKind.Object
                        && e.TryGetProperty("name", out JsonElement n)
                        && n.ValueKind == JsonValueKind.String)
                    {
                        string? name = n.GetString();
                        if (!string.IsNullOrEmpty(name)) unresolved.Add(name);
                    }
                }
            }
            return new FlSymbolStatus(ver, ok, fail, unresolved);
        }
        catch (JsonException) { return null; }
    }

    private static int ReadInt(JsonElement obj, string prop) =>
        obj.TryGetProperty(prop, out JsonElement e) && e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out int v)
            ? v : 0;
}
