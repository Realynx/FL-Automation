using FruityLink.Agent.Plugins;
using Shouldly;
using Xunit;

namespace FruityLink.Agent.Tests;

/// <summary>
/// Channel write tools accept a channel NAME as well as an index, so the model can act on "Bass"
/// without a native_list_channels round-trip to find the index. These pin that resolution:
/// numeric refs skip the name survey entirely, a name resolves to its index end-to-end, and an
/// unknown name fails cleanly WITHOUT mutating. (The fake's channels are "Channel 0".."Channel 1".)
/// </summary>
public sealed class NativeControlPluginChannelResolveTests
{
    private readonly FakeNativeFlControl _fl = new();
    private readonly NativeControlPlugin _plugin;

    public NativeControlPluginChannelResolveTests() => _plugin = new NativeControlPlugin(_fl);

    [Fact]
    public async Task RouteChannel_ResolvesAName_ToItsIndex()
    {
        string result = await _plugin.SetChannelFxRouteAsync("Channel 1", 5);

        result.ShouldStartWith("OK:");
        _fl.Calls.ShouldContain("SetChannelFxRouteAsync(1,5)");
    }

    [Fact]
    public async Task NumericChannelReference_SkipsTheNameSurvey()
    {
        // A numeric ref must resolve without reading channel names — else it defeats the round-trip saving.
        await _plugin.SetChannelFxRouteAsync("2", 5);

        _fl.Calls.ShouldContain("SetChannelFxRouteAsync(2,5)");
        _fl.Calls.ShouldNotContain("GetChannelCountAsync()");
    }

    [Fact]
    public async Task AddNotes_AcceptsAChannelName()
    {
        string result = await _plugin.AddNotesAsync(1, "Channel 0", "60,0,480");

        result.ShouldStartWith("OK:");
        _fl.Calls.ShouldContain("AddNotesAsync(1,[1 notes])");
    }

    [Fact]
    public async Task UnknownChannelName_ReturnsErr_WithoutMutating()
    {
        string result = await _plugin.AddNotesAsync(1, "Nonexistent", "60,0,480");

        result.ShouldStartWith("ERR:");
        _fl.Calls.ShouldNotContain("AddNotesAsync(1,[1 notes])");
    }
}
