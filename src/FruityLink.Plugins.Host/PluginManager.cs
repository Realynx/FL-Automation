using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using FruityLink.Core.Abstractions;
using FruityLink.Plugins.Abstractions;

namespace FruityLink.Plugins.Host;

/// <summary>
/// The host's plugin registry. Discovers installed plugins on disk, loads each (shadow-copied) into
/// its own collectible <see cref="PluginLoadContext"/>, reports their state, enables/disables them
/// while persisting the user's on/off choices, and — when hot-reload is enabled — watches the plugins
/// directory and live-reloads rebuilt/added/removed plugins.
///
/// <para><b>Plugins directory layout</b> (both supported under the configured plugins dir):</para>
/// <list type="bullet">
///   <item>Per-plugin folder (recommended): <c>&lt;plugins&gt;\&lt;pluginId&gt;\MyPlugin.dll</c> (+ its
///   private deps next to it). The whole folder is shadow-copied per load.</item>
///   <item>Flat: <c>&lt;plugins&gt;\MyPlugin.dll</c> directly in the plugins dir (the dll + its
///   .deps.json/.pdb sidecars are shadow-copied).</item>
/// </list>
///
/// <para><b>Shadow-copy loading:</b> every plugin assembly is loaded from a private copy under the
/// shadow root (default <c>%LocalAppData%\FruityLink\plugin-shadow</c>), never from the original file,
/// so the original stays writable — a <c>dotnet build</c> over it succeeds and TRIGGERS the watcher.</para>
///
/// <para><b>State persistence:</b> a JSON file (default <c>%LocalAppData%\FruityLink\plugins.json</c>)
/// holding the set of enabled plugin ids; restored at discovery and rewritten on every enable/disable.</para>
///
/// <para><b>Unload caveat (CoreCLR):</b> <see cref="AssemblyLoadContext.Unload"/> only *requests* an
/// unload — the assembly is physically unmapped lazily after the GC observes no managed references
/// remain into the context. We drop our refs and prod the GC, but a plugin that leaks a rooted
/// reference (e.g. an un-removed static event handler) blocks the unmap. Reload always loads the new
/// version into a FRESH, separate context, so a lingering old context never breaks reloading.</para>
///
/// All public methods are exception-safe (a misbehaving plugin can never take the host down); the
/// enable/disable/reload operations are idempotent and serialized.
/// </summary>
public sealed class PluginManager : IPluginManager, IDisposable
{
    private readonly string _pluginsDir;
    private readonly INativeFlControl _fl;
    private readonly IServiceProvider _services;
    private readonly IFlWindowHost _windows;
    private readonly MenuContributionRegistry _menu;
    private readonly ToolbarContributionRegistry _toolbar;
    private readonly Action<string> _log;
    private readonly EnabledStateStore _state;
    private readonly ShadowCopyStore _shadow;

    private readonly object _sync = new();                 // guards _plugins + entry/owner mutable fields
    private readonly SemaphoreSlim _gate = new(1, 1);      // serializes all state-changing operations
    private readonly Dictionary<string, PluginEntry> _plugins = new(StringComparer.OrdinalIgnoreCase);

    private PluginHotReloader? _reloader;
    private bool _disposed;

    /// <param name="fl">Safe FL control surface handed to plugins via their <see cref="IPluginContext"/>.</param>
    /// <param name="services">Host service provider exposed to plugins.</param>
    /// <param name="pluginsDir">Directory scanned for plugins.</param>
    /// <param name="stateFile">JSON file path for the persisted enabled-ids set.</param>
    /// <param name="logFile">File the plugin-host (and plugins) append diagnostic lines to.</param>
    /// <param name="shadowRoot">Root directory for per-load shadow copies of plugin assemblies.</param>
    /// <param name="windows">FL window-embed + hint-bar host handed to plugins via their
    /// <see cref="IPluginContext.Windows"/>; null = a fail-soft no-op host (non-FL contexts/tests).</param>
    public PluginManager(INativeFlControl fl, IServiceProvider services, string pluginsDir, string stateFile, string logFile, string shadowRoot, IFlWindowHost? windows = null)
    {
        ArgumentNullException.ThrowIfNull(fl);
        ArgumentNullException.ThrowIfNull(services);
        _pluginsDir = pluginsDir ?? throw new ArgumentNullException(nameof(pluginsDir));
        ArgumentNullException.ThrowIfNull(stateFile);
        ArgumentNullException.ThrowIfNull(logFile);
        ArgumentNullException.ThrowIfNull(shadowRoot);
        _log = new PluginHostLogger(logFile).Write;
        _fl = fl;
        _services = services;
        _windows = windows ?? NullFlWindowHost.Instance;
        _menu = new MenuContributionRegistry(_log);
        _toolbar = new ToolbarContributionRegistry(_log);
        _state = new EnabledStateStore(stateFile, _log);
        _shadow = new ShadowCopyStore(shadowRoot, _pluginsDir, _log);   // reclaims stale copies from a crashed session
    }

