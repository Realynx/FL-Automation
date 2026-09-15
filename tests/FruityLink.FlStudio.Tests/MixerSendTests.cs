using System.Text.Json;
using FruityLink.Core.Abstractions;
using FruityLink.FlStudio.Inject;
using Xunit;

namespace FruityLink.FlStudio.Tests;

/// <summary>Typed send readback, route disconnect, the 16000 mixer fader range, channel-control ops and the
/// insert-capacity error text: the mixer half of the Parking Lot Moon fix batch.</summary>
public sealed class MixerSendTests : IDisposable
{
    private static readonly FlMixerLayout Layout = new(0x1478, 12, 8, 24, 26, 0x394, 0x134c, 8, 0, 4, 8, 0x64, 0x58, 0xf0);
    private const ulong ArrayBase = 0x100000UL, Manager = 0x300000UL;
    private const int NativeCount = 4;   // Master + 2 inserts + Current
    private readonly Func<string, int, CancellationToken, Task<string>>? original = FlInjectBridge.Transport;
    private readonly Dictionary<string, string> replies = new(StringComparer.Ordinal);
    private readonly List<string> commands = [];

    public MixerSendTests()
    {
        FlInjectBridge.Transport = SendAsync;
        replies["syms"] = JsonSerializer.Serialize(new { ok = 112, fail = 0, complete = true, supported = true, mixerLayout = Layout },
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        replies["peek 14a9850 8"] = Hex(0x20000UL);
        replies["peekabs 20000 4"] = Hex(NativeCount);
        replies["peek 14a7eb0 8"] = Hex(ArrayBase);
        replies["peek 14a99a0 8"] = Hex(0x2f0000UL);
        replies["peekabs 2f0000 8"] = Hex(Manager);
        for (int track = 0; track <= NativeCount - 2; track++)
        {
            ulong address = ArrayBase + (ulong)track * (ulong)Layout.TrackStride;
            replies[$"peekabs {address + 8:x} 4"] = Hex(track == 0 ? 0 : 1);
            replies[$"peekabs {address + 12:x} 8"] = Hex(0UL);   // default names: Master / Insert n
        }
    }

    public void Dispose() => FlInjectBridge.Transport = original;

    [Fact]
    public void DecodeActiveSendsSkipsInactiveRecordsAndScalesLevelsBy16000()
    {
        byte[] table = new byte[NativeCount * Layout.SendStride];
        Record(table, 0, 12800, active: true);
        Record(table, 1, 16000, active: false);
        Record(table, 2, 0, active: true);

        var sends = FlInjectBridge.DecodeActiveSends(table, NativeCount, Layout);

        Assert.Equal(new[] { (0, 0.8), (2, 0.0) }, sends.Select(send => (send.Destination, send.Level)));
        Assert.Throws<InvalidOperationException>(() => FlInjectBridge.DecodeActiveSends(new byte[8], NativeCount, Layout));
    }

    [Fact]
    public async Task QueryMixerSendsListsLevelZeroRoutesButNotDisconnectedOnes()
    {
        byte[] table = new byte[NativeCount * Layout.SendStride];
        Record(table, 0, 0, active: true);        // Master route zeroed but still connected
        Record(table, 1, 16000, active: false);   // disconnected
        Record(table, 2, 12800, active: true);    // bus at unity
        ulong track1 = ArrayBase + (ulong)Layout.TrackStride;
        replies[$"peekabs {track1 + (ulong)Layout.SendTableOffset:x} {table.Length}"] = Convert.ToHexString(table);

        var sends = await new FlInjectBridge().QueryMixerSendsAsync(1);

        Assert.Equal(new[] { (1, 0, "Master", 0.0, true), (1, 2, "Insert 2", 0.8, true) },
            sends.Select(send => (send.Source, send.Destination, send.DestinationName, send.Level, send.Active)));
        Assert.DoesNotContain(commands, command => command.StartsWith("poke", StringComparison.Ordinal) || command.StartsWith("call", StringComparison.Ordinal));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => new FlInjectBridge().QueryMixerSendsAsync(3));
    }

