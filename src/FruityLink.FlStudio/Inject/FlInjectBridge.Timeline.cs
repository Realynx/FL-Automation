using System.Text;
using FruityLink.Core.Abstractions;

namespace FruityLink.FlStudio.Inject;

// Timeline calls and marker reads share an exact-build contract supplied by the native scanner.
public sealed partial class FlInjectBridge
{
    private async Task<(int Start, int Last)> ReadTransportRangeAsync(CancellationToken ct)
    {
        try
        {
            ulong startPointer = await GPtrAsync("14a95f8", ct);
            ulong endPointer = await GPtrAsync("14abb38", ct);
            if (startPointer == 0 || endPointer == 0) return (-1, -1);
            int start = await AI32Async(startPointer, ct);
            int end = await AI32Async(endPointer, ct);
            // Native transport bounds are [start,end); preserve the historical inclusive text format.
            return start >= 0 && end > start ? (start, end - 1) : (-1, -1);
        }
        catch (InvalidOperationException) { return (-1, -1); }
    }

    private async Task<FlTimelineLayout> TimelineLayoutAsync(CancellationToken ct)
        => (await GetSymbolStatusAsync(ct).ConfigureAwait(false))?.TimelineLayout
            ?? throw new InvalidOperationException("The running FL Studio build has no verified timeline layout; marker and loop operations are unavailable.");

    private async Task<ulong> SongArrangementAsync(CancellationToken ct)
    {
        // FL's own arrangement resolver dereferences each indirection-table slot twice.
        // A nonzero slot can hold a null object; only then does FL use the main playlist.
        ulong slot = await GPtrAsync("14aba80", ct);
        ulong arrangement = slot == 0 ? 0 : await APtrAsync(slot, ct);
        if (arrangement != 0) return arrangement;
        slot = await GPtrAsync("14aab88", ct);
        return slot == 0 ? 0 : await APtrAsync(slot, ct);
    }

    /// <summary>Set the exclusive-end song loop/time selection, or clear it when the end
    /// does not follow the nonnegative start. FL updates its own selection and transport state.</summary>
    public async Task SetLoopRegionAsync(int startTick, int endTick, CancellationToken ct = default)
    {
        await TimelineLayoutAsync(ct);
        ulong arrangement = await SongArrangementAsync(ct);
        if (arrangement == 0) throw new InvalidOperationException("No arrangement.");
        int start = Math.Max(0, startTick);
        bool clear = endTick <= start;
        int end = clear ? -1 : endTick;
        if (clear) start = -1;
        LogOp("SetLoopRegion", clear ? "clear" : $"[{start}..{end})");
        // Native UI callers on both inspected versions supply refresh-controls=1, notify=1.
        // The fifth argument refreshes the actual transport range; omitting it leaves stale state.
        await CallAsync("d41e60", new ulong[] { arrangement, (uint)start, (uint)end, 1, 1 }, ct);
        await RepaintPlaylistAsync(ct);
    }

    /// <summary>Read timeline markers using the running version's verified manager and record layout.</summary>
    public async Task<string> ListMarkersAsync(CancellationToken ct = default)
    {
        var layout = await TimelineLayoutAsync(ct);
        ulong arrangement = await SongArrangementAsync(ct);
        if (arrangement == 0) return "(no arrangement)";
        ulong manager = await APtrAsync(checked(arrangement + (ulong)layout.MarkerManagerOffset), ct);
        if (manager == 0) return "(no markers)";
        ulong data = await APtrAsync(manager, ct);
        if (data == 0) return "(no markers)";
        if (data < 8) throw new InvalidOperationException("Invalid marker array pointer.");
        // A Delphi x64 dynamic-array length is a signed 64-bit value, not its low 32 bits.
        long count = BitConverter.ToInt64(await PeekAbsAsync(data - 8, 8, ct), 0);
        if (count is < 0 or > 1_000_000) throw new InvalidOperationException("Invalid marker array count.");
        if (count == 0) return "(no markers)";
        long ticksPerBar = (long)await GetPpqAsync(ct) * 4;
        var text = new StringBuilder();
        for (int index = 0; index < Math.Min(count, 200); index++)
        {
            ulong record = checked(data + (ulong)index * (ulong)layout.MarkerStride);
            int tick = await AI32Async(checked(record + (ulong)layout.MarkerTickOffset), ct);
            ulong name = await APtrAsync(checked(record + (ulong)layout.MarkerNameOffset), ct);
            string label = name == 0 ? "" : await ReadDelphiStringAsync(name, ct);
            string bar = ticksPerBar > 0 ? $" (bar {tick / ticksPerBar + 1})" : "";
            text.Append($"{(string.IsNullOrEmpty(label) ? "(marker)" : label)} @ tick {tick}{bar}\n");
        }
        return $"{count} markers:\n" + text.ToString().TrimEnd();
    }

    /// <summary>Add a plain named timeline marker. FL owns the copied Unicode name and refreshes its state.</summary>
    public async Task AddMarkerAsync(int tick, string name, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(name);
        var layout = await TimelineLayoutAsync(ct);
        ulong arrangement = await SongArrangementAsync(ct);
        if (arrangement == 0) throw new InvalidOperationException("No arrangement.");
        if (await APtrAsync(checked(arrangement + (ulong)layout.MarkerManagerOffset), ct) == 0)
            throw new InvalidOperationException("Timeline marker manager is unavailable.");
        using var scratch = await LeaseScratchAsync(ct).ConfigureAwait(false);
        ulong text = await WriteDelphiStringAsync(name, scratch, ct);
        await CallAsync("d523c0", new ulong[] { arrangement, (uint)Math.Max(0, tick), text, 0, 4, 4 }, ct);
    }
}
