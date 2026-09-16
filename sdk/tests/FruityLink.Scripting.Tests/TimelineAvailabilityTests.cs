using FruityLink.Core.Abstractions;
using Xunit;

namespace FruityLink.Scripting.Tests;

public sealed class TimelineAvailabilityTests
{
    [Theory]
    [InlineData("add_marker", "{\"tick\":0,\"name\":\"Intro\"}", "AddMarkerAsync")]
    [InlineData("delete_marker", "{\"index\":0}", "DeleteMarkerAsync")]
    [InlineData("list_markers", "{}", "ListMarkersAsync")]
    [InlineData("set_loop_region", "{\"startTick\":0,\"endTick\":-1}", "SetLoopRegionAsync")]
    public async Task CapabilityAndDispatchRequireVerifiedTimeline(string operation, string arguments, string method)
    {
        var (control, recorder) = RecordingControl.Create<ICompleteControl>();
        recorder.Status = new(0, 109, 0, new HashSet<string>()) { Supported = true, Complete = true };
        await using var dispatcher = new FlScriptingDispatcher(control);
        Assert.Contains("timeline_layout", Assert.Single(dispatcher.Catalog.Operations, item => item.Name == operation).Requires);
        Assert.Contains(operation, (await dispatcher.GetCapabilitiesAsync()).UnavailableOperations.Keys);

        var error = await Assert.ThrowsAsync<ScriptingException>(() => dispatcher.InvokeAsync(operation, RecordingControl.Json(arguments)));

        Assert.Equal("unavailable", error.Code);
        Assert.DoesNotContain(recorder.Calls, call => call.Method == method);
        recorder.Status = recorder.Status with { TimelineLayout = new FlTimelineLayout(0xd84, 0x34, 0, 8) };
        await dispatcher.InvokeAsync(operation, RecordingControl.Json(arguments));
        Assert.Contains(recorder.Calls, call => call.Method == method);
    }
}
