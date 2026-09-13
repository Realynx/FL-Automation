using System.Globalization;
using FruityLink.Core.Abstractions;
using FruityLink.FlStudio.Inject;
using Xunit;

namespace FruityLink.FlStudio.Tests;

public sealed class AutomationTests : IDisposable
{
    private readonly Func<string, int, CancellationToken, Task<string>>? original = FlInjectBridge.Transport;
    private readonly Dictionary<string, string> replies = [];
    private readonly List<string> commands = [];
    private const string State = "{\"ok\":1,\"channel\":4,\"sourceEventId\":589824,\"points\":[{\"index\":0,\"timeBeats\":0,\"value\":0.5,\"tension\":0,\"curve\":0},{\"index\":1,\"timeBeats\":4,\"value\":0.75,\"tension\":0,\"curve\":0}]}";

    public AutomationTests()
    {
        replies["syms"] = "{\"ver\":0,\"ok\":110,\"fail\":0,\"supported\":true,\"complete\":true,\"automationClips\":true}";
        FlInjectBridge.Transport = SendAsync;
    }
    public void Dispose() => FlInjectBridge.Transport = original;

    [Fact]
    public async Task TypedReadUsesAtomicGuardedNativeSnapshot()
    {
        replies["automation_read 4"] = State;
        var points = await new FlInjectBridge().QueryAutomationPointsAsync(4);
        Assert.Equal(4, points[1].TimeBeats);
        Assert.Equal(.75, points[1].Value);
        Assert.Equal(new[] { "syms", "automation_read 4" }, commands);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"automationClips\":false}")]
    public async Task MissingOrUnsupportedProfileRefusesBeforeNativeAutomation(string status)
    {
        replies["syms"] = status;
        await Assert.ThrowsAsync<NotSupportedException>(() => new FlInjectBridge().QueryAutomationPointsAsync(4));
        Assert.Equal(new[] { "syms" }, commands);
    }

    [Theory]
    [InlineData(double.NaN, .5, 0)]
    [InlineData(-1, .5, 0)]
    [InlineData(2, double.PositiveInfinity, 0)]
    [InlineData(2, .5, 2)]
    public async Task InvalidAddPointIsRejectedBeforeTransport(double time, double value, double tension)
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => new FlInjectBridge().AddAutomationPointAsync(4, time, value, tension));
        Assert.Empty(commands);
    }

    [Theory]
    [InlineData(0, 4, 0)]
    [InlineData(1, 0, 0)]
    [InlineData(0, 0, 0)]
    [InlineData(0, 4, 1)]
    public async Task InvalidReplacementIsRejectedBeforeTransport(double first, double second, int curve)
    {
        // The first case is a valid point sequence on a non-automation channel; native guard refuses it.
        replies["automation_set 4 2 0 0.5 0 0 4 0.5 0 0"] = "{\"ok\":0,\"reason\":\"channel-is-not-an-automation-clip\"}";
        var points = new[] { new FlAutomationPointSpec(first, .5), new FlAutomationPointSpec(second, .5, 0, curve) };
        var action = () => new FlInjectBridge().SetAutomationPointsAsync(4, points);
        if (first == 0 && second == 4 && curve == 0)
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(action);
            Assert.Contains("not-an-automation", error.Message);
            Assert.DoesNotContain(commands, command => command.StartsWith("poke", StringComparison.Ordinal));
        }
        else
        {
            await Assert.ThrowsAsync<ArgumentException>(action);
            Assert.Empty(commands);
        }
    }

    [Theory]
    [InlineData("{\"ok\":1,\"channel\":4}")]
    [InlineData("{\"ok\":1,\"channel\":4,\"sourceEventId\":true,\"points\":[]}")]
    [InlineData("{\"ok\":1,\"channel\":4,\"sourceEventId\":4294967295,\"points\":[]}")]
    public async Task MalformedSuccessWarnsToInspectWithoutRetrying(string result)
    {
        replies["automation_add 4 2 0.5 0 0"] = result;
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new FlInjectBridge().AddAutomationPointAsync(4, 2, .5, 0));
        Assert.Contains("Inspect", error.Message);
        Assert.Single(commands, command => command.StartsWith("automation_add", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AddPointUsesInvariantWireNumbersAndNoRawWrites()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            replies["automation_add 4 1.5 0.25 -0.5 0"] = State;
            replies["call 107ead0"] = "{\"ok\":1,\"ret\":\"0x0\"}";
            await new FlInjectBridge().AddAutomationPointAsync(4, 1.5, .25, -.5);
            Assert.Contains("automation_add 4 1.5 0.25 -0.5 0", commands);
            Assert.DoesNotContain(commands, command => command.StartsWith("poke", StringComparison.Ordinal));
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Theory]
    [InlineData(0, 0, 96)]
    [InlineData(501, 0, 96)]
    [InlineData(1, -1, 96)]
    [InlineData(1, int.MaxValue, 96)]
    public async Task InvalidPlacementCannotCreateChannel(int track, int start, int length)
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => new FlInjectBridge().CreateAutomationClipAsync(
            new("channel_volume", 4), track, start, length));
        Assert.Empty(commands);
    }

    [Fact]
    public async Task CreationUsesPersistentChannelEventInsteadOfDisplayedIndex()
    {
        replies["peek 14a98d8 8"] = Hex(0x10000UL);
        replies["peekabs 10000 8"] = Hex(0x20000UL);
        replies["peekabs 20010 4"] = Hex(5);
        replies["call f00f80 20000 4"] = "{\"ok\":1,\"ret\":\"0x30000\"}";
        replies["peekabs 3009c 4"] = Hex(0x90000);
        replies["peek 14a79f8 8"] = Hex(0x40000UL);
        replies["peekabs 40000 4"] = Hex(96);
        replies["call 11e32c0"] = "{\"ok\":1,\"ret\":\"0x50000\"}";
        replies["peekabs 50014 8"] = Hex(0x60000UL);
        replies["automation_create 589825"] = "{\"ok\":0,\"reason\":\"test-boundary\",\"mayHaveChanged\":true}";
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new FlInjectBridge().CreateAutomationClipAsync(
            new("channel_pan", 4), 2, 0, 384));
        Assert.Contains("may have changed", error.Message);
        Assert.Contains("automation_create 589825", commands);
        Assert.Single(commands, command => command.StartsWith("automation_create", StringComparison.Ordinal));
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
