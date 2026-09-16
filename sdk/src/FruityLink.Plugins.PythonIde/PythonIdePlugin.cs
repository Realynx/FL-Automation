using FruityLink.Plugins.Abstractions;
using FruityLink.Plugins.PythonIde.Execution;
using FruityLink.Plugins.PythonIde.Ui;

namespace FruityLink.Plugins.PythonIde;

/// <summary>Owns an IDE window and a lease on the shared interpreter; disabling never closes FL itself.</summary>
public sealed class PythonIdePlugin : IFlPlugin
{
    private readonly SemaphoreSlim lifecycle = new(1, 1);
    private readonly Func<IPluginContext, IPythonIdeExecution> createExecution;
    private readonly Func<IPluginContext, IPythonIdeExecution, IPythonIdeWindowHost> createWindow;
    private readonly List<IDisposable> registrations = [];
    private IPythonIdeExecution? execution;
    private IPythonIdeWindowHost? window;
    private CancellationTokenSource? lifetime;
    private IPluginContext? context;

    /// <summary>Constructs an idle plugin; no interpreter, native window or files are opened.</summary>
    public PythonIdePlugin() : this(ctx => new PythonIdeExecutionService(ctx.Fl),
        (ctx, service) => new PythonIdeWindowHost(ctx, service)) { }

    internal PythonIdePlugin(Func<IPluginContext, IPythonIdeExecution> executionFactory,
        Func<IPluginContext, IPythonIdeExecution, IPythonIdeWindowHost> windowFactory)
    {
        createExecution = executionFactory;
        createWindow = windowFactory;
    }

    /// <inheritdoc />
    public string Id => "fl-python-ide";
    /// <inheritdoc />
    public string Name => "FL Python IDE";
    /// <inheritdoc />
    public string Description => "Edit and run Python inside FL Studio using the shared FruityLink scripting runtime.";
    /// <inheritdoc />
    public string Version => typeof(PythonIdePlugin).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    /// <inheritdoc />
    public async Task EnableAsync(IPluginContext pluginContext, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(pluginContext);
        await lifecycle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (window is not null) return;
            context = pluginContext;
            lifetime = new();
            using var startup = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime.Token);
            execution = createExecution(pluginContext);
            window = createWindow(pluginContext, execution);
            registrations.Add(pluginContext.Menu.AddToggle(FlNativeMenu.View, Name, () => window?.IsVisible == true, QueueToggle));
            registrations.Add(pluginContext.Toolbar.AddToggle("PY", "Show/hide FL Python IDE", () => window?.IsVisible == true, QueueToggle));
            if ((pluginContext.Windows as IFlWindowVisibilityState)?.StartupVisible != false)
                await window.ShowAsync(startup.Token).ConfigureAwait(false);
            pluginContext.Log("[fl-python-ide] Ready. Python initializes only when a script runs.");
        }
        catch (Exception error)
        {
            pluginContext.Log($"[fl-python-ide] Enable failed: {error.Message}");
            try { await CleanupAsync().ConfigureAwait(false); }
            catch (Exception cleanup) { throw new AggregateException("Python IDE startup and cleanup failed.", error, cleanup); }
            throw;
        }
        finally { lifecycle.Release(); }
    }

    private void QueueToggle()
    {
        // Native menu callbacks run on FL's main thread. Never block it while another UI thread opens a window.
        _ = Task.Run(ToggleAsync);
    }

    private async Task ToggleAsync()
    {
        await lifecycle.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (window is null || lifetime is null) return;
            await window.ToggleAsync(lifetime.Token).ConfigureAwait(false);
            context?.Menu.Refresh();
            context?.Toolbar.Refresh();
        }
        catch (Exception error) { context?.Log($"[fl-python-ide] Window toggle failed: {error.Message}"); }
        finally { lifecycle.Release(); }
    }

    /// <inheritdoc />
    public async Task DisableAsync(CancellationToken ct = default)
    {
        // A caller timeout cannot authorize unloading a plugin while its embedded/native work is active.
        await lifecycle.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try { await CleanupAsync().ConfigureAwait(false); }
        finally { lifecycle.Release(); }
    }

    private async Task CleanupAsync()
    {
        var errors = new List<Exception>();
        var windowClosed = window is null;
        try
        {
            try { lifetime?.Cancel(); }
            catch (Exception error) { errors.Add(error); }
            foreach (var registration in registrations)
            {
                try { registration.Dispose(); }
                catch (Exception error) { errors.Add(error); }
            }
            // Draining execution precedes closing the UI or allowing this plugin's load context to unload.
            if (execution is not null) await CollectAsync(() => execution.DisposeAsync().AsTask(), errors).ConfigureAwait(false);
            windowClosed = await CloseWindowAsync(errors).ConfigureAwait(false);
        }
        finally
        {
            registrations.Clear();
            execution = null;
            if (windowClosed)
            {
                lifetime?.Dispose();
                lifetime = null;
                window = null;
                context = null;
            }
        }
        if (errors.Count != 0) throw new AggregateException("Python IDE cleanup completed with errors.", errors);
    }

    private async Task<bool> CloseWindowAsync(List<Exception> errors)
    {
        try
        {
            if (window is not null) await window.DisposeAsync().ConfigureAwait(false);
            return true;
        }
        catch (Exception error) { errors.Add(error); return false; }
    }

    private static async Task CollectAsync(Func<Task> action, List<Exception> errors)
    {
        try { await action().ConfigureAwait(false); }
        catch (Exception error) { errors.Add(error); }
    }
}
