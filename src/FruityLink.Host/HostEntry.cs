using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using FruityLink.FlStudio.Inject;
using FruityLink.Plugins.Abstractions;

namespace FruityLink.Host;

/// <summary>
/// Managed entry point for the in-process host. <see cref="Bootstrap"/> is invoked by the native
/// CoreCLR host (FlClrHost.dll), which was loaded by the version.dll proxy inside FL Studio's
/// process. It launches a WPF UI on a dedicated STA thread and switches the FL bridge to the
/// in-process P/Invoke transport — so FruityLink runs entirely inside FL with no named pipe.
/// </summary>
public static class HostEntry
{
    /// <summary>
    /// Unmanaged entry called by FlClrHost via hostfxr (UnmanagedCallersOnly). Must not throw across
    /// the native boundary. Returns quickly after spinning up the UI thread; 0 = ok.
    /// </summary>
    [UnmanagedCallersOnly]
    public static int Bootstrap(IntPtr arg, int argLength)
    {
        try
        {
            Log($"managed Bootstrap entered (pid={Environment.ProcessId}, mtid={Environment.CurrentManagedThreadId})");
            var ui = new Thread(UiThread) { IsBackground = true, Name = "FruityLink-UI" };
            ui.SetApartmentState(ApartmentState.STA);
            ui.Start();
            return 0;
        }
        catch (Exception ex)
        {
            Log("Bootstrap FAILED: " + ex);
            return -1;
        }
    }

    private static void UiThread()
    {
        try
        {
            // Make the native bridge available for the in-process transport (load by full path so the
            // [DllImport("FlBridge.dll")] in InProcBridge binds to this exact module).
            TryLoadBridge();
            FlInjectBridge.UseInProcessTransport();
            Log("in-process bridge transport enabled (named pipe bypassed)");

            // TEST hook: stand up a fake plugin manager so the native "Plugins" dropdown can be
            // exercised end-to-end before the real plugin host registers one. Opt-in + never ships on.
            // The stub touches no FL state, so it is fine to install immediately (no readiness wait).
            bool useStub = Environment.GetEnvironmentVariable("FRUITYLINK_PLUGIN_STUB") == "1" && PluginManagerLocator.Current is null;
            if (useStub)
            {
                PluginManagerLocator.Current = new StubPluginManager();
                Log("TEST stub IPluginManager installed (FRUITYLINK_PLUGIN_STUB=1)");
            }

            // Install the "FL Plugins" Tools submenu once FL's toolbar form exists (~1.5s in). This
            // does NOT need full FL readiness — it only touches the menu, which is up well before the
            // song/channel objects. The native side reads PluginManagerLocator live on each open, so
            // installing before the real manager is registered is fine ("Plugin host not ready").
            InstallPluginsToolbarButtonDeferred();

            // Gate the FL-state-dependent work behind FL readiness (re/20 / task #60): poll the native
            // fl_ready check, then run the diagnostic probes (to the file log) and stand up the REAL
            // plugin host (whose persisted-enable restore may touch FL/UI). Done on a worker thread so
            // the WPF dispatcher below starts pumping immediately.
            StartReadinessGatedInit(useStub);

            // The diagnostic proof window is HIDDEN by default (task #61). Create the WPF Application
            // and pump its dispatcher with NO window shown; visibility is toggled later via
            // SetDebugVisible (FL Plugins ▸ Settings ▸ Show Debug Output, or the debug_show command).
            // OnExplicitShutdown keeps the app + this STA thread + dispatcher alive across the debug
            // window's show/hide/close so it can be re-shown on demand.
            _app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            Log("diagnostic window hidden by default (toggle via FL Plugins ▸ Settings ▸ Show Debug Output)");
            _app.Run();   // no window argument -> nothing visible; dispatcher loop keeps the host alive
            Log("UI thread exited (application shut down)");
        }
        catch (Exception ex)
        {
            Log("UI thread FAILED: " + ex);
        }
    }

    // The WPF Application + the (lazily-created) diagnostic window. Both live on the STA UI thread;
    // SetDebugVisible/GetDebugVisible marshal to _app.Dispatcher to touch the window safely.
    private static Application? _app;
    private static HostWindow? _debugWindow;

