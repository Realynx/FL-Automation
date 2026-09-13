using FruityLink.Core.Abstractions;
using System.Text.Json;

namespace FruityLink.Scripting;

/// <summary>
/// Exposes only explicitly allowlisted typed SDK interfaces. Every FL operation is serialized;
/// cancelling a caller stops queued work but never releases an active operation's gate prematurely.
/// Started native operations run to completion and disposal drains them. Batches are not transactions.
/// </summary>
public sealed class FlScriptingDispatcher : IAsyncDisposable
{
    private readonly INativeFlControl _control;
    private readonly OperationRegistry _registry;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly object _disposeSync = new();
    private Task? _disposeTask;

    /// <summary>Creates a dispatcher for the safe SDK control surface and optional structured-query interface.</summary>
    public FlScriptingDispatcher(INativeFlControl control, string? instanceId = null)
    {
        _control = control ?? throw new ArgumentNullException(nameof(control));
        InstanceId = instanceId is null ? Guid.NewGuid().ToString("D") : Guid.Parse(instanceId).ToString("D");
        _registry = new(control);
    }

    /// <summary>Unique identity of this dispatcher lifetime, echoed in endpoint discovery and capabilities.</summary>
    public string InstanceId { get; }
    /// <summary>The host process ID.</summary>
    public int Pid => Environment.ProcessId;
    /// <summary>All operations implemented by this dispatcher's explicitly allowed interfaces.</summary>
    public ScriptingCatalog Catalog => _registry.Catalog;

