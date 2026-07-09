using System.Text;
using FruityLink.Plugins.Abstractions;

namespace FruityLink.Plugins.Host;

/// <summary>
/// Central registry backing <see cref="IFlToolbarRegistrar"/> for every plugin. It holds all live
/// toolbar-button contributions, tags each with the owning plugin id, and exposes the read surface
/// (<see cref="IFlToolbarContributions"/>) that the native toolbar glue drives: enumerate as JSON, query
/// a button's lit-state, and invoke it by id.
///
/// <para><b>Flow:</b> a plugin gets a per-plugin <see cref="IFlToolbarRegistrar"/> from
/// <see cref="ScopeFor"/> (via its <see cref="IPluginContext.Toolbar"/>). AddToggle/AddButton register a
/// contribution and return an <see cref="IDisposable"/> that unregisters it. The host publishes this
/// registry on <see cref="FlToolbarRegistryLocator.Current"/>; the FlClrHost ↔ FlBridge glue reads it
/// (<see cref="ListJson"/>/<see cref="Active"/>/<see cref="Invoke"/>) to render the buttons onto FL's
/// toolbar and route clicks back. <see cref="Changed"/> fires whenever the set or a lit-state changes so
/// the host can trigger a native rebuild.</para>
///
/// <para>Thread-safe: registrar mutations run on plugin threads while the read surface is called from
/// FL's UI thread through the glue. Handlers and <c>isActive</c> callbacks are invoked OUTSIDE the lock,
/// and every plugin callback is exception-guarded so a misbehaving plugin can never fault the native
/// caller or take the host down. All the shared machinery lives in
/// <see cref="ContributionRegistryCore{TContribution}"/>.</para>
/// </summary>
public sealed class ToolbarContributionRegistry : IFlToolbarContributions
{
    private sealed class Contribution : ContributionItem
    {
        public required string Caption;
        public required string Tooltip;
        public int Order;
    }

    private readonly ContributionRegistryCore<Contribution> _core;

    /// <summary>
    /// Raised when buttons are added/removed, or when a plugin calls
    /// <see cref="IFlToolbarRegistrar.Refresh"/>. The host wires this to a native toolbar rebuild so FL's
    /// buttons and lit states stay current. Handlers must not throw (they are invoked best-effort).
    /// </summary>
    public event Action? Changed
    {
        add => _core.Changed += value;
        remove => _core.Changed -= value;
    }

    /// <param name="log">Optional diagnostic sink (plugin-callback failures are logged here).</param>
    public ToolbarContributionRegistry(Action<string>? log = null)
        => _core = new ContributionRegistryCore<Contribution>("toolbar-registry", "isActive", log);

    /// <summary>
    /// Return an <see cref="IFlToolbarRegistrar"/> scoped to one plugin: everything it adds is tagged with
    /// <paramref name="pluginId"/> so the whole set can be removed on disable via
    /// <see cref="RemoveByPlugin"/>.
    /// </summary>
    public IFlToolbarRegistrar ScopeFor(string pluginId) => new Scoped(this, pluginId);

    // ---------------------------------------------------------------------------------------------
    // Registrar side (plugin threads)
    // ---------------------------------------------------------------------------------------------

    private IDisposable Add(string pluginId, string caption, string tooltip, bool toggle, Func<bool>? isActive, Action handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return _core.Add(new Contribution
        {
            Id = _core.NextId(pluginId),
            PluginId = pluginId,
            Caption = caption ?? string.Empty,
            Tooltip = tooltip ?? string.Empty,
            IsToggle = toggle,
            State = isActive,
            Handler = handler,
            Order = 0,
        });
    }

    /// <summary>Remove every button owned by <paramref name="pluginId"/> (called on disable / unload).
    /// Idempotent; fires <see cref="Changed"/> only if something was actually removed.</summary>
    public void RemoveByPlugin(string pluginId) => _core.RemoveByPlugin(pluginId);

    /// <summary>Fire <see cref="Changed"/> (used by <see cref="IFlToolbarRegistrar.Refresh"/>). Safe; never throws.</summary>
    public void RaiseChanged() => _core.RaiseChanged();

    // ---------------------------------------------------------------------------------------------
    // IFlToolbarContributions (glue side — FL UI thread)
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
              .Append("\",\"caption\":\"").Append(ContributionRegistryCore.Esc(c.Caption))
              .Append("\",\"kind\":\"").Append(c.IsToggle ? "toggle" : "button")
              .Append("\",\"active\":").Append(_core.EvalState(c) ? "true" : "false")
              .Append(",\"order\":").Append(c.Order.ToString(System.Globalization.CultureInfo.InvariantCulture))
              .Append('}');
        }
        sb.Append(']');
        return sb.ToString();
    }

    /// <inheritdoc/>
    public bool Invoke(string id) => _core.Invoke(id);

    /// <inheritdoc/>
    public int Active(string id) => _core.QueryState(id);

    // ---------------------------------------------------------------------------------------------
    // Per-plugin registrar
    // ---------------------------------------------------------------------------------------------

    private sealed class Scoped : IFlToolbarRegistrar
    {
        private readonly ToolbarContributionRegistry _owner;
        private readonly string _pluginId;
        public Scoped(ToolbarContributionRegistry owner, string pluginId) { _owner = owner; _pluginId = pluginId; }

        public IDisposable AddToggle(string caption, string tooltip, Func<bool> isActive, Action onToggled)
        {
            ArgumentNullException.ThrowIfNull(isActive);
            ArgumentNullException.ThrowIfNull(onToggled);
            return _owner.Add(_pluginId, caption, tooltip, toggle: true, isActive, onToggled);
        }

        public IDisposable AddButton(string caption, string tooltip, Action onClick)
        {
            ArgumentNullException.ThrowIfNull(onClick);
            return _owner.Add(_pluginId, caption, tooltip, toggle: false, isActive: null, onClick);
        }

        public void Refresh() => _owner.RaiseChanged();
    }
}
