using FruityLink.Core.Abstractions;
using System.Buffers.Binary;
using System.Text.Json;
using Xunit;

namespace FruityLink.Scripting.Tests;

public sealed class PipeServerTests
{
    [Fact]
    public async Task AuthenticatedCapabilitiesRoundTripMatchesPrivateDiscovery()
    {
        await using var fixture = new PipeFixture(structured: true);
        await fixture.Server.StartAsync();
        await fixture.Server.StartAsync();
        var endpoint = JsonSerializer.Deserialize<ScriptingEndpoint>(await File.ReadAllTextAsync(fixture.DiscoveryPath), ScriptingJson.Options)!;
        Assert.Equal(fixture.Server.Endpoint.InstanceId, endpoint.InstanceId);
        Assert.Equal(32, Convert.FromBase64String(endpoint.Token).Length);
        var response = await fixture.RequestAsync("capabilities", new { });
        var capabilities = response.GetProperty("result");
        Assert.Equal(endpoint.Pid, capabilities.GetProperty("pid").GetInt32());
        Assert.Equal(endpoint.InstanceId, capabilities.GetProperty("instanceId").GetString());
        Assert.Equal(typeof(INativeFlControl).GetMethods().Length + typeof(IFlStructuredQuery).GetMethods().Length,
            capabilities.GetProperty("operationCount").GetInt32());
        await fixture.Server.DisposeAsync();
        Assert.False(File.Exists(fixture.DiscoveryPath));
    }

    [Fact]
    public async Task WrongTokenNeverReachesTheControlSurface()
    {
        await using var fixture = new PipeFixture();
        await fixture.Server.StartAsync();
        var response = await fixture.RequestAsync("invoke", new { operation = "set_tempo", arguments = new { bpm = 124 } }, new string('X', 44));
        Assert.Equal("unauthenticated", response.GetProperty("error").GetProperty("code").GetString());
        Assert.Empty(fixture.Recorder.Calls);
    }

    [Fact]
    public async Task UnicodeAndBatchesRoundTripWithoutLoss()
    {
        await using var fixture = new PipeFixture();
        fixture.Recorder.Handler = (method, _) => method.Name == "GetChannelNameAsync"
            ? Task.FromResult("音楽 🎛️\nline two") : RecordingControl.DefaultReturn(method);
        await fixture.Server.StartAsync();
        var response = await fixture.RequestAsync("invoke", new { operation = "get_channel_name", arguments = new { index = 0 } });
        Assert.Equal("音楽 🎛️\nline two", response.GetProperty("result").GetString());
        var batch = await fixture.RequestAsync("batch", new
        {
            operations = new object[]
            {
                new { operation = "set_tempo", arguments = new { bpm = 124 } },
                new { operation = "get_tempo", arguments = new { } }
            },
            stopOnError = true
        });
        Assert.Equal(120, batch.GetProperty("result").GetProperty("results")[1].GetProperty("result").GetDouble());
    }

    [Fact]
    public async Task OversizedFrameIsRefusedBeforeAllocatingOrCallingFl()
    {
        await using var fixture = new PipeFixture();
        await fixture.Server.StartAsync();
        using var client = await fixture.ConnectAsync();
        byte[] header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, ScriptingPipeFraming.MaximumFrameBytes + 1);
        await client.WriteAsync(header);
        using var response = await ScriptingPipeFraming.ReadAsync(client).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("invalid_frame", response.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Empty(fixture.Recorder.Calls);
    }

    [Fact]
    public async Task NativeIoFailureReturnsAnOperationErrorAndListenerRemainsUsable()
    {
        await using var fixture = new PipeFixture();
        fixture.Recorder.Handler = (method, _) => method.Name == "SaveCopyAsync"
            ? Task.FromException(new IOException("Project destination is unavailable.")) : RecordingControl.DefaultReturn(method);
        await fixture.Server.StartAsync();
        var response = await fixture.RequestAsync("invoke", new { operation = "save_copy", arguments = new { path = @"C:\missing\copy.flp" } });
        Assert.Equal("operation_failed", response.GetProperty("error").GetProperty("code").GetString());
        var next = await fixture.RequestAsync("invoke", new { operation = "get_tempo", arguments = new { } });
        Assert.Equal(120, next.GetProperty("result").GetDouble());
    }

