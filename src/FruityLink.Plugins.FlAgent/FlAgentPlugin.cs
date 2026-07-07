using FruityLink.Core.Abstractions;
using FruityLink.Plugins.Abstractions;
using FruityLink.Plugins.FlAgent.Composition;
using FruityLink.Plugins.FlAgent.Ui;
using FruityLink.Ui.Avalonia.Hosting;

namespace FruityLink.Plugins.FlAgent;

/// <summary>
/// "FL Agent" plugin — the AI music-production co-pilot, packaged behind the host's
/// <see cref="IFlPlugin"/> contract. Construction is cheap + side-effect-free (per the contract):
/// all real work happens in <see cref="EnableAsync"/> and is fully undone in
/// <see cref="DisableAsync"/>.
///
/// On enable it brings the EXISTING FruityLink LLM agent to life (reused
/// <see cref="FruityLink.Agent.FlAgent"/> + plugins + Semantic Kernel) and opens the flagship
/// <b>Avalonia</b> chat UI, hosted IN-PROCESS on its own STA Avalonia thread and reparented into an FL
/// native window host. The agent drives FL through the host's single safe bridge
/// (<see cref="IPluginContext.Fl"/>). The legacy WPF chat is kept as a fallback (set
/// <c>FRUITYLINK_UI=wpf</c>) and is also the automatic fallback if Avalonia can't initialize in-process
/// (e.g. a missing native), so FL is never left broken. On disable it closes the window and releases
/// what it owns, leaving FL untouched. Both are idempotent and never throw the host down.
/// </summary>
public sealed class FlAgentPlugin : IFlPlugin
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

            FruityLink.Agent.FlAgent agent = AgentComposition.ResolveOrBuild(context);
            (IDictationService? dictation, bool ownsDictation) =
                AgentComposition.ResolveOrBuildDictation(context);

            // Build the kernel now (no network call) so the first message isn't slow. A missing/
            // unreachable backend is not fatal here — the first chat turn surfaces it in the window.
            try { await agent.ConfigureAsync(ct).ConfigureAwait(false); }
            catch (Exception cfgEx) { context.Log("[fl-agent] backend not configured yet: " + cfgEx.Message); }

            // Phase 1 (task #22): embed the chat INSIDE an FL native window-host form (TScriptDialog, FL skinned
            // chrome — border/close/drag/close-reopen all work). DEFAULT ON; set FRUITYLINK_EMBED=0 to force an
            // external window. Must be default-on (not env-gated) so an INSTALLER-launched FL — which doesn't
            // inherit our dev-loop env var — still embeds. ALWAYS degrades to an external top-level window if the
            // embed can't happen (bridge absent / reparent fails).
            bool wantEmbed = Environment.GetEnvironmentVariable("FRUITYLINK_EMBED") != "0";

            // Avalonia is the flagship embedded surface. FRUITYLINK_UI=wpf forces the legacy WPF chat; Avalonia
            // also auto-falls-back to WPF if it can't initialize in-process (so FL is never left broken).
            bool forceWpf = string.Equals(Environment.GetEnvironmentVariable("FRUITYLINK_UI"), "wpf", StringComparison.OrdinalIgnoreCase);
            bool started = false;
            if (!forceWpf)
            {
                try { started = EnableAvalonia(agent, dictation, ownsDictation, wantEmbed, context); }
                catch (Exception ex) { context.Log("[fl-agent] Avalonia UI init failed; falling back to WPF: " + ex.Message); }
            }
            if (!started)
                EnableWpf(agent, dictation, ownsDictation, wantEmbed, context);

            _chatVisible = true;

            // Dogfood the menu-contribution SDK: add an "FL Agent" toggle to FL's native View menu
            // (where FL keeps its window show/hide toggles). ✓ reflects the chat window's visibility;
            // clicking it shows/hides the window. The handle is disposed in DisableAsync.
            try
            {
                _menuToggle = context.Menu.AddToggle(
                    FlNativeMenu.View,
                    "FL Automate",
                    isChecked: () => _embedded ? EmbeddedChatHost.IsHostVisible() : _chatVisible,
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
                    "Toggle the FL Agent window",
                    isActive: () => _embedded ? EmbeddedChatHost.IsHostVisible() : _chatVisible,
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
            // Never take the host down. Roll back the enabled flag so the user can retry.
            try { context.Log("[fl-agent] EnableAsync failed: " + ex); } catch { /* logging is best-effort */ }
            lock (_gate) _enabled = false;
        }
    }

    /// <summary>
    /// Bring up the flagship Avalonia chat in-process and (by default) reparent it into an FL window host.
    /// Returns true when the UI is up (embedded OR external). Throws only if Avalonia itself can't
    /// initialize in this process — the caller then falls back to WPF.
    /// </summary>
    private bool EnableAvalonia(
        FruityLink.Agent.FlAgent agent, IDictationService? dictation, bool ownsDictation, bool wantEmbed, IPluginContext context)
    {
        EmbeddedAvaloniaHost host = EmbeddedAvaloniaHost.Instance;
        host.EnsureStarted();   // sets up Avalonia + the dispatcher thread once; throws if a native is missing

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
            AgentComposition.BuildBugReportClient());   // "Report bug" on failed turns → gateway

        // The user's OS-close (X) on the EXTERNAL window hides it (keeps it re-showable), mirroring FL's
        // View-menu windows. (In the embedded case FL's native close is handled by the bridge instead.)
        chat.HiddenByUser += () =>
        {
            _chatVisible = false;
            try { _context?.Menu.Refresh(); } catch { /* update the View ✓; best-effort */ }
            try { _context?.Toolbar.Refresh(); } catch { /* update the toolbar button's lit state; best-effort */ }
        };

        bool embedded = false;
        if (wantEmbed && EmbeddedChatHost.IsBridgeAvailable())
        {
            // Do the borderless/off-screen show, HWND grab AND the reparent on the child's OWN (Avalonia UI)
            // thread — mirrors the WPF path + the bridge's cross-thread contract (see EmbeddedChatHost).
            embedded = host.Invoke(() =>
            {
                try
                {
                    chat.PrepareForEmbedding();          // borderless, off-screen, show → HWND + first paint
                    IntPtr hwnd = chat.Handle;
                    if (hwnd == IntPtr.Zero) return false;
                    return EmbeddedChatHost.TryEmbed(hwnd, show: true);
                }
                catch { return false; }
            });

            if (embedded)
            {
                chat.ForceRender();   // synchronous first paint inside FL (else blank until an input event)
                context.Log("[fl-agent] Avalonia chat embedded inside an FL window host.");
            }
            else
            {
                context.Log("[fl-agent] Avalonia embed unavailable (host-form/reparent failed); using external window. bridge=" + EmbeddedChatHost.LastEmbedReply);
            }
        }

        if (!embedded)
            chat.ShowExternal();   // zero-risk external top-level fallback

        // UI is up (embedded or external) — clear the native "loading plugin host…" hint the host set at boot.
        EmbeddedChatHost.SetStatusHint("FL Automate ready");

        lock (_gate)
        {
            _useAvalonia = true;
            _avHost = host;
            _avChat = chat;
            _avPresenter = presenter;
            _embedded = embedded;
        }
        return true;
    }

    /// <summary>Legacy WPF chat surface (fallback). Same embed machinery, on a WPF dispatcher thread.</summary>
    private void EnableWpf(
        FruityLink.Agent.FlAgent agent, IDictationService? dictation, bool ownsDictation, bool wantEmbed, IPluginContext context)
    {
        var ui = new UiHost();
        ui.Start();
        ui.Dispatcher.Invoke(() =>
        {
            var window = new FlAgentChatWindow(agent, context.Log, dictation, ownsDictation);
            // Match FL's View-menu windows: the X HIDES the window (keeps it alive to re-show)
            // instead of destroying it — unless we're disabling the plugin (then really close).
            window.Closing += OnChatWindowClosing;
            window.Closed += (_, _) => { lock (_gate) { if (ReferenceEquals(_window, window)) { _window = null; _chatVisible = false; _embedded = false; } } };
            _window = window;

            bool embedded = false;
            if (wantEmbed && EmbeddedChatHost.IsBridgeAvailable())
            {
                try
                {
                    window.PrepareForEmbedding();           // borderless, off-screen, no taskbar/activation
                    window.Show();                          // force a WPF layout/render pass before reparenting
                    IntPtr hwnd = window.EnsureNativeHandle();
                    embedded = EmbeddedChatHost.TryEmbed(hwnd, show: true);
                    if (embedded)
                    {
                        try { window.PinToHostContent(EmbeddedChatHost.LastInsetX, EmbeddedChatHost.LastInsetY); } catch { /* best-effort */ }
                        try { window.ForceRerender(); } catch { /* best-effort */ }   // synchronous first paint (else blank until click)
                    }
                    context.Log(embedded
                        ? "[fl-agent] chat embedded inside an FL window host (WPF)."
                        : "[fl-agent] embed unavailable (host-form/reparent failed); using external window. bridge=" + EmbeddedChatHost.LastEmbedReply);
                }
                catch (Exception ex)
                {
                    context.Log("[fl-agent] embed attempt threw; using external window: " + ex.Message);
                    embedded = false;
                }
            }

            if (!embedded)
            {
                // FALLBACK: the external top-level window (zero-risk, always works).
                try { window.RestoreExternalChrome(); } catch { /* PrepareForEmbedding may not have run */ }
                window.Show();
                window.Activate();
            }
            _embedded = embedded;
        });
        lock (_gate) { _useAvalonia = false; _ui = ui; }

        // UI is up (embedded or external) — clear the native "loading plugin host…" hint set at boot.
        EmbeddedChatHost.SetStatusHint("FL Automate ready");
    }

    /// <summary>
    /// View-menu toggle handler: show the chat window if hidden, hide it if shown. The visibility flag
    /// is flipped synchronously so the subsequent native menu refresh reads the new ✓ state; the actual
    /// Show/Hide is marshalled to the window's UI thread (non-blocking, so this never stalls FL).
    /// </summary>
    private void ToggleChatVisible()
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
        // sanctioned app-window show) rather than the window directly. This runs on FL's UI thread
        // (the native menu-click path), and the bridge marshals back to it, so there's no deadlock.
        if (embedded)
        {
            // Toggle off the REAL host visibility (the user may have hidden it via the native X, which our
            // WM_CLOSE handler turns into a hide) so a click always flips correctly + re-shows the live form.
            bool vis = EmbeddedChatHost.IsHostVisible();
            EmbeddedChatHost.SetVisible(!vis);
            lock (_gate) { _chatVisible = !vis; }
            if (!vis)   // just re-shown → repaint (else blank until an input event)
            {
                try { if (useAvalonia) avChat?.ForceRender(); else window?.ForceRerender(); } catch { /* best-effort */ }
            }
            try { _context?.Menu.Refresh(); } catch { /* update the View ✓ to match */ }
            try { _context?.Toolbar.Refresh(); } catch { /* update the toolbar button's lit state to match */ }
            return;
        }

        // External (non-embedded) window: show/hide directly on its own UI thread.
        if (useAvalonia)
        {
            avChat?.SetVisible(show);
            return;
        }
        if (ui is null || window is null) return;
        ui.Dispatcher.BeginInvoke(() =>
        {
            try
            {
                if (show) { window.Show(); window.Activate(); }
                else window.Hide();
            }
            catch { /* window may be closing; visibility change is best-effort */ }
        });
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
        try { (sender as System.Windows.Window)?.Hide(); } catch { /* best-effort */ }
        try { _context?.Menu.Refresh(); } catch { /* update the View ✓ now; best-effort */ }
        try { _context?.Toolbar.Refresh(); } catch { /* update the toolbar button's lit state now; best-effort */ }
    }

    public Task DisableAsync(CancellationToken ct = default)
    {
        UiHost? ui;
        FlAgentChatWindow? window;
        EmbeddedAvaloniaHost? avHost;
        AvaloniaChatView? avChat;
        AvaloniaChatPresenter? avPresenter;
        IDisposable? menuToggle;
        IDisposable? toolbarToggle;
        bool embedded;
        bool useAvalonia;
        lock (_gate)
        {
            if (!_enabled) return Task.CompletedTask;   // idempotent
            _enabled = false;
            _disposing = true;                           // allow the window's Closing to really close
            _chatVisible = false;
            embedded = _embedded;
            _embedded = false;
            useAvalonia = _useAvalonia;
            ui = _ui;
            window = _window;
            avHost = _avHost;
            avChat = _avChat;
            avPresenter = _avPresenter;
            menuToggle = _menuToggle;
            toolbarToggle = _toolbarToggle;
            _ui = null;
            _window = null;
            _avHost = null;
            _avChat = null;
            _avPresenter = null;
            _menuToggle = null;
            _toolbarToggle = null;
        }

        // Remove the "FL Agent" entry from FL's View menu (also auto-removed by the manager on disable).
        try { menuToggle?.Dispose(); } catch { /* best-effort */ }
        // Remove the "AI" toggle button from FL's toolbar (also auto-removed by the manager on disable).
        try { toolbarToggle?.Dispose(); } catch { /* best-effort */ }

        // If embedded, detach our child + hide the FL host form BEFORE the window is destroyed. The bridge
        // also restores the host subclass on full unload (eject-safe), independent of this.
        if (embedded)
        {
            try
            {
                // Detach on the child's OWN thread (Avalonia UI thread for Avalonia; the disable thread has
                // historically worked for the WPF path).
                if (useAvalonia && avHost is not null) avHost.Invoke(() => EmbeddedChatHost.Close());
                else EmbeddedChatHost.Close();
            }
            catch { /* best-effort */ }
        }

        try
        {
            if (useAvalonia)
            {
                try { avPresenter?.Dispose(); } catch { /* best-effort */ }
                try { avChat?.Close(); } catch { /* best-effort */ }   // closes the window; Avalonia THREAD stays alive
            }
            else if (ui is not null)
            {
                ui.Dispatcher.Invoke(() => { try { window?.Close(); } catch { /* already closed */ } });
                ui.Stop();   // tears down only a dispatcher thread we own; host's is left alone
            }
            _context?.Log("[fl-agent] disabled.");
        }
        catch (Exception ex)
        {
            try { _context?.Log("[fl-agent] DisableAsync error: " + ex.Message); } catch { /* best-effort */ }
        }

        return Task.CompletedTask;
    }
}
