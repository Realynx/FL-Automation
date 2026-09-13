using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FruityLink.Scripting;

/// <summary>
/// Authenticated, current-user-only Windows named-pipe endpoint with one request per connection.
/// Owns the supplied dispatcher; disposal stops connections, removes owned discovery, and drains FL work.
/// </summary>
public sealed class ScriptingPipeServer : IAsyncDisposable
{
    private readonly FlScriptingDispatcher _dispatcher;
    private readonly ScriptingServerOptions _options;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly SemaphoreSlim _connections;
    private readonly object _workersSync = new();
    private readonly HashSet<Task> _workers = new();
    private readonly byte[] _token;
    private Task? _accept;
    private ScriptingDiscovery? _discovery;
    private bool _disposed;

    /// <summary>Creates an inactive endpoint with a fresh cryptographic token.</summary>
    public ScriptingPipeServer(FlScriptingDispatcher dispatcher, ScriptingServerOptions? options = null)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _options = options ?? new ScriptingServerOptions();
        ValidateOptions(_options);
        _connections = new(_options.MaximumConnections, _options.MaximumConnections);
        string token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        _token = Encoding.UTF8.GetBytes(token);
        Endpoint = new(1, dispatcher.Pid, dispatcher.InstanceId, _options.PipeName, token, DateTimeOffset.UtcNow);
    }

    /// <summary>Endpoint identity. Its token is a secret and must not be logged or sent to another user.</summary>
    public ScriptingEndpoint Endpoint { get; }

    /// <summary>Starts listening and atomically publishes discovery metadata after the first pipe exists.</summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        await _lifecycle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_accept is not null) return;
            var first = CreatePipe(firstInstance: true);
            try
            {
                if (_options.PublishDiscovery)
                {
                    _discovery = new(_options.DiscoveryDirectory, Endpoint);
                    await _discovery.PublishAsync(Endpoint, ct).ConfigureAwait(false);
                }
                _accept = AcceptAsync(first);
            }
            catch { first.Dispose(); _discovery?.Dispose(); _discovery = null; throw; }
        }
        finally { _lifecycle.Release(); }
    }

    /// <summary>Stops accepting requests and drains the owned dispatcher before releasing discovery ownership.</summary>
    public async ValueTask DisposeAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            _disposed = true;
            _stop.Cancel();
            if (_accept is not null) await _accept.ConfigureAwait(false);
            Task[] pending;
            lock (_workersSync) pending = _workers.ToArray();
            await Task.WhenAll(pending).ConfigureAwait(false);
            await _dispatcher.DisposeAsync().ConfigureAwait(false);
            _discovery?.Dispose();
            _discovery = null;
        }
        finally { _lifecycle.Release(); }
    }

    private async Task AcceptAsync(NamedPipeServerStream pending)
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                await _connections.WaitAsync(_stop.Token).ConfigureAwait(false);
                bool handedOff = false;
                try
                {
                    await pending.WaitForConnectionAsync(_stop.Token).ConfigureAwait(false);
                    var connected = pending;
                    pending = CreatePipe(firstInstance: false);
                    Track(HandleConnectionAsync(connected));
                    handedOff = true;
                }
                finally { if (!handedOff) _connections.Release(); }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception error) { Log($"Scripting listener stopped: {error.GetType().Name}."); }
        finally { pending.Dispose(); }
    }

    private void Track(Task worker)
    {
        lock (_workersSync) _workers.Add(worker);
        _ = worker.ContinueWith(completed =>
        {
            lock (_workersSync) _workers.Remove(completed);
            _ = completed.Exception;
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private async Task HandleConnectionAsync(NamedPipeServerStream pipe)
    {
        using (pipe)
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token))
        {
            timeout.CancelAfter(_options.RequestTimeout);
            try { await ProcessRequestAsync(pipe, timeout).ConfigureAwait(false); }
            catch (IOException) { }
            catch (OperationCanceledException) { }
            catch (Exception error) { Log($"Scripting connection failed: {error.GetType().Name}."); }
            finally { _connections.Release(); }
        }
    }

    private async Task ProcessRequestAsync(NamedPipeServerStream pipe, CancellationTokenSource requestLifetime)
    {
        string id = "";
        Task? disconnected = null;
        using var watch = CancellationTokenSource.CreateLinkedTokenSource(requestLifetime.Token);
        try
        {
            using var document = await ScriptingPipeFraming.ReadAsync(pipe, requestLifetime.Token).ConfigureAwait(false);
            var request = document.RootElement;
            JsonValidation.Object(request, "request", new[] { "id", "token", "method", "params" });
            id = JsonValidation.RequiredString(request, "id");
            if (id.Length > 128) throw new ScriptingException("invalid_request", "Request id exceeds 128 characters.");
            Authenticate(request);
            string method = JsonValidation.RequiredString(request, "method");
            var parameters = request.TryGetProperty("params", out var value) ? value : ScriptingJson.EmptyObject;
            disconnected = WatchDisconnectAsync(pipe, requestLifetime, watch.Token);
            object? result = await _dispatcher.HandleRequestAsync(method, parameters, requestLifetime.Token).ConfigureAwait(false);
            await ScriptingPipeFraming.WriteAsync(pipe, new { id, result }, requestLifetime.Token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException || !requestLifetime.IsCancellationRequested)
        {
            await ScriptingPipeFraming.WriteAsync(pipe, new { id, error = ScriptingError.FromException(error) }, requestLifetime.Token).ConfigureAwait(false);
        }
        finally
        {
            watch.Cancel();
            if (disconnected is not null) await disconnected.ConfigureAwait(false);
        }
    }

    private static async Task WatchDisconnectAsync(Stream pipe, CancellationTokenSource requestLifetime, CancellationToken ct)
    {
        try
        {
            // EOF means disconnection; extra bytes violate the one-request-per-connection contract.
            _ = await pipe.ReadAsync(new byte[1], ct).ConfigureAwait(false);
            requestLifetime.Cancel();
        }
        catch (OperationCanceledException) { }
        catch (IOException) { requestLifetime.Cancel(); }
    }

    private void Authenticate(JsonElement request)
    {
        string supplied = JsonValidation.RequiredString(request, "token");
        if (supplied.Length != _token.Length || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(supplied), _token))
            throw new ScriptingException("unauthenticated", "Endpoint authentication failed.");
    }

    private NamedPipeServerStream CreatePipe(bool firstInstance) => new(_options.PipeName, PipeDirection.InOut,
        NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly | (firstInstance ? PipeOptions.FirstPipeInstance : PipeOptions.None));

    private void Log(string message)
    {
        try { _options.Log?.Invoke(message); }
        catch { /* A host logging callback must not prevent endpoint teardown. */ }
    }

    private static void ValidateOptions(ScriptingServerOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.PipeName) || options.PipeName.IndexOfAny(new[] { '\\', '/', ':' }) >= 0)
            throw new ArgumentException("A scripting pipe name must be a nonempty local name.", nameof(options));
        if (options.MaximumConnections is < 1 or > 64) throw new ArgumentOutOfRangeException(nameof(options), "MaximumConnections must be 1..64.");
        if (options.RequestTimeout <= TimeSpan.Zero || options.RequestTimeout > TimeSpan.FromHours(1))
            throw new ArgumentOutOfRangeException(nameof(options), "RequestTimeout must be positive and no greater than one hour.");
    }
}
