using System.Text.Json;
using FruityLink.Core.Abstractions;
using FruityLink.FlStudio.Inject;
using Xunit;

namespace FruityLink.FlStudio.Tests;

public sealed class MixerLayoutTests : IDisposable
{
    private readonly Func<string, int, CancellationToken, Task<string>>? original = FlInjectBridge.Transport;

    public void Dispose() => FlInjectBridge.Transport = original;

    [Theory]
    [InlineData(0, 0x1500)]
    [InlineData(1, 0x1478)]
    [InlineData(2, 0x1474)]
    public async Task MixerMutationUsesExplicitProfileInsteadOfVersionIndex(int version, int stride)
    {
        var layout = Layout(stride) with { EnabledOffset = 0x24 };
        var transport = new MixerTransport(layout, version);
        string expectedWrite = $"pokeabs {transport.Track(3) + (ulong)layout.EnabledOffset:x} 00";
        transport.Replies[expectedWrite] = "{\"ok\":1}";

        await new FlInjectBridge().SetMixerTrackMutedAsync(3, true);

        Assert.Contains(expectedWrite, transport.Commands);
        Assert.Single(transport.Commands, command => command.StartsWith("poke", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("{\"ver\":0,\"ok\":1,\"fail\":0}")]
    [InlineData("{\"ver\":1,\"ok\":1,\"fail\":0,\"mixerTrackStride\":5240}")]
    [InlineData("{\"ver\":2,\"ok\":1,\"fail\":0,\"mixerTrackStride\":5240,\"mixerLayout\":null}")]
    [InlineData("{\"ver\":2,\"ok\":1,\"fail\":0,\"mixerLayout\":{\"trackStride\":5240}}")]
    [InlineData("not-json")]
    public async Task UnverifiedLayoutRefusesMuteSendAndEffectBeforeAnyNativeMutation(string status)
    {
        var commands = new List<string>();
        FlInjectBridge.Transport = (command, _, _) =>
        {
            commands.Add(command);
            return Task.FromResult(status);
        };
        var bridge = new FlInjectBridge();

        var muteError = await Assert.ThrowsAsync<InvalidOperationException>(() => bridge.SetMixerTrackMutedAsync(3, true));
        var sendError = await Assert.ThrowsAsync<InvalidOperationException>(() => bridge.SetMixerSendAsync(3, 5, 1));
        var effectError = await Assert.ThrowsAsync<InvalidOperationException>(() => bridge.CloneMixerEffectAsync(3, 0, 1));

        Assert.Contains("no verified mixer-track layout", muteError.Message);
        Assert.Contains("no verified mixer-track layout", sendError.Message);
        Assert.Contains("no verified mixer-track layout", effectError.Message);
        Assert.All(commands, command => Assert.Equal("syms", command));
    }

    [Theory]
    [InlineData(0x1474, 0x2E4)]
    [InlineData(0x1478, 0x394)]
    public async Task SendWritesTheProfilesSendRecord(int stride, int sendTableOffset)
    {
        var layout = Layout(stride) with { SendTableOffset = sendTableOffset };
        var transport = new MixerTransport(layout) { RoutingManager = 0x400000 };
        ulong address = transport.Track(3) + (ulong)sendTableOffset + 5UL * (ulong)layout.SendStride + (ulong)layout.SendLevelOffset;
        string expectedWrite = $"pokeabs {address:x} {IntHex(8000).ToLowerInvariant()}";
        transport.Replies[expectedWrite] = "{\"ok\":1}";

        await new FlInjectBridge().SetMixerSendAsync(3, 5, 0.5);

        Assert.Contains(expectedWrite, transport.Commands);
        Assert.Single(transport.Commands, command => command.StartsWith("poke", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(0x1474, 0x1324)]
    [InlineData(0x1478, 0x134C)]
    public async Task EffectsUseTheProfilesSlotTableAndObjectIndex(int stride, int slotsOffset)
    {
        var layout = Layout(stride) with { EffectSlotsOffset = slotsOffset, EffectIndexOffset = 0x70 };
        var transport = new MixerTransport(layout);
        string pointerRead = $"peekabs {transport.Track(3) + (ulong)slotsOffset + 4UL * (ulong)layout.EffectSlotStride:x} 8";
        transport.Replies[pointerRead] = Hex(0x300000);
        string indexRead = $"peekabs {0x300000UL + (ulong)layout.EffectIndexOffset:x} 4";
        transport.Replies[indexRead] = IntHex(-1);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new FlInjectBridge().CloneMixerEffectAsync(3, 4, 5));

        Assert.Contains("empty", error.Message);
        Assert.Contains(pointerRead, transport.Commands);
        Assert.Contains(indexRead, transport.Commands);
        Assert.DoesNotContain(transport.Commands, IsMutation);
    }

    [Fact]
    public async Task NameReadUsesProfileNameAndTypeFields()
    {
        var layout = Layout() with { NameOffset = 0x30, TypeOffset = 0x28 };
        var transport = new MixerTransport(layout);
        transport.Replies[$"peekabs {transport.Track(3) + (ulong)layout.NameOffset:x} 8"] = Hex(0);
        transport.Replies[$"peekabs {transport.Track(3) + (ulong)layout.TypeOffset:x} 4"] = IntHex(1);

        Assert.Equal("Insert 3", await new FlInjectBridge().GetMixerTrackNameAsync(3));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(127)]
    [InlineData(199)]
    public async Task InvalidTrackCannotReadStructsOrMutateRouting(int track)
    {
        var transport = new MixerTransport(Layout());
        var bridge = new FlInjectBridge();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => bridge.GetMixerTrackNameAsync(track));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => bridge.SetMixerSendAsync(track, 0, 1));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => bridge.SetMixerSendAsync(0, track, 1));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => bridge.CloneMixerEffectAsync(track, 0, 1));