    /// <summary>
    /// Poll the native FL-readiness check, then run the FL-state diagnostic probes (to the file log,
    /// since the window is hidden) and — unless the test stub is in use — initialize the real plugin
    /// host. All on a dedicated worker thread (the readiness wait + the blocking restore must not stall
    /// the WPF message pump).
    /// </summary>
    private static void StartReadinessGatedInit(bool useStub)
    {
        var t = new Thread(() =>
        {
            try
            {
                // Pre-warm the plugin host (Phase 1) BEFORE the readiness wait: its heavy FL-INDEPENDENT
                // init — the plugin manager + discovery, and for FL Agent the Avalonia/Skia cold-start,
                // kernel build, and first off-screen paint — runs concurrently with FL's own UI load. Only
                // the fast FL-DEPENDENT embed is left for after readiness. Because the readiness poll returns
                // immediately once FL is ready, total boot time ≈ max(pre-warm, FL-ready), not their sum.
                if (!useStub) PreWarmPluginHost();

                WaitForFlReady();
                LogSymbolResolution();   // record FL version + any unresolved native symbols (multi-version gap)
                // Loading indicator in FL's native status/hint bar. With pre-warm, the heavy work (Avalonia/
                // Skia init + first paint) is usually already done by now, so only the FL embed remains —
                // this hint is typically brief. FlAgentPlugin clears it ("ready") once the UI is embedded.
                SetLoadingHint("FL Automate — loading plugin host…");
                RunProbesToLog();                          // diagnostic probes -> file log (window hidden)
                if (!useStub) InitializeRealPluginHost();  // restore-enable may touch FL/UI -> after readiness
            }
            catch (Exception ex) { Log("readiness-gated init FAILED: " + ex); }
        })
        { IsBackground = true, Name = "FruityLink-ReadyInit" };
        t.Start();
    }

