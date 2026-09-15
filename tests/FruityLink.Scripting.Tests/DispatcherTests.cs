using FruityLink.Core.Abstractions;
using System.Text.Json;
using Xunit;

namespace FruityLink.Scripting.Tests;

public sealed class DispatcherTests
{
    [Fact]
    public async Task CatalogMatchesOnlyAllowedInterfacesAndExplainsDefaults()
    {
        var (control, _) = RecordingControl.Create<INativeFlControl>();
        await using var dispatcher = new FlScriptingDispatcher(control);
        Assert.Equal(typeof(INativeFlControl).GetMethods().Length, dispatcher.Catalog.Operations.Count);
        Assert.DoesNotContain(dispatcher.Catalog.Operations, operation => operation.Name.Contains("raw", StringComparison.Ordinal));
        var notes = Assert.Single(dispatcher.Catalog.Operations, operation => operation.Name == "get_notes");
        Assert.Contains("piano-roll", notes.Description, StringComparison.OrdinalIgnoreCase);
        var offset = Assert.Single(notes.Parameters, parameter => parameter.Name == "offset");
        Assert.False(offset.Required);
        Assert.Equal(0, offset.DefaultValue);
        Assert.DoesNotContain(notes.Parameters, parameter => parameter.Name == "ct");
    }

    [Fact]
    public async Task StructuredQueryInterfaceAddsTypedOperations()
    {
        var (control, recorder) = RecordingControl.Create<ICompleteControl>();
        recorder.Handler = (method, _) => method.Name == "QueryChannelsAsync"
            ? Task.FromResult<IReadOnlyList<FlChannelInfo>>(new[] { new FlChannelInfo(0, "Piano 🎹\nSecond line", 1, false, 9000, 6400) })
            : RecordingControl.DefaultReturn(method);
        await using var dispatcher = new FlScriptingDispatcher(control);
        Assert.Equal(typeof(INativeFlControl).GetMethods().Length + typeof(IFlStructuredQuery).GetMethods().Length,
            dispatcher.Catalog.Operations.Count);
        var result = await dispatcher.InvokeAsync("query_channels", ScriptingJson.EmptyObject);
        var json = JsonSerializer.SerializeToElement(result, ScriptingJson.Options);
        Assert.Equal("Piano 🎹\nSecond line", json[0].GetProperty("name").GetString());
        Assert.Equal(1, json[0].GetProperty("mixerTrack").GetInt32());
        var notes = Assert.Single(dispatcher.Catalog.Operations, operation => operation.Name == "query_notes");
        Assert.Equal("FlQueryPage<FlNoteInfo>", notes.ReturnType);
        var pageSchema = notes.ReturnSchema!.Value.GetProperty("properties");
        var itemProperties = pageSchema.GetProperty("items").GetProperty("items").GetProperty("properties");
        Assert.Equal("integer", itemProperties.GetProperty("startTick").GetProperty("type").GetString());
        Assert.Equal("null", pageSchema.GetProperty("nextOffset").GetProperty("anyOf")[1].GetProperty("type").GetString());
    }

    [Theory]
    [InlineData("set_tempo", "{\"Bpm\":120}")]
    [InlineData("set_tempo", "{\"bpm\":\"120\"}")]
    [InlineData("set_tempo", "{\"bpm\":null}")]
    [InlineData("set_tempo", "{\"bpm\":1e999}")]
    [InlineData("set_tempo", "{\"bpm\":120,\"bpm\":130}")]
    [InlineData("set_tempo", "{\"bpm\":120,\"ct\":null}")]
    [InlineData("set_channel_volume", "{\"channel\":0,\"value\":2147483648}")]
    [InlineData("set_channel_volume", "{\"channel\":0,\"value\":1.5}")]
    [InlineData("add_notes", "{\"pattern\":1,\"notes\":[{\"key\":60,\"startTick\":0,\"lengthTick\":96,\"velocity\":90}]}")]
    [InlineData("add_notes", "{\"pattern\":1,\"notes\":[{\"channel\":0,\"key\":60,\"startTick\":0,\"lengthTick\":96,\"velocity\":90,\"rawAddress\":42}]}")]
    [InlineData("add_notes", "{\"pattern\":1,\"notes\":[null]}")]
    [InlineData("add_notes", "{\"pattern\":1,\"notes\":null}")]
    public async Task InvalidArgumentsFailBeforeAnyControlCall(string operation, string arguments)
    {
        var (control, recorder) = RecordingControl.Create<INativeFlControl>();
        await using var dispatcher = new FlScriptingDispatcher(control);
        var error = await Assert.ThrowsAsync<ScriptingException>(() => dispatcher.InvokeAsync(operation, RecordingControl.Json(arguments)));
        Assert.Equal("invalid_arguments", error.Code);
        Assert.Empty(recorder.Calls);
    }

