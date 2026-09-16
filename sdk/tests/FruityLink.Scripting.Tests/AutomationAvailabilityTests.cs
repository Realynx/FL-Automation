using FruityLink.Core.Abstractions;
using Xunit;

namespace FruityLink.Scripting.Tests;

public sealed class AutomationAvailabilityTests
{
    [Theory]
    [InlineData("query_automation_points", "{\"channel\":0}")]
    [InlineData("add_automation_point", "{\"channel\":0,\"timeBeats\":1,\"value\":0.5,\"tension\":0}")]
    [InlineData("create_automation_clip", "{\"target\":{\"kind\":\"channel_volume\",\"index\":0},\"track\":1,\"startTick\":0,\"lengthTick\":384}")]
    public async Task LayoutAndSymbolRequirementsReachCatalogueAndDispatch(string operation, string arguments)
    {
        var (control, recorder) = RecordingControl.Create<ICompleteControl>();
        recorder.Status = new(0, 110, 0, new HashSet<string>()) { Supported = true, Complete = true };
        await using var dispatcher = new FlScriptingDispatcher(control);
        Assert.Contains("automation_clips", Assert.Single(dispatcher.Catalog.Operations, item => item.Name == operation).Requires);
        Assert.Contains(operation, (await dispatcher.GetCapabilitiesAsync()).UnavailableOperations.Keys);
        var error = await Assert.ThrowsAsync<ScriptingException>(() => dispatcher.InvokeAsync(operation, RecordingControl.Json(arguments)));
        Assert.Equal("unavailable", error.Code);
        recorder.Status = recorder.Status with { AutomationClips = true, Unresolved = new HashSet<string> { "FLac_CreateForEvent" } };
        Assert.Contains(operation, (await dispatcher.GetCapabilitiesAsync()).UnavailableOperations.Keys);
        recorder.Status = recorder.Status with { Unresolved = new HashSet<string>() };
        Assert.DoesNotContain(operation, (await dispatcher.GetCapabilitiesAsync()).UnavailableOperations.Keys);
    }
}
