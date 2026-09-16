using System.Text;
using System.Text.Json;

namespace FruityLink.Scripting;

/// <summary>Absolute locations of the private CPython 3.14 runtime, SDK package, and trusted installed extensions.</summary>
public sealed record EmbeddedPythonOptions(string RuntimeDirectory, string PythonPackagePath)
{
    /// <summary>Import paths discovered beneath the framework's private Python extension directory: one wheel per
    /// extension, or that extension's unpacked <c>site-packages</c> directory when it ships compiled modules.</summary>
    public IReadOnlyList<string> ExtensionPackagePaths { get; init; } = [];
}

/// <summary>Executes trusted Python inside the host process. Cancellation is cooperative and drains active work.</summary>
public interface IEmbeddedPythonRuntime : IAsyncDisposable
{
    /// <summary>
    /// Runs with the typed SDK object <c>fl</c> and returns bounded output and the script's <c>result</c>.
    /// A script exception yields <c>ok:false</c> with <c>error</c>, <c>traceback</c>, the stdout/stderr captured
    /// before the failure and, when <c>result</c> was already assigned, its value with <c>resultPartial:true</c>.
    /// </summary>
    Task<JsonElement> ExecuteAsync(string code, int timeoutSeconds, CancellationToken ct = default);
}

/// <summary>
/// A plugin's lease on a process-lifetime, isolated CPython interpreter. Disposal cancels and drains this
/// lease without finalizing Python or unloading its DLL. Request handlers must themselves drain started
/// native work before completing, including when cancelled. Scripts execute with the host's privileges.
/// </summary>
public sealed class EmbeddedPythonRuntime : IEmbeddedPythonRuntime
{
    private readonly EmbeddedPythonOptions _options;
    private readonly CancellationTokenSource _stop = new();
    private readonly object _sync = new();
    private readonly HashSet<Task<JsonElement>> _pending = [];
    private Func<string, JsonElement, CancellationToken, Task<object?>>? _handler;
    private Task? _disposeTask;

    /// <summary>Creates a lazy interpreter lease; the first execution initializes the private runtime.</summary>
    public EmbeddedPythonRuntime(EmbeddedPythonOptions options,
        Func<string, JsonElement, CancellationToken, Task<object?>> requestHandler)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(requestHandler);
        if (!Path.IsPathFullyQualified(options.RuntimeDirectory) || !Path.IsPathFullyQualified(options.PythonPackagePath))
            throw new ArgumentException("Embedded Python runtime and package locations must be absolute paths.", nameof(options));
        _options = new(Path.GetFullPath(options.RuntimeDirectory), Path.GetFullPath(options.PythonPackagePath))
        {
            ExtensionPackagePaths = options.ExtensionPackagePaths.Select(path =>
                Path.IsPathFullyQualified(path) ? Path.GetFullPath(path) :
                throw new ArgumentException("Embedded Python extension locations must be absolute paths.", nameof(options))).ToArray(),
        };
        _handler = requestHandler;
    }

    /// <inheritdoc />
    public Task<JsonElement> ExecuteAsync(string code, int timeoutSeconds, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(code);
        if (timeoutSeconds is < 1 or > 300) throw new ArgumentOutOfRangeException(nameof(timeoutSeconds));
        if (Encoding.UTF8.GetByteCount(code) > 4 * 1024 * 1024)
            throw new ArgumentException("Python code exceeds 4 MiB.", nameof(code));
        ct.ThrowIfCancellationRequested();
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposeTask is not null, this);
            var task = RunAsync(code, timeoutSeconds, ct);
            _pending.Add(task);
            _ = task.ContinueWith(completed => { lock (_sync) _pending.Remove(completed); },
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return task;
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        lock (_sync) return new(_disposeTask ??= StopAsync());
    }

    private async Task<JsonElement> RunAsync(string code, int seconds, CancellationToken caller)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(caller, _stop.Token);
        var engine = EmbeddedPythonEngine.Get(_options);
        return await engine.ExecuteAsync(code, seconds, _handler!, linked.Token).ConfigureAwait(false);
    }

    private async Task StopAsync()
    {
        _stop.Cancel();
        var pending = _pending.ToArray();
        try { await Task.WhenAll(pending).ConfigureAwait(false); }
        catch (Exception) { /* Execution failures remain observable by their original callers. */ }
        finally { _handler = null; }
    }
}
