using System.Globalization;
using System.Text;
using System.Text.Json;
using FruityLink.Core.Abstractions;
using FruityLink.FlStudio.Inject;
using Xunit;

namespace FruityLink.FlStudio.Tests;

public sealed class TimelineTests : IDisposable
{
    private readonly Func<string, int, CancellationToken, Task<string>>? original = FlInjectBridge.Transport;
    public void Dispose() => FlInjectBridge.Transport = original;

    [Theory]
    [InlineData(25, 0xd5c)]
    [InlineData(26, 0xd84)]
    public async Task MarkerRoundTripUsesRealObjectVersionLayoutAndOwnedUnicode(int version, int offset)
    {
        var native = new TimelineTransport(version, offset);
        var bridge = new FlInjectBridge();

        await bridge.AddMarkerAsync(0, "Intro 🎹 音");
        await bridge.AddMarkerAsync(768, "Chorus");
        string markers = await bridge.ListMarkersAsync();

        Assert.Equal(new[] { "Intro 🎹 音", "Chorus" }, native.MarkerNames);
        Assert.Contains("2 markers:", markers);
        Assert.Contains("Intro 🎹 音 @ tick 0 (bar 1)", markers);
        Assert.Contains("Chorus @ tick 768 (bar 3)", markers);
        Assert.All(native.Calls.Where(call => call[0] == "d523c0"), call =>
        {
            Assert.Equal(7, call.Length);
            Assert.Equal("50000", call[1]);
            Assert.Equal(new[] { "0", "4", "4" }, call[4..]);
        });
        Assert.Contains($"peekabs {0x50000 + offset:x} 8", native.Commands);
        Assert.Contains("peekabs 7fff8 8", native.Commands);
    }

