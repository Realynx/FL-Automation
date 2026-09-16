using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using FruityLink.Core.Abstractions;
using FruityLink.Scripting;

[assembly: InternalsVisibleTo("FruityLink.Plugins.PythonIde.Execution.Tests")]

namespace FruityLink.Plugins.PythonIde.Execution;

/// <summary>A lazy shared-interpreter lease for one IDE window. Completed edits are never rolled back.</summary>
public sealed class PythonIdeExecutionService : IPythonIdeExecution
{
    private readonly object sync = new();
    private readonly FlScriptingDispatcher dispatcher;
    private readonly Func<Func<string, JsonElement, CancellationToken, Task<object?>>, IEmbeddedPythonRuntime> createRuntime;
    private readonly CancellationTokenSource lifetime = new();
    private IEmbeddedPythonRuntime? runtime;
    private ExecutionRun? active;
    private Task? disposal;
    private bool stopping;

    /// <summary>Creates an idle adapter. No Python runtime is located or loaded until ExecuteAsync.</summary>
    public PythonIdeExecutionService(INativeFlControl fl)
        : this(fl, handler => new EmbeddedPythonRuntime(EmbeddedPythonRuntimeLocator.Resolve(), handler)) { }

    internal PythonIdeExecutionService(INativeFlControl fl,
        Func<Func<string, JsonElement, CancellationToken, Task<object?>>, IEmbeddedPythonRuntime> createRuntime)
    {
        dispatcher = new(fl);
        this.createRuntime = createRuntime ?? throw new ArgumentNullException(nameof(createRuntime));
    }

    /// <inheritdoc />
    public Task<JsonElement> ExecuteAsync(string code, int timeoutSeconds = 60, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        if (Encoding.UTF8.GetByteCount(code) > 4 * 1024 * 1024) throw new ArgumentException("Python code exceeds 4 MiB.", nameof(code));
        if (timeoutSeconds is < 1 or > 300) throw new ArgumentOutOfRangeException(nameof(timeoutSeconds));
        ct.ThrowIfCancellationRequested();
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(stopping, this);
            if (active is not null) throw new InvalidOperationException("Python is already running. Stop it or wait for it to finish before running again.");
            var run = new ExecutionRun(CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime.Token));
            active = run;
            _ = Task.Run(() => CompleteRunAsync(run, code, timeoutSeconds));
            return run.Completion.Task;
        }
    }

    /// <inheritdoc />
    public async Task<JsonElement> GetCatalogAsync(string? filter = null, CancellationToken ct = default)
    {
        lock (sync) ObjectDisposedException.ThrowIf(stopping, this);
        var value = await dispatcher.HandleRequestAsync("catalog", JsonSerializer.SerializeToElement(new { filter }, ScriptingJson.Options), ct).ConfigureAwait(false);
        return JsonSerializer.SerializeToElement(value, ScriptingJson.Options);
    }

    /// <inheritdoc />
    public Task CancelAsync()
    {
        lock (sync)
        {
            var run = active;
            if (run is null) return Task.CompletedTask;
            Exception? failure = null;
            try { run.Cancellation.Cancel(); }
            catch (Exception error) { failure = error; }
            return CompleteCancellationAsync(run.Completion.Task, failure);
        }
    }

    private async Task CompleteRunAsync(ExecutionRun run, string code, int timeout)
    {
        JsonElement result = default;
        Exception? failure = null;
        try
        {
            run.Cancellation.Token.ThrowIfCancellationRequested();
            runtime ??= createRuntime(HandleRequestAsync);
            result = await runtime.ExecuteAsync(code, timeout, run.Cancellation.Token).ConfigureAwait(false);
        }
        catch (Exception error) { failure = error; }
        finally
        {
            await dispatcher.DrainAsync().ConfigureAwait(false);
            lock (sync)
            {
                active = null;
                run.Cancellation.Dispose();
                if (failure is OperationCanceledException cancelled) run.Completion.TrySetCanceled(cancelled.CancellationToken);
                else if (failure is not null) run.Completion.TrySetException(failure);
                else run.Completion.TrySetResult(result);
            }
        }
    }

    private async Task<object?> HandleRequestAsync(string method, JsonElement parameters, CancellationToken ct)
    {
        try { return await dispatcher.HandleRequestAsync(method, parameters, ct).ConfigureAwait(false); }
        finally { await dispatcher.DrainAsync().ConfigureAwait(false); }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        lock (sync)
        {
            if (disposal is null)
            {
                stopping = true;
                var pending = active?.Completion.Task ?? Task.CompletedTask;
                Exception? failure = null;
                try { lifetime.Cancel(); }
                catch (Exception error) { failure = error; }
                disposal = DisposeCoreAsync(pending, failure);
            }
            return new(disposal);
        }
    }

    private async Task DisposeCoreAsync(Task pending, Exception? cancellationFailure)
    {
        await ObserveRunAsync(pending).ConfigureAwait(false);
        try { if (runtime is not null) await runtime.DisposeAsync().ConfigureAwait(false); }
        finally
        {
            await dispatcher.DisposeAsync().ConfigureAwait(false);
            lifetime.Dispose();
        }
        if (cancellationFailure is not null) throw cancellationFailure;
    }

    private static async Task CompleteCancellationAsync(Task pending, Exception? failure)
    {
        await ObserveRunAsync(pending).ConfigureAwait(false);
        if (failure is not null) throw failure;
    }

    private static async Task ObserveRunAsync(Task task)
    {
        try { await task.ConfigureAwait(false); }
        catch (Exception) { /* The original execution task carries its outcome to the UI. */ }
    }

    private sealed class ExecutionRun(CancellationTokenSource cancellation)
    {
        internal CancellationTokenSource Cancellation { get; } = cancellation;
        internal TaskCompletionSource<JsonElement> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