    /// <summary>The directory this manager scans for plugins.</summary>
    public string PluginsDirectory => _pluginsDir;

    /// <summary>The JSON file persisting the set of enabled plugin ids.</summary>
    public string StateFilePath => _state.StateFile;

    /// <summary>Whether the directory watcher (hot-reload) is currently running.</summary>
    public bool HotReloadEnabled => _reloader is not null;

    /// <summary>
    /// The menu-contribution registry backing every plugin's <see cref="IPluginContext.Menu"/>.
    /// Published by <see cref="PluginHost"/> on <see cref="FlMenuRegistryLocator.Current"/> so the
    /// native menu glue can enumerate/dispatch contributions; the host also subscribes to its
    /// <see cref="MenuContributionRegistry.Changed"/> event to trigger a native menu rebuild.
    /// </summary>
    public MenuContributionRegistry MenuRegistry => _menu;

    /// <summary>
    /// The toolbar-button registry backing every plugin's <see cref="IPluginContext.Toolbar"/>.
    /// Published by <see cref="PluginHost"/> on <see cref="FlToolbarRegistryLocator.Current"/> so the
    /// native toolbar glue can enumerate/dispatch buttons; the host also subscribes to its
    /// <see cref="ToolbarContributionRegistry.Changed"/> event to trigger a native toolbar rebuild.
    /// </summary>
    public ToolbarContributionRegistry ToolbarRegistry => _toolbar;

    // ----------------------------------------------------------------------------------------------
    // Discovery
    // ----------------------------------------------------------------------------------------------

    /// <summary>
    /// Scan the plugins directory, load each candidate assembly (shadow-copied) in its own collectible
    /// context, and register every public <see cref="IFlPlugin"/> implementor with a parameterless
    /// constructor. Reads the persisted enabled-set so <see cref="List"/> reports the right Enabled
    /// state, but does NOT activate anything — call <see cref="RestoreEnabledAsync"/> for that. Safe to
    /// call once at startup; re-callable (already-known ids are skipped). Never throws.
    /// </summary>
    public void Discover()
    {
        HashSet<string> enabledIds = _state.LoadPersistedEnabled();
        int before;
        lock (_sync) before = _plugins.Count;

        foreach (string dll in EnumerateCandidateDlls())
        {
            try { DiscoverDll(dll, enabledIds); }
            catch (Exception ex) { _log($"discover: unexpected failure on '{dll}': {ex.Message}"); }
        }

        int after;
        lock (_sync) after = _plugins.Count;
        _log($"discovery complete: {after - before} new, {after} total, from '{_pluginsDir}'");
    }