    [Theory]
    [InlineData(true, 0.8, "1")]
    [InlineData(false, 0.0, "0")]
    public async Task SetMixerSendPassesTheActiveFlagToTheRouteCoreAndWritesTheLevel(bool active, double level, string enable)
    {
        replies["call 11a67f0 300000 1 2 " + enable + " 1"] = "{\"ok\":1,\"ret\":\"0x0\"}";
        replies["call 11a5d20 300000"] = "{\"ok\":1,\"ret\":\"0x0\"}";
        ulong slot = ArrayBase + (ulong)Layout.TrackStride + (ulong)Layout.SendTableOffset + 2 * (ulong)Layout.SendStride;
        string poke = $"pokeabs {slot:x} {Convert.ToHexString(BitConverter.GetBytes((int)Math.Round(level * 16000)))}";
        replies[poke] = "{\"ok\":1}";

        await new FlInjectBridge().SetMixerSendAsync(1, 2, level, active);

        Assert.Equal(new[] { "call 11a67f0 300000 1 2 " + enable + " 1", poke, "call 11a5d20 300000" },
            commands.Where(command => command.StartsWith("call", StringComparison.Ordinal) || command.StartsWith("poke", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task MixerVolumeReachesTheFaderTopButNotBeyond()
    {
        uint id = FlInjectBridge.MixerTrackParamId(1, FlInjectBridge.MixerVolOffset);
        replies[$"call f53fe0 {id:x} 3e80 11"] = "{\"ok\":1,\"ret\":\"0x0\"}";
        var bridge = new FlInjectBridge();

        await bridge.SetMixerVolumeAsync(1, 16000);
        await bridge.SetMixerVolumeAsync(1, 20000);

        Assert.Equal(2, commands.Count(command => command == $"call f53fe0 {id:x} 3e80 11"));
        Assert.Equal(16000, FlInjectBridge.MixerVolumeMax);
        Assert.Equal(12800, FlInjectBridge.MixerVolumeUnity);
    }

    [Fact]
    public async Task ChannelControlsUseTheRecChanNamespaceAndRejectThePluginBlock()
    {
        replies["call f53fe0 8000e 0 2"] = "{\"ok\":1,\"ret\":\"0x4b0\"}";
        replies["call f53fe0 8000d 113a 11"] = "{\"ok\":1,\"ret\":\"0x0\"}";
        var bridge = new FlInjectBridge();

        Assert.Equal(1200, await bridge.GetChannelControlAsync(8, (int)FlInjectBridge.ChanStretchTime));
        await bridge.SetChannelControlAsync(8, (int)FlInjectBridge.ChanSmpOffset, 4410);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => bridge.SetChannelControlAsync(8, 0x2000, 1));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => bridge.GetChannelControlAsync(-1, 0));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => bridge.SetChannelControlAsync(8, 14, 1L << 40));

        Assert.Equal(new[] { "call f53fe0 8000e 0 2", "call f53fe0 8000d 113a 11" }, commands.Where(command => command.StartsWith("call", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData(17, 18, true)]
    [InlineData(600, 18, false)]
    [InlineData(-1, 18, false)]
    public void RangeMessageNamesTheInsertCountAndHowToGrowIt(int track, int count, bool growHint)
    {
        string message = FlInjectBridge.MixerTrackRangeMessage(track, count);

        Assert.Contains("0..16", message);
        Assert.Equal(growHint, message.Contains($"ensure_inserts({track})", StringComparison.Ordinal));
        Assert.Equal(growHint, message.Contains("capacity 500", StringComparison.Ordinal));
        Assert.Equal(500, FlInjectBridge.MaxMixerInserts);
    }

    private static void Record(byte[] table, int destination, int level, bool active)
    {
        int offset = destination * Layout.SendStride;
        BitConverter.GetBytes(level).CopyTo(table, offset + Layout.SendLevelOffset);
        table[offset + Layout.SendActiveOffset] = (byte)(active ? 1 : 0);
    }

    private Task<string> SendAsync(string command, int timeout, CancellationToken ct)
    {
        commands.Add(command);
        return Task.FromResult(replies.TryGetValue(command, out string? reply)
            ? reply : throw new InvalidOperationException("Unexpected native operation: " + command));
    }

    private static string Hex(ulong value) => Convert.ToHexString(BitConverter.GetBytes(value));
    private static string Hex(int value) => Convert.ToHexString(BitConverter.GetBytes(value));
}
