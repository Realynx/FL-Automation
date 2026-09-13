using FruityLink.Core.Abstractions;

namespace FruityLink.FlStudio.Inject;

public sealed partial class FlInjectBridge : IFlStructuredQuery
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<FlChannelInfo>> QueryChannelsAsync(CancellationToken ct = default)
    {
        int count = CheckedQueryCount(await GetChannelCountAsync(ct), 1000);
        var items = new List<FlChannelInfo>(count);
        for (int i = 0; i < count; i++)
            items.Add(new(i, await GetChannelNameAsync(i, ct), await GetChannelFxRouteAsync(i, ct),
                await GetChannelMutedAsync(i, ct), await GetChannelVolumeAsync(i, ct), await GetChannelPanAsync(i, ct)));
        return items;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<FlPatternInfo>> QueryPatternsAsync(CancellationToken ct = default)
    {
        int current = await GetCurrentPatternAsync(ct);
        ulong patterns = await GPtrAsync("14aa0c8", ct);
        var items = new List<FlPatternInfo>();
        for (int i = 1; i <= 999; i++)
        {
            if (await IsPatternEmptyAsync(i, ct)) continue;
            int length = patterns != 0 ? await AI32Async(patterns + (ulong)i * 0xC0 + 0x50, ct) : -1;
            ulong recorder = await NoteRecorderAsync(i, ct);
            int count = recorder != 0 ? await AI32Async(recorder + 0x14, ct) : 0;
            items.Add(new(i, await GetPatternNameAsync(i, ct),
                length is >= 0 and <= 100_000_000 ? length : null,
                count is >= 0 and <= 1_000_000 ? count : null, i == current));
        }
        return items;
    }

    private Task<ulong> NoteRecorderAsync(int pattern, CancellationToken ct) =>
        GPtrAsync((0x1803B90 + (ulong)pattern * 0xC0).ToString("x"), ct);

    /// <inheritdoc />
    public async Task<FlQueryPage<FlNoteInfo>> QueryNotesAsync(int pattern, int channel = -1,
        int offset = 0, int limit = 512, CancellationToken ct = default)
    {
        ValidateQueryPage(offset, limit);
        ArgumentOutOfRangeException.ThrowIfLessThan(channel, -1);
        int index = pattern <= 0 ? await GetCurrentPatternAsync(ct) : pattern;
        ValidatePattern(index);
        ulong recorder = await NoteRecorderAsync(index, ct);
        int count = recorder == 0 ? 0 : CheckedQueryCount(await AI32Async(recorder + 0x14, ct), 1_000_000);
        int take = Math.Min(Math.Max(count - offset, 0), limit);
        var items = new List<FlNoteInfo>(take);
        if (take > 0)
        {
            ulong data = await APtrAsync(recorder + 8, ct);
            RequireQueryData(data);
            byte[] raw = await PeekAbsAsync(data + (ulong)offset * NoteStride, take * NoteStride, ct);
            for (int i = 0; i < take; i++)
            {
                var note = DecodeQueryNote(raw, i * NoteStride, offset + i);
                if (channel < 0 || note.Channel == channel) items.Add(note);
            }
        }
        return QueryPage(items, offset, take, count);
    }

    private static FlNoteInfo DecodeQueryNote(byte[] raw, int start, int index) => new(index,
        BitConverter.ToUInt16(raw, start + 6), BitConverter.ToUInt16(raw, start + 0xC),
        BitConverter.ToInt32(raw, start), BitConverter.ToInt32(raw, start + 8), raw[start + 0x15],
        (raw[start + 0x13] & 0x20) != 0);

    /// <inheritdoc />
    public async Task<IReadOnlyList<FlPlaylistTrackInfo>> QueryPlaylistTracksAsync(CancellationToken ct = default)
    {
        ulong root = await PlaylistRootAsync(ct);
        RequireQueryData(root);
        var items = new List<FlPlaylistTrackInfo>(500);
        for (int i = 1; i <= 500; i++)
        {
            byte[] raw = await PeekAbsAsync(TrackField(root, i, 0), (int)PlaylistTrackStride, ct);
            string name = await ReadDelphiStringAsync(BitConverter.ToUInt64(raw, TrackFieldName), ct);
            items.Add(new(i, string.IsNullOrEmpty(name) ? $"Track {i}" : name,
                BgrToRgb(BitConverter.ToUInt32(raw, TrackFieldColor)), raw[TrackFieldEnabled] == 0,
                raw[TrackFieldCollapsed] != 0, raw[TrackFieldSelected] != 0, BitConverter.ToInt32(raw, TrackFieldMode)));
        }
        return items;
    }

    /// <inheritdoc />
    public async Task<FlQueryPage<FlClipInfo>> QueryClipsAsync(int track = -1,
        int offset = 0, int limit = 512, CancellationToken ct = default)
    {
        ValidateQueryPage(offset, limit);
        if (track != -1 && (track < 1 || track > 500)) throw new ArgumentOutOfRangeException(nameof(track));
        var (data, stride, rawCount) = await ClipCollectionAsync(ct);
        int count = CheckedQueryCount(rawCount, 1_000_000);
        int take = Math.Min(Math.Max(count - offset, 0), limit);
        var items = new List<FlClipInfo>(take);
        if (take > 0)
        {
            RequireQueryData(data);
            if (stride is < 0x24 or > 4096) throw new InvalidOperationException("Invalid clip record stride.");
            for (int i = offset; i < offset + take; i++)
            {
                byte[] raw = await PeekAbsAsync(data + (ulong)i * (ulong)stride, 0x24, ct);
                var clip = DecodeQueryClip(raw, i);
                if (track < 0 || clip.Track == track) items.Add(clip);
            }
        }
        return QueryPage(items, offset, take, count);
    }

    private static FlClipInfo DecodeQueryClip(byte[] raw, int index)
    {
        uint source = BitConverter.ToUInt32(raw, 4);
        bool pattern = source >= 0x50000000;
        return new(index, 500 - BitConverter.ToInt16(raw, 0xC), BitConverter.ToInt32(raw, 0),
            BitConverter.ToInt32(raw, 8), pattern ? "pattern" : "channel",
            (int)((pattern ? source - 0x50000000 : source) >> 16), (raw[0x13] & 0x20) != 0);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<FlArrangementInfo>> QueryArrangementsAsync(CancellationToken ct = default)
    {
        int count = CheckedQueryCount(checked((int)await CallAsync("11fb1a0", Array.Empty<ulong>(), ct)), 1000);
        int current = BitConverter.ToInt32(await PeekAsync("149e8b4", 4, ct), 0);
        var items = new List<FlArrangementInfo>(count);
        for (int i = 0; i < count; i++) items.Add(new(i, await GetArrangementNameAsync(i, ct), i == current));
        return items;
    }

    /// <inheritdoc />
    public async Task<FlQueryPage<FlPluginParameterInfo>> QueryPluginParametersAsync(int channelOrTrack,
        int slot = -1, string? filter = null, int offset = 0, int limit = 512, CancellationToken ct = default)
    {
        ValidateQueryPage(offset, limit);
        var (instance, rawCount, _) = await ResolvePluginAsync(channelOrTrack, slot, ct);
        if (instance == 0) throw new InvalidOperationException(PluginParametersUnavailableMessage(channelOrTrack, slot));
        int count = CheckedQueryCount(rawCount, 1_000_000);
        int take = Math.Min(Math.Max(count - offset, 0), limit);
        var items = new List<FlPluginParameterInfo>(take);
        for (int i = offset; i < offset + take; i++)
        {
            string name = await ReadParamNameAsync(instance, i, ct);
            if (!string.IsNullOrEmpty(filter) && !name.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
            int raw = await ReadParamValueAsync(instance, i, ct);
            items.Add(new(i, name, raw, await ReadParamValueStringAsync(instance, i, raw, ct)));
        }
        return QueryPage(items, offset, take, count);
    }

    /// <inheritdoc />
    public async Task<FlProjectInfo> QueryProjectAsync(CancellationToken ct = default)
    {
        string path = await ReadProjectPathAsync(ct);
        string title = await ReadDelphiStringAsync(await GPtrAsync("15812a0", ct), ct, maximumLength: 32768, strict: true);
        return new(title, path, IsUntitledPath(path));
    }

    private async Task<string> ReadProjectPathAsync(CancellationToken ct) =>
        await ReadDelphiStringAsync(await GPtrAsync("1581298", ct), ct, maximumLength: 32768, strict: true);

    /// <inheritdoc />
    public async Task<IReadOnlyList<FlAutomationPointInfo>> QueryAutomationPointsAsync(int channel, CancellationToken ct = default)
        => (await ReadAutomationAsync(channel, ct)).Points;

    private static void ValidateQueryPage(int offset, int limit)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        if (limit is < 1 or > 512) throw new ArgumentOutOfRangeException(nameof(limit), "Page size must be 1..512.");
    }

    private static int CheckedQueryCount(int count, int maximum)
    {
        if (count < 0 || count > maximum) throw new InvalidOperationException($"Implausible collection count: {count}.");
        return count;
    }

    private static void RequireQueryData(ulong data)
    {
        if (data == 0) throw new InvalidOperationException("Collection data is unavailable or changed during the query.");
    }

    private static FlQueryPage<T> QueryPage<T>(IReadOnlyList<T> items, int offset, int take, int total) =>
        new(items, offset + take < total ? offset + take : null, total);
}