    /// <summary>Handles one protocol method without transport or authentication concerns.</summary>
    public Task<object?> HandleRequestAsync(string method, JsonElement parameters, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_stop.IsCancellationRequested, this);
        return method switch
        {
            "catalog" => Task.FromResult<object?>(FilterCatalog(parameters)),
            "capabilities" => CapabilitiesRequestAsync(parameters, ct),
            "invoke" => InvokeRequestAsync(parameters, ct),
            "batch" => BatchRequestAsync(parameters, ct),
            _ => throw new ScriptingException("invalid_request", $"Unknown protocol method '{method}'.")
        };
    }

    /// <summary>Invokes one snake_case operation with case-sensitive camelCase JSON arguments.</summary>
    public Task<object?> InvokeAsync(string operation, JsonElement arguments, CancellationToken ct = default)
    {
        var bound = _registry.Find(operation);
        var values = bound.Bind(arguments);
        var ownedArguments = arguments.Clone();
        return SerializedAsync(async () =>
        {
            var status = await SymbolStatusAsync().ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            _stop.Token.ThrowIfCancellationRequested();
            EnsureAvailable(operation, ownedArguments, status);
            return await bound.InvokeAsync(_control, values).ConfigureAwait(false);
        }, ct);
    }

    /// <summary>Reports host identity and known native compatibility requirements.</summary>
    public async Task<ScriptingCapabilities> GetCapabilitiesAsync(CancellationToken ct = default)
        => (ScriptingCapabilities)(await SerializedAsync(async () =>
        {
            var status = await SymbolStatusAsync().ConfigureAwait(false);
            bool available = await _control.IsAvailableAsync(CancellationToken.None).ConfigureAwait(false);
            var unavailable = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var operation in Catalog.Operations)
                if (OperationAvailability.Unavailable(operation.Name, status) is { } reason) unavailable.Add(operation.Name, reason);
            return new ScriptingCapabilities(1, Pid, InstanceId, Catalog.Operations.Count,
                available && status?.Supported != false && status?.Complete != false, status, status?.MixerLayout is not null, unavailable);
        }, ct).ConfigureAwait(false))!;

    /// <summary>Executes an ordered batch without interleaving; completed changes remain after a later failure.</summary>
    public async Task<ScriptingBatchResult> BatchAsync(IReadOnlyList<ScriptingCall> operations, bool stopOnError = true,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(operations);
        if (operations.Count is < 1 or > 256) throw new ScriptingException("invalid_arguments", "A batch must contain between 1 and 256 operations.");
        var owned = operations.Select(call => new ScriptingCall(call.Operation, call.Arguments.Clone())).ToArray();
        return (ScriptingBatchResult)(await SerializedAsync(() => RunBatchAsync(owned, stopOnError, ct), ct).ConfigureAwait(false))!;
    }

    /// <summary>Cancels queued work and waits for any started FL operation to complete before returning.</summary>
    public ValueTask DisposeAsync()
    {
        lock (_disposeSync) return new(_disposeTask ??= StopAsync());
    }

    /// <summary>Waits until previously queued or active FL work finishes, without stopping the dispatcher.
    /// A transport can use this barrier before acknowledging cancellation to a process-owning caller.</summary>
    public async Task DrainAsync()
    {
        await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        _gate.Release();
    }

    private async Task<object?> RunBatchAsync(IReadOnlyList<ScriptingCall> operations, bool stopOnError, CancellationToken caller)
    {
        var status = await SymbolStatusAsync().ConfigureAwait(false);
        var results = new List<ScriptingBatchItem>();
        foreach (var operation in operations)
        {
            caller.ThrowIfCancellationRequested();
            _stop.Token.ThrowIfCancellationRequested();
            var outcome = await RunBatchItemAsync(operation, status).ConfigureAwait(false);
            results.Add(outcome);
            if (outcome.Error is not null && stopOnError) return new ScriptingBatchResult(results, true);
        }
        return new ScriptingBatchResult(results, false);
    }

    private async Task<ScriptingBatchItem> RunBatchItemAsync(ScriptingCall call, FlSymbolStatus? status)
    {
        try
        {
            var bound = _registry.Find(call.Operation);
            var values = bound.Bind(call.Arguments);
            EnsureAvailable(call.Operation, call.Arguments, status);
            return new(call.Operation, await bound.InvokeAsync(_control, values).ConfigureAwait(false), null);
        }
        catch (Exception error) { return new(call.Operation, null, ScriptingError.FromException(error)); }
    }

    private Task<object?> SerializedAsync(Func<Task<object?>> action, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_stop.IsCancellationRequested, this);
        var execution = RunSerializedAsync(action, ct);
        // Observe a later native failure even when the caller has cancelled its response wait.
        _ = execution.ContinueWith(task => _ = task.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return execution.WaitAsync(ct);
    }

    private async Task<object?> RunSerializedAsync(Func<Task<object?>> action, CancellationToken caller)
    {
        using var queued = CancellationTokenSource.CreateLinkedTokenSource(caller, _stop.Token);
        await _gate.WaitAsync(queued.Token).ConfigureAwait(false);
        try
        {
            queued.Token.ThrowIfCancellationRequested();
            using var nativeCompletion = (_control as IFlNativeCompletionScope)?.RequireNativeCompletion();
            return await action().ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private async Task StopAsync()
    {
        _stop.Cancel();
        await _gate.WaitAsync().ConfigureAwait(false);
        _gate.Release();
        // The lightweight gate/token remain valid for cancelled waiters and repeated disposal checks.
    }

    private Task<FlSymbolStatus?> SymbolStatusAsync() => _control is IFlSymbolResolution symbols
        ? symbols.GetSymbolStatusAsync(CancellationToken.None) : Task.FromResult<FlSymbolStatus?>(null);

    private static void EnsureAvailable(string operation, JsonElement arguments, FlSymbolStatus? status)
    {
        if (OperationAvailability.Unavailable(operation, status, arguments) is { } reason)
            throw new ScriptingException("unavailable", reason);
    }

    private ScriptingCatalog FilterCatalog(JsonElement parameters)
    {
        JsonValidation.Object(parameters, "params", new[] { "filter" });
        if (!parameters.TryGetProperty("filter", out var filter) || filter.ValueKind == JsonValueKind.Null) return Catalog;
        if (filter.ValueKind != JsonValueKind.String) throw new ScriptingException("invalid_arguments", "Catalogue filter must be a string.");
        string term = filter.GetString()!;
        return new(1, Catalog.Operations.Where(operation => operation.Name.Contains(term, StringComparison.OrdinalIgnoreCase) ||
            operation.Description.Contains(term, StringComparison.OrdinalIgnoreCase)).ToArray());
    }

    private Task<object?> InvokeRequestAsync(JsonElement parameters, CancellationToken ct)
    {
        JsonValidation.Object(parameters, "params", new[] { "operation", "arguments" });
        return InvokeAsync(JsonValidation.RequiredString(parameters, "operation"), JsonValidation.Arguments(parameters), ct);
    }

    private async Task<object?> CapabilitiesRequestAsync(JsonElement parameters, CancellationToken ct)
    {
        JsonValidation.Object(parameters, "params", Array.Empty<string>());
        return await GetCapabilitiesAsync(ct).ConfigureAwait(false);
    }

    private async Task<object?> BatchRequestAsync(JsonElement parameters, CancellationToken ct)
    {
        JsonValidation.Object(parameters, "params", new[] { "operations", "stopOnError" });
        if (!parameters.TryGetProperty("operations", out var operations) || operations.ValueKind != JsonValueKind.Array)
            throw new ScriptingException("invalid_arguments", "Batch operations must be an array.");
        bool stop = ReadStopOnError(parameters);
        var calls = operations.EnumerateArray().Select(ParseCall).ToArray();
        return await BatchAsync(calls, stop, ct).ConfigureAwait(false);
    }

    private static ScriptingCall ParseCall(JsonElement call)
    {
        JsonValidation.Object(call, "batch operation", new[] { "operation", "arguments" });
        return new(JsonValidation.RequiredString(call, "operation"), JsonValidation.Arguments(call));
    }

    private static bool ReadStopOnError(JsonElement parameters)
    {
        if (!parameters.TryGetProperty("stopOnError", out var value)) return true;
        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new ScriptingException("invalid_arguments", "stopOnError must be a boolean.");
        return value.GetBoolean();
    }
}
