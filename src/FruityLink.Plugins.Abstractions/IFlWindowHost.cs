namespace FruityLink.Plugins.Abstractions;

/// <summary>
/// FL-native window hosting for plugins: embed ANY top-level HWND (WPF, Avalonia, Win32, …) inside an
/// FL Studio native host form (FL-skinned chrome — border/close/drag all behave like FL's own windows),
/// plus the FL status/hint-bar side-channel. Exposed on <see cref="IPluginContext.Windows"/>.
///
/// <para><b>Thread model (the crux — 2026-07-01 VST-embed RE).</b> FL only ever <c>SetParent</c>s a window
/// from that window's OWN owning thread; when threads differ it POSTs, never blocks (this is how FL embeds
/// VST editor windows). An embedding plugin's UI window lives on its own UI thread, so the implementation
/// mirrors FL exactly:
/// <list type="number">
/// <item><b>Phase A — FL's MAIN thread (bridge):</b> the bridge command creates + realizes the FL host
/// form and returns its HWND — it never touches the plugin's child window, so the bridge's blocking
/// <c>SendMessage</c> is safe.</item>
/// <item><b>Phase B — the CALLER's thread (the child window's own thread):</b> the <c>SetParent</c> +
/// restyle + position + show run HERE, via Win32. FL's main thread is just pumping, so the reparent's
/// cross-thread notifications complete with no deadlock. No further BLOCKING bridge call may be issued
/// while the child is parented, so no cross-thread op can target the (possibly blocked) caller thread.</item>
/// </list>
/// The earlier hangs were the opposite: the caller blocked in the bridge <c>SendMessage</c> while FL's
/// main thread tried to <c>SetParent</c>/activate the window — classic cross-thread deadlock.</para>
///
/// <para>Fully fail-safe: any failure — or a missing bridge (running outside FL) — leaves the caller on
/// its external top-level window; every member degrades to a no-op / <c>false</c> rather than throwing.</para>
/// </summary>
public interface IFlWindowHost
{
    /// <summary>True only when the native bridge is loaded in this process and answers (the in-FL case).
    /// When false, embedding is unavailable and the plugin should show an external top-level window.</summary>
    bool IsBridgeAvailable();

    /// <summary>
    /// The raw JSON the bridge returned from the last <see cref="TryEmbed"/> call (includes the native
    /// per-step "diag" field). Surfaced so the caller can log WHY an embed failed. Empty until first attempt.
    /// </summary>
    string LastEmbedReply { get; }

    /// <summary>The FL content-rect X inset (border) from the last embed — the caller pins the child right of it.</summary>
    int LastInsetX { get; }

    /// <summary>The FL content-rect Y inset (titlebar) from the last embed — the caller pins the child below it.</summary>
    int LastInsetY { get; }

    /// <summary>
    /// Embed <paramref name="childHwnd"/> into an FL host form. MUST be called on the thread that OWNS
    /// <paramref name="childHwnd"/> (the window's own UI thread) — Phase A (bridge, FL's main thread) only
    /// creates the FL host form; Phase B (the reparent + restyle) is performed here, on the caller's thread.
    /// Returns true only when the child is confirmed reparented; any failure keeps the external window.
    /// </summary>
    /// <param name="childHwnd">The top-level window to reparent into FL, owned by the calling thread.</param>
    /// <param name="show">Whether the FL host form is shown immediately.</param>
    bool TryEmbed(IntPtr childHwnd, bool show);

    /// <summary>True if the FL host form is currently shown (so a menu ✓ / toolbar toggle can track the real
    /// state, incl. after the user clicks the native close (X), which hides — not destroys — the form).</summary>
    bool IsHostVisible();

    /// <summary>Show/hide the FL HOST form (a plugin's show/hide toggle drives this when embedded).
    /// Non-activating, and issues no blocking bridge call while a child is parented.</summary>
    void SetVisible(bool visible);

    /// <summary>
    /// Detach the embedded child (call on the child's own thread) + hide the host — reversible, used on
    /// plugin disable before the child window is closed. Mirrors FL's teardown order (park the child
    /// first, then drop the host).
    /// </summary>
    void Close();

    /// <summary>
    /// Set FL's status/hint bar text (e.g. a loading indicator) via the native bridge side-channel.
    /// Fire-and-forget on a threadpool thread so it never blocks the caller and never issues a blocking
    /// bridge call on the child's own (possibly parented) UI thread. Best-effort: silently no-ops if the
    /// bridge/FL isn't ready. The native side routes this to FL's own hint setter on FL's main thread.
    /// </summary>
    void SetStatusHint(string text);
}
