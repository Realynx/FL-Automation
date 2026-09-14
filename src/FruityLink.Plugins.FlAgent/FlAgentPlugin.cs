using FruityLink.Core.Abstractions;
using FruityLink.Plugins.Abstractions;
using FruityLink.Plugins.FlAgent.Composition;
using FruityLink.Plugins.FlAgent.Ui;
using FruityLink.Ui.Avalonia.Hosting;

namespace FruityLink.Plugins.FlAgent;

/// <summary>
/// "FL Agent" plugin — the AI music-production co-pilot, packaged behind the host's
/// <see cref="IFlPlugin"/> contract. Construction is cheap + side-effect-free (per the contract):
/// all real work happens in <see cref="PrepareAsync"/> / <see cref="EnableAsync"/> and is fully undone
/// in <see cref="DisableAsync"/>.
///
/// <para><b>Two-phase startup (why this also implements <see cref="IFlPreWarmPlugin"/>).</b> FL's own UI
/// takes seconds to load, and almost everything our UI needs is FL-INDEPENDENT: the agent + Semantic
/// Kernel, the in-process Avalonia host (the Skia cold-start — the single biggest cost), the chat view,
/// and its first paint. Only the final reparent into FL's native window host needs FL to be ready. So the
/// host calls <see cref="PrepareAsync"/> <i>before</i> FL is ready — it builds the whole surface off the
/// critical path, concurrently with FL's UI load — and <see cref="EnableAsync"/> (after FL is ready) just
/// reparents the already-built, already-painted window. The chat now appears <i>with</i> FL's UI instead
/// of seconds later. Pre-warm is best-effort: if it doesn't run (or fails), <see cref="EnableAsync"/>
/// builds the surface cold, exactly as before.</para>
/// 
/// On enable it brings the EXISTING FruityLink LLM agent to life (reused
/// <see cref="FruityLink.Agent.FlAgent"/> + plugins + Semantic Kernel) and opens the flagship
/// <b>Avalonia</b> chat UI, hosted IN-PROCESS on its own STA Avalonia thread and reparented into an FL
/// native window host. The agent drives FL through the host's single safe bridge
/// (<see cref="IPluginContext.Fl"/>). The legacy WPF chat is kept as a fallback (set
/// <c>FRUITYLINK_UI=wpf</c>) and is also the automatic fallback if Avalonia can't initialize in-process
/// (e.g. a missing native), so FL is never left broken. On disable it closes the window and releases
/// what it owns, leaving FL untouched. Lifecycle failures are reported to the plugin manager, which
/// retains ownership when native cleanup cannot be confirmed.
/// </summary>
public sealed partial class FlAgentPlugin : IFlPlugin, IFlPreWarmPlugin
{
    private readonly object _gate = new();

    // WPF fallback surface.
    private UiHost? _ui;
    private FlAgentChatWindow? _window;

    // Avalonia primary surface.
    private EmbeddedAvaloniaHost? _avHost;
    private AvaloniaChatView? _avChat;
    private AvaloniaChatPresenter? _avPresenter;
    private bool _useAvalonia;                    // which UI framework is live

    // Pre-warm: the FL-independent Avalonia surface built in PrepareAsync (before FL is ready) and
    // consumed by EnableAsync (which only does the fast FL-dependent embed). Null if pre-warm didn't run,
    // was skipped (WPF forced), or failed — EnableAsync then builds the surface cold.
    private PreparedAvalonia? _prepared;
    private bool _prepareAttempted;              // guards PrepareAsync (idempotent)

    private IPluginContext? _context;
    private IDisposable? _menuToggle;            // the "FL Agent" entry in FL's View menu (removed on disable)
    private IDisposable? _toolbarToggle;         // the "AI" square toggle button on FL's toolbar (removed on disable)
    private volatile bool _chatVisible;          // tracks window visibility for the View toggle's ✓ (lock-free read)
    private volatile bool _embedded;             // true when the chat window is reparented inside an FL host form (task #22)
    private bool _disposing;                     // true only while DisableAsync runs, so the window really closes
    private bool _enabled;

    public string Id => "fl-agent";   // stable persistence id — do NOT rename (drives plugins/fl-agent + native paths)
    public string Name => "FL Automate";
    public string Description => "AI music-production co-pilot that controls FL Studio via natural language.";
    public string Version => "1.0.0";

