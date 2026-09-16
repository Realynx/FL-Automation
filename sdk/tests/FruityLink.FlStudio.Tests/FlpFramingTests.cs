using System.IO;
using FruityLink.FlStudio;
using FruityLink.FlStudio.Inject;
using Xunit;

namespace FruityLink.FlStudio.Tests;

/// <summary>Event framing across FL builds: FL 26.1.3.5570 writes event 0xAC with a tag byte and a u16/u32 payload
/// (3 or 5 bytes), which the fixed four-byte rule misreads until a bogus "event 254 is truncated" (Parking Lot Moon,
/// 2026-09-14). Byte patterns copied from Parking-Lot-Moon-v015-master.flp offsets 46..58 and 176..190.</summary>
public sealed class FlpFramingTests
{
    // 1c 01 | ac 01 01 00 | c0 36 "FL Studio 26.1.3.5570.5570"(utf-16) — the version text event follows the tagged event.
    private static readonly byte[] TaggedU16 = { 0xAC, 0x01, 0x01, 0x00 };
    // c3 02 00 00 | ac 00 01 00 00 00 | ed 10 <16 bytes> — the project-time event follows the tagged event.
    private static readonly byte[] TaggedU32 = { 0xAC, 0x00, 0x01, 0x00, 0x00, 0x00 };

    [Fact]
    public void TaggedEventFramingLandsOnTheChunkEndAndAttributesRecords()
    {
        byte[] version = Utf16("FL Studio 26.1.3.5570.5570");
        byte[] lead = Payload(0xB2, 200);
        byte[] image = Flp(
            Data(199, "26.1.3.5570\0"u8.ToArray()), DWord(159, 5570), Byte(28, 1),
            TaggedU16, Data(192, version),
            Data(195, new byte[] { 0, 0 }), TaggedU32, Data(237, new byte[16]),
            Word(64, 1), Data(213, lead),
            Data(236, new byte[12]), Data(213, Payload(1, 20)), Word(98, 0));

        var states = FlpPluginStateReader.Parse(image);

        Assert.Equal(FlpPluginStateReader.Framing.Tagged, states.Framing);
        Assert.Equal("26.1.3.5570", states.Version);
        Assert.Equal("26.1.3.5570", FlpPluginStateReader.ReadVersion(image));
        Assert.Equal(lead, states.Channels[1]);
        Assert.Equal(20, states.MixerSlots[(0, 0)].Length);
    }

    [Fact]
    public void FixedFourByteRuleDesynchronisesOnTheTaggedEvent()
    {
        // ac 01 01 00 | c0 c8 01 <200 x 0xFE> read as four bytes swallows the text event id, so the walk lands one
        // byte ahead inside the text and reads 0xFE as an event with a runaway length prefix (the Parking Lot Moon
        // snapshot failed the same way with "event 254 is truncated").
        byte[] image = Flp(TaggedU16, Data(192, Enumerable.Repeat((byte)0xFE, 200).ToArray()), Word(64, 1), Data(213, Payload(3, 10)));

        var error = Assert.Throws<InvalidDataException>(() => FlpPluginStateReader.ParseWith(image, FlpPluginStateReader.Framing.Legacy));
        Assert.Contains("FL event 254 at offset", error.Message);
        Assert.Equal(10, FlpPluginStateReader.Parse(image).Channels[1].Length);
    }

    [Fact]
    public void LegacyFourByteEventIsStillAcceptedWhenTheTaggedRuleDesynchronises()
    {
        // A genuine four-byte 0xAC payload "01 00 00 c0": the tagged rule takes three bytes, then reads 0xC0 as a
        // text event whose length byte (0x40 from the channel event) overruns the chunk, so Parse falls back.
        byte[] legacy = { 0xAC, 0x01, 0x00, 0x00, 0xC0 };
        byte[] image = Flp(Data(199, "26.1.3.4726\0"u8.ToArray()), legacy, Word(64, 2), Data(213, Payload(7, 30)));

        var states = FlpPluginStateReader.Parse(image);

        Assert.Equal(FlpPluginStateReader.Framing.Legacy, states.Framing);
        Assert.Equal(30, states.Channels[2].Length);
    }

    [Fact]
    public void UnframeableStreamNamesEventOffsetBuildAndRemedy()
    {
        // 0xFE with a huge varint length: neither rule can walk this.
        byte[] image = Flp(Data(199, "26.1.3.5570\0"u8.ToArray()), Word(64, 1), new byte[] { 0xFE, 0xFF, 0xFF, 0x40 });

        var error = Assert.Throws<InvalidDataException>(() => FlpPluginStateReader.Parse(image));

        Assert.Contains("FL event 254 at offset", error.Message);
        Assert.Contains("written by FL 26.1.3.5570", error.Message);
        Assert.Contains("fl.project.save()", error.Message);
        Assert.NotNull(error.InnerException);
    }

    [Fact]
    public void StructuralErrorsAreNotRetriedAsFramingErrors()
    {
        var error = Assert.Throws<InvalidDataException>(() => FlpPluginStateReader.Parse("FLhd\0\0\0\0\0\0\0\0\0"u8.ToArray()));
        Assert.Contains("FLdt", error.Message);
        Assert.Null(error.InnerException);
    }

    [Fact]
    public void TimeoutMessageNamesTheBlockingWindowsWhenTheProbeHasThem()
    {
        string hint = UiThreadProbe.Format(hung: true, new[] { "Sign in - Super VHS", "Fruity Wrapper" })!;
        string text = InProcBridge.TimeoutMessage("set_plugin_param", 60000, hint);

        Assert.Contains("FL's UI thread is not processing messages; visible FL windows: 'Sign in - Super VHS', 'Fruity Wrapper'", text);
        Assert.Contains("dismissed in the GUI", text);
        Assert.EndsWith(": set_plugin_param", text);
        Assert.Null(UiThreadProbe.Format(hung: false, Array.Empty<string>()));
        Assert.Equal("FL Studio did not respond within 5 ms (it may be busy or showing a dialog): x", InProcBridge.TimeoutMessage("x", 5, null));
    }

    [Fact]
    public async Task TimeoutPathUsesTheProbeAndSurvivesAProbeFailure()
    {
        using var gate = new ManualResetEventSlim(false);
        try
        {
            var error = await Assert.ThrowsAsync<TimeoutException>(() =>
                InProcBridge.RawAsync("m", 20, CancellationToken.None, _ => { gate.Wait(); return "late"; }, () => throw new InvalidOperationException("no probe")));
            Assert.Contains("it may be busy or showing a dialog", error.Message);
            var named = await Assert.ThrowsAsync<TimeoutException>(() =>
                InProcBridge.RawAsync("m", 20, CancellationToken.None, _ => { gate.Wait(); return "late"; }, () => "visible FL windows: 'Sign in'"));
            Assert.Contains("visible FL windows: 'Sign in'", named.Message);
        }
        finally
        {
            gate.Set();
        }
    }

    private static byte[] Utf16(string text)
    {
        var bytes = new List<byte>(System.Text.Encoding.Unicode.GetBytes(text)) { 0, 0 };
        return bytes.ToArray();
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
        image.AddRange(new byte[] { 0, 0, 19, 0, 96, 0 });
        image.AddRange("FLdt"u8.ToArray());
        image.AddRange(BitConverter.GetBytes((uint)data.Length));
        image.AddRange(data);
        return image.ToArray();
    }
}
