using FruityLink.Core.Abstractions;
using Xunit;

namespace FruityLink.Scripting.Tests;

public sealed class AutomationCreationContractTests
{
    [Fact]
    public async Task TypedCreationBindsTargetAndExposesResultSchema()
    {
        var (control, recorder) = RecordingControl.Create<INativeFlControl>();
        recorder.Handler = (method, args) =>
        {
            Assert.Equal(nameof(INativeFlControl.CreateAutomationClipAsync), method.Name);
            Assert.Equal(new FlAutomationTarget("plugin_parameter", 2, 0, 42), args[0]);
            Assert.Equal(500, args[1]);
            Assert.Null(args[4]);
            return Task.FromResult(new FlAutomationClipResult(19, 100));
        };
        await using var dispatcher = new FlScriptingDispatcher(control);
        var entry = Assert.Single(dispatcher.Catalog.Operations, item => item.Name == "create_automation_clip");
        Assert.NotNull(entry.ReturnSchema);
        var properties = entry.ReturnSchema.Value.GetProperty("properties");
        Assert.Equal("integer", properties.GetProperty("channel").GetProperty("type").GetString());
        Assert.Equal("integer", properties.GetProperty("clipIndex").GetProperty("type").GetString());
        var result = await dispatcher.InvokeAsync("create_automation_clip", RecordingControl.Json(
            """{"target":{"kind":"plugin_parameter","index":2,"slot":0,"parameter":42},"track":500,"startTick":96,"lengthTick":384}"""));
        Assert.Equal(new FlAutomationClipResult(19, 100), result);
    }

    [Fact]
    public async Task CompleteEnvelopePreservesBeatAndValueCoordinates()
    {
        var (control, recorder) = RecordingControl.Create<INativeFlControl>();
        recorder.Handler = (_, args) =>
        {
            var points = Assert.IsAssignableFrom<IReadOnlyList<FlAutomationPointSpec>>(args[1]);
            Assert.Equal(new[] { new FlAutomationPointSpec(0, 0.2), new FlAutomationPointSpec(1.5, 0.8) }, points);
            return Task.CompletedTask;
        };
        await using var dispatcher = new FlScriptingDispatcher(control);
        await dispatcher.InvokeAsync("set_automation_points", RecordingControl.Json(
            """{"channel":19,"points":[{"timeBeats":0,"value":0.2},{"timeBeats":1.5,"value":0.8}]}"""));
        Assert.Single(recorder.Calls);
    }

    [Theory]
    [InlineData("""{"channel":19,"points":[{"timeBeats":0}]}""")]
    [InlineData("""{"channel":19,"points":[null]}""")]
    public async Task MissingPointFieldsDoNotReachNativeControl(string arguments)
    {
        var (control, recorder) = RecordingControl.Create<INativeFlControl>();
        await using var dispatcher = new FlScriptingDispatcher(control);
        var error = await Assert.ThrowsAsync<ScriptingException>(() => dispatcher.InvokeAsync(
            "set_automation_points", RecordingControl.Json(arguments)));
        Assert.Equal("invalid_arguments", error.Code);
        Assert.Empty(recorder.Calls);
    }
}