    /// <summary>
    /// Pre-warm (Phase 1, <see cref="IFlPreWarmPlugin"/>): build the FL-INDEPENDENT part of the UI — the
    /// agent + kernel, the in-process Avalonia host (Skia cold-start), the chat view + presenter, and the
    /// first off-screen paint — BEFORE FL is ready, so it overlaps FL's own UI load. <see cref="EnableAsync"/>
    /// then only has to reparent the already-built window into FL. Best-effort: any failure (incl. Avalonia
    /// unable to initialize) leaves <see cref="_prepared"/> null and <see cref="EnableAsync"/> builds cold /
    /// falls back to WPF. Touches NO FL state. Idempotent.
    /// </summary>
    public async Task PrepareAsync(IPluginContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        lock (_gate) { if (_prepareAttempted) return; _prepareAttempted = true; }
        _context = context;

        // The WPF fallback surface can't pre-warm through this Avalonia route; if WPF is forced, skip
        // pre-warm and let EnableAsync build WPF after readiness (its cold-start is far cheaper than Skia).
        if (string.Equals(Environment.GetEnvironmentVariable("FRUITYLINK_UI"), "wpf", StringComparison.OrdinalIgnoreCase))
        {
            context.Log("[fl-agent] pre-warm skipped (FRUITYLINK_UI=wpf).");
            return;
        }

        try
        {
            context.Log("[fl-agent] pre-warming Avalonia UI + agent (FL-independent, off the critical path)…");
            PreparedAvalonia prepared = await BuildAvaloniaSurfaceAsync(context, ct).ConfigureAwait(false);
            lock (_gate) _prepared = prepared;
            context.Log("[fl-agent] pre-warm complete — surface built + first paint done; only the FL embed remains.");
        }
        catch (Exception ex)
        {
            // Non-fatal: EnableAsync will build the surface cold, or fall back to WPF. Never take the host down.
            try { context.Log("[fl-agent] pre-warm failed (will build on enable): " + ex.Message); } catch { /* logging is best-effort */ }
            lock (_gate) _prepared = null;
        }
    }

