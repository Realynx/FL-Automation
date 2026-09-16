using System.IO;
using FruityLink.FlStudio;
using Xunit;

namespace FruityLink.FlStudio.Tests;

public sealed class FlpPluginStateReaderTests
{
    [Fact]
    public void AttributesChannelAndMixerSlotRecordsLikeFlWritesThem()
    {
        byte[] chords = Payload(0xA1, 300);
        byte[] lead = Payload(0xB2, 200);
        byte[] masterLimiter = Payload(0xC3, 40);
        byte[] leadDelay = Payload(0xD4, 9000);
        byte[] leadReverb = Payload(0xE5, 130);
        byte[] image = Flp(
            Word(64, 0), Data(201, "Sampler"u8.ToArray()),                                   // sampler: no 213
            Word(64, 1), Data(201, "Fruity Wrapper"u8.ToArray()), Data(213, chords), Byte(32, 0),
            Word(64, 4), Data(213, lead), DWord(128, 5),
            Data(241, "Arrangement"u8.ToArray()),
            Data(236, new byte[12]), Data(213, masterLimiter), Word(98, 0), Word(98, 1), Data(235, new byte[] { 0 }),
            Data(236, new byte[12]), Word(98, 0), Word(98, 1), Data(235, new byte[] { 1 }),               // insert 1: empty
            Data(236, new byte[12]), Data(213, leadDelay), Word(98, 0), Data(213, leadReverb), Word(98, 1),
            Word(98, 2), Data(235, new byte[] { 1 }));

        var states = FlpPluginStateReader.Parse(image);

        Assert.Equal(new[] { 1, 4 }, states.Channels.Keys.OrderBy(k => k));
        Assert.Equal(chords, states.Channels[1]);
        Assert.Equal(lead, FlpPluginStateReader.FindChannelState(image, 4));
        Assert.Null(FlpPluginStateReader.FindChannelState(image, 0));
        Assert.Null(FlpPluginStateReader.FindChannelState(image, 7));
        Assert.Equal(masterLimiter, FlpPluginStateReader.FindMixerSlotState(image, 0, 0));
        Assert.Null(FlpPluginStateReader.FindMixerSlotState(image, 1, 0));
        Assert.Equal(leadDelay, FlpPluginStateReader.FindMixerSlotState(image, 2, 0));
        Assert.Equal(leadReverb, FlpPluginStateReader.FindMixerSlotState(image, 2, 1));
        Assert.Null(FlpPluginStateReader.FindMixerSlotState(image, 2, 2));
        Assert.Equal(3, states.MixerSlots.Count);
    }

    [Fact]
    public void VarintLengthsAboveOneBytePrefixAreDecoded()
    {
        byte[] big = Payload(0x5A, 70_000);   // needs a three-byte 7-bit length prefix
        byte[] image = Flp(Word(64, 2), Data(213, big));

        Assert.Equal(big, FlpPluginStateReader.FindChannelState(image, 2));
    }

    [Fact]
    public void RejectsForeignAndTruncatedImages()
    {
        Assert.Throws<InvalidDataException>(() => FlpPluginStateReader.Parse("RIFF....WAVE"u8.ToArray()));
        byte[] image = Flp(Word(64, 1), Data(213, Payload(1, 50)));
        byte[] truncated = image[..(image.Length - 10)];
        Assert.Throws<InvalidDataException>(() => FlpPluginStateReader.Parse(truncated));
        Assert.Throws<ArgumentNullException>(() => FlpPluginStateReader.Parse(null!));
    }

    private static byte[] Payload(byte seed, int length)
        => Enumerable.Range(0, length).Select(i => unchecked((byte)(seed + i))).ToArray();

    private static byte[] Byte(int id, byte value) => new[] { (byte)id, value };
    private static byte[] Word(int id, ushort value) => new[] { (byte)id, (byte)value, (byte)(value >> 8) };
    private static byte[] DWord(int id, uint value) => new[] { (byte)id, (byte)value, (byte)(value >> 8), (byte)(value >> 16), (byte)(value >> 24) };

    private static byte[] Data(int id, byte[] payload)
    {
        var bytes = new List<byte> { (byte)id };
        int length = payload.Length;
        do
        {
            byte b = (byte)(length & 0x7F);
            length >>= 7;
            if (length > 0) b |= 0x80;
            bytes.Add(b);
        } while (length > 0);
        bytes.AddRange(payload);
        return bytes.ToArray();
    }

    private static byte[] Flp(params byte[][] events)
    {
        byte[] data = events.SelectMany(e => e).ToArray();
        var image = new List<byte>();
        image.AddRange("FLhd"u8.ToArray());
        image.AddRange(BitConverter.GetBytes(6u));
        image.AddRange(new byte[] { 0, 0, 19, 0, 96, 0 });   // format, channel count, ppq
        image.AddRange("FLdt"u8.ToArray());
        image.AddRange(BitConverter.GetBytes((uint)data.Length));
        image.AddRange(data);
        return image.ToArray();
    }
}

public class PluginStateLoadEvidenceTests
{
    [Fact]
    public void ReportsUnavailableWhenEitherSnapshotIsMissing()
    {
        Assert.Contains("unavailable", FruityLink.FlStudio.Inject.FlInjectBridge.DescribeStateEvidence(null, new byte[] { 1 }));
        Assert.Contains("unavailable", FruityLink.FlStudio.Inject.FlInjectBridge.DescribeStateEvidence(new byte[] { 1 }, null));
    }

    [Fact]
    public void ReportsUnchangedForIdenticalRecords()
    {
        var text = FruityLink.FlStudio.Inject.FlInjectBridge.DescribeStateEvidence(new byte[] { 1, 2, 3 }, new byte[] { 1, 2, 3 });
        Assert.StartsWith("state record unchanged (3 bytes", text);
    }

    [Fact]
    public void ReportsFirstDifferenceForChangedRecords()
    {
        var text = FruityLink.FlStudio.Inject.FlInjectBridge.DescribeStateEvidence(new byte[] { 1, 2, 3 }, new byte[] { 1, 9, 3, 4 });
        Assert.StartsWith("state record changed (3 -> 4 bytes, sha256 ", text);
        Assert.EndsWith(", 2 differing bytes = 50%, first at byte 1)", text);   // one changed byte + one appended byte over 4
    }
}
