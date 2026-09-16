using System.Text;

namespace FruityLink.Plugins.Host;

/// <summary>
/// Shared per-item state for a native UI contribution (menu entry / toolbar button). The concrete
/// registries derive their own item type from this to add surface-specific fields (menu target,
/// tooltip, order, …) while the <see cref="ContributionRegistryCore{TContribution}"/> handles all
/// the common bookkeeping.
/// </summary>
internal abstract class ContributionItem
{
    public required string Id;
    public required string PluginId;
    public required bool IsToggle;
    /// <summary>The toggle's state callback (<c>isChecked</c> for menus, <c>isActive</c> for toolbar
    /// buttons); null for plain commands/buttons.</summary>
    public Func<bool>? State;
    public required Action Handler;
}

/// <summary>Non-generic companion holding the pieces that don't depend on the item type.</summary>
internal static class ContributionRegistryCore
{
    // Minimal JSON string escaping: only the two characters the native unescaper handles, plus
    // newlines/tabs flattened to spaces so a caption never breaks the single-line menu/button text.
    public static string Esc(string? s)
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
}

/// <summary>
/// The machinery shared by <see cref="MenuContributionRegistry"/> and
/// <see cref="ToolbarContributionRegistry"/>: the synchronized contribution list (insertion order ==
/// display order), id sequencing, add/remove (incl. the per-plugin sweep), the guarded
/// <see cref="Changed"/> raise, invoke-by-id, and the exception-guarded toggle-state evaluation.
/// The registries own their public surface, their per-plugin registrars, and their exact
/// <c>ListJson</c> row formatting (which the native glue parses — kept byte-identical there).
///
/// <para>Thread-safe: mutations run on plugin threads while the read surface is called from FL's UI
/// thread through the glue. Handlers and state callbacks are invoked OUTSIDE the lock, and every
/// plugin callback is exception-guarded so a misbehaving plugin can never fault the native caller or
/// take the host down.</para>
/// </summary>
internal sealed class ContributionRegistryCore<TContribution> where TContribution : ContributionItem
{
    private readonly object _sync = new();
    private readonly List<TContribution> _items = new();   // insertion order == display order
    private readonly Action<string>? _log;
    private readonly string _logPrefix;    // "menu-registry" / "toolbar-registry"
    private readonly string _stateName;    // "isChecked" / "isActive" (for log messages)
    private long _seq;

    /// <summary>Raised (via <see cref="RaiseChanged"/>) whenever the set or a toggle-state changes.</summary>
    public event Action? Changed;

    public ContributionRegistryCore(string logPrefix, string stateName, Action<string>? log)
    {
        _logPrefix = logPrefix;
        _stateName = stateName;
        _log = log;
    }

    /// <summary>Next unique contribution id for <paramref name="pluginId"/>.</summary>
    public string NextId(string pluginId) => pluginId + "#" + System.Threading.Interlocked.Increment(ref _seq).ToString();

    /// <summary>Register a fully-built contribution and return the handle that unregisters it.</summary>
    public IDisposable Add(TContribution c)
    {
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

    /// <summary>Remove every contribution owned by <paramref name="pluginId"/>. Idempotent; fires
    /// <see cref="Changed"/> only if something was actually removed.</summary>
    public void RemoveByPlugin(string pluginId)
    {
        bool removed;
        lock (_sync) removed = _items.RemoveAll(x => string.Equals(x.PluginId, pluginId, StringComparison.Ordinal)) > 0;
        if (removed) RaiseChanged();
    }

    /// <summary>Fire <see cref="Changed"/>. Safe; never throws.</summary>
    public void RaiseChanged()
    {
        try { Changed?.Invoke(); }
        catch (Exception ex) { _log?.Invoke(_logPrefix + ": Changed handler threw: " + ex.Message); }
    }

    /// <summary>Snapshot of the live contributions in display order (taken under the lock).</summary>
    public TContribution[] Snapshot()
    {
        lock (_sync) return _items.ToArray();
    }

    /// <summary>Invoke the contribution's handler by id (OUTSIDE the lock, exception-guarded).</summary>
    public bool Invoke(string id)
    {
        Action? handler = null;
        lock (_sync)
        {
            foreach (TContribution c in _items)
                if (c.Id == id) { handler = c.Handler; break; }
        }
        if (handler is null) return false;
        try { handler(); return true; }
        catch (Exception ex) { _log?.Invoke($"{_logPrefix}: handler for '{id}' threw: {ex.Message}"); return false; }
    }

    /// <summary>Toggle-state by id: -1 unknown id, 1 on, 0 off (commands/buttons report 0).</summary>
    public int QueryState(string id)
    {
        TContribution? found = null;
        lock (_sync)
        {
            foreach (TContribution c in _items)
                if (c.Id == id) { found = c; break; }
        }
        if (found is null) return -1;
        return EvalState(found) ? 1 : 0;
    }

    /// <summary>Evaluate a toggle's state callback OUTSIDE the lock, exception-guarded. Non-toggles report false.</summary>
    public bool EvalState(TContribution c)
    {
        if (!c.IsToggle || c.State is null) return false;
        try { return c.State(); }
        catch (Exception ex) { _log?.Invoke($"{_logPrefix}: {_stateName} for '{c.Id}' threw: {ex.Message}"); return false; }
    }

    private sealed class Handle : IDisposable
    {
        private ContributionRegistryCore<TContribution>? _owner;
        private readonly string _id;
        public Handle(ContributionRegistryCore<TContribution> owner, string id) { _owner = owner; _id = id; }
        public void Dispose()
        {
            ContributionRegistryCore<TContribution>? o = System.Threading.Interlocked.Exchange(ref _owner, null);
            o?.Remove(_id);
        }
    }
}
