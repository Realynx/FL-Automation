using System.Collections.Concurrent;
using System.Text.Json;

namespace FruityLink.Scripting;

internal sealed class EmbeddedPythonEngine
{
    private static readonly object ProcessLock = new();
    private static EmbeddedPythonEngine? _instance;
    private readonly EmbeddedPythonOptions _options;
    private readonly BlockingCollection<EmbeddedPythonJob> _jobs = new();
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private EmbeddedPythonSession? _session;
    private nint _threadState;

    private EmbeddedPythonEngine(EmbeddedPythonOptions options)
    {
        _options = options;
        new Thread(Run) { Name = "FruityLink embedded CPython", IsBackground = true }.Start();
    }

    internal static EmbeddedPythonEngine Get(EmbeddedPythonOptions options)
    {
        lock (ProcessLock)
        {
            _instance ??= new(options);
            if (!SamePath(_instance._options.RuntimeDirectory, options.RuntimeDirectory) ||
                !SamePath(_instance._options.PythonPackagePath, options.PythonPackagePath) ||
                !SamePaths(_instance._options.ExtensionPackagePaths, options.ExtensionPackagePaths))
                throw new InvalidOperationException("The process already owns another embedded Python configuration. Restart FL to change it.");
            return _instance;
        }
    }

    internal static EmbeddedPythonOptions? GetActiveOptions()
    {
        lock (ProcessLock) return _instance?._options;
    }

    internal async Task<JsonElement> ExecuteAsync(string code, int seconds,
        Func<string, JsonElement, CancellationToken, Task<object?>> handler, CancellationToken ct)
    {
        // Initialization executes native code too: cancellation must not acknowledge
        // a drained lease while Py_InitializeFromInitConfig is still running.
        await _ready.Task.ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        var job = new EmbeddedPythonJob(code, seconds, handler, ct);
        _jobs.Add(job, ct);
        return await job.Completion.Task.ConfigureAwait(false);
    }

    private void Run()
    {
        EmbeddedPythonApi api;
        try
        {
            api = EmbeddedPythonApi.Load(_options.RuntimeDirectory);
            EmbeddedPythonConfiguration.Initialize(api, _options);
            _session = new(api);
            _threadState = api.SaveThread();
            _ready.SetResult();
        }
        catch (Exception error) { _ready.SetException(error); return; }
        foreach (var job in _jobs.GetConsumingEnumerable()) RunJob(api, job);
    }

    private void RunJob(EmbeddedPythonApi api, EmbeddedPythonJob job)
    {
        if (job.Caller.IsCancellationRequested)
        {
            job.Handler = null;
            job.Completion.SetCanceled(job.Caller);
            return;
        }
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(job.Seconds));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(job.Caller, deadline.Token);
        job.Token = linked.Token;
        JsonElement result = default;
        Exception? failure = null;
        api.RestoreThread(_threadState);
        try { result = _session!.Execute(job); }
        catch (Exception error) { failure = error; }
        finally
        {
            job.Handler = null;
            _threadState = api.SaveThread();
        }
        // Completion is signalled only after Python, child threads, callbacks and GIL ownership drain.
        Complete(job, deadline.IsCancellationRequested, result, failure);
    }

    private static void Complete(EmbeddedPythonJob job, bool timedOut, JsonElement result, Exception? failure)
    {
        if (job.Caller.IsCancellationRequested) job.Completion.SetCanceled(job.Caller);
        else if (timedOut) job.Completion.SetException(new TimeoutException("Embedded Python exceeded its timeout; active work has now drained."));
        else if (failure is not null) job.Completion.SetException(failure);
        else job.Completion.SetResult(result);
    }

    private static bool SamePath(string first, string second) => string.Equals(first, second, StringComparison.OrdinalIgnoreCase);
    private static bool SamePaths(IReadOnlyList<string> first, IReadOnlyList<string> second) => first.Count == second.Count &&
        first.Zip(second).All(pair => SamePath(pair.First, pair.Second));
}

internal sealed class EmbeddedPythonJob(string code, int seconds,
    Func<string, JsonElement, CancellationToken, Task<object?>> handler, CancellationToken caller)
{
    internal string Scope { get; } = Guid.NewGuid().ToString("N");
    internal string Code { get; } = code;
    internal int Seconds { get; } = seconds;
    internal CancellationToken Caller { get; } = caller;
    internal CancellationToken Token { get; set; }
    internal Func<string, JsonElement, CancellationToken, Task<object?>>? Handler { get; set; } = handler;
    internal TaskCompletionSource<JsonElement> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}
