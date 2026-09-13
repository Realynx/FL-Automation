using System.Text;
using System.Text.Json;
using FruityLink.Core.Abstractions;
using FruityLink.FlStudio.Inject;
using Xunit;

namespace FruityLink.FlStudio.Tests;

public sealed class MixerCreationTests : IDisposable
{
    private readonly Func<string, int, CancellationToken, Task<string>>? original = FlInjectBridge.Transport;
    public void Dispose() => FlInjectBridge.Transport = original;

    [Theory]
    [InlineData(-1, 3)]
    [InlineData(0, 1)]
    [InlineData(1, 2)]
    public async Task AtomicInsertionReturnsNativeIndexAndQueryExcludesDormantCurrent(int after, int expected)
    {
        var native = new NativeMixer();
        var bridge = new FlInjectBridge();
        var before = await bridge.QueryMixerTracksAsync();
        Assert.Equal(new[] { "master", "insert", "insert" }, before.Select(track => track.Kind));

        int actual = await bridge.AddMixerTrackAsync(after);
        var afterTracks = await bridge.QueryMixerTracksAsync();

        Assert.Equal(expected, actual);
        Assert.Equal(new[] { 0, 1, 2, 3 }, afterTracks.Select(track => track.Index));
        Assert.Equal($"Insert {expected}", afterTracks[actual].Name);
        Assert.Equal(new[] { "Kick", "Clap" }, afterTracks.Where(track => track.Name is "Kick" or "Clap").Select(track => track.Name));
        Assert.Equal(5, await bridge.GetMixerTrackCountAsync());
        Assert.Single(native.Commands, command => command.StartsWith("mixer_add", StringComparison.Ordinal));
        Assert.DoesNotContain(native.Commands, command => command.StartsWith("poke", StringComparison.Ordinal) || command.StartsWith("call", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(-2)]
    [InlineData(501)]
    public async Task InvalidCreationInputFailsBeforeNativeAccess(int after)
    {
        var native = new NativeMixer();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => new FlInjectBridge().AddMixerTrackAsync(after));
        Assert.Empty(native.Commands);
    }

    [Fact]
    public async Task UnverifiedLayoutDoesNotReachInsertionCommand()
    {
        var native = new NativeMixer { LayoutAvailable = false };
        await Assert.ThrowsAsync<InvalidOperationException>(() => new FlInjectBridge().AddMixerTrackAsync());
        Assert.Equal(new[] { "syms" }, native.Commands);
    }

    [Theory]
    [InlineData("{\"ok\":0,\"reason\":\"native-insertion-fault\",\"mayHaveChanged\":true}")]
    [InlineData("{\"ok\":0,\"reason\":\"native-insertion-unconfirmed\",\"mayHaveChanged\":true}")]
    [InlineData("{\"ok\":1,\"index\":501,\"count\":502}")]
    [InlineData("{\"ok\":1,\"index\":4,\"count\":5}")]
    [InlineData("err:unknown")]
    [InlineData("{\"ok\":1,")]
    [InlineData("")]
    public async Task NativeAmbiguityOrInvalidSuccessNeverRetries(string response)
    {
        var native = new NativeMixer { InsertionResponse = response };
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new FlInjectBridge().AddMixerTrackAsync());
        Assert.Contains("inspect the mixer", error.Message);
        Assert.Single(native.Commands, command => command.StartsWith("mixer_add", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(3)]
    [InlineData(501)]
    public async Task EveryMixerMutationRejectsDormantAndCurrentIndices(int track)
    {
        var native = new NativeMixer();
        var bridge = new FlInjectBridge();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => bridge.SetMixerVolumeAsync(track, 100));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => bridge.SetMixerPanAsync(track, 100));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => bridge.SetMixerEqGainAsync(track, 0, 100));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => bridge.SetMixerFxParamAsync(track, 0, 0, 100));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => bridge.SetMixerSendAsync(1, track, 1));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => bridge.SetMixerTrackNameAsync(track, "Invalid"));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => bridge.SetChannelFxRouteAsync(0, track));
        Assert.DoesNotContain(native.Commands, command => command.StartsWith("poke", StringComparison.Ordinal) || command.StartsWith("call", StringComparison.Ordinal));
    }

    private sealed class NativeMixer
    {
        private static readonly FlMixerLayout Layout = new(0x1478, 12, 8, 24, 26, 0x394, 0x134c, 8, 0, 4, 8, 0x64, 0x58, 0xf0);
        private readonly List<string> names = ["Master", "Kick", "Clap"];
        public List<string> Commands { get; } = [];
        public bool LayoutAvailable { get; init; } = true;
        public string? InsertionResponse { get; init; }
        public NativeMixer() => FlInjectBridge.Transport = SendAsync;

        private Task<string> SendAsync(string command, int timeout, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); Commands.Add(command);
            return Task.FromResult(command switch
            {
                "syms" => JsonSerializer.Serialize(new { ok = 112, fail = 0, complete = true, supported = true,
                    mixerLayout = LayoutAvailable ? Layout : null }, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
                "peek 14a9850 8" => Hex(0x20000UL),
                "peekabs 20000 4" => Hex(names.Count + 1),
                "peek 14a7eb0 8" => Hex(0x100000UL),
                _ when command.StartsWith("mixer_add ", StringComparison.Ordinal) => Insert(command),
                _ => ReadTrack(command),
            });
        }

        private string Insert(string command)
        {
            if (InsertionResponse is not null) return InsertionResponse;
            int after = int.Parse(command.AsSpan(10), System.Globalization.CultureInfo.InvariantCulture);
            int index = after == -1 ? names.Count : after + 1;
            names.Insert(index, "");
            return JsonSerializer.Serialize(new { ok = 1, index, count = names.Count + 1 });
        }

        private string ReadTrack(string command)
        {
            for (int index = 0; index < names.Count; index++)
            {
                ulong track = 0x100000UL + (ulong)index * (ulong)Layout.TrackStride;
                ulong text = 0x70000UL + (ulong)index * 1024;
                if (command == $"peekabs {track + 8:x} 4") return Hex(index == 0 ? 0 : 1);
                if (command == $"peekabs {track + 12:x} 8") return Hex(names[index].Length == 0 ? 0 : text);
                if (command == $"peekabs {text - 4:x} 4") return Hex(names[index].Length);
                if (command == $"peekabs {text:x} {names[index].Length * 2}") return Convert.ToHexString(Encoding.Unicode.GetBytes(names[index]));
            }
            throw new InvalidOperationException("Unexpected native operation: " + command);
        }
        private static string Hex(ulong value) => Convert.ToHexString(BitConverter.GetBytes(value));
        private static string Hex(int value) => Convert.ToHexString(BitConverter.GetBytes(value));
    }
}