    [Theory]
    [InlineData(384, 1152, "180", "480")]
    [InlineData(0, -1, "ffffffff", "ffffffff")]
    [InlineData(384, 384, "ffffffff", "ffffffff")]
    [InlineData(768, 384, "ffffffff", "ffffffff")]
    [InlineData(-20, 384, "0", "180")]
    [InlineData(-20, 0, "ffffffff", "ffffffff")]
    public async Task SelectionPassesBothNativeRefreshFlagsAndNeverWritesCachedFields(int start, int end, string expectedStart, string expectedEnd)
    {
        var native = new TimelineTransport();
        var bridge = new FlInjectBridge();

        await bridge.SetLoopRegionAsync(start, end);
        await bridge.SetLoopRegionAsync(start, end);

        var selections = native.Calls.Where(call => call[0] == "d41e60").ToArray();
        Assert.Equal(2, selections.Length);
        Assert.All(selections, call => Assert.Equal(new[] { "d41e60", "50000", expectedStart, expectedEnd, "1", "1" }, call));
        Assert.Contains("call da40c0 60000", native.Commands);
        Assert.DoesNotContain(native.Commands, command => command.StartsWith("poke", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NullActiveArrangementFallsBackThroughMainPlaylistIndirection(bool missingSlot)
    {
        var native = new TimelineTransport { ActiveSlot = missingSlot ? 0UL : 0x20000UL };
        native.Write(0x20000, BitConverter.GetBytes(0UL));

        await new FlInjectBridge().SetLoopRegionAsync(0, -1);

        Assert.Equal("60000", Assert.Single(native.Calls, call => call[0] == "d41e60")[1]);
        Assert.Contains("peekabs 20008 8", native.Commands);
    }

    [Theory]
    [InlineData("marker")]
    [InlineData("list")]
    [InlineData("loop")]
    public async Task UnverifiedLayoutFailsBeforeAnyPointerReadOrMutation(string operation)
    {
        var native = new TimelineTransport { IncludeLayout = false };
        var bridge = new FlInjectBridge();

        await Assert.ThrowsAsync<InvalidOperationException>(() => Invoke(bridge, operation));

        Assert.Equal(new[] { "syms" }, native.Commands);
    }

    [Fact]
    public async Task MissingObjectsAndMarkerManagerNeverReachNativeMutation()
    {
        var native = new TimelineTransport();
        native.Write(0x20000, BitConverter.GetBytes(0UL));
        native.Write(0x20008, BitConverter.GetBytes(0UL));
        var bridge = new FlInjectBridge();
        Assert.Equal("(no arrangement)", await bridge.ListMarkersAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => bridge.AddMarkerAsync(0, "Intro"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => bridge.SetLoopRegionAsync(0, -1));
        Assert.Empty(native.Calls);

        native.Write(0x20000, BitConverter.GetBytes(0x50000UL));
        native.Write(0x50d84, BitConverter.GetBytes(0UL));
        await Assert.ThrowsAsync<InvalidOperationException>(() => bridge.AddMarkerAsync(0, "Intro"));
        Assert.DoesNotContain("scratch", native.Commands);
    }

    [Theory]
    [InlineData(-1L)]
    [InlineData(1_000_001L)]
    [InlineData(4_294_967_297L)]
    public async Task Full64BitArrayCountIsValidatedBeforeReadingRecords(long count)
    {
        var native = new TimelineTransport();
        native.Write(0x7fff8, BitConverter.GetBytes(count));

        await Assert.ThrowsAsync<InvalidOperationException>(() => new FlInjectBridge().ListMarkersAsync());

        Assert.DoesNotContain(native.Commands, command => command.StartsWith("peekabs 80000", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NativeSelectionFailurePropagatesWithoutRepeatingOrRepainting()
    {
        var native = new TimelineTransport { FailCall = true };

        await Assert.ThrowsAsync<InvalidOperationException>(() => new FlInjectBridge().SetLoopRegionAsync(384, 1152));

        Assert.Single(native.Calls);
        Assert.DoesNotContain("call da40c0 60000", native.Commands);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"markerManagerOffset\":3460,\"markerStride\":12,\"markerTickOffset\":0,\"markerNameOffset\":8}")]
    [InlineData("{\"markerManagerOffset\":-1,\"markerStride\":52,\"markerTickOffset\":0,\"markerNameOffset\":8}")]
    public void MissingOrMalformedLayoutCannotApproveTimeline(string layout)
    {
        var status = FlInjectBridge.ParseSyms("{\"ver\":1,\"ok\":109,\"timelineLayout\":" + layout + "}");
        Assert.Null(status?.TimelineLayout);
    }

    [Fact]
    public async Task DeleteMarkerShiftsLaterRecordsShrinksTheArrayInPlaceAndRefreshesFl()
    {
        var native = new TimelineTransport();
        var bridge = new FlInjectBridge();
        await bridge.AddMarkerAsync(0, "Intro");
        await bridge.AddMarkerAsync(768, "Chorus");
        await bridge.AddMarkerAsync(1536, "Outro");

        await bridge.DeleteMarkerAsync(1);
        string markers = await bridge.ListMarkersAsync();

        Assert.Contains("2 markers:", markers);
        Assert.Contains("Intro @ tick 0 (bar 1)", markers);
        Assert.Contains("Outro @ tick 1536 (bar 5)", markers);
        Assert.DoesNotContain("Chorus", markers);
        Assert.Contains("pokeabs 7fff8 " + Convert.ToHexString(BitConverter.GetBytes(2L)).ToLowerInvariant(), native.Commands);
        Assert.Single(native.Commands, command => command.StartsWith("pokeabs 80034 ", StringComparison.Ordinal));
        Assert.Contains("call da40c0 60000", native.Commands);
    }

    [Fact]
    public async Task DeleteMarkerRefusesAMissingIndexWithoutWritingOrRefreshing()
    {
        var native = new TimelineTransport();
        var bridge = new FlInjectBridge();
        await bridge.AddMarkerAsync(0, "Intro");
        int before = native.Commands.Count;

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => bridge.DeleteMarkerAsync(1));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => bridge.DeleteMarkerAsync(-1));

        Assert.Contains("does not exist", error.Message);
        Assert.DoesNotContain(native.Commands.Skip(before), command => command.StartsWith("poke", StringComparison.Ordinal));
        Assert.DoesNotContain(native.Commands.Skip(before), command => command.StartsWith("call", StringComparison.Ordinal));
        Assert.Contains("Intro @ tick 0 (bar 1)", await bridge.ListMarkersAsync());
    }

    private static Task Invoke(FlInjectBridge bridge, string operation) => operation switch
    {
        "marker" => bridge.AddMarkerAsync(0, "Intro"),
        "list" => bridge.ListMarkersAsync(),
        _ => bridge.SetLoopRegionAsync(0, -1),
    };

    private sealed class TimelineTransport
    {
        private const string Ok = "{\"ok\":1,\"ret\":\"0x0\"}";
        private readonly Dictionary<ulong, byte> memory = [];
        private readonly FlTimelineLayout layout;
        private readonly int version;
        public ulong ActiveSlot { get; init; } = 0x20000;
        public bool IncludeLayout { get; init; } = true;
        public bool FailCall { get; init; }
        public List<string> Commands { get; } = [];
        public List<string[]> Calls { get; } = [];
        public List<string> MarkerNames { get; } = [];

        public TimelineTransport(int version = 26, int offset = 0xd84)
        {
            this.version = version;
            layout = new(offset, 0x34, 0, 8);
            Write(0x20000, BitConverter.GetBytes(0x50000UL));
            Write(0x20008, BitConverter.GetBytes(0x60000UL));
            Write(0x50000UL + (ulong)offset, BitConverter.GetBytes(0x70000UL));
            Write(0x70000, BitConverter.GetBytes(0x80000UL));
            Write(0x7fff8, BitConverter.GetBytes(0L));
            FlInjectBridge.Transport = Send;
        }

        public void Write(ulong address, byte[] bytes)
        {
            for (int index = 0; index < bytes.Length; index++) memory[address + (ulong)index] = bytes[index];
        }

        private byte[] Read(ulong address, int count)
            => Enumerable.Range(0, count).Select(index => memory[address + (ulong)index]).ToArray();

        private Task<string> Send(string command, int timeout, CancellationToken ct)
        {
            Commands.Add(command);
            string[] parts = command.Split(' ');
            string reply = parts[0] switch
            {
                "syms" => JsonSerializer.Serialize(new { ok = 109, fail = 0, supported = true, complete = true,
                    fileVersion = version == 25 ? "25.2.5.5319" : "26.1.3.5570",
                    timelineLayout = IncludeLayout ? layout : null }, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
                "peek" => Global(parts[1]),
                "peekabs" => Convert.ToHexString(Read(Hex(parts[1]), int.Parse(parts[2], CultureInfo.InvariantCulture))),
                "scratch" => "0x90000",
                "pokeabs" => Poke(parts),
                "call" => Call(parts[1..]),
                _ => throw new InvalidOperationException("Unexpected command: " + command),
            };
            return Task.FromResult(reply);
        }

        private string Global(string address) => Convert.ToHexString(BitConverter.GetBytes(address switch
        {
            "14aba80" => ActiveSlot,
            "14aab88" => 0x20008UL,
            "14a79f8" => 0UL,
            "149e8b4" => 0xFFFFFFFFUL,
            _ => throw new InvalidOperationException("Unexpected global: " + address),
        }));

        private string Poke(string[] parts)
        {
            Write(Hex(parts[1]), Convert.FromHexString(parts[2]));
            return Ok;
        }

        private string Call(string[] call)
        {
            Calls.Add(call);
            if (FailCall) return "{\"ok\":0}";
            if (call[0] != "d523c0") return Ok;
            Assert.Equal("50000", call[1]);
            ulong text = Hex(call[3]);
            Assert.Equal(-1, BitConverter.ToInt32(Read(text - 8, 4)));
            Assert.Equal((ushort)1200, BitConverter.ToUInt16(Read(text - 12, 2)));
            int length = BitConverter.ToInt32(Read(text - 4, 4));
            byte[] name = Read(text, length * 2);
            ulong record = 0x80000UL + (ulong)MarkerNames.Count * 0x34;
            ulong ownedName = 0xa0004UL + (ulong)MarkerNames.Count * 1024;
            Write(record, new byte[0x34]);
            Write(record, BitConverter.GetBytes((int)Hex(call[2])));
            Write(record + 8, BitConverter.GetBytes(ownedName));
            Write(ownedName - 4, BitConverter.GetBytes(length));
            Write(ownedName, name);
            MarkerNames.Add(Encoding.Unicode.GetString(name));
            Write(0x7fff8, BitConverter.GetBytes((long)MarkerNames.Count));
            return Ok;
        }

        private static ulong Hex(string value) => ulong.Parse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    }
}
