using System.Buffers.Binary;
using System.IO;

namespace FruityLink.FlStudio;

/// <summary>Extracts plugin-state records (FLP event 213, "plugin data") from an <c>.flp</c>/<c>.fst</c> byte
/// image and attributes them to channel-rack channels and mixer FX slots.</summary>
/// <remarks>
/// FLP framing: <c>FLhd</c> + u32 length + header, then <c>FLdt</c> + u32 length + an event stream. Event ids
/// below 64 carry one byte, below 128 two bytes, below 192 four bytes, and 192..255 a 7-bit varint length
/// followed by that many bytes. Attribution (verified on FL 26.1.3 project files): a channel section starts
/// with event 64 (u16 channel index) and its plugin data follows as event 213; mixer inserts start with event
/// 236 (Master first, then inserts in physical order) and each occupied FX slot writes its plugin data as event
/// 213 followed by event 98 (u16 slot index) — empty slots emit only the 98 marker. Nothing else in the stream
/// is interpreted, so unknown events are skipped by size.
/// </remarks>
public static class FlpPluginStateReader
{
    public const int ChannelNewEvent = 64;
    public const int SlotIndexEvent = 98;
    public const int PluginDataEvent = 213;
    public const int InsertParamsEvent = 236;

    /// <summary>Plugin-state records found in one FLP image, keyed by channel index and by (track, slot).</summary>
    public sealed record FlpPluginStates(
        IReadOnlyDictionary<int, byte[]> Channels,
        IReadOnlyDictionary<(int Track, int Slot), byte[]> MixerSlots);

    /// <summary>The plugin-data record of a channel-rack channel, or null when the channel stores none
    /// (built-in Sampler channels, automation clips) or does not exist in the image.</summary>
    public static byte[]? FindChannelState(byte[] flp, int channel)
        => Parse(flp).Channels.TryGetValue(channel, out byte[]? data) ? data : null;

    /// <summary>The plugin-data record of a mixer FX slot (track 0 = Master), or null when the slot is empty
    /// or the track does not exist in the image.</summary>
    public static byte[]? FindMixerSlotState(byte[] flp, int track, int slot)
        => Parse(flp).MixerSlots.TryGetValue((track, slot), out byte[]? data) ? data : null;

    /// <summary>Walk the whole event stream once and collect every plugin-data record with its owner.</summary>
    public static FlpPluginStates Parse(byte[] flp)
    {
        ArgumentNullException.ThrowIfNull(flp);
        var channels = new Dictionary<int, byte[]>();
        var slots = new Dictionary<(int, int), byte[]>();
        ReadOnlySpan<byte> data = flp;
        int position = OpenDataChunk(data, out int end);

        int currentChannel = -1;
        int currentInsert = -1;        // -1 until the first insert section; then 0 = Master
        byte[]? pendingSlotData = null;
        while (position < end)
        {
            int id = data[position++];
            ReadOnlySpan<byte> payload = ReadPayload(data, id, ref position, end);
            switch (id)
            {
                case ChannelNewEvent:
                    currentChannel = BinaryPrimitives.ReadUInt16LittleEndian(payload);
                    break;
                case InsertParamsEvent:
                    currentInsert++;
                    pendingSlotData = null;
                    break;
                case SlotIndexEvent when currentInsert >= 0:
                    if (pendingSlotData is not null) slots[(currentInsert, BinaryPrimitives.ReadUInt16LittleEndian(payload))] = pendingSlotData;
                    pendingSlotData = null;
                    break;
                case PluginDataEvent:
                    if (currentInsert >= 0) pendingSlotData = payload.ToArray();
                    else if (currentChannel >= 0) channels[currentChannel] = payload.ToArray();
                    break;
            }
        }
        return new FlpPluginStates(channels, slots);
    }

    private static int OpenDataChunk(ReadOnlySpan<byte> data, out int end)
    {
        if (data.Length < 8 || !data[..4].SequenceEqual("FLhd"u8))
            throw new InvalidDataException("Not an FL Studio project/preset image (missing FLhd header).");
        long headerLength = BinaryPrimitives.ReadUInt32LittleEndian(data[4..]);
        long chunk = 8 + headerLength;
        if (chunk + 8 > data.Length || !data.Slice((int)chunk, 4).SequenceEqual("FLdt"u8))
            throw new InvalidDataException("FL project image has no FLdt data chunk.");
        long dataLength = BinaryPrimitives.ReadUInt32LittleEndian(data[(int)(chunk + 4)..]);
        long start = chunk + 8;
        if (start + dataLength > data.Length)
            throw new InvalidDataException("FL project image is truncated: the FLdt chunk exceeds the file.");
        end = (int)(start + dataLength);
        return (int)start;
    }

    private static ReadOnlySpan<byte> ReadPayload(ReadOnlySpan<byte> data, int id, ref int position, int end)
    {
        int size;
        if (id < 192)
            size = id < 64 ? 1 : id < 128 ? 2 : 4;
        else
        {
            long length = 0;
            int shift = 0;
            while (true)
            {
                if (position >= end) throw new InvalidDataException("FL event stream is truncated inside a length prefix.");
                byte b = data[position++];
                length |= (long)(b & 0x7F) << shift;
                shift += 7;
                if ((b & 0x80) == 0) break;
                if (shift > 35) throw new InvalidDataException("FL event length prefix is malformed.");
            }
            if (length > int.MaxValue) throw new InvalidDataException("FL event length prefix is malformed.");
            size = (int)length;
        }
        if (position + size > end) throw new InvalidDataException($"FL event {id} is truncated.");
        ReadOnlySpan<byte> payload = data.Slice(position, size);
        position += size;
        return payload;
    }
}
