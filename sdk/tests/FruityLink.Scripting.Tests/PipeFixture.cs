using FruityLink.Core.Abstractions;
using System.IO.Pipes;
using System.Text.Json;

namespace FruityLink.Scripting.Tests;

internal sealed class PipeFixture : IAsyncDisposable
{
    internal string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "FruityLink-ScriptingTests-" + Guid.NewGuid().ToString("N"));
    internal ScriptingPipeServer Server { get; }
    internal RecordingControl Recorder { get; }
    internal string DiscoveryPath => Path.Combine(DirectoryPath, $"{Environment.ProcessId}.json");

    internal PipeFixture(bool structured = false, TimeSpan? timeout = null)
    {
        INativeFlControl control;
        if (structured) (control, Recorder) = RecordingControl.Create<ICompleteControl>();
        else (control, Recorder) = RecordingControl.Create<INativeFlControl>();
        Server = new(new FlScriptingDispatcher(control), new ScriptingServerOptions
        {
            PipeName = "FruityLinkScripting-test-" + Guid.NewGuid().ToString("N"),
            DiscoveryDirectory = DirectoryPath,
            RequestTimeout = timeout ?? TimeSpan.FromSeconds(30)
        });
    }

    internal async Task<NamedPipeClientStream> ConnectAsync()
    {
        var client = new NamedPipeClientStream(".", Server.Endpoint.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try { await client.ConnectAsync(5000); return client; }
        catch { client.Dispose(); throw; }
    }

    internal async Task<JsonElement> RequestAsync(string method, object parameters, string? token = null)
    {
        using var client = await ConnectAsync();
        string id = Guid.NewGuid().ToString("N");
        await ScriptingPipeFraming.WriteAsync(client, new { id, token = token ?? Server.Endpoint.Token, method, @params = parameters });
        using var response = await ScriptingPipeFraming.ReadAsync(client).WaitAsync(TimeSpan.FromSeconds(10));
        if (response.RootElement.GetProperty("id").GetString() != id) throw new InvalidOperationException("Response id mismatch.");
        return response.RootElement.Clone();
    }

    public async ValueTask DisposeAsync()
    {
        await Server.DisposeAsync();
        if (Directory.Exists(DirectoryPath)) Directory.Delete(DirectoryPath, recursive: true);
    }
}
