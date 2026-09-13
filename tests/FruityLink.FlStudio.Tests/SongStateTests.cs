using FruityLink.FlStudio.Inject;
using Xunit;

namespace FruityLink.FlStudio.Tests;

public sealed class SongStateTests : IDisposable
{
    private readonly Func<string, int, CancellationToken, Task<string>>? original = FlInjectBridge.Transport;

    public void Dispose() => FlInjectBridge.Transport = original;

    [Theory]
    [InlineData(384, 1152, "[384..1151]")]
    [InlineData(0, 1536, "[0..1535]")]
    [InlineData(12, 13, "[12..12]")]
    [InlineData(1, int.MaxValue, "[1..2147483646]")]
    public async Task ReportsActualTransportRangeIndependentlyOfToolbarSeekBounds(int start, int end, string expected)
    {
        var transport = new SongTransport([Clip(0, 1536)]) { RangeStart = start, RangeEnd = end, HasToolbar = true };
        var bridge = new FlInjectBridge();

        Assert.Contains("playRange=" + expected, await bridge.GetSongStateAsync());
        Assert.Contains("playRange=" + expected, await bridge.DiagTransportAsync());

        Assert.DoesNotContain("peekabs 823b8 4", transport.Commands);
        Assert.DoesNotContain("peekabs 823bc 4", transport.Commands);
        Assert.Contains("peekabs 70000 4", transport.Commands);
        Assert.Contains("peekabs 70010 4", transport.Commands);
    }

    [Theory]
    [InlineData(null, 1536)]
    [InlineData(0, null)]
    [InlineData(-1, 1536)]
    [InlineData(384, 384)]
    [InlineData(1152, 384)]
    [InlineData(0, int.MinValue)]
    public async Task MissingOrInvalidNativeRangeNeverFallsBackToToolbarDomain(int? start, int? end)
    {
        _ = new SongTransport([Clip(0, 1536)]) { RangeStart = start, RangeEnd = end, HasToolbar = true };
        var bridge = new FlInjectBridge();

        Assert.Contains("playRange=[-1..-1]", await bridge.GetSongStateAsync());
        Assert.Contains("playRange=[-1..-1]", await bridge.DiagTransportAsync());
    }

    [Fact]
    public async Task UnresolvedNativeRangeIsReportedUnavailable()
    {
        _ = new SongTransport([Clip(0, 1536)]) { RangeReadFailure = new InvalidOperationException("unresolved symbol"), HasToolbar = true };

        Assert.Contains("playRange=[-1..-1]", await new FlInjectBridge().GetSongStateAsync());
    }

