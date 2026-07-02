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
/// caller or take the host down.</para>
/// </summary>
public sealed class ToolbarContributionRegistry : IFlToolbarContributions
{
    private sealed class Contribution
    {
        public required string Id;
        public required string PluginId;
        public required string Caption;
        public required string Tooltip;
        public required bool IsToggle;
        public Func<bool>? IsActive;
        public required Action Handler;
        public int Order;
    }

    private readonly object _sync = new();
    private readonly List<Contribution> _items = new();   // insertion order == display order
    private readonly Action<string>? _log;
    private long _seq;

    /// <summary>
    /// Raised when buttons are added/removed, or when a plugin calls
    /// <see cref="IFlToolbarRegistrar.Refresh"/>. The host wires this to a native toolbar rebuild so FL's
    /// buttons and lit states stay current. Handlers must not throw (they are invoked best-effort).
    /// </summary>
    public event Action? Changed;

    /// <param name="log">Optional diagnostic sink (plugin-callback failures are logged here).</param>
    public ToolbarContributionRegistry(Action<string>? log = null) => _log = log;

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
        var c = new Contribution
        {
            Id = pluginId + "#" + System.Threading.Interlocked.Increment(ref _seq).ToString(),
            PluginId = pluginId,
            Caption = caption ?? string.Empty,
            Tooltip = tooltip ?? string.Empty,
            IsToggle = toggle,
            IsActive = isActive,
            Handler = handler,
            Order = 0,
        };
        lock (_sync) _items.Add(c);
        RaiseChanged();
        return new Handle(this, c.Id);
    }

    private void Remove(string id)
    {
        bool removed;
        lock (_sync) removed = _items.RemoveAll(x => x.Id == id) > 0;
        if (removed) RaiseChanged();
    }

    /// <summary>Remove every button owned by <paramref name="pluginId"/> (called on disable / unload).
    /// Idempotent; fires <see cref="Changed"/> only if something was actually removed.</summary>
    public void RemoveByPlugin(string pluginId)
    {
        bool removed;
        lock (_sync) removed = _items.RemoveAll(x => string.Equals(x.PluginId, pluginId, StringComparison.Ordinal)) > 0;
        if (removed) RaiseChanged();
    }

    /// <summary>Fire <see cref="Changed"/> (used by <see cref="IFlToolbarRegistrar.Refresh"/>). Safe; never throws.</summary>
    public void RaiseChanged()
    {
        try { Changed?.Invoke(); }
        catch (Exception ex) { _log?.Invoke("toolbar-registry: Changed handler threw: " + ex.Message); }
    }

    // ---------------------------------------------------------------------------------------------
    // IFlToolbarContributions (glue side — FL UI thread)
    // ---------------------------------------------------------------------------------------------

    /// <inheritdoc/>
    public string ListJson()
    {
        Contribution[] snapshot;
        lock (_sync) snapshot = _items.ToArray();

        var sb = new StringBuilder(128);
        sb.Append('[');
        for (int i = 0; i < snapshot.Length; i++)
        {
            Contribution c = snapshot[i];
            if (i > 0) sb.Append(',');
            sb.Append("{\"id\":\"").Append(Esc(c.Id))
              .Append("\",\"caption\":\"").Append(Esc(c.Caption))
              .Append("\",\"kind\":\"").Append(c.IsToggle ? "toggle" : "button")
              .Append("\",\"active\":").Append(EvalActive(c) ? "true" : "false")
              .Append(",\"order\":").Append(c.Order.ToString(System.Globalization.CultureInfo.InvariantCulture))
              .Append('}');
        }
        sb.Append(']');
        return sb.ToString();
    }

    /// <inheritdoc/>
    public bool Invoke(string id)
    {
        Action? handler = null;
        lock (_sync)
        {
            foreach (Contribution c in _items)
                if (c.Id == id) { handler = c.Handler; break; }
        }
        if (handler is null) return false;
        try { handler(); return true; }
        catch (Exception ex) { _log?.Invoke($"toolbar-registry: handler for '{id}' threw: {ex.Message}"); return false; }
    }

    /// <inheritdoc/>
    public int Active(string id)
    {
        Contribution? found = null;
        lock (_sync)
        {
            foreach (Contribution c in _items)
                if (c.Id == id) { found = c; break; }
        }
        if (found is null) return -1;
        return EvalActive(found) ? 1 : 0;
    }

    // Evaluate a toggle's isActive OUTSIDE the lock, exception-guarded. Momentary buttons report false.
    private bool EvalActive(Contribution c)
    {
        if (!c.IsToggle || c.IsActive is null) return false;
        try { return c.IsActive(); }
        catch (Exception ex) { _log?.Invoke($"toolbar-registry: isActive for '{c.Id}' threw: {ex.Message}"); return false; }
    }

    // Minimal JSON string escaping: only the two characters the native unescaper handles, plus
    // newlines/tabs flattened to spaces so a caption never breaks the single-line button text.
    private static string Esc(string? s)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        var sb = new StringBuilder(s.Length + 8);
        foreach (char ch in s)
        {
            switch (ch)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\r':
                case '\n':
                case '\t': sb.Append(' '); break;
                default: sb.Append(ch); break;
            }
        }
        return sb.ToString();
    }

    // ---------------------------------------------------------------------------------------------
    // Per-plugin registrar + disposable handle
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

    private sealed class Handle : IDisposable
    {
        private ToolbarContributionRegistry? _owner;
        private readonly string _id;
        public Handle(ToolbarContributionRegistry owner, string id) { _owner = owner; _id = id; }
        public void Dispose()
        {
            ToolbarContributionRegistry? o = System.Threading.Interlocked.Exchange(ref _owner, null);
            o?.Remove(_id);
        }
    }
}