    /// <summary>
    /// Poll the bridge's <c>fl_ready</c> command (native double-deref of mainForm/toolbarForm/songObj/
    /// chanList; see re/20) every 50 ms up to a 30 s timeout. Returns when FL reports ready, or logs a
    /// timeout and proceeds degraded. Reads "1" = ready.
    /// </summary>
    private static void WaitForFlReady()
    {
        const int timeoutMs = 30000, intervalMs = 50;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        bool loggedError = false;
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            try
            {
                if (InProcBridge.Raw("fl_ready").Trim() == "1")
                {
                    Log($"FL ready after {sw.ElapsedMilliseconds}ms");
                    return;
                }
            }
            catch (Exception ex)
            {
                if (!loggedError) { Log("fl_ready probe error (will keep polling): " + ex.Message); loggedError = true; }
            }
            Thread.Sleep(intervalMs);
        }
        Log($"FL readiness timeout ({timeoutMs}ms) — proceeding degraded");
    }

    /// <summary>
    /// Query the bridge's <c>syms</c> diagnostic once FL is up and record it in the boot log: the FL
    /// version the bridge detected plus any reverse-engineered symbols that did NOT resolve on this FL
    /// build. This makes a multi-version gap visible at startup — the managed tool gate hides native
    /// tools whose required symbol is in that unresolved set. The raw JSON
    /// (<c>{"ver":N,"ok":N,"fail":M,"unresolved":[...]}</c>) is logged as-is. Best-effort, never fatal.
    /// </summary>
    private static void LogSymbolResolution()
    {
        try { Log("FL native symbol resolution: " + InProcBridge.Raw("syms").Trim()); }
        catch (Exception ex) { Log("syms probe failed (non-fatal): " + ex.Message); }
    }

    /// <summary>
    /// Show a message in FL's native status/hint bar (the plugin-host loading indicator) via the bridge's
    /// <c>hint</c> command. Fire-and-forget + bounded: it must never block or throw into the boot path, so we
    /// dispatch it to the thread pool and swallow everything. The native side routes it to FL's own hint
    /// setter on FL's main thread; FlAgentPlugin clears it ("ready") once the chat UI is up.
    /// </summary>
    private static void SetLoadingHint(string text)
    {
        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            try { await InProcBridge.RawAsync("hint " + text, 2000, System.Threading.CancellationToken.None).ConfigureAwait(false); }
            catch { /* the loading indicator is best-effort */ }
        });
    }

    /// <summary>Run the shared probe sequence into the file log (the proof window is hidden by default).</summary>
    private static void RunProbesToLog()
    {
        try { HostWindow.RunProbeSequence(line => Log("PROBE: " + line)).GetAwaiter().GetResult(); }
        catch (Exception ex) { Log("RunProbesToLog FAILED: " + ex.Message); }
    }

    /// <summary>
    /// Show or hide the diagnostic proof window on the WPF UI thread. The window is created lazily on
    /// first show (and re-created if it was closed), so this is safe to call before it ever existed and
    /// from any thread. No-op if the WPF Application has not been created yet.
    /// </summary>
    internal static void SetDebugVisible(bool visible)
    {
        Application? app = _app;
        if (app is null) { Log("SetDebugVisible: no WPF application yet (ignored)"); return; }
        app.Dispatcher.InvokeAsync(() =>
        {
            try
            {
                if (visible)
                {
                    if (_debugWindow is null)
                    {
                        _debugWindow = new HostWindow();
                        _debugWindow.Closed += (_, _) => _debugWindow = null; // allow re-create after a manual close
                    }
                    _debugWindow.Show();
                    _debugWindow.Activate();
                }
                else
                {
                    _debugWindow?.Hide();
                }
                Log("debug window visible=" + visible);
            }
            catch (Exception ex) { Log("SetDebugVisible failed: " + ex.Message); }
        });
    }

    /// <summary>Whether the diagnostic window currently exists and is visible (UI-thread query).</summary>
    internal static bool GetDebugVisible()
    {
        Application? app = _app;
        if (app is null) return false;
        try { return app.Dispatcher.Invoke(() => _debugWindow is { IsVisible: true }); }
        catch (Exception ex) { Log("GetDebugVisible failed: " + ex.Message); return false; }
    }

    /// <summary>
    /// Send a raw command over the in-proc bridge, logging failures instead of throwing. Returns the
    /// bridge's response, or null when the call failed ("<paramref name="context"/> failed: …" is
    /// logged). When <paramref name="logResult"/> is set a success is logged as
    /// "<paramref name="context"/> -&gt; response".
    /// </summary>
    private static string? TryRaw(string command, string context, bool logResult = false)
    {
        try
        {
            string r = InProcBridge.Raw(command);
            if (logResult) Log(context + " -> " + r);
            return r;
        }
        catch (Exception ex)
        {
            Log(context + " failed: " + ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Drive the native bridge to add the far-right "Plugins" dropdown button to FL's toolbar. Runs
    /// on a background thread and retries, because at Bootstrap time FL's toolbar form may not be
    /// constructed yet. Best-effort: failures are logged, never fatal. Production hosts can also call
    /// <c>InProcBridge.Raw("plugins_button_install")</c> directly right after registering the manager.
    /// </summary>
    private static void InstallPluginsToolbarButtonDeferred()
    {
        var t = new Thread(() =>
        {
            for (int i = 0; i < 30; i++)
            {
                try
                {
                    Thread.Sleep(1500);
                    string r = InProcBridge.Raw("plugins_button_install");      // add/refresh the Tools > Plugins submenu
                    // Also materialize plugin menu contributions (e.g. the FL Agent View toggle) once
                    // FL's menus exist; harmless before any contribution/manager is registered.
                    TryRaw("menu_contrib_install", "menu_contrib_install");     // best-effort
                    // And materialize plugin toolbar buttons (e.g. the FL Agent window toggle) once FL's
                    // toolbar form exists; harmless before any contribution/manager is registered. Toolbar
                    // uses a single refresh command for both initial materialize and updates.
                    TryRaw("toolbar_button_refresh", "toolbar_button_refresh"); // best-effort
                    bool installed = r.Contains("\"ok\":1");
                    // Keep refreshing until the plugin manager is actually registered (list is a JSON
                    // array, not "null"/host-missing) so the submenu shows real plugins, not the
                    // "Plugin host not ready" placeholder.
                    string list = InProcBridge.Raw("plugins_list");
                    bool managerReady = list.TrimStart().StartsWith("[");
                    Log($"plugins install attempt {i}: install={r} managerReady={managerReady}");
                    if (installed && managerReady) return;
                }
                catch (Exception ex) { Log("plugins_button_install attempt failed: " + ex.Message); }
            }
            Log("plugins_button_install: installed (manager may register later; call plugins_button_install again to refresh)");
        })
        { IsBackground = true, Name = "FruityLink-PluginsBtn" };
        t.Start();
    }

    /// <summary>
    /// Pre-warm the real plugin host (Phase 1) BEFORE FL is ready: build + discover the plugin manager and
    /// let persisted-enabled plugins do their FL-INDEPENDENT init (for FL Agent: the Avalonia/Skia cold
    /// start, kernel build, and first paint). Runs concurrently with FL's own UI load so only the fast
    /// FL-dependent activation (the window embed) is left for <see cref="InitializeRealPluginHost"/> after
    /// readiness. Best-effort: any failure is logged and never fatal (activation then builds cold).
    /// </summary>
    private static void PreWarmPluginHost()
    {
        try
        {
            var fl = new FlInjectBridge(); // in-proc transport is set statically (UseInProcessTransport)
            FruityLink.Plugins.Host.PluginHost.PreWarm(fl, EmptyServiceProvider.Instance, windows: _windowHost);
            Log("plugin host pre-warmed (FL-independent init running concurrently with FL startup)");
        }
        catch (Exception ex) { Log("PreWarmPluginHost FAILED: " + ex); }
    }

    /// <summary>
    /// The process-wide FL window-embed + hint-bar host handed to plugins via
    /// <see cref="IPluginContext.Windows"/>. Constructed eagerly (it holds no resources); every member
    /// fails soft when the bridge is absent, so the SAME instance also serves non-FL runs.
    /// </summary>
    private static readonly IFlWindowHost _windowHost = new FruityLink.FlStudio.Windowing.FlWindowHost();

    /// <summary>
    /// Stand up the real plugin host (called from the readiness-gated worker thread, after FL is ready).
    /// Builds a <see cref="FlInjectBridge"/> (the in-process transport is already enabled, so it talks to
    /// the in-FL bridge directly), calls <see cref="FruityLink.Plugins.Host.PluginHost.Initialize"/>
    /// (idempotent; discovers plugins under <c>&lt;host-dir&gt;\plugins</c>, restores persisted-enabled
    /// ones, starts the hot-reload watcher, and publishes <see cref="PluginManagerLocator.Current"/>),
    /// then refreshes the native submenu. Runs synchronously on the caller's worker thread (off the STA
    /// UI thread) so the blocking restore never stalls the message pump.
    /// </summary>
    private static void InitializeRealPluginHost()
    {
        try
        {
            var fl = new FlInjectBridge(); // in-proc transport is set statically (UseInProcessTransport)
            IPluginManager mgr = FruityLink.Plugins.Host.PluginHost.Initialize(fl, EmptyServiceProvider.Instance, windows: _windowHost);
            string dir = (mgr as FruityLink.Plugins.Host.PluginManager)?.PluginsDirectory ?? "(unknown)";
            Log($"real plugin host initialized: {mgr.List().Count} plugin(s) discovered; watching '{dir}'");

            // Whenever a plugin's menu contributions change (added/removed on enable/disable, or a
            // plugin calls IFlMenuRegistrar.Refresh because a tracked window's visibility changed),
            // rebuild FL's native menus so the entries + ✓ checkmarks stay current.
            if (mgr is FruityLink.Plugins.Host.PluginManager pm)
            {
                pm.MenuRegistry.Changed += () => TryRaw("menu_contrib_refresh", "menu_contrib_refresh");

                // Same for toolbar buttons: whenever a plugin's toolbar contributions change (added/
                // removed on enable/disable, or a plugin calls IFlToolbarRegistrar.Refresh because a
                // tracked window's visibility changed), rebuild FL's native toolbar so the buttons + lit
                // states stay current. Toolbar uses one refresh command for both materialize and update.
                pm.ToolbarRegistry.Changed += () => TryRaw("toolbar_button_refresh", "toolbar_button_refresh");
            }

            // Refresh the Tools > FL Plugins submenu with the real list right away (idempotent).
            TryRaw("plugins_button_install", "plugins_button_install (post-init)", logResult: true);
            // Materialize any plugin menu contributions (e.g. the FL Agent View toggle) now that the
            // real manager + registry are live. Idempotent; the Changed subscription keeps it fresh.
            TryRaw("menu_contrib_install", "menu_contrib_install (post-init)", logResult: true);
            // Materialize any plugin toolbar buttons (e.g. the FL Agent window toggle) now that the real
            // manager + registry are live. Idempotent; the Changed subscription keeps it fresh.
            TryRaw("toolbar_button_refresh", "toolbar_button_refresh (post-init)", logResult: true);
        }
        catch (Exception ex) { Log("InitializeRealPluginHost FAILED: " + ex); }
    }

    /// <summary>Minimal empty <see cref="IServiceProvider"/> for hosts with no DI container at the call
    /// site (the plugin host + plugins tolerate a provider that returns null for everything).</summary>
    private sealed class EmptyServiceProvider : IServiceProvider
    {
        public static readonly EmptyServiceProvider Instance = new();
        public object? GetService(Type serviceType) => null;
    }

    private static void TryLoadBridge()
    {
        try
        {
            string dir = AppContext.BaseDirectory;
            string p = Path.Combine(dir, "FlBridge.dll");
            if (File.Exists(p)) { NativeLibrary.Load(p); Log("FlBridge.dll loaded from " + p); }
            else Log("WARNING: FlBridge.dll not found next to host (" + dir + ") — in-proc bridge unavailable");
        }
        catch (Exception ex) { Log("TryLoadBridge: " + ex.Message); }
    }

    /// <summary>Append a line to %TEMP%\fruitylink-proxy.log (shared with the native proxy/host chain).</summary>
    internal static void Log(string msg)
    {
        try
        {
            string path = Path.Combine(Path.GetTempPath(), "fruitylink-proxy.log");
            File.AppendAllText(path, "[managed] " + DateTime.Now.ToString("HH:mm:ss.fff") + " " + msg + "\r\n");
        }
        catch { /* logging must never throw */ }
    }
}
