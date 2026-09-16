namespace FruityLink.Plugins.Abstractions;

/// <summary>
/// Lets a plugin contribute its own big square buttons to FL Studio's main toolbar (alongside FL's
/// own metronome / typing-keyboard style toggles). Obtained from <see cref="IPluginContext.Toolbar"/>.
/// Direct analogue of <see cref="IFlMenuRegistrar"/>.
///
/// <para>This is part of the plugin <b>trust boundary</b> (see re/17-drm-guard.md): a contribution is
/// only a caption plus a pair of managed callbacks — no raw memory, addresses, or FL internals ever
/// cross it. The host materializes the buttons onto FL's toolbar natively and routes clicks and
/// lit-state queries back to your callbacks by an opaque id.</para>
///
/// <para><b>Lifetime:</b> every button a plugin adds is removed automatically when the plugin is
/// disabled (and when its assembly is unloaded). Calling <see cref="IDisposable.Dispose"/> on the
/// returned handle removes a single button early.</para>
///
/// <para><b>Threading:</b> callbacks fire on FL's UI thread. If a handler touches UI that lives on a
/// different thread (e.g. a WPF/Avalonia window on its own dispatcher), marshal to that thread yourself.
/// <see cref="AddToggle"/>'s <c>isActive</c> is polled while the toolbar is (re)built, so keep it fast
/// and non-blocking (read a cached flag rather than blocking on another thread).</para>
///
/// <para><b>v1 icon:</b> <c>caption</c> is a SHORT text/glyph drawn on the button face (1–3 chars work
/// best at the toolbar's square size). PNG icons are not supported yet.</para>
/// </summary>
public interface IFlToolbarRegistrar
{
    /// <summary>
    /// Add a TOGGLE button to FL's main toolbar. <paramref name="isActive"/> is queried when the toolbar
    /// is (re)built so the lit state reflects live reality (e.g. a window being open);
    /// <paramref name="onToggled"/> fires when the user clicks it. Returns a handle whose
    /// <see cref="IDisposable.Dispose"/> removes the button (it is also removed automatically when the
    /// plugin is disabled).
    /// </summary>
    /// <param name="caption">Short text/glyph drawn on the button face.</param>
    /// <param name="tooltip">Hover text shown in FL's hint bar.</param>
    /// <param name="isActive">Returns the current lit state; polled when the toolbar is built.</param>
    /// <param name="onToggled">Invoked when the user clicks the button.</param>
    IDisposable AddToggle(string caption, string tooltip, Func<bool> isActive, Action onToggled);

    /// <summary>
    /// Add a MOMENTARY (non-lit) button to FL's main toolbar; <paramref name="onClick"/> fires when the
    /// user clicks it. Returns a handle whose <see cref="IDisposable.Dispose"/> removes the button (also
    /// removed automatically when the plugin is disabled).
    /// </summary>
    /// <param name="caption">Short text/glyph drawn on the button face.</param>
    /// <param name="tooltip">Hover text shown in FL's hint bar.</param>
    /// <param name="onClick">Invoked when the user clicks the button.</param>
    IDisposable AddButton(string caption, string tooltip, Action onClick);

    /// <summary>
    /// Ask the host to re-render this plugin's toolbar buttons now. Call it when a toggle's active-state
    /// changed without a click (e.g. the window it tracks was closed another way) so the lit face updates
    /// without waiting for the next natural rebuild. Cheap and safe to call from any thread.
    /// </summary>
    void Refresh();
}