    private IEnumerable<string> EnumerateCandidateDlls()
    {
        if (!Directory.Exists(_pluginsDir))
        {
            _log($"discover: plugins directory not found: '{_pluginsDir}' (none loaded)");
            yield break;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Flat layout: *.dll directly in the plugins dir.
        foreach (string f in SafeEnumerate(_pluginsDir, SearchOption.TopDirectoryOnly))
            if (IsPluginCandidate(f) && ReferencesPluginContract(f) && seen.Add(f))
                yield return f;

        // Per-plugin-folder layout: *.dll one level down (a folder per plugin package).
        foreach (string sub in SafeEnumerateDirs(_pluginsDir))
            foreach (string f in SafeEnumerate(sub, SearchOption.TopDirectoryOnly))
                if (IsPluginCandidate(f) && ReferencesPluginContract(f) && seen.Add(f))
                    yield return f;
    }

    // A dll can only be a plugin if it references the plugin CONTRACT assembly (you must reference it to
    // implement IFlPlugin). This is a cheap PE-metadata read — no assembly load, no load context — so it
    // filters a per-plugin package's DOZENS of framework/UI dependency dlls (Avalonia, SkiaSharp, …) down
    // to the actual plugin dll BEFORE the expensive path. Without it, discovery shadow-copied the whole
    // package folder AND spun up a throwaway load context for every dependency dll — the dominant startup
    // cost (~20s for the Avalonia-heavy FL Agent). Native dlls (no managed metadata) and unreadable files
    // return false. Contract name matches the set PluginLoadContext unifies with the host.
    private bool ReferencesPluginContract(string dllPath)
    {
        try
        {
            using FileStream fs = File.OpenRead(dllPath);
            using var pe = new PEReader(fs);
            if (!pe.HasMetadata) return false;          // native dll (e.g. libSkiaSharp) → not a plugin
            MetadataReader mr = pe.GetMetadataReader();
            foreach (AssemblyReferenceHandle h in mr.AssemblyReferences)
            {
                AssemblyReference ar = mr.GetAssemblyReference(h);
                if (string.Equals(mr.GetString(ar.Name), ContractAssemblyName, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }
        catch (Exception ex)
        {
            // Unreadable/corrupt/locked → treat as not-a-plugin (it could not be loaded as one anyway).
            _log($"discover: metadata probe failed for '{Path.GetFileName(dllPath)}': {ex.Message} (skipped)");
            return false;
        }
    }

    // The contract assembly every plugin references (see PluginLoadContext's shared set).
    private const string ContractAssemblyName = "FruityLink.Plugins.Abstractions";

    private IEnumerable<string> SafeEnumerate(string dir, SearchOption opt)
    {
        try { return Directory.EnumerateFiles(dir, "*.dll", opt); }
        catch (Exception ex) { _log($"discover: cannot enumerate '{dir}': {ex.Message}"); return Array.Empty<string>(); }
    }

    private IEnumerable<string> SafeEnumerateDirs(string dir)
    {
        try { return Directory.EnumerateDirectories(dir); }
        catch (Exception ex) { _log($"discover: cannot enumerate sub-dirs of '{dir}': {ex.Message}"); return Array.Empty<string>(); }
    }

    // Skip the contract assemblies + framework dlls so we don't spin up doomed load contexts for them.
    private static bool IsPluginCandidate(string path)
    {
        string name = Path.GetFileNameWithoutExtension(path);
        if (name.Equals("FruityLink.Plugins.Abstractions", StringComparison.OrdinalIgnoreCase)) return false;
        if (name.Equals("FruityLink.Core", StringComparison.OrdinalIgnoreCase)) return false;
        if (name.StartsWith("System.", StringComparison.OrdinalIgnoreCase)) return false;
        if (name.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    // Loads the dll (shadow-copied) into a fresh collectible context and registers any plugin types.
    private void DiscoverDll(string dllPath, HashSet<string> enabledIds)
    {
        PluginLoadContext alc;
        Assembly asm;
        string shadowDir;
        try
        {
            (shadowDir, string shadowDll) = _shadow.ShadowCopy(dllPath);
            alc = new PluginLoadContext(shadowDll);
            asm = alc.LoadFromAssemblyPath(shadowDll);
        }
        catch (Exception ex)
        {
            _log($"discover: cannot load '{dllPath}': {ex.Message}");
            return;
        }

        Type[] types;
        try
        {
            types = asm.GetTypes();
        }
        catch (ReflectionTypeLoadException rtle)
        {
            types = rtle.Types.Where(t => t is not null).ToArray()!;
            string detail = string.Join(" | ", rtle.LoaderExceptions
                .Where(e => e is not null).Select(e => e!.Message).Distinct().Take(6));
            _log($"discover: partial type load for '{dllPath}' ({rtle.LoaderExceptions.Length} loader error(s)): {detail}");
        }
        catch (Exception ex)
        {
            _log($"discover: GetTypes failed for '{dllPath}': {ex.Message}");
            SafeUnload(alc, dllPath, shadowDir);
            return;
        }

        List<Type> pluginTypes = types.Where(IsLoadablePluginType).ToList();
        if (pluginTypes.Count == 0)
        {
            SafeUnload(alc, dllPath, shadowDir); // not a plugin assembly — reclaim the context + shadow
            return;
        }

        var owner = new PluginAssembly(dllPath) { Alc = alc, Assembly = asm, ShadowDir = shadowDir };
        int added = 0;
        foreach (Type t in pluginTypes)
        {
            try
            {
                var instance = (IFlPlugin)Activator.CreateInstance(t)!;
                string id = instance.Id;
                if (string.IsNullOrWhiteSpace(id))
                {
                    _log($"discover: skipping '{t.FullName}' in '{Path.GetFileName(dllPath)}' (empty Id)");
                    continue;
                }

                var entry = new PluginEntry
                {
                    Id = id,
                    Name = string.IsNullOrWhiteSpace(instance.Name) ? id : instance.Name,
                    Description = instance.Description ?? string.Empty,
                    Version = instance.Version ?? string.Empty,
                    TypeFullName = t.FullName!,
                    Owner = owner,
                    Instance = instance,
                    EnabledDesired = enabledIds.Contains(id),
                };

                lock (_sync)
                {
                    if (_plugins.ContainsKey(id))
                    {
                        _log($"discover: duplicate plugin id '{id}' from '{Path.GetFileName(dllPath)}' ignored");
                        continue;
                    }
                    owner.Entries.Add(entry);
                    _plugins[id] = entry;
                }
                added++;
                _log($"discovered '{id}' ({entry.Name} v{entry.Version}) from '{Path.GetFileName(dllPath)}'");
            }
            catch (Exception ex)
            {
                _log($"discover: failed to instantiate '{t.FullName}' in '{Path.GetFileName(dllPath)}': {ex.Message}");
            }
        }

        if (added == 0)
            SafeUnload(alc, dllPath, shadowDir); // every type failed/duplicate — reclaim the context
    }

    private static bool IsLoadablePluginType(Type t) =>
        t is { IsClass: true, IsAbstract: false }
        && (t.IsPublic || t.IsNestedPublic)
        && typeof(IFlPlugin).IsAssignableFrom(t)
        && t.GetConstructor(Type.EmptyTypes) is not null;

    // ----------------------------------------------------------------------------------------------
    // IPluginManager
    // ----------------------------------------------------------------------------------------------

    /// <inheritdoc/>
    public IReadOnlyList<PluginInfo> List()
    {
        lock (_sync)
        {
            return _plugins.Values
                .Select(e => new PluginInfo(e.Id, e.Name, e.Description, e.Version, e.EnabledDesired, e.Loaded))
                .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    /// <inheritdoc/>
    public bool IsEnabled(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;
        lock (_sync)
            return _plugins.TryGetValue(id, out PluginEntry? e) && e.EnabledDesired;
    }

    /// <inheritdoc/>
    public async Task<bool> EnableAsync(string id, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            PluginEntry? entry;
            lock (_sync) _plugins.TryGetValue(id, out entry);
            if (entry is null) { _log($"enable: unknown plugin '{id}'"); return false; }

            if (entry.Active)
            {
                if (!entry.EnabledDesired) { lock (_sync) entry.EnabledDesired = true; PersistSafe(); }
                return true; // idempotent
            }

            try
            {
                await ActivateAsync_NoLock(entry, ct).ConfigureAwait(false);
                lock (_sync) entry.EnabledDesired = true;
                PersistSafe();
                _log($"enabled '{id}'");
                return true;
            }
            catch (Exception ex)
            {
                _log($"enable FAILED for '{id}': {ex}");
                return false;
            }
        }
        finally { _gate.Release(); }
    }

    /// <inheritdoc/>
    public async Task<bool> DisableAsync(string id, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            PluginEntry? entry;
            lock (_sync) _plugins.TryGetValue(id, out entry);
            if (entry is null) { _log($"disable: unknown plugin '{id}'"); return false; }

            bool ok = await DeactivateAsync_NoLock(entry, ct).ConfigureAwait(false);
            lock (_sync) entry.EnabledDesired = false;
            PersistSafe();
            UnloadAlc(entry.Owner);
            _log($"disabled '{id}' (ALC unload requested; physical unmap is lazy)");
            return ok;
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Pre-warm every persisted-enabled plugin that implements <see cref="IFlPreWarmPlugin"/>, off the
    /// critical path and BEFORE FL is ready, so their heavy FL-independent init (UI toolkit cold-start,
    /// kernel build, first paint) overlaps FL's own startup instead of running after it. Each such plugin
    /// is loaded and its <see cref="IFlPreWarmPlugin.PrepareAsync"/> awaited; the SAME context is reused by
    /// the later <see cref="EnableAsync"/>. Plugins without the interface are skipped. This does NOT
    /// activate anything — call <see cref="RestoreEnabledAsync"/> after FL is ready for that. Each plugin
    /// is serialized through the same gate as enable/disable; a per-plugin failure is logged, never fatal.
    /// </summary>
    public async Task PreWarmEnabledAsync(CancellationToken ct = default)
    {
        List<string> toPrewarm;
        lock (_sync)
            toPrewarm = _plugins.Values.Where(e => e.EnabledDesired && !e.Active && !e.Prewarmed).Select(e => e.Id).ToList();

        foreach (string id in toPrewarm)
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                PluginEntry? entry;
                lock (_sync) _plugins.TryGetValue(id, out entry);
                if (entry is null || entry.Active || entry.Prewarmed) continue;

                try
                {
                    IFlPlugin instance = EnsureLoaded_NoLock(entry);
                    if (instance is IFlPreWarmPlugin prewarm)
                    {
                        IPluginContext context = GetOrCreateContext_NoLock(entry);
                        await prewarm.PrepareAsync(context, ct).ConfigureAwait(false);
                        _log($"pre-warmed '{id}'");
                    }
                    lock (_sync) entry.Prewarmed = true;
                }
                catch (Exception ex) { _log($"pre-warm FAILED for '{id}': {ex}"); }
            }
            finally { _gate.Release(); }
        }
    }

    /// <summary>
    /// Activate every plugin the persisted state marks enabled but that isn't running yet (used by
    /// <see cref="PluginHost.Initialize"/> at startup). Each is enabled via the normal idempotent
    /// <see cref="EnableAsync"/> path; a failure of one is logged and does not stop the others.
    /// </summary>
    public async Task RestoreEnabledAsync(CancellationToken ct = default)
    {
        List<string> toEnable;
        lock (_sync)
            toEnable = _plugins.Values.Where(e => e.EnabledDesired && !e.Active).Select(e => e.Id).ToList();

        foreach (string id in toEnable)
        {
            try { await EnableAsync(id, ct).ConfigureAwait(false); }
            catch (Exception ex) { _log($"restore: enabling '{id}' failed: {ex.Message}"); }
        }
    }

    // ----------------------------------------------------------------------------------------------
    // Hot-reload (manual + watcher-driven)
    // ----------------------------------------------------------------------------------------------

    /// <summary>
    /// Start watching the plugins directory and live-reload plugins as their dlls are rebuilt, added,
    /// or removed (idempotent). Reloads preserve each plugin's enabled state. Reloading is also
    /// available programmatically via <see cref="ReloadAsync"/> regardless of the watcher.
    /// </summary>
    public void EnableHotReload()
    {
        lock (_sync)
        {
            if (_reloader is not null) return;
            try
            {
                _reloader = new PluginHotReloader(
                    _pluginsDir,
                    (dlls, structureChanged) => ReloadFromWatcherAsync(dlls, structureChanged),
                    _log);
            }
            catch (Exception ex) { _log($"hot-reload: failed to start watcher: {ex.Message}"); }
        }
    }

    /// <summary>Stop the directory watcher (no-op if not running).</summary>
    public void DisableHotReload()
    {
        PluginHotReloader? r;
        lock (_sync) { r = _reloader; _reloader = null; }
        r?.Dispose();
    }

    /// <summary>
    /// Reload the plugin with the given id from disk: stop it (if running), unload its old context, load
    /// the current bytes into a fresh context, and re-enable it if it was enabled. Returns false for an
    /// unknown id. Manual entry point for a "Reload" command/button.
    /// </summary>
    public async Task<bool> ReloadAsync(string id, CancellationToken ct = default)
    {
        string? path;
        lock (_sync) path = _plugins.TryGetValue(id, out PluginEntry? e) ? e.Owner.Path : null;
        if (path is null) { _log($"reload: unknown plugin '{id}'"); return false; }
        return await ReloadDllAsync(path, ct).ConfigureAwait(false);
    }

    /// <summary>Reload (or discover, or drop) all plugins backed by a specific dll path.</summary>
    public async Task<bool> ReloadDllAsync(string originalDllPath, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try { return await ReloadDll_NoLock(originalDllPath, ct).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    // Watcher batch entry point: reload each changed dll, then reconcile adds/removes if the directory
    // structure changed. One gate acquisition for the whole batch.
    private async Task ReloadFromWatcherAsync(IReadOnlyCollection<string> changedDllPaths, bool structureChanged, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            foreach (string p in changedDllPaths)
            {
                try { await ReloadDll_NoLock(p, ct).ConfigureAwait(false); }
                catch (Exception ex) { _log($"hot-reload: reloading '{p}' failed: {ex.Message}"); }
            }
            if (structureChanged)
            {
                try { await Reconcile_NoLock(ct).ConfigureAwait(false); }
                catch (Exception ex) { _log($"hot-reload: reconcile failed: {ex.Message}"); }
            }
        }
        finally { _gate.Release(); }
    }

    // Core reload, assumes the gate is held.
    private async Task<bool> ReloadDll_NoLock(string originalDllPath, CancellationToken ct)
    {
        List<PluginEntry> affected;
        lock (_sync) affected = _plugins.Values.Where(e => ShadowCopyStore.PathEquals(e.Owner.Path, originalDllPath)).ToList();

        // Ignore contract/framework dlls a folder-wide rebuild may touch (unless we already track them).
        if (affected.Count == 0 && !IsPluginCandidate(originalDllPath))
            return true;

        var keepEnabled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (PluginEntry e in affected)
            if (e.EnabledDesired) keepEnabled.Add(e.Id);

        PluginAssembly? owner = affected.Count > 0 ? affected[0].Owner : null;
        foreach (PluginEntry e in affected)
            await DeactivateAsync_NoLock(e, ct).ConfigureAwait(false); // stop + drop instance; keep EnabledDesired
        if (owner is not null) UnloadAlc(owner);
        lock (_sync) foreach (PluginEntry e in affected) _plugins.Remove(e.Id);

        if (!File.Exists(originalDllPath))
        {
            if (affected.Count > 0)
                _log($"hot-reload: removed [{string.Join(", ", affected.Select(a => a.Id))}] — file gone: {Path.GetFileName(originalDllPath)}");
            return true;
        }

        // Re-discover the (possibly rebuilt) dll into a fresh context; restore the enabled choices.
        HashSet<string> enabledIds = _state.LoadPersistedEnabled();
        foreach (string id in keepEnabled) enabledIds.Add(id);
        DiscoverDll(originalDllPath, enabledIds);

        await ActivateFreshFromPath_NoLock(originalDllPath, ct).ConfigureAwait(false);

        List<PluginEntry> fresh;
        lock (_sync) fresh = _plugins.Values.Where(e => ShadowCopyStore.PathEquals(e.Owner.Path, originalDllPath)).ToList();
        if (affected.Count > 0 || fresh.Count > 0)
            _log($"hot-reload: reloaded '{Path.GetFileName(originalDllPath)}' -> [{string.Join(", ", fresh.Select(f => $"{f.Id} v{f.Version}"))}]"
               + (keepEnabled.Count > 0 ? $" (re-enabled: {string.Join(", ", keepEnabled)})" : ""));
        return true;
    }

    // Discover newly-added candidate dlls + drop entries whose backing file vanished. Gate held.
    private async Task Reconcile_NoLock(CancellationToken ct)
    {
        HashSet<string> persisted = _state.LoadPersistedEnabled();
        List<string> candidates = EnumerateCandidateDlls().ToList();

        HashSet<string> known;
        lock (_sync) known = _plugins.Values.Select(e => Path.GetFullPath(e.Owner.Path)).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (string c in candidates)
        {
            if (known.Contains(Path.GetFullPath(c))) continue;
            DiscoverDll(c, persisted);
            await ActivateFreshFromPath_NoLock(c, ct).ConfigureAwait(false);
        }

        List<string> gone;
        lock (_sync)
            gone = _plugins.Values.Select(e => e.Owner.Path)
                                  .Distinct(StringComparer.OrdinalIgnoreCase)
                                  .Where(p => !File.Exists(p))
                                  .ToList();
        foreach (string p in gone)
            await ReloadDll_NoLock(p, ct).ConfigureAwait(false); // file missing => removes the entries
    }

    private async Task ActivateFreshFromPath_NoLock(string path, CancellationToken ct)
    {
        List<PluginEntry> fresh;
        lock (_sync) fresh = _plugins.Values.Where(e => ShadowCopyStore.PathEquals(e.Owner.Path, path) && e.EnabledDesired && !e.Active).ToList();
        foreach (PluginEntry e in fresh)
        {
            try { await ActivateAsync_NoLock(e, ct).ConfigureAwait(false); }
            catch (Exception ex) { _log($"hot-reload: re-enabling '{e.Id}' failed: {ex.Message}"); }
        }
    }

    // ----------------------------------------------------------------------------------------------
    // Activate / deactivate / load / unload (gate held; _sync taken only around field writes)
    // ----------------------------------------------------------------------------------------------

    private async Task ActivateAsync_NoLock(PluginEntry entry, CancellationToken ct)
    {
        IFlPlugin instance = EnsureLoaded_NoLock(entry);
        IPluginContext context = GetOrCreateContext_NoLock(entry);
        await instance.EnableAsync(context, ct).ConfigureAwait(false);
        lock (_sync) entry.Active = true;
    }

    // The per-plugin context, built once and cached on the entry so a plugin's PrepareAsync (pre-warm)
    // and its EnableAsync share ONE context — and one Menu + Toolbar scope, tagged with this plugin's id
    // so its contributions are removed as a set on disable/unload. Cheap to build (the registrars just
    // close over the id). Cleared on deactivate so the next activation gets a fresh scope. Gate held.
    private IPluginContext GetOrCreateContext_NoLock(PluginEntry entry)
    {
        if (entry.Context is not null) return entry.Context;
        var context = new PluginContext(_fl, _services, _log, _menu.ScopeFor(entry.Id), _toolbar.ScopeFor(entry.Id), _windows);
        lock (_sync) entry.Context = context;
        return context;
    }

    private async Task<bool> DeactivateAsync_NoLock(PluginEntry entry, CancellationToken ct)
    {
        IFlPlugin? inst;
        bool active;
        lock (_sync) { inst = entry.Instance; active = entry.Active; }

        bool ok = true;
        if (active && inst is not null)
        {
            try { await inst.DisableAsync(ct).ConfigureAwait(false); }
            catch (Exception ex) { ok = false; _log($"disable: plugin '{entry.Id}' DisableAsync threw: {ex}"); }
        }
        // Belt-and-suspenders: drop ALL of this plugin's menu + toolbar contributions even if its
        // DisableAsync forgot to dispose them (or threw). Covers disable, reload, and shutdown-deactivate.
        _menu.RemoveByPlugin(entry.Id);
        _toolbar.RemoveByPlugin(entry.Id);
        // Drop the instance ref so the ALC can unload; also drop the cached context + pre-warm flag so a
        // later re-activation builds a FRESH context/scope and (if applicable) re-prepares cleanly.
        lock (_sync) { entry.Active = false; entry.Instance = null; entry.Context = null; entry.Prewarmed = false; }
        return ok;
    }

    private IFlPlugin EnsureLoaded_NoLock(PluginEntry entry)
    {
        if (entry.Instance is not null) return entry.Instance;

        PluginAssembly owner = entry.Owner;
        Assembly? asm;
        lock (_sync) asm = owner.Alc is null ? null : owner.Assembly;

        if (asm is null)
        {
            (string shadowDir, string shadowDll) = _shadow.ShadowCopy(owner.Path);
            var alc = new PluginLoadContext(shadowDll);
            Assembly loaded = alc.LoadFromAssemblyPath(shadowDll);
            lock (_sync) { owner.Alc = alc; owner.Assembly = loaded; owner.ShadowDir = shadowDir; }
            asm = loaded;
        }

        Type type = asm.GetType(entry.TypeFullName, throwOnError: true)!;
        var instance = (IFlPlugin)Activator.CreateInstance(type)!;
        lock (_sync) entry.Instance = instance;
        return instance;
    }

    // Unload an assembly's context once every plugin instance from it is gone. Best-effort + lazy.
    private void UnloadAlc(PluginAssembly owner)
    {
        PluginLoadContext? alc;
        string? shadow;
        lock (_sync)
        {
            if (owner.LoadedCount != 0) return; // another plugin from this dll is still live
            alc = owner.Alc;
            shadow = owner.ShadowDir;
            owner.Alc = null;
            owner.Assembly = null;
            owner.ShadowDir = null;
        }

        if (alc is not null)
        {
            var weak = new WeakReference(alc, trackResurrection: false);
            try { alc.Unload(); }
            catch (Exception ex) { _log($"unload: ALC for '{owner.Path}' Unload() threw: {ex.Message}"); }
            alc = null;
            // Prod the GC so the context is unmapped promptly (lets us delete the shadow copy and lets a
            // rebuild replace the original). Bounded — if a leaked reference keeps it alive we move on.
            for (int i = 0; i < 4 && weak.IsAlive; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
        }

        if (shadow is not null) _shadow.TryDeleteDir(shadow);
    }

    private void SafeUnload(PluginLoadContext alc, string dllPath, string shadowDir)
    {
        try { alc.Unload(); }
        catch (Exception ex) { _log($"unload(probe) for '{dllPath}' threw: {ex.Message}"); }
        for (int i = 0; i < 2; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); }
        _shadow.TryDeleteDir(shadowDir);
    }

    // ----------------------------------------------------------------------------------------------
    // Persistence (the file I/O lives in EnabledStateStore; only the snapshot is taken here)
    // ----------------------------------------------------------------------------------------------

    private void PersistSafe()
    {
        List<string> enabled;
        lock (_sync)
            enabled = _plugins.Values.Where(e => e.EnabledDesired)
                                     .Select(e => e.Id)
                                     .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
                                     .ToList();
        _state.PersistSafe(enabled);
    }

    // ----------------------------------------------------------------------------------------------
    // Lifetime
    // ----------------------------------------------------------------------------------------------

    /// <summary>Stop the watcher and release the operation semaphore. Does not unload plugins.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        DisableHotReload();
        _gate.Dispose();
    }

    // ----------------------------------------------------------------------------------------------
    // Internal state types
    // ----------------------------------------------------------------------------------------------

    /// <summary>One loaded plugin .dll and the collectible context it lives in. Several
    /// <see cref="PluginEntry"/> may share it if the dll exposes more than one plugin type.</summary>
    private sealed class PluginAssembly(string path)
    {
        public string Path { get; } = path;                  // ORIGINAL on-disk path (watcher key)
        public PluginLoadContext? Alc { get; set; }
        public Assembly? Assembly { get; set; }
        public string? ShadowDir { get; set; }               // per-load shadow copy folder
        public List<PluginEntry> Entries { get; } = new();
        public int LoadedCount => Entries.Count(e => e.Instance is not null);
    }

    /// <summary>One discovered plugin: cached metadata + live load/active state.</summary>
    private sealed class PluginEntry
    {
        public required string Id { get; init; }
        public required string Name { get; init; }
        public required string Description { get; init; }
        public required string Version { get; init; }
        public required string TypeFullName { get; init; }
        public required PluginAssembly Owner { get; init; }
        public IFlPlugin? Instance { get; set; }    // non-null => the assembly is loaded for this plugin
        public IPluginContext? Context { get; set; } // built once, reused across PrepareAsync + EnableAsync
        public bool Active { get; set; }            // EnableAsync completed, DisableAsync not yet
        public bool Prewarmed { get; set; }         // IFlPreWarmPlugin.PrepareAsync has run (pre-warm phase)
        public bool EnabledDesired { get; set; }    // persisted user choice
        public bool Loaded => Instance is not null;
    }
}