    public async Task EnableAsync(IPluginContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        try
        {
            lock (_gate)
            {
                if (_enabled) return;       // idempotent
                _enabled = true;
                _disposing = false;
            }
            _context = context;
            context.Log("[fl-agent] enabling…");

            // Embed the chat inside its own FL native frame. Set FRUITYLINK_EMBED=0 to force an
            // external window. Must be default-on (not env-gated) so an INSTALLER-launched FL — which doesn't
            // inherit our dev-loop env var — still embeds. External fallback is allowed only after the
            // SDK confirms that no native child remains attached.
            bool wantEmbed = Environment.GetEnvironmentVariable("FRUITYLINK_EMBED") != "0";

            // Avalonia is the flagship embedded surface. FRUITYLINK_UI=wpf forces the legacy WPF chat; Avalonia
            // also auto-falls-back to WPF if it can't initialize in-process (so FL is never left broken).
            bool forceWpf = string.Equals(Environment.GetEnvironmentVariable("FRUITYLINK_UI"), "wpf", StringComparison.OrdinalIgnoreCase);
            bool started = false;
            if (!forceWpf)
            {
                try { started = await EnableAvaloniaAsync(wantEmbed, context, ct).ConfigureAwait(false); }
                catch (Exception ex) when (ex is not NativeWindowHostingException && !_embedded)
                { context.Log("[fl-agent] Avalonia UI init failed; falling back to WPF: " + ex.Message); }
            }
            if (!started)
                await EnableWpfFallbackAsync(wantEmbed, context, ct).ConfigureAwait(false);

            _chatVisible = (context.Windows as IFlWindowVisibilityState)?.StartupVisible != false;

            // Dogfood the menu-contribution SDK: add an "FL Agent" toggle to FL's native View menu
            // (where FL keeps its window show/hide toggles). ✓ reflects the chat window's visibility;
            // clicking it shows/hides the window. The handle is disposed in DisableAsync.
            try
            {
                _menuToggle = context.Menu.AddToggle(
                    FlNativeMenu.View,
                    "FL Automate",
                    isChecked: () => _embedded ? context.Windows.IsHostVisible() : _chatVisible,
                    onToggled: ToggleChatVisible);
                context.Log("[fl-agent] added 'FL Automate' toggle to FL's View menu.");
            }
            catch (Exception menuEx)
            {
                context.Log("[fl-agent] could not add View menu toggle: " + menuEx.Message);
            }

            // Dogfood the toolbar-button SDK too: add a big square "AI" toggle to FL's main toolbar
            // (beside FL's own metronome/typing-keyboard toggles). Same predicate + handler as the View
            // toggle: lit == chat window visible; clicking it shows/hides the window. Disposed in DisableAsync.
            try
            {
                _toolbarToggle = context.Toolbar.AddToggle(
                    "AI",
                    "Toggle the FL Automate window",
                    isActive: () => _embedded ? context.Windows.IsHostVisible() : _chatVisible,
                    onToggled: ToggleChatVisible);
                context.Log("[fl-agent] added 'AI' toggle button to FL's toolbar.");
            }
            catch (Exception tbEx)
            {
                context.Log("[fl-agent] could not add toolbar toggle: " + tbEx.Message);
            }

            context.Log("[fl-agent] enabled — chat window open (" + (_useAvalonia ? "Avalonia" : "WPF") + ").");
        }
        catch (Exception ex)
        {
            // Keep partial UI ownership until cleanup succeeds. The manager reports a failed enable
            // and retains the plugin if native detachment needs another attempt.
            try { context.Log("[fl-agent] EnableAsync failed: " + ex); } catch { /* logging is best-effort */ }
            await DisableAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Build the FL-INDEPENDENT Avalonia surface: resolve/build the agent (+ configure the kernel), start
    /// the in-process Avalonia host (Skia cold-start), create the chat view + presenter + gateways, and
    /// realize the HWND with a first off-screen paint. Contains NO FL calls, so it is safe to run BEFORE FL
    /// is ready (pre-warm) or cold on the enable path. Throws only if Avalonia itself can't initialize in
    /// this process (e.g. a missing native such as libSkiaSharp) — the enable path then falls back to WPF.
    /// </summary>
    private async Task<PreparedAvalonia> BuildAvaloniaSurfaceAsync(IPluginContext context, CancellationToken ct)
    {
        FruityLink.Agent.FlAgent agent = AgentComposition.ResolveOrBuild(context);
        (IDictationService? dictation, bool ownsDictation) = AgentComposition.ResolveOrBuildDictation(context);

        // Build the kernel now (no network call) so the first message isn't slow. A missing/unreachable
        // backend is not fatal here — the first chat turn surfaces it in the window.
        try { await agent.ConfigureAsync(ct).ConfigureAwait(false); }
        catch (Exception cfgEx) { context.Log("[fl-agent] backend not configured yet: " + cfgEx.Message); }

        EmbeddedAvaloniaHost host = EmbeddedAvaloniaHost.Instance;
        // Avalonia + Skia cold-start (the big cost); throws if a native is missing. The product's App is
        // supplied as an AppBuilder factory (the SDK host is app-agnostic and appends the embed-safe
        // software-rendering options itself).
        host.EnsureStarted(FruityLink.Ui.Avalonia.App.BuildEmbeddedAppBuilder);

        AvaloniaChatView chat = host.Invoke(() => new AvaloniaChatView());
        // Settings ACCOUNT card → the agent's real FL Automate account + live re-configure.
        var accountGateway = AgentComposition.BuildAccountGateway(agent);

        // Version-history panel → the agent's real project version control (backup/undo/redo/restore), plus
        // the coordinator that commits one project version per mutating AI work-unit. Inert (UI keeps its
        // stub, no commits) when the backend can't be built.
        var versionControl = AgentComposition.BuildVersionControl(context);
        var versionCoordinator = versionControl is not null
            ? AgentComposition.BuildVersionCoordinator(agent, versionControl) : null;
        var versionGateway = versionControl is not null
            ? AgentComposition.BuildVersionControlGateway(versionControl) : null;
        if (versionControl is not null)
            agent.AttachVersionControl(versionControl);   // model-facing list_versions / get_version_changes

        var presenter = new AvaloniaChatPresenter(
            host, chat.ViewModel, agent, context.Log, dictation, ownsDictation,
            accountGateway, versionGateway, versionCoordinator,
            AgentComposition.BuildBugReportClient(),    // "Report bug" on failed turns → gateway
            AgentComposition.BuildUpdateCheckClient(),  // once-per-run installer update check → banner
            // The installed product version (stamped solution-wide via Directory.Build.props
            // <Version>, kept in lock-step with the installer's own version at release time).
            typeof(FlAgentPlugin).Assembly.GetName().Version?.ToString(3));

        // The user's OS-close (X) on the EXTERNAL window hides it (keeps it re-showable), mirroring FL's
        // View-menu windows. (In the embedded case FL's native close is handled by the bridge instead.)
        chat.HiddenByUser += OnExternalWindowHidden;

        // Realize the HWND + first Skia paint off-screen NOW, so the enable path only needs the fast reparent.
        // PrepareForEmbedding parks the window borderless off-screen — a valid state whether we later embed
        // OR switch to an external window (ShowExternal re-applies full chrome + on-screen).
        host.Invoke(() => chat.PrepareForEmbedding());

        return new PreparedAvalonia { Host = host, Chat = chat, Presenter = presenter };
    }

    /// <summary>
    /// Enable the flagship Avalonia chat (Phase 2 — the FL-DEPENDENT part). Consumes the surface
    /// pre-warmed by <see cref="PrepareAsync"/>, or builds it cold here if pre-warm didn't run (or failed).
    /// Then reparents it into an FL window host (default) or shows it as an external window. Returns true
    /// when the UI is up (embedded OR external); throws only if Avalonia itself can't initialize (the
    /// caller then falls back to WPF).
    /// </summary>
    private async Task<bool> EnableAvaloniaAsync(bool wantEmbed, IPluginContext context, CancellationToken ct)
    {
        PreparedAvalonia? prepared;
        lock (_gate) prepared = _prepared;
        if (prepared is null)
        {
            // Pre-warm didn't run (or failed) — build the surface now (the old cold path). Throws → WPF fallback.
            context.Log("[fl-agent] no pre-warmed surface; building Avalonia UI on enable.");
            prepared = await BuildAvaloniaSurfaceAsync(context, ct).ConfigureAwait(false);
        }

        EmbeddedAvaloniaHost host = prepared.Host;
        AvaloniaChatView chat = prepared.Chat;
        lock (_gate) _prepared = prepared; // Retain ownership until embedding commits or cleanup succeeds.

        IFlWindowHost windows = context.Windows;   // the SDK's FL window-embed + hint-bar host
        bool embedded = false;
        if (wantEmbed && (windows is IAsyncFlWindowHost || windows.IsBridgeAvailable()))
        {
            // Do the HWND grab AND the reparent on the child's OWN (Avalonia UI) thread — mirrors the
            // bridge's cross-thread contract (see IFlWindowHost).
            embedded = await host.Invoke(async () =>
            {
                try
                {
                    if (chat.Handle == IntPtr.Zero) chat.PrepareForEmbedding();  // ensure HWND (pre-warm normally did this)
                    IntPtr hwnd = chat.Handle;
                    if (hwnd == IntPtr.Zero) return false;
                    bool attached = await EmbedWindowAsync(windows, hwnd, chat.GetWindowOptions(Name), ct);
                    if (attached)
                    {
                        try { chat.PinToHostContent(windows.LastInsetX, windows.LastInsetY); }
                        catch (Exception error) { throw new NativeWindowHostingException("The native chat window was attached but could not be aligned.", error); }
                    }
                    return attached;
                }
                catch when (windows is not IAsyncFlWindowHost) { return false; }
            }).ConfigureAwait(false);
            _embedded = embedded; // A later setup failure must clean up this child before considering another toolkit.

            if (embedded)
            {
                chat.ForceRender();   // synchronous first paint inside FL (else blank until an input event)
                context.Log("[fl-agent] Avalonia chat embedded inside an FL window host.");
            }
            else
            {
                context.Log("[fl-agent] Avalonia embed unavailable (host-form/reparent failed); using external window. bridge=" + windows.LastEmbedReply);
            }
        }

        if (!embedded)
        {
            if ((windows as IFlWindowVisibilityState)?.StartupVisible != false) chat.ShowExternal();
            else chat.SetVisible(false);
        }

        // UI is up (embedded or external) — clear the native "loading plugin host…" hint the host set at boot.
        windows.SetStatusHint("FL Automate ready");

        lock (_gate)
        {
            _useAvalonia = true;
            _avHost = host;
            _avChat = chat;
            _avPresenter = prepared.Presenter;
            _prepared = null;         // ownership transferred to the _av* fields (teardown handles those)
            _embedded = embedded;
        }
        return true;
    }

    /// <summary>
    /// WPF fallback (Phase 2): build the agent + the legacy WPF chat and embed it the same way. Used when
    /// Avalonia is forced off (<c>FRUITYLINK_UI=wpf</c>) or can't initialize in-process. Not pre-warmed —
    /// WPF's cold-start is far cheaper than Skia's, so it stays on the enable path.
    /// </summary>
    private async Task EnableWpfFallbackAsync(bool wantEmbed, IPluginContext context, CancellationToken ct)
    {
        FruityLink.Agent.FlAgent agent = AgentComposition.ResolveOrBuild(context);
        (IDictationService? dictation, bool ownsDictation) = AgentComposition.ResolveOrBuildDictation(context);
        try { await agent.ConfigureAsync(ct).ConfigureAwait(false); }
        catch (Exception cfgEx) { context.Log("[fl-agent] backend not configured yet: " + cfgEx.Message); }
        await EnableWpfAsync(agent, dictation, ownsDictation, wantEmbed, context, ct).ConfigureAwait(false);
    }

    /// <summary>The external (non-embedded) window's OS-close (X): hide instead of destroy (mirrors FL's
    /// View-menu windows) so the toggle can re-show it, and refresh the View ✓ + toolbar lit state.</summary>
    private void OnExternalWindowHidden()
    {
        _chatVisible = false;
        (_context?.Windows as IFlWindowVisibilityState)?.RememberVisibility(false);
        try { _context?.Menu.Refresh(); } catch { /* update the View ✓; best-effort */ }
        try { _context?.Toolbar.Refresh(); } catch { /* update the toolbar button's lit state; best-effort */ }
    }

    /// <summary>Legacy WPF chat surface (fallback). Same embed machinery, on a WPF dispatcher thread.</summary>
    private async Task EnableWpfAsync(
        FruityLink.Agent.FlAgent agent, IDictationService? dictation, bool ownsDictation, bool wantEmbed, IPluginContext context, CancellationToken ct)
    {
        IFlWindowHost windows = context.Windows;   // the SDK's FL window-embed + hint-bar host
        var ui = new UiHost();
        ui.Start();
        lock (_gate) { _useAvalonia = false; _ui = ui; }
        await ui.Dispatcher.InvokeAsync(async () =>
        {
            var window = new FlAgentChatWindow(agent, context.Log, dictation, ownsDictation);
            // Match FL's View-menu windows: the X HIDES the window (keeps it alive to re-show)
            // instead of destroying it — unless we're disabling the plugin (then really close).
            window.Closing += OnChatWindowClosing;
            window.Closed += (_, _) => { lock (_gate) { if (ReferenceEquals(_window, window)) { _window = null; _chatVisible = false; _embedded = false; } } };
            _window = window;

            bool embedded = false;
            if (wantEmbed && (windows is IAsyncFlWindowHost || windows.IsBridgeAvailable()))
            {
                try
                {
                    window.PrepareForEmbedding();           // borderless, off-screen, no taskbar/activation
                    window.Show();                          // force a WPF layout/render pass before reparenting
                    IntPtr hwnd = window.EnsureNativeHandle();
                    var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(window);
                    var options = new FlWindowOptions(Name, Pixels(window.Width, dpi.DpiScaleX), Pixels(window.Height, dpi.DpiScaleY),
                        Pixels(window.MinWidth, dpi.DpiScaleX), Pixels(window.MinHeight, dpi.DpiScaleY));
                    embedded = await EmbedWindowAsync(windows, hwnd, options, ct);
                    if (embedded)
                    {
                        try { window.PinToHostContent(windows.LastInsetX, windows.LastInsetY); } catch { /* best-effort */ }
                        try { window.ForceRerender(); } catch { /* best-effort */ }   // synchronous first paint (else blank until click)
                    }
                    context.Log(embedded
                        ? "[fl-agent] chat embedded inside an FL window host (WPF)."
                        : "[fl-agent] embed unavailable (host-form/reparent failed); using external window. bridge=" + windows.LastEmbedReply);
                }
                catch (Exception ex) when (windows is not IAsyncFlWindowHost)
                {
                    context.Log("[fl-agent] embed attempt threw; using external window: " + ex.Message);
                    embedded = false;
                }
            }

            if (!embedded)
            {
                // FALLBACK: the external top-level window (zero-risk, always works).
                try { window.RestoreExternalChrome(); } catch { /* PrepareForEmbedding may not have run */ }
                if ((windows as IFlWindowVisibilityState)?.StartupVisible != false)
                {
                    window.Show();
                    window.Activate();
                }
                else window.Hide();
            }
            _embedded = embedded;
        }).Task.Unwrap().ConfigureAwait(false);

        // UI is up (embedded or external) — clear the native "loading plugin host…" hint set at boot.
        windows.SetStatusHint("FL Automate ready");
    }

    /// <summary>
    /// Serialized window toggle work, dispatched off the native menu callback. Native display changes
    /// are awaited without blocking FL or the child's UI dispatcher.
    /// </summary>
    private async Task ToggleChatVisibleCoreAsync()
    {
        UiHost? ui;
        FlAgentChatWindow? window;
        AvaloniaChatView? avChat;
        bool show;
        bool embedded;
        bool useAvalonia;
        lock (_gate)
        {
            if (!_enabled) return;
            ui = _ui;
            window = _window;
            avChat = _avChat;
            embedded = _embedded;
            useAvalonia = _useAvalonia;
            show = !_chatVisible;
            _chatVisible = show;
        }

        // When embedded, the chat is a child of the FL HOST form — drive the host's visibility (FL's
        // sanctioned app-window show) rather than the child directly. The async host keeps both
        // FL and the shared toolkit UI thread free to pump during the transition.
        if (embedded)
        {
            IFlWindowHost? windows = _context?.Windows;
            if (windows is null) return;   // context gone (cannot happen while enabled) — nothing to drive
            // Toggle off the REAL host visibility (the user may have hidden it via the native X, which our
            // WM_CLOSE handler turns into a hide) so a click always flips correctly + re-shows the live form.
            bool vis = windows.IsHostVisible();
            if (windows is IAsyncFlWindowHost asynchronous)
            {
                if (!await asynchronous.SetVisibleAsync(!vis, activate: !vis).ConfigureAwait(false))
                    throw new InvalidOperationException("FL Automate could not change its native window visibility.");
            }
            else windows.SetVisible(!vis);
            lock (_gate) { _chatVisible = !vis; }
            if (!vis)   // just re-shown → repaint (else blank until an input event)
            {
                try
                {
                    if (useAvalonia)
                    {
                        avChat?.ForceRender();
                        _avHost?.Post(() => avChat?.FocusComposer());
                    }
                    else window?.ForceRerender();
                }
                catch { /* best-effort */ }
            }
            try { _context?.Menu.Refresh(); } catch { /* update the View ✓ to match */ }
            try { _context?.Toolbar.Refresh(); } catch { /* update the toolbar button's lit state to match */ }
            return;
        }

        // External (non-embedded) window: show/hide directly on its own UI thread.
        if (useAvalonia)
        {
            if (show) avChat?.ShowExternal();
            else avChat?.SetVisible(false);
            (_context?.Windows as IFlWindowVisibilityState)?.RememberVisibility(show);
            if (show) _avHost?.Post(() => avChat?.FocusComposer());
            return;
        }
        if (ui is null || window is null) return;
        await ui.Dispatcher.InvokeAsync(() =>
        {
            try
            {
                if (show) { window.Show(); window.Activate(); }
                else window.Hide();
                (_context?.Windows as IFlWindowVisibilityState)?.RememberVisibility(show);
            }
            catch { /* window may be closing; visibility change is best-effort */ }
        }).Task.ConfigureAwait(false);
    }

    /// <summary>
    /// Hide the WPF chat window on the user's X (and refresh the View ✓) instead of destroying it, so the
    /// toggle can re-show it — mirroring FL's own View-menu windows. During <see cref="DisableAsync"/>
    /// (<see cref="_disposing"/>) the close is allowed through so the window is really torn down.
    /// </summary>
    private void OnChatWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_disposing) return;     // plugin is being disabled → let it really close
        e.Cancel = true;
        _chatVisible = false;
        (_context?.Windows as IFlWindowVisibilityState)?.RememberVisibility(false);
        try { (sender as System.Windows.Window)?.Hide(); } catch { /* best-effort */ }
        try { _context?.Menu.Refresh(); } catch { /* update the View ✓ now; best-effort */ }
        try { _context?.Toolbar.Refresh(); } catch { /* update the toolbar button's lit state now; best-effort */ }
    }