    [Fact]
    public async Task OversizedResultReturnsABoundedErrorBeforeWritingItsFrame()
    {
        await using var fixture = new PipeFixture();
        fixture.Recorder.Handler = (method, _) => method.Name == "GetChannelNameAsync"
            ? Task.FromResult(new string('x', ScriptingPipeFraming.MaximumFrameBytes)) : RecordingControl.DefaultReturn(method);
        await fixture.Server.StartAsync();
        var response = await fixture.RequestAsync("invoke", new { operation = "get_channel_name", arguments = new { index = 0 } });
        Assert.Equal("response_too_large", response.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task FirstInstanceProtectionCannotReplaceAnExistingEndpoint()
    {
        await using var fixture = new PipeFixture();
        await fixture.Server.StartAsync();
        var (control, _) = RecordingControl.Create<INativeFlControl>();
        await using var duplicate = new ScriptingPipeServer(new FlScriptingDispatcher(control), new()
        {
            PipeName = fixture.Server.Endpoint.PipeName,
            PublishDiscovery = false
        });
        var error = await Record.ExceptionAsync(() => duplicate.StartAsync());
        Assert.True(error is IOException or UnauthorizedAccessException);
        var response = await fixture.RequestAsync("invoke", new { operation = "get_tempo", arguments = new { } });
        Assert.Equal(120, response.GetProperty("result").GetDouble());
    }

    [Fact]
    public async Task TeardownNeverDeletesAnotherInstanceMetadata()
    {
        await using var fixture = new PipeFixture();
        await fixture.Server.StartAsync();
        var successor = fixture.Server.Endpoint with { InstanceId = Guid.NewGuid().ToString("D") };
        await File.WriteAllTextAsync(fixture.DiscoveryPath, JsonSerializer.Serialize(successor, ScriptingJson.Options));
        await fixture.Server.DisposeAsync();
        var remaining = JsonSerializer.Deserialize<ScriptingEndpoint>(await File.ReadAllTextAsync(fixture.DiscoveryPath), ScriptingJson.Options);
        Assert.Equal(successor.InstanceId, remaining!.InstanceId);
    }

    [Fact]
    public async Task ClientDisconnectStopsBatchRemainderButKeepsCurrentNativeCallSerialized()
    {
        await using var fixture = new PipeFixture();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var complete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Recorder.Handler = (method, _) =>
        {
            if (method.Name != "SetTempoAsync") return RecordingControl.DefaultReturn(method);
            entered.TrySetResult();
            return complete.Task;
        };
        await fixture.Server.StartAsync();
        try
        {
            using var client = await fixture.ConnectAsync();
            await ScriptingPipeFraming.WriteAsync(client, new
            {
                id = "disconnect-test", token = fixture.Server.Endpoint.Token, method = "batch",
                @params = new
                {
                    operations = new object[]
                    {
                        new { operation = "set_tempo", arguments = new { bpm = 124 } },
                        new { operation = "transport_play", arguments = new { } }
                    }, stopOnError = true
                }
            });
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            client.Dispose();
            var next = fixture.RequestAsync("invoke", new { operation = "get_tempo", arguments = new { } });
            Assert.False(next.IsCompleted);
            // Allow the pipe's EOF notification to cancel the batch while its first native call is held.
            await Task.Delay(100);
            complete.SetResult();
            Assert.Equal(120, (await next).GetProperty("result").GetDouble());
            Assert.DoesNotContain(fixture.Recorder.Calls, call => call.Method == "TransportPlayAsync");
        }
        finally { complete.TrySetResult(); }
    }
}
