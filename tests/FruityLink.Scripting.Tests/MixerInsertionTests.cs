using FruityLink.Core.Abstractions;
using Xunit;

namespace FruityLink.Scripting.Tests;

public sealed class MixerInsertionTests
{
    private static readonly FlMixerLayout Layout = new(0x1478, 0xc, 8, 0x18, 0x1a, 0x394, 0x134c,
        8, 0, 4, 8, 0x64, 0x58, 0xf0);

    [Theory]
    [InlineData("{}", -1)]
    [InlineData("{\"afterTrack\":0}", 0)]
    [InlineData("{\"afterTrack\":3}", 3)]
    public async Task CatalogueDefaultAndDispatchPreserveInsertionIndex(string arguments, int expectedAfter)
    {
        var (control, recorder) = RecordingControl.Create<INativeFlControl>();
        recorder.Handler = (method, args) =>
        {
            Assert.Equal(nameof(INativeFlControl.AddMixerTrackAsync), method.Name);
            Assert.Equal(expectedAfter, args[0]);
            return Task.FromResult(17);
        };
        await using var dispatcher = new FlScriptingDispatcher(control);
        var operation = Assert.Single(dispatcher.Catalog.Operations, item => item.Name == "add_mixer_track");
        Assert.Equal("integer", operation.ReturnType);
        var parameter = Assert.Single(operation.Parameters);
        Assert.Equal("afterTrack", parameter.Name);
        Assert.False(parameter.Required);
        Assert.Equal(-1, parameter.DefaultValue);
        Assert.Contains("mixer_layout", operation.Requires);

        Assert.Equal(17, await dispatcher.InvokeAsync("add_mixer_track", RecordingControl.Json(arguments)));
        Assert.Single(recorder.Calls);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task MissingLayoutOrInsertionSymbolPreventsDispatch(bool hasLayout, bool unresolved)
    {
        var (control, recorder) = RecordingControl.Create<ICompleteControl>();
        recorder.Status = new(0, 100, unresolved ? 1 : 0,
            unresolved ? new HashSet<string> { "FLmx_InsertTracks" } : new HashSet<string>())
        { Complete = true, Supported = true, MixerLayout = hasLayout ? Layout : null };
        await using var dispatcher = new FlScriptingDispatcher(control);
        Assert.Contains("add_mixer_track", (await dispatcher.GetCapabilitiesAsync()).UnavailableOperations.Keys);

        var error = await Assert.ThrowsAsync<ScriptingException>(() =>
            dispatcher.InvokeAsync("add_mixer_track", ScriptingJson.EmptyObject));

        Assert.Equal("unavailable", error.Code);
        Assert.DoesNotContain(recorder.Calls, call => call.Method == nameof(INativeFlControl.AddMixerTrackAsync));
    }

    [Theory]
    [InlineData("{\"afterTrack\":true}")]
    [InlineData("{\"afterTrack\":1.5}")]
    [InlineData("{\"afterTrack\":\"3\"}")]
    public async Task NonIntegerInsertionIndexNeverReachesNativeControl(string arguments)
    {
        var (control, recorder) = RecordingControl.Create<INativeFlControl>();
        await using var dispatcher = new FlScriptingDispatcher(control);
        var error = await Assert.ThrowsAsync<ScriptingException>(() =>
            dispatcher.InvokeAsync("add_mixer_track", RecordingControl.Json(arguments)));
        Assert.Equal("invalid_arguments", error.Code);
        Assert.Empty(recorder.Calls);
    }

    [Fact]
    public async Task MixerQueryAdvertisesTypedSchemaAndRequiresVerifiedLayout()
    {
        var (control, recorder) = RecordingControl.Create<ICompleteControl>();
        recorder.Status = new(0, 100, 0, new HashSet<string>()) { Complete = true, Supported = true };
        await using var dispatcher = new FlScriptingDispatcher(control);
        var operation = Assert.Single(dispatcher.Catalog.Operations, item => item.Name == "query_mixer_tracks");
        Assert.Contains("mixer_layout", operation.Requires);
        var fields = operation.ReturnSchema!.Value.GetProperty("items").GetProperty("properties");
        Assert.Equal("integer", fields.GetProperty("index").GetProperty("type").GetString());
        Assert.Equal("string", fields.GetProperty("kind").GetProperty("type").GetString());
        await Assert.ThrowsAsync<ScriptingException>(() => dispatcher.InvokeAsync("query_mixer_tracks", ScriptingJson.EmptyObject));
        Assert.DoesNotContain(recorder.Calls, call => call.Method == nameof(IFlStructuredQuery.QueryMixerTracksAsync));

        recorder.Status = recorder.Status with { MixerLayout = Layout };
        recorder.Handler = (method, _) => method.Name == nameof(IFlStructuredQuery.QueryMixerTracksAsync)
            ? Task.FromResult<IReadOnlyList<FlMixerTrackInfo>>(new[] { new FlMixerTrackInfo(0, "Master", "master"), new FlMixerTrackInfo(16, "Bus", "insert") })
            : RecordingControl.DefaultReturn(method);
        var tracks = Assert.IsAssignableFrom<IReadOnlyList<FlMixerTrackInfo>>(
            await dispatcher.InvokeAsync("query_mixer_tracks", ScriptingJson.EmptyObject));
        Assert.Equal(new[] { 0, 16 }, tracks.Select(track => track.Index));
    }
}
