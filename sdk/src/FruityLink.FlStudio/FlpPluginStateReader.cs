using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace FruityLink.FlStudio;

/// <summary>Extracts plugin-state records (FLP event 213, "plugin data") from an <c>.flp</c>/<c>.fst</c> byte
/// image and attributes them to channel-rack channels and mixer FX slots.</summary>
/// <remarks>
/// FLP framing: <c>FLhd</c> + u32 length + header, then <c>FLdt</c> + u32 length + an event stream. Event ids
/// below 64 carry one byte, below 128 two bytes, below 192 four bytes, and 192..255 a 7-bit varint length
/// followed by that many bytes. FL 26.1.3.5570 breaks the fixed-size rule for event 172 (0xAC): it writes a tag
/// byte followed by a u16 (tag 1) or a u32 (tag 0) — <c>ac 01 01 00</c> before the "FL Studio 26.1.3.5570"
/// text event and <c>ac 00 01 00 00 00</c> before the project-time event in every project saved by that build
/// (44 files checked; build 4726 projects do not write the event). Reading it as four bytes desynchronises the
/// stream and eventually reports a bogus "event 254 is truncated". The reader therefore walks the stream with
/// the tagged rule first and the legacy fixed-size rule second, accepting only a walk that lands exactly on the
/// chunk end. Attribution (verified on FL 26.1.3 project files): a channel section starts with event 64 (u16
/// channel index) and its plugin data follows as event 213; mixer inserts start with event 236 (Master first,
/// then inserts in physical order) and each occupied FX slot writes its plugin data as event 213 followed by
/// event 98 (u16 slot index) — empty slots emit only the 98 marker. Nothing else in the stream is interpreted,
/// so unknown events are skipped by size.
/// </remarks>
public static class FlpPluginStateReader
{
    public const int ChannelNewEvent = 64;
    public const int SlotIndexEvent = 98;
    public const int PluginDataEvent = 213;
    public const int InsertParamsEvent = 236;
    /// <summary>FL 26.1.3.5570 writes this id with a tag byte and a tag-dependent payload (see the class remarks).</summary>
    public const int TaggedEvent = 0xAC;
    public const int VersionTextEvent = 199;

    /// <summary>Event-size rules the reader knows; tried in order until one walks the stream to its end.</summary>
    public enum Framing
    {
        /// <summary>Fixed sizes by id range, except event 0xAC = tag + (tag 1 ? u16 : u32). FL 26.1.3.5570.</summary>
        Tagged,
        /// <summary>Fixed sizes by id range for every id below 192 (FL 26.1.3.4726 and older files).</summary>
        Legacy,
    }

    /// <summary>Plugin-state records found in one FLP image, keyed by channel index and by (track, slot).</summary>
    public sealed record FlpPluginStates(
        IReadOnlyDictionary<int, byte[]> Channels,
        IReadOnlyDictionary<(int Track, int Slot), byte[]> MixerSlots)
    {
        /// <summary>The framing rule that walked the stream to its end.</summary>
        public Framing Framing { get; init; }

        /// <summary>The writer's version text (event 199), or "" when the image carries none.</summary>
        public string Version { get; init; } = "";
    }

    /// <summary>The plugin-data record of a channel-rack channel, or null when the channel stores none
    /// (built-in Sampler channels, automation clips) or does not exist in the image.</summary>
    public static byte[]? FindChannelState(byte[] flp, int channel)
        => Parse(flp).Channels.TryGetValue(channel, out byte[]? data) ? data : null;

    /// <summary>The plugin-data record of a mixer FX slot (track 0 = Master), or null when the slot is empty
    /// or the track does not exist in the image.</summary>
    public static byte[]? FindMixerSlotState(byte[] flp, int track, int slot)
        => Parse(flp).MixerSlots.TryGetValue((track, slot), out byte[]? data) ? data : null;

    /// <summary>The writer's version text (event 199, e.g. "26.1.3.5570") without walking past it; "" when
    /// absent or unreadable. Used to make parse errors name the FL build that wrote the file.</summary>
    public static string ReadVersion(byte[] flp)
    {
        try
        {
            ReadOnlySpan<byte> data = flp;
            int position = OpenDataChunk(data, out int end);
            for (int i = 0; i < 8 && position < end; i++)
            {
                int id = data[position++];
                ReadOnlySpan<byte> payload = ReadPayload(data, id, ref position, end, Framing.Tagged);
                if (id == VersionTextEvent) return Encoding.ASCII.GetString(payload).TrimEnd('\0');
            }
        }
        catch (InvalidDataException) { }
        return "";
    }

