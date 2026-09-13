using FruityLink.Plugins.Abstractions;
using FruityLink.Scripting;

namespace FruityLink.Plugins.Python;

/// <summary>Runs an authenticated scripting endpoint inside FL Studio without creating plugin UI.</summary>
public sealed class PythonControlPlugin : IFlPlugin
{
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private ScriptingPipeServer? _server;

    /// <inheritdoc />
    public string Id => "fl-python";
    /// <inheritdoc />
    public string Name => "FL Python";
    /// <inheritdoc />
    public string Description => "Local Python and scripting access to the typed FL Studio SDK.";
    /// <inheritdoc />
    public string Version => typeof(PythonControlPlugin).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    /// <inheritdoc />
    public async Task EnableAsync(IPluginContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        await _lifecycle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_server is not null) return;
            var options = context.Services.GetService(typeof(ScriptingServerOptions)) as ScriptingServerOptions
                ?? new ScriptingServerOptions { Log = context.Log };
            var server = new ScriptingPipeServer(new FlScriptingDispatcher(context.Fl), options);
            try { await server.StartAsync(ct).ConfigureAwait(false); }
            catch { await server.DisposeAsync().ConfigureAwait(false); throw; }
            _server = server;
            context.Log($"FL Python scripting endpoint ready: {server.Endpoint.PipeName}.");
        }
        finally { _lifecycle.Release(); }
    }

    /// <inheritdoc />
    public async Task DisableAsync(CancellationToken ct = default)
    {
        // Once teardown begins, drain active native operations even if the caller stops waiting.
        await _lifecycle.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (_server is null) return;
            await _server.DisposeAsync().ConfigureAwait(false);
            _server = null;
        }
        finally { _lifecycle.Release(); }
    }
}
