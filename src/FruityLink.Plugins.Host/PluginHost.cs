using FruityLink.Core.Abstractions;
using FruityLink.Plugins.Abstractions;

namespace FruityLink.Plugins.Host;

/// <summary>
/// One-call initialization entry for the FruityLink plugin system. The in-FL host
/// (FruityLink.Host.Bootstrap) calls <see cref="Initialize"/> once at startup; it builds the
/// <see cref="PluginManager"/> + the base <see cref="IPluginContext"/>, discovers installed plugins,
/// re-activates the ones the user previously enabled, optionally starts the hot-reload watcher, and
/// publishes the manager on <see cref="PluginManagerLocator.Current"/> so the native "Plugins" toolbar
/// menu can drive it.
/// </summary>
public static class PluginHost
{
    private static readonly object _sync = new();
    private static PluginManager? _instance;

    /// <summary>
    /// <b>Phase 1 (pre-warm) — call BEFORE FL is ready.</b> Builds + publishes the manager (idempotent),
    /// discovers plugins, and pre-warms the persisted-enabled ones that implement
    /// <see cref="IFlPreWarmPlugin"/> (their heavy FL-independent init runs concurrently with FL's own
    /// UI load). Does NOT activate anything and does NOT start hot-reload — that's <see cref="Initialize"/>,
    /// called after FL is ready. Blocks until pre-warm completes. Safe to skip entirely: if only
    /// <see cref="Initialize"/> is called, plugins just build cold after readiness as before.
    /// </summary>
    /// <param name="fl">The safe, typed FL control surface handed to plugins.</param>
    /// <param name="services">Host service provider exposed to plugins (may be empty).</param>
    /// <param name="pluginsDir">Override the plugins directory; null = <see cref="DefaultPluginsDir"/>.</param>
    /// <param name="windows">FL window-embed + hint-bar host handed to plugins via their
    /// <see cref="IPluginContext.Windows"/>; null = a fail-soft no-op host (non-FL contexts). Only the
    /// FIRST call (PreWarm or Initialize) to build the manager captures it.</param>
    /// <returns>The live <see cref="IPluginManager"/> (also published on the locator).</returns>
    public static IPluginManager PreWarm(INativeFlControl fl, IServiceProvider services, string? pluginsDir = null, IFlWindowHost? windows = null)
    {
        PluginManager manager = EnsureManager(fl, services, pluginsDir, windows);
        // Pre-warm FL-independent plugin init off the critical path. Per-plugin guarded; a failure here
        // is non-fatal — Initialize's activation will build cold as a fallback.
        try { manager.PreWarmEnabledAsync().GetAwaiter().GetResult(); }
        catch { /* PreWarmEnabledAsync already guards per-plugin; final safety net */ }
        return manager;
    }

    /// <summary>
    /// <b>Phase 2 (activate) — call AFTER FL is ready.</b> Ensures the manager exists (builds + discovers
    /// if <see cref="PreWarm"/> wasn't called), re-activates persisted-enabled plugins (awaiting their
    /// <see cref="IFlPlugin.EnableAsync"/> — the FL-dependent part, e.g. the window embed), and optionally
    /// starts hot-reload. Idempotent: re-activation only touches plugins not already active. Blocks until
    /// restore completes so the toolbar reflects the correct state on return; call it off the UI
    /// message-pump thread if any restored plugin's EnableAsync might marshal back to it.
    /// </summary>
    /// <param name="fl">The safe, typed FL control surface handed to plugins.</param>
    /// <param name="services">Host service provider exposed to plugins (may be empty).</param>
    /// <param name="pluginsDir">Override the plugins directory; null = <see cref="DefaultPluginsDir"/>
    /// (<c>&lt;host-dir&gt;\plugins</c>).</param>
    /// <param name="hotReload">Force hot-reload on/off; null = resolve from the
    /// <c>FRUITYLINK_PLUGIN_HOTRELOAD</c> env var (default ON).</param>
    /// <param name="windows">FL window-embed + hint-bar host handed to plugins via their
    /// <see cref="IPluginContext.Windows"/>; null = a fail-soft no-op host (non-FL contexts). Only the
    /// FIRST call (PreWarm or Initialize) to build the manager captures it.</param>
    /// <returns>The live <see cref="IPluginManager"/> (also published on the locator).</returns>
    public static IPluginManager Initialize(INativeFlControl fl, IServiceProvider services, string? pluginsDir = null, bool? hotReload = null, IFlWindowHost? windows = null)
    {
        PluginManager manager = EnsureManager(fl, services, pluginsDir, windows);

        // Re-activate persisted-enabled plugins. Block here (startup) so the toolbar is consistent on
        // return. Any single plugin failure is swallowed + logged inside RestoreEnabledAsync.
        try { manager.RestoreEnabledAsync().GetAwaiter().GetResult(); }
        catch { /* RestoreEnabledAsync already guards per-plugin; this is a final safety net */ }

        if (hotReload ?? ResolveHotReloadDefault())
            manager.EnableHotReload();

        return manager;
    }