    [Fact]
    public async Task CancelledNativeRangeReadPropagatesCancellation()
    {
        _ = new SongTransport([Clip(0, 1536)]) { RangeReadFailure = new OperationCanceledException() };

        await Assert.ThrowsAsync<OperationCanceledException>(() => new FlInjectBridge().GetSongStateAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReportsFourBarsFromLiveClipSpansRegardlessOfSelection(bool selected)
    {
        var transport = new SongTransport([Clip(0, 384), Clip(384, 1152, selected)]);

        var state = await new FlInjectBridge().GetSongStateAsync();

        Assert.Contains("ppq=96 songLength=4 bars", state);
        Assert.DoesNotContain("peek 14aab88 8", transport.Commands);
        Assert.Contains("peekabs 30000 20", transport.Commands);
        Assert.Contains("peekabs 30038 20", transport.Commands);
    }

    [Fact]
    public async Task UnselectedAudioChannelZeroClipContributesItsFullLength()
    {
        var transport = new SongTransport([Clip(0, 384), Clip(1152, 384, source: 0)]);

        var state = await new FlInjectBridge().GetSongStateAsync();

        Assert.Contains("songLength=4 bars", state);
        Assert.Equal(2, transport.ClipReads);
    }

    [Theory]
    [InlineData(0, 0x20000, 0x30000, 1)]
    [InlineData(0x10000, 0, 0x30000, 1)]
    [InlineData(0x10000, 0x20000, 0, 1)]
    [InlineData(0x10000, 0x20000, 0x30000, 0)]
    public async Task MissingOrEmptyCollectionHasNoContentLength(long arrangement, long collection, long data, int count)
    {
        var transport = new SongTransport([]) { Arrangement = (ulong)arrangement, Collection = (ulong)collection, Data = (ulong)data, Count = count };

        var state = await new FlInjectBridge().GetSongStateAsync();

        Assert.Contains("songLength=0 bars", state);
        Assert.Equal(0, transport.ClipReads);
    }

    [Fact]
    public async Task IgnoresMalformedSpansAndRemovedSlotsBeyondTheLiveCount()
    {
        var transport = new SongTransport([
            Clip(0, 1536), Clip(-1, int.MaxValue), Clip(int.MaxValue, 0),
            Clip(int.MaxValue, -1), Clip(int.MaxValue, int.MaxValue)]) { Count = 4 };

        var state = await new FlInjectBridge().GetSongStateAsync();

        Assert.Contains("songLength=4 bars", state);
        Assert.Equal(4, transport.ClipReads);
        Assert.DoesNotContain("peekabs 300e0 20", transport.Commands);
    }

    [Theory]
    [InlineData(0, 1537, 96, 5)]
    [InlineData(int.MaxValue, int.MaxValue, 96, 11_184_811)]
    [InlineData(int.MaxValue, int.MaxValue, int.MaxValue, 1)]
    public async Task ClipEndAndCeilingBarArithmeticDoNotOverflow(int start, int length, int ppq, long expectedBars)
    {
        _ = new SongTransport([Clip(start, length)]) { Ppq = ppq };

        var state = await new FlInjectBridge().GetSongStateAsync();

        Assert.Contains($"songLength={expectedBars} bars", state);
    }

    [Theory]
    [InlineData(0x13, 1)]
    [InlineData(4097, 1)]
    [InlineData(0x38, 1_000_001)]
    public async Task ImplausibleCollectionLayoutFailsBeforeReadingClipData(int stride, int count)
    {
        var transport = new SongTransport([]) { Stride = stride, Count = count };

        await Assert.ThrowsAsync<InvalidOperationException>(() => new FlInjectBridge().GetSongStateAsync());

        Assert.Equal(0, transport.ClipReads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MovingClipRefreshesCurrentArrangementWithoutAnyCachedFieldFallback(bool refreshFault)
    {
        var transport = new SongTransport([Clip(0, 384)]) { RefreshFault = refreshFault };

        await new FlInjectBridge().MoveClipAsync(0, 1152, -1);

        Assert.Contains("call 11fc880 2", transport.Commands);
        Assert.Equal("pokeabs 30000 80040000", Assert.Single(transport.Commands, command => command.StartsWith("poke", StringComparison.Ordinal)));
        Assert.Single(transport.Commands, command => command == "peek 14aab88 8"); // Repaint only.
        Assert.DoesNotContain(transport.Commands, command => command.Contains("60b04", StringComparison.Ordinal) || command.Contains("60d5c", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(1536, false)]
    [InlineData(int.MinValue, true)]
    public async Task LegacyDiagnosticMutationRequestsFailBeforeNativeAccess(int force, bool recompute)
    {
        var transport = new SongTransport([]);

        await Assert.ThrowsAsync<NotSupportedException>(() => new FlInjectBridge().DiagSongScopeAsync(force, recompute));

        Assert.Empty(transport.Commands);
    }

    [Fact]
    public async Task ScopeDiagnosticReportsContentWithoutReadingCachedFields()
    {
        var transport = new SongTransport([Clip(0, 1536)]);

        var state = await new FlInjectBridge().DiagSongScopeAsync();

        Assert.Equal("maxClipEnd=1536 cachedFields=unavailable", state);
        Assert.DoesNotContain("peek 14aab88 8", transport.Commands);
    }

    [Fact]
    public async Task TransportDiagnosticDoesNotMislabelLegacyFieldsAsSongLength()
    {
        var transport = new SongTransport([Clip(0, 1536)]);

        var state = await new FlInjectBridge().DiagTransportAsync();

        Assert.Contains("contentEndTicks=1536 cachedSongFields=unavailable", state);
        Assert.DoesNotContain("songLenTicks=", state);
        Assert.DoesNotContain("peek 14aab88 8", transport.Commands);
    }

    private static byte[] Clip(int start, int length, bool selected = false, uint source = 0x50010000)
    {
        var bytes = new byte[0x14];
        BitConverter.GetBytes(start).CopyTo(bytes, 0);
        BitConverter.GetBytes(source).CopyTo(bytes, 4);
        BitConverter.GetBytes(length).CopyTo(bytes, 8);
        bytes[0x13] = selected ? (byte)0x80 : (byte)0;
        return bytes;
    }

    private sealed class SongTransport
    {
        private readonly byte[][] clips;
        public List<string> Commands { get; } = [];
        public ulong Arrangement { get; init; } = 0x10000;
        public ulong Collection { get; init; } = 0x20000;
        public ulong Data { get; init; } = 0x30000;
        public int Count { get; init; }
        public int Stride { get; init; } = 0x38;
        public int Ppq { get; init; } = 96;
        public int ClipReads { get; private set; }
        public bool RefreshFault { get; init; }
        public int? RangeStart { get; init; } = 0;
        public int? RangeEnd { get; init; } = 1536;
        public bool HasToolbar { get; init; }
        public Exception? RangeReadFailure { get; init; }

        public SongTransport(byte[][] clips)
        {
            this.clips = clips;
            Count = clips.Length;
            FlInjectBridge.Transport = SendAsync;
        }

        private Task<string> SendAsync(string command, int timeout, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Commands.Add(command);
            string result = command switch
            {
                "call 11e32c0" => $"{{\"ok\":1,\"ret\":\"0x{Arrangement:x}\"}}",
                "peekabs 10014 8" => Hex(Collection),
                "peekabs 20008 8" => Hex(Data),
                "peekabs 20010 4" => IntHex(Stride),
                "peekabs 20014 4" => IntHex(Count),
                "peek 14a79f8 8" => Hex(0x40000),
                "peekabs 40000 4" => IntHex(Ppq),
                "peek 14a8670 8" or "peek 14a81c0 8" => Hex(0),
                _ when command.StartsWith("call ", StringComparison.Ordinal) && command.EndsWith(" 0 2", StringComparison.Ordinal)
                    => "{\"ok\":1,\"ret\":\"0x0\"}",
                _ => TransportReply(command),
            };
            return Task.FromResult(result);
        }

        private string TransportReply(string command) => command switch
        {
            "peek 14a95f8 8" when RangeReadFailure is not null => throw RangeReadFailure,
            "peek 14a95f8 8" => Hex(RangeStart.HasValue ? 0x70000UL : 0),
            "peek 14abb38 8" => Hex(RangeEnd.HasValue ? 0x70010UL : 0),
            "peekabs 70000 4" => IntHex(RangeStart!.Value),
            "peekabs 70010 4" => IntHex(RangeEnd!.Value),
            "peek 14aa4c8 8" => Hex(HasToolbar ? 0x80000UL : 0),
            "peekabs 80000 8" => Hex(0x81000),
            "peekabs 817e8 8" => Hex(0x82000),
            "peekabs 823c0 4" => IntHex(600),
            "peekabs 823b8 4" => IntHex(0),
            "peekabs 823bc 4" => IntHex(1535),
            _ => EditReply(command),
        };

        private string EditReply(string command) => command switch
        {
            "peek 14aab88 8" => Hex(0x50000),
            "peekabs 50000 8" => Hex(0x60000),
            "peek 149e8b4 4" => IntHex(2),
            "call da40c0 60000" => "{\"ok\":1,\"ret\":\"0x0\"}",
            "call 11fc880 2" => RefreshFault ? "{\"ok\":0}" : "{\"ok\":1,\"ret\":\"0x0\"}",
            _ when command.StartsWith("pokeabs ", StringComparison.Ordinal) => "{\"ok\":1}",
            _ => ClipReply(command),
        };

        private string ClipReply(string command)
        {
            for (int i = 0; i < clips.Length; i++)
            {
                if (command != $"peekabs {Data + (ulong)i * (ulong)Stride:x} 20") continue;
                ClipReads++;
                return Convert.ToHexString(clips[i]);
            }
            throw new InvalidOperationException("Unexpected native operation: " + command);
        }

        private static string Hex(ulong value) => Convert.ToHexString(BitConverter.GetBytes(value));
        private static string IntHex(int value) => Convert.ToHexString(BitConverter.GetBytes(value));
    }
}