    /// <summary>Walk the whole event stream once and collect every plugin-data record with its owner.</summary>
    /// <exception cref="InvalidDataException">The image is not an FLP, or no known framing rule walks its event
    /// stream to the chunk end; the message names the offending event, offset, writer version and remedy.</exception>
    public static FlpPluginStates Parse(byte[] flp)
    {
        ArgumentNullException.ThrowIfNull(flp);
        InvalidDataException? first = null;
        foreach (Framing framing in new[] { Framing.Tagged, Framing.Legacy })
        {
            try { return ParseWith(flp, framing); }
            catch (InvalidDataException error) when (error.Data.Contains("offset"))
            {
                first ??= error;
            }
        }
        string version = ReadVersion(flp);
        throw new InvalidDataException(
            $"{first!.Message} The FL project snapshot (written by FL {(version.Length > 0 ? version : "unknown build")}) " +
            "could not be framed by any known event-size rule (tagged 0xAC and legacy). Remedy: save the project " +
            "(fl.project.save()) or close it to a new version and reopen it, then retry; if the error persists, keep " +
            "a copy of the .flp so the reader's event-size table can be extended for this FL build.", first);
    }

    /// <summary>Walk the stream with one framing rule. Throws when an event overruns the chunk or the walk does
    /// not land exactly on the chunk end (a desynchronised walk can "succeed" by luck otherwise).</summary>
    public static FlpPluginStates ParseWith(byte[] flp, Framing framing)
    {
        ArgumentNullException.ThrowIfNull(flp);
        var channels = new Dictionary<int, byte[]>();
        var slots = new Dictionary<(int, int), byte[]>();
        ReadOnlySpan<byte> data = flp;
        int position = OpenDataChunk(data, out int end);
        string version = "";

        int currentChannel = -1;
        int currentInsert = -1;        // -1 until the first insert section; then 0 = Master
        byte[]? pendingSlotData = null;
        while (position < end)
        {
            int id = data[position++];
            ReadOnlySpan<byte> payload = ReadPayload(data, id, ref position, end, framing);
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
                case VersionTextEvent when version.Length == 0:
                    version = Encoding.ASCII.GetString(payload).TrimEnd('\0');
                    break;
            }
        }
        if (position != end)
            throw Desync($"FL event stream walked past the FLdt chunk end ({position} > {end}) under the {framing} framing rule.", position);
        return new FlpPluginStates(channels, slots) { Framing = framing, Version = version };
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

    private static ReadOnlySpan<byte> ReadPayload(ReadOnlySpan<byte> data, int id, ref int position, int end, Framing framing)
    {
        int size;
        int eventStart = position - 1;
        if (id == TaggedEvent && framing == Framing.Tagged)
        {
            if (position >= end) throw Desync($"FL event 0x{id:X2} at offset {eventStart} has no tag byte.", eventStart);
            size = 1 + (data[position] == 1 ? 2 : 4);
        }
        else if (id < 192)
            size = id < 64 ? 1 : id < 128 ? 2 : 4;
        else
        {
            long length = 0;
            int shift = 0;
            while (true)
            {
                if (position >= end) throw Desync($"FL event {id} at offset {eventStart} is truncated inside its length prefix.", eventStart);
                byte b = data[position++];
                length |= (long)(b & 0x7F) << shift;
                shift += 7;
                if ((b & 0x80) == 0) break;
                if (shift > 35) throw Desync($"FL event {id} at offset {eventStart} has a malformed length prefix.", eventStart);
            }
            if (length > int.MaxValue) throw Desync($"FL event {id} at offset {eventStart} has a malformed length prefix.", eventStart);
            size = (int)length;
        }
        if (position + size > end)
            throw Desync($"FL event {id} at offset {eventStart} is truncated ({size} bytes claimed, {end - position} left in the chunk).", eventStart);
        ReadOnlySpan<byte> payload = data.Slice(position, size);
        position += size;
        return payload;
    }

    /// <summary>A framing failure: the stream may simply be desynchronised, so <see cref="Parse"/> retries with the
    /// next rule. Tagged with the offset so structural errors (no header, no chunk) are not retried.</summary>
    private static InvalidDataException Desync(string message, int offset)
    {
        var error = new InvalidDataException(message);
        error.Data["offset"] = offset;
        return error;
    }
}