    [Theory]
    [InlineData("GetType")]
    [InlineData("get_type")]
    [InlineData("raw")]
    [InlineData("poke_abs")]
    [InlineData("get_tempo_async")]
    public async Task ArbitraryReflectionAndNativePrimitivesAreNotOperations(string operation)
    {
        var (control, recorder) = RecordingControl.Create<INativeFlControl>();
        await using var dispatcher = new FlScriptingDispatcher(control);
        var error = await Assert.ThrowsAsync<ScriptingException>(() => dispatcher.InvokeAsync(operation, ScriptingJson.EmptyObject));
        Assert.Equal("operation_not_found", error.Code);
        Assert.Empty(recorder.Calls);
    }

    [Fact]
    public async Task NullableStringsAndNestedRecordDefaultsRemainDistinctFromMissingArguments()
    {
        var (control, recorder) = RecordingControl.Create<INativeFlControl>();
        await using var dispatcher = new FlScriptingDispatcher(control);
        await dispatcher.InvokeAsync("list_samples", RecordingControl.Json("{\"filter\":null}"));
        Assert.Null(recorder.Calls.Last().Arguments[0]);
        await Assert.ThrowsAsync<ScriptingException>(() => dispatcher.InvokeAsync("list_samples", ScriptingJson.EmptyObject));
        await dispatcher.InvokeAsync("edit_notes", RecordingControl.Json("{\"pattern\":1,\"edits\":[{\"channel\":0,\"key\":60,\"startTick\":0,\"newVelocity\":100}]}"));
        var edit = Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<NoteEdit>>(recorder.Calls.Last().Arguments[1]));
        Assert.Equal(100, edit.NewVelocity);
        Assert.Null(edit.NewKey);
    }

    [Theory]
    [InlineData("select_channel", nameof(INativeFlControl.SelectChannelAsync))]
    [InlineData("set_channel_solo", nameof(INativeFlControl.SetChannelSoloAsync))]
    public async Task RenamedChannelArgumentAcceptsTheLegacyIndexAlias(string operation, string method)
    {
        var (control, recorder) = RecordingControl.Create<INativeFlControl>();
        await using var dispatcher = new FlScriptingDispatcher(control);
        Assert.Contains(dispatcher.Catalog.Operations.Single(o => o.Name == operation).Parameters, p => p.Name == "channel");
        await dispatcher.InvokeAsync(operation, RecordingControl.Json("{\"channel\":3}"));
        await dispatcher.InvokeAsync(operation, RecordingControl.Json("{\"index\":5}"));
        Assert.Equal(new[] { (method, 3), (method, 5) }, recorder.Calls.Select(c => (c.Method, (int)c.Arguments[0]!)).ToArray());
        var error = await Assert.ThrowsAsync<ScriptingException>(() => dispatcher.InvokeAsync(operation, RecordingControl.Json("{\"index\":1,\"channel\":2}")));
        Assert.Equal("invalid_arguments", error.Code);
        Assert.Equal(2, recorder.Calls.Count);
    }

    [Theory]
    [InlineData("set_channel_volume", "{\"channel\":15,\"volume\":8000}", nameof(INativeFlControl.SetChannelVolumeAsync), 1, 8000)]
    [InlineData("set_mixer_volume", "{\"track\":3,\"volume\":12800}", nameof(INativeFlControl.SetMixerVolumeAsync), 1, 12800)]
    [InlineData("set_master_volume", "{\"volume\":9000}", nameof(INativeFlControl.SetMasterVolumeAsync), 0, 9000)]
    [InlineData("set_channel_pan", "{\"channel\":2,\"pan\":6400}", nameof(INativeFlControl.SetChannelPanAsync), 1, 6400)]
    [InlineData("set_mixer_pan", "{\"track\":3,\"pan\":-3200}", nameof(INativeFlControl.SetMixerPanAsync), 1, -3200)]
    public async Task VolumeAndPanSettersAcceptThePropertyNameAsAnAlias(string operation, string json, string method, int argument, int expected)
    {
        var (control, recorder) = RecordingControl.Create<INativeFlControl>();
        await using var dispatcher = new FlScriptingDispatcher(control);
        Assert.Contains(dispatcher.Catalog.Operations.Single(o => o.Name == operation).Parameters, p => p.Name == "value");

        await dispatcher.InvokeAsync(operation, RecordingControl.Json(json));

        var call = Assert.Single(recorder.Calls);
        Assert.Equal(method, call.Method);
        Assert.Equal(expected, (int)call.Arguments[argument]!);
        var error = await Assert.ThrowsAsync<ScriptingException>(() => dispatcher.InvokeAsync(operation, RecordingControl.Json(json.Replace("}", ",\"value\":1}"))));
        Assert.Equal("invalid_arguments", error.Code);
        Assert.Single(recorder.Calls);
    }

    [Fact]
    public async Task NoteTargetsBindOptionalLengthTickAndAllowMultiple()
    {
        var (control, recorder) = RecordingControl.Create<INativeFlControl>();
        await using var dispatcher = new FlScriptingDispatcher(control);
        await dispatcher.InvokeAsync("delete_notes", RecordingControl.Json(
            "{\"pattern\":1,\"targets\":[{\"channel\":0,\"key\":60,\"startTick\":0,\"lengthTick\":96},{\"channel\":0,\"key\":62,\"startTick\":96}],\"allowMultiple\":true}"));
        var targets = Assert.IsAssignableFrom<IReadOnlyList<NoteRef>>(recorder.Calls.Last().Arguments[1]);
        Assert.Equal(96, targets[0].LengthTick);
        Assert.Null(targets[1].LengthTick);
        Assert.Equal(true, recorder.Calls.Last().Arguments[2]);
        await dispatcher.InvokeAsync("edit_notes", RecordingControl.Json(
            "{\"pattern\":1,\"edits\":[{\"channel\":0,\"key\":60,\"startTick\":0,\"lengthTick\":192,\"newVelocity\":100}]}"));
        var edit = Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<NoteEdit>>(recorder.Calls.Last().Arguments[1]));
        Assert.Equal(192, edit.LengthTick);
        Assert.Equal(false, recorder.Calls.Last().Arguments[2]);
    }

    [Fact]
    public async Task BatchKeepsCompletedChangesAndHonorsStopOnError()
    {
        var (control, recorder) = RecordingControl.Create<INativeFlControl>();
        await using var dispatcher = new FlScriptingDispatcher(control);
        var calls = new[]
        {
            new ScriptingCall("set_tempo", RecordingControl.Json("{\"bpm\":124}")),
            new ScriptingCall("not_an_operation", ScriptingJson.EmptyObject),
            new ScriptingCall("get_tempo", ScriptingJson.EmptyObject)
        };
        var stopped = await dispatcher.BatchAsync(calls);
        Assert.True(stopped.StoppedOnError);
        Assert.Equal(2, stopped.Results.Count);
        Assert.Null(stopped.Results[0].Error);
        Assert.Equal("operation_not_found", stopped.Results[1].Error!.Code);
        Assert.Single(recorder.Calls);
        var continued = await dispatcher.BatchAsync(calls, stopOnError: false);
        Assert.False(continued.StoppedOnError);
        Assert.Equal(3, continued.Results.Count);
        Assert.Equal(120.0, continued.Results[2].Result);
    }

    [Fact]
    public async Task CapabilitiesEchoIdentityAndRefuseUnverifiedMixerAndChatLayouts()
    {
        var (control, recorder) = RecordingControl.Create<ICompleteControl>();
        recorder.Status = new(0, 102, 5, new HashSet<string>()) { FileVersion = "26.1.3.5570", Complete = true, Supported = true };
        await using var dispatcher = new FlScriptingDispatcher(control);
        var capabilities = await dispatcher.GetCapabilitiesAsync();
        Assert.Equal(Environment.ProcessId, capabilities.Pid);
        Assert.Equal(dispatcher.InstanceId, capabilities.InstanceId);
        Assert.False(capabilities.MixerLayoutAvailable);
        Assert.Contains("set_mixer_send", capabilities.UnavailableOperations.Keys);
        Assert.Contains("open_chat_tab", capabilities.UnavailableOperations.Keys);
        var error = await Assert.ThrowsAsync<ScriptingException>(() => dispatcher.InvokeAsync("get_mixer_track_name", RecordingControl.Json("{\"track\":0}")));
        Assert.Equal("unavailable", error.Code);
        Assert.DoesNotContain(recorder.Calls, call => call.Method == "GetMixerTrackNameAsync");
    }

    [Fact]
    public async Task ExplicitUnsupportedScannerRefusesCommandsButAllowsAvailabilityProbe()
    {
        var (control, recorder) = RecordingControl.Create<ICompleteControl>();
        recorder.Status = new(0, 0, 107, new HashSet<string>()) { Complete = true, Supported = false };
        await using var dispatcher = new FlScriptingDispatcher(control);
        await Assert.ThrowsAsync<ScriptingException>(() => dispatcher.InvokeAsync("set_tempo", RecordingControl.Json("{\"bpm\":124}")));
        Assert.True((bool)(await dispatcher.InvokeAsync("is_available", ScriptingJson.EmptyObject))!);
        Assert.False((await dispatcher.GetCapabilitiesAsync()).Available);
    }

    [Fact]
    public async Task CallerCancellationDoesNotReleaseActiveNativeOperation()
    {
        var (control, recorder) = RecordingControl.Create<INativeFlControl>();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var complete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        recorder.Handler = (method, arguments) =>
        {
            if (method.Name != "SetTempoAsync") return RecordingControl.DefaultReturn(method);
            Assert.Equal(CancellationToken.None, arguments[^1]);
            entered.SetResult();
            return complete.Task;
        };
        await using var dispatcher = new FlScriptingDispatcher(control);
        using var caller = new CancellationTokenSource();
        try
        {
            var first = dispatcher.InvokeAsync("set_tempo", RecordingControl.Json("{\"bpm\":124}"), caller.Token);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            caller.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
            var second = dispatcher.InvokeAsync("get_tempo", ScriptingJson.EmptyObject);
            var drained = dispatcher.DrainAsync();
            Assert.False(second.IsCompleted);
            Assert.False(drained.IsCompleted);
            complete.SetResult();
            Assert.Equal(120.0, await second.WaitAsync(TimeSpan.FromSeconds(5)));
            await drained.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { complete.TrySetResult(); }
    }

    [Fact]
    public async Task DisposalCancelsQueuedCallsAndDrainsActiveWork()
    {
        var (control, recorder) = RecordingControl.Create<INativeFlControl>();
        var complete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        recorder.Handler = (method, _) => method.Name == "SetTempoAsync" ? complete.Task : RecordingControl.DefaultReturn(method);
        var dispatcher = new FlScriptingDispatcher(control);
        try
        {
            var active = dispatcher.InvokeAsync("set_tempo", RecordingControl.Json("{\"bpm\":124}"));
            var queued = dispatcher.InvokeAsync("get_tempo", ScriptingJson.EmptyObject);
            var disposal = dispatcher.DisposeAsync().AsTask();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
            Assert.False(disposal.IsCompleted);
            complete.SetResult();
            await Task.WhenAll(active, disposal).WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.ThrowsAsync<ObjectDisposedException>(() => dispatcher.InvokeAsync("get_tempo", ScriptingJson.EmptyObject));
        }
        finally { complete.TrySetResult(); await dispatcher.DisposeAsync(); }
    }
}