        Assert.DoesNotContain("peek 14a7eb0 8", transport.Commands);
        Assert.DoesNotContain(transport.Commands, IsMutation);
    }

    [Theory]
    [InlineData(0, 0x100000)]
    [InlineData(0x400000, 0)]
    public async Task MissingRoutingPointersAreRejectedBeforeRouteActivation(long manager, long array)
    {
        var transport = new MixerTransport(Layout()) { RoutingManager = (ulong)manager, ArrayBase = (ulong)array };

        await Assert.ThrowsAsync<InvalidOperationException>(() => new FlInjectBridge().SetMixerSendAsync(3, 5, 1));

        Assert.DoesNotContain(transport.Commands, IsMutation);
    }

    [Fact]
    public async Task SendTableMustFitInsideTheVerifiedTrackLayout()
    {
        var transport = new MixerTransport(Layout()) { TrackCount = 1000 };

        await Assert.ThrowsAsync<InvalidOperationException>(() => new FlInjectBridge().SetMixerSendAsync(3, 5, 1));

        Assert.DoesNotContain(transport.Commands, IsMutation);
    }

    [Fact]
    public async Task SixteenInsertProjectExcludesDormantSeventeenthSlotAndFixedCurrent()
    {
        var transport = new MixerTransport(Layout()) { TrackCount = 18 };
        transport.Replies[$"peekabs {transport.Track(16) + 0x0CUL:x} 8"] = Hex(0);
        transport.Replies[$"peekabs {transport.Track(16) + 0x08UL:x} 4"] = IntHex(1);
        var bridge = new FlInjectBridge();

        Assert.Equal(18, await bridge.GetMixerTrackCountAsync());
        Assert.Equal("Insert 16", await bridge.GetMixerTrackNameAsync(16));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => bridge.GetMixerTrackNameAsync(17));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => bridge.GetMixerTrackNameAsync(501));
        transport.Commands.Clear();
        var error = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => bridge.SetMixerSendAsync(0, 18, 1));

        Assert.Contains("0..16", error.Message);
        Assert.DoesNotContain("peek 14a7eb0 8", transport.Commands);
        Assert.DoesNotContain(transport.Commands, IsMutation);
    }

    [Fact]
    public async Task EmptyEffectSlotReportsItsLocationWithoutSamplerDiagnostic()
    {
        var layout = Layout();
        var transport = new MixerTransport(layout);
        transport.Replies[$"peekabs {transport.Track(3) + (ulong)layout.EffectSlotsOffset + 4UL * (ulong)layout.EffectSlotStride:x} 8"] = Hex(0);
        var bridge = new FlInjectBridge();

        string listing = await bridge.ListPluginParamsAsync(3, 4, null);
        var query = await Assert.ThrowsAsync<InvalidOperationException>(() => bridge.QueryPluginParametersAsync(3, 4));
        var write = await Assert.ThrowsAsync<InvalidOperationException>(() => bridge.SetPluginParamAsync(3, 4, 0, 0.5));

        Assert.Contains("Mixer track 3, effect slot 4", listing);
        Assert.Contains("slot may be empty", listing);
        Assert.DoesNotContain("Sampler", listing);
        Assert.Equal(listing, query.Message);
        Assert.Equal(listing, write.Message);
        Assert.DoesNotContain(transport.Commands, IsMutation);
    }

    private static bool IsMutation(string command)
        => command.StartsWith("poke", StringComparison.Ordinal) || command.StartsWith("call ", StringComparison.Ordinal);

    private static FlMixerLayout Layout(int stride = 0x1478)
        => new(stride, 0x0C, 0x08, 0x18, 0x1A, 0x394, 0x134C, 8, 0, 4, 8, 0x64, 0x58, 0xF0);

    private static string Hex(ulong value) => Convert.ToHexString(BitConverter.GetBytes(value));
    private static string IntHex(int value) => Convert.ToHexString(BitConverter.GetBytes(value));

    private sealed class MixerTransport
    {
        private readonly FlMixerLayout layout;
        private readonly string status;
        public List<string> Commands { get; } = [];
        public Dictionary<string, string> Replies { get; } = [];
        public int TrackCount { get; init; } = 127;
        public ulong RoutingManager { get; init; }
        public ulong ArrayBase { get; init; } = 0x100000;

        public MixerTransport(FlMixerLayout layout, int version = 0)
        {
            this.layout = layout;
            status = JsonSerializer.Serialize(new
            {
                ver = version, ok = 1, fail = 0, supported = true, complete = true, mixerLayout = layout,
            }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            FlInjectBridge.Transport = SendAsync;
        }

        public ulong Track(int index) => ArrayBase + (ulong)index * (ulong)layout.TrackStride;

        private Task<string> SendAsync(string command, int timeout, CancellationToken ct)
        {
            Commands.Add(command);
            if (Replies.TryGetValue(command, out string? reply)) return Task.FromResult(reply);
            string response = command switch
            {
                "syms" => status,
                "peek 14a9850 8" => Hex(0x20000),
                "peekabs 20000 4" => IntHex(TrackCount),
                "peek 14a7eb0 8" => Hex(ArrayBase),
                "peek 14a99a0 8" => Hex(0x20008),
                "peekabs 20008 8" => Hex(RoutingManager),
                _ when command.StartsWith("call ", StringComparison.Ordinal) => "{\"ok\":1,\"ret\":\"0x0\"}",
                _ => throw new InvalidOperationException("Unexpected native operation: " + command),
            };
            return Task.FromResult(response);
        }
    }
}