    private async Task DisableCoreAsync()
    {
        UiHost? ui;
        FlAgentChatWindow? window;
        EmbeddedAvaloniaHost? avHost;
        AvaloniaChatView? avChat;
        AvaloniaChatPresenter? avPresenter;
        PreparedAvalonia? prepared;
        IDisposable? menuToggle;
        IDisposable? toolbarToggle;
        bool embedded;
        bool useAvalonia;
        lock (_gate)
        {
            if (!_enabled && _prepared is null && _window is null && _avChat is null) return;
            _disposing = true;                           // allow the window's Closing to really close
            _prepareAttempted = false;                   // a later re-enable may pre-warm again
            _chatVisible = false;
            embedded = _embedded;
            useAvalonia = _useAvalonia;
            ui = _ui;
            window = _window;
            avHost = _avHost;
            avChat = _avChat;
            avPresenter = _avPresenter;
            prepared = _prepared;                        // non-null only if pre-warmed but never enabled
            menuToggle = _menuToggle;
            toolbarToggle = _toolbarToggle;
        }

        // Remove the "FL Agent" entry from FL's View menu (also auto-removed by the manager on disable).
        try { menuToggle?.Dispose(); } catch { /* best-effort */ }
        // Remove the "AI" toggle button from FL's toolbar (also auto-removed by the manager on disable).
        try { toolbarToggle?.Dispose(); } catch { /* best-effort */ }

        // Detach the child and release its native frame before destroying the toolkit window. An
        // unconfirmed native or toolkit cleanup retains the plugin instance for a later retry.
        if (embedded || _context?.Windows is IAsyncFlWindowHost)
        {
            await DetachWindowAsync(_context?.Windows, avHost ?? prepared?.Host, ui).ConfigureAwait(false);
        }

        try
        {
            if (useAvalonia)
            {
                try { avPresenter?.Dispose(); } catch { /* best-effort */ }
                if (avHost is not null) avHost.Invoke(() => avChat?.Close());
            }
            else if (ui is not null)
            {
                ui.Dispatcher.Invoke(() => window?.Close());
                ui.Stop();   // tears down only a dispatcher thread we own; host's is left alone
            }

            // Belt-and-suspenders: a surface that was pre-warmed but never promoted by EnableAsync (a
            // disable between pre-warm and enable) still owns a live chat window + presenter — tear it down.
            if (prepared is not null && !ReferenceEquals(prepared.Chat, avChat))
            {
                try { prepared.Presenter.Dispose(); } catch { /* best-effort */ }
                prepared.Host.Invoke(prepared.Chat.Close);
            }

            _context?.Log("[fl-agent] disabled.");
            lock (_gate)
            {
                _enabled = _embedded = false;
                _ui = null; _window = null; _avHost = null; _avChat = null; _avPresenter = null;
                _prepared = null; _menuToggle = null; _toolbarToggle = null;
            }
        }
        catch (Exception ex)
        {
            try { _context?.Log("[fl-agent] DisableAsync error: " + ex.Message); } catch { /* best-effort */ }
            throw;
        }

    }

    /// <summary>The FL-independent Avalonia surface built during pre-warm (host + chat + presenter, HWND
    /// realized + first paint done). Consumed by <see cref="EnableAvaloniaAsync"/>, which only reparents
    /// it into FL. Held on the plugin instance so it survives from <see cref="PrepareAsync"/> to
    /// <see cref="EnableAsync"/>.</summary>
    private sealed class PreparedAvalonia
    {
        public required EmbeddedAvaloniaHost Host { get; init; }
        public required AvaloniaChatView Chat { get; init; }
        public required AvaloniaChatPresenter Presenter { get; init; }
    }
}
