using FruityLink.Plugins.Abstractions;
using PluginPrivateDependency;

namespace LifecycleFixture;

public sealed class LifecyclePlugin : IFlPlugin, IFlPreWarmPlugin
{
    private IPluginContext? _context;
    private Action<string>? _report;
    private int _activationCount;
    public string Id => "lifecycle-fixture";
    public string Name => "Lifecycle fixture";
    public string Description => "Lifecycle regression fixture";
    public string Version => DependencyMarker.Value;

    public Task PrepareAsync(IPluginContext context, CancellationToken ct = default)
    {
        SetContext(context);
        context.Menu.AddCommand(FlNativeMenu.View, "Prepared", () => { });
        _report!("prepare");
        return Task.CompletedTask;
    }

    public async Task EnableAsync(IPluginContext context, CancellationToken ct = default)
    {
        SetContext(context);
        context.Menu.AddCommand(FlNativeMenu.View, "Enabled", () => { });
        context.Toolbar.AddButton("E", "Enabled", () => { });
        _report!("enable:" + ++_activationCount);
        if (context.Services.GetService(typeof(Func<Task>)) is Func<Task> wait)
            await wait().ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
    }

    public Task DisableAsync(CancellationToken ct = default)
    {
        _report!("disable:" + ct.IsCancellationRequested);
        return Task.CompletedTask;
    }

    private void SetContext(IPluginContext context)
    {
        if (_context is not null && !ReferenceEquals(_context, context))
            throw new InvalidOperationException("Pre-warm and enable must share a context.");
        _context = context;
        _report = (Action<string>)context.Services.GetService(typeof(Action<string>))!;
        if (context.Services.GetService(typeof(Action<Type>)) is Action<Type> observeScripting)
            observeScripting(typeof(FruityLink.Scripting.FlScriptingDispatcher));
        if (context.Services.GetService(typeof(Action<object>)) is Action<object> observeUi)
            observeUi(FruityLink.Ui.Avalonia.Hosting.EmbeddedAvaloniaHost.Instance);
        if (context.Services.GetService(typeof(Action<IFlWindowHost>)) is Action<IFlWindowHost> observeWindows)
            observeWindows(context.Windows);
    }
}