    /// <summary>
    /// Build the <see cref="PluginManager"/>, discover installed plugins, and publish the manager +
    /// registries on their locators — exactly once per process (idempotent; later calls return the same
    /// instance). Publishing the locators up front lets the native "Plugins" menu/toolbar render the
    /// discovered plugins before any of them is activated. Shared by <see cref="PreWarm"/> and
    /// <see cref="Initialize"/> so either can be the first call.
    /// </summary>
    private static PluginManager EnsureManager(INativeFlControl fl, IServiceProvider services, string? pluginsDir, IFlWindowHost? windows = null)
    {
        ArgumentNullException.ThrowIfNull(fl);
        ArgumentNullException.ThrowIfNull(services);

        lock (_sync)
        {
            if (_instance is not null) return _instance;

            string dir = string.IsNullOrWhiteSpace(pluginsDir) ? DefaultPluginsDir() : pluginsDir!;
            string dataDir = DefaultDataDir();
            string stateFile = Path.Combine(dataDir, "plugins.json");
            string logFile = Path.Combine(dataDir, "logs", $"plugin-host-{DateTime.Now:yyyyMMdd}.log");
            string shadowRoot = Path.Combine(dataDir, "plugin-shadow");

            var manager = new PluginManager(fl, services, dir, stateFile, logFile, shadowRoot, windows);
            manager.Discover();

            PluginManagerLocator.Current = manager;
            // Publish the menu-contribution registry too, so the native menu glue can render plugin
            // entries into FL's dropdowns (e.g. the FL Agent View toggle). Set alongside the manager so
            // both are live before anything reads them.
            FlMenuRegistryLocator.Current = manager.MenuRegistry;
            // Publish the toolbar-button registry too, so the native toolbar glue can render plugin
            // buttons onto FL's main toolbar (e.g. the FL Agent window toggle). Set alongside the menu
            // registry so both are live before anything reads them.
            FlToolbarRegistryLocator.Current = manager.ToolbarRegistry;
            _instance = manager;
            return manager;
        }
    }

    /// <summary>The default plugins directory: <c>&lt;host-dir&gt;\plugins</c> (next to the host dll).</summary>
    public static string DefaultPluginsDir() => Path.Combine(AppContext.BaseDirectory, "plugins");

    // Hot-reload defaults ON (dev productivity); FRUITYLINK_PLUGIN_HOTRELOAD=0/false/off disables it.
    private static bool ResolveHotReloadDefault()
    {
        string? v = Environment.GetEnvironmentVariable("FRUITYLINK_PLUGIN_HOTRELOAD");
        if (string.IsNullOrWhiteSpace(v)) return true;
        return v.Trim() switch
        {
            "0" => false,
            _ when v.Equals("false", StringComparison.OrdinalIgnoreCase) => false,
            _ when v.Equals("off", StringComparison.OrdinalIgnoreCase) => false,
            _ when v.Equals("no", StringComparison.OrdinalIgnoreCase) => false,
            _ => true,
        };
    }

    /// <summary>
    /// The directory for the persisted state file, logs, and shadow copies:
    /// <c>%LocalAppData%\FruityLink</c>, or the path in the <c>FRUITYLINK_PLUGINHOST_DIR</c> environment
    /// variable when set (used for tests / portable installs).
    /// </summary>
    private static string DefaultDataDir()
    {
        string? overrideDir = Environment.GetEnvironmentVariable("FRUITYLINK_PLUGINHOST_DIR");
        if (!string.IsNullOrWhiteSpace(overrideDir)) return overrideDir!;
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FruityLink");
    }
}
