using FruityLink.Core.Abstractions;
using FruityLink.Plugins.Abstractions;

namespace FruityLink.Plugins.Host;

/// <summary>
/// The host-provided <see cref="IPluginContext"/> handed to a plugin on enable. It exposes ONLY the
/// safe, typed FL control surface (<see cref="INativeFlControl"/>), a service provider, a per-plugin
/// menu registrar (<see cref="IFlMenuRegistrar"/>), and a log sink — never the raw memory/call
/// primitives (this is the plugin trust boundary; see re/17-drm-guard.md). One instance is created
/// per plugin so its <see cref="Menu"/> contributions are tagged with that plugin's id and removed as
/// a set when it is disabled.
/// </summary>
internal sealed class PluginContext : IPluginContext
{
    private readonly Action<string> _log;

    public PluginContext(INativeFlControl fl, IServiceProvider services, Action<string> log, IFlMenuRegistrar menu, IFlToolbarRegistrar toolbar, IFlWindowHost? windows = null)
    {
        Fl = fl;
        Services = services;
        _log = log;
        Menu = menu;
        Toolbar = toolbar;
        Windows = windows ?? NullFlWindowHost.Instance;
    }

    /// <inheritdoc/>
    public INativeFlControl Fl { get; }

    /// <inheritdoc/>
    public IServiceProvider Services { get; }

    /// <inheritdoc/>
    public IFlMenuRegistrar Menu { get; }

    /// <inheritdoc/>
    public IFlToolbarRegistrar Toolbar { get; }

    /// <inheritdoc/>
    public IFlWindowHost Windows { get; }

    /// <inheritdoc/>
    public void Log(string message) => _log(message);
}
