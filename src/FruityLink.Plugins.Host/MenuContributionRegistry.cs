using System.Text;
using FruityLink.Plugins.Abstractions;

namespace FruityLink.Plugins.Host;

/// <summary>
/// Central registry backing <see cref="IFlMenuRegistrar"/> for every plugin. It holds all live menu
/// contributions, tags each with the owning plugin id, and exposes the read surface
/// (<see cref="IFlMenuContributions"/>) that the native menu glue drives: enumerate as JSON, query a
/// contribution's checked-state, and invoke it by id.
///
/// <para><b>Flow:</b> a plugin gets a per-plugin <see cref="IFlMenuRegistrar"/> from
/// <see cref="ScopeFor"/> (via its <see cref="IPluginContext.Menu"/>). AddToggle/AddCommand register a
/// contribution and return an <see cref="IDisposable"/> that unregisters it. The host publishes this
/// registry on <see cref="FlMenuRegistryLocator.Current"/>; the FlClrHost ↔ FlBridge glue reads it
/// (<see cref="ListJson"/>/<see cref="Checked"/>/<see cref="Invoke"/>) to render the entries into FL's
/// native dropdowns and route clicks back. <see cref="Changed"/> fires whenever the set or a
/// checked-state changes so the host can trigger a native rebuild.</para>
///
/// <para>Thread-safe: registrar mutations run on plugin threads while the read surface is called from
/// FL's UI thread through the glue. Handlers and <c>isChecked</c> callbacks are invoked OUTSIDE the
/// lock, and every plugin callback is exception-guarded so a misbehaving plugin can never fault the
/// native caller or take the host down. All the shared machinery lives in
/// <see cref="ContributionRegistryCore{TContribution}"/>.</para>
/// </summary>
public sealed class MenuContributionRegistry : IFlMenuContributions
{
    private sealed class Contribution : ContributionItem
    {
        public required FlNativeMenu Menu;
        public required string Caption;
    }

    private readonly ContributionRegistryCore<Contribution> _core;

    /// <summary>
    /// Raised when contributions are added/removed, or when a plugin calls
    /// <see cref="IFlMenuRegistrar.Refresh"/>. The host wires this to a native menu rebuild so FL's
    /// dropdowns and checkmarks stay current. Handlers must not throw (they are invoked best-effort).
    /// </summary>
    public event Action? Changed
    {
        add => _core.Changed += value;
        remove => _core.Changed -= value;
    }

    /// <param name="log">Optional diagnostic sink (plugin-callback failures are logged here).</param>
    public MenuContributionRegistry(Action<string>? log = null)
        => _core = new ContributionRegistryCore<Contribution>("menu-registry", "isChecked", log);

    /// <summary>
    /// Return an <see cref="IFlMenuRegistrar"/> scoped to one plugin: everything it adds is tagged with
    /// <paramref name="pluginId"/> so the whole set can be removed on disable via
    /// <see cref="RemoveByPlugin"/>.
    /// </summary>
    public IFlMenuRegistrar ScopeFor(string pluginId) => new Scoped(this, pluginId);

    // ---------------------------------------------------------------------------------------------
    // Registrar side (plugin threads)
    // ---------------------------------------------------------------------------------------------

    private IDisposable Add(string pluginId, FlNativeMenu menu, string caption, bool toggle, Func<bool>? isChecked, Action handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return _core.Add(new Contribution
        {
            Id = _core.NextId(pluginId),
            PluginId = pluginId,
            Menu = menu,
            Caption = caption ?? string.Empty,
            IsToggle = toggle,
            State = isChecked,
            Handler = handler,
        });
    }

    /// <summary>Remove every contribution owned by <paramref name="pluginId"/> (called on disable /
    /// unload). Idempotent; fires <see cref="Changed"/> only if something was actually removed.</summary>
    public void RemoveByPlugin(string pluginId) => _core.RemoveByPlugin(pluginId);

    /// <summary>Fire <see cref="Changed"/> (used by <see cref="IFlMenuRegistrar.Refresh"/>). Safe; never throws.</summary>
    public void RaiseChanged() => _core.RaiseChanged();

    // ---------------------------------------------------------------------------------------------
    // IFlMenuContributions (glue side — FL UI thread)
    // ---------------------------------------------------------------------------------------------

    /// <inheritdoc/>
    public string ListJson()
    {
        Contribution[] snapshot = _core.Snapshot();

        var sb = new StringBuilder(128);
        sb.Append('[');
        for (int i = 0; i < snapshot.Length; i++)
        {
            Contribution c = snapshot[i];
            if (i > 0) sb.Append(',');
            sb.Append("{\"id\":\"").Append(ContributionRegistryCore.Esc(c.Id))
              .Append("\",\"menu\":\"").Append(c.Menu.ToString())
              .Append("\",\"caption\":\"").Append(ContributionRegistryCore.Esc(c.Caption))
              .Append("\",\"kind\":\"").Append(c.IsToggle ? "toggle" : "command")
              .Append("\",\"checked\":").Append(_core.EvalState(c) ? "true" : "false")
              .Append('}');
        }
        sb.Append(']');
        return sb.ToString();
    }

    /// <inheritdoc/>
    public bool Invoke(string id) => _core.Invoke(id);

    /// <inheritdoc/>
    public int Checked(string id) => _core.QueryState(id);

    // ---------------------------------------------------------------------------------------------
    // Per-plugin registrar
    // ---------------------------------------------------------------------------------------------

    private sealed class Scoped : IFlMenuRegistrar
    {
        private readonly MenuContributionRegistry _owner;
        private readonly string _pluginId;
        public Scoped(MenuContributionRegistry owner, string pluginId) { _owner = owner; _pluginId = pluginId; }

        public IDisposable AddToggle(FlNativeMenu menu, string caption, Func<bool> isChecked, Action onToggled)
        {
            ArgumentNullException.ThrowIfNull(isChecked);
            ArgumentNullException.ThrowIfNull(onToggled);
            return _owner.Add(_pluginId, menu, caption, toggle: true, isChecked, onToggled);
        }

        public IDisposable AddCommand(FlNativeMenu menu, string caption, Action onInvoke)
        {
            ArgumentNullException.ThrowIfNull(onInvoke);
            return _owner.Add(_pluginId, menu, caption, toggle: false, isChecked: null, onInvoke);
        }

        public void Refresh() => _owner.RaiseChanged();
    }
}
