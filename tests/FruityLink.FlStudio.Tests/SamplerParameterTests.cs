using FruityLink.FlStudio.Inject;
using Xunit;

namespace FruityLink.FlStudio.Tests;

public sealed class SamplerParameterTests : IDisposable
{
    private readonly Func<string, int, CancellationToken, Task<string>>? original = FlInjectBridge.Transport;
    private readonly Dictionary<string, string> replies = [];
    private readonly List<string> commands = [];

    public SamplerParameterTests() => FlInjectBridge.Transport = SendAsync;
    public void Dispose() => FlInjectBridge.Transport = original;

    [Theory]
    [InlineData(0)]
    [InlineData(64)]
    public async Task ChannelWithoutHostedInterfaceExplainsSamplerLimitBeforeAnyParameterCall(int count)
    {
        ConfigureChannel(count);
        var bridge = new FlInjectBridge();

        string listing = await bridge.ListPluginParamsAsync(4, -1, null);
        var query = await Assert.ThrowsAsync<InvalidOperationException>(() => bridge.QueryPluginParametersAsync(4));
        var write = await Assert.ThrowsAsync<InvalidOperationException>(() => bridge.SetPluginParamAsync(4, -1, 0, 0.5));

        Assert.Contains("Channel 4 has no hosted-plugin parameter interface", listing);
        Assert.Contains("Sampler envelopes or sample settings", listing);
        Assert.Contains("channel volume, pan, mute, routing", listing);
        Assert.Equal(listing, query.Message);
        Assert.Equal(listing, write.Message);
        Assert.DoesNotContain(commands, command => command.StartsWith("poke", StringComparison.Ordinal));
        Assert.All(commands.Where(command => command.StartsWith("call", StringComparison.Ordinal)),
            command => Assert.Equal("call f00f80 20000 4", command));
    }

    [Fact]
    public async Task MissingHostedInterfaceDoesNotDisableSeparateChannelVolumeControl()
    {
        ConfigureChannel(0);
        replies["call f53fe0 40000 0 2"] = "{\"ok\":1,\"ret\":\"0x2710\"}";
        var bridge = new FlInjectBridge();
        await Assert.ThrowsAsync<InvalidOperationException>(() => bridge.QueryPluginParametersAsync(4));

        long volume = await bridge.GetChannelVolumeAsync(4);

        Assert.Equal(10000, volume);
        Assert.Contains("call f53fe0 40000 0 2", commands);
    }

    [Fact]
    public async Task MissingChannelKeepsIndexErrorInsteadOfClaimingSamplerLimitation()
    {
        ConfigureChannel(0);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new FlInjectBridge().QueryPluginParametersAsync(5));

        Assert.Contains("Channel 5 does not exist", error.Message);
        Assert.DoesNotContain(commands, command => command.StartsWith("call", StringComparison.Ordinal));
    }

    private void ConfigureChannel(int parameterCount)
    {
        replies["peek 14a98d8 8"] = Hex(0x10000UL);
        replies["peekabs 10000 8"] = Hex(0x20000UL);
        replies["peekabs 20010 4"] = Hex(5);
        replies["call f00f80 20000 4"] = "{\"ok\":1,\"ret\":\"0x30000\"}";
        replies["peekabs 3009c 4"] = Hex(0x40000);
        replies["peekabs 30068 4"] = Hex(parameterCount);
        replies["peekabs 30038 8"] = Hex(0UL);
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
