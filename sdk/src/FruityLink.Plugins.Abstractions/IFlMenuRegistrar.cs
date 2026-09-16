namespace FruityLink.Plugins.Abstractions;

/// <summary>
/// FL Studio's eight native top-level menu-bar dropdowns. A plugin names one of these when it
/// contributes a menu entry through <see cref="IFlMenuRegistrar"/>; the host finds the matching FL
/// dropdown by its caption and renders the entry among FL's own items there. <see cref="View"/> is
/// where FL keeps its window show/hide toggles, so a window toggle belongs there.
/// </summary>
public enum FlNativeMenu
{
    /// <summary>The <c>File</c> dropdown.</summary>
    File,
    /// <summary>The <c>Edit</c> dropdown.</summary>
    Edit,
    /// <summary>The <c>Add</c> dropdown.</summary>
    Add,
    /// <summary>The <c>Patterns</c> dropdown.</summary>
    Patterns,
    /// <summary>The <c>View</c> dropdown (FL's window show/hide toggles live here).</summary>
    View,
    /// <summary>The <c>Options</c> dropdown.</summary>
    Options,
    /// <summary>The <c>Tools</c> dropdown.</summary>
    Tools,
    /// <summary>The <c>Help</c> dropdown.</summary>
    Help,
}

/// <summary>
/// Lets a plugin contribute its own entries to FL Studio's native top-level menu dropdowns
/// (File / Edit / Add / Patterns / View / Options / Tools / Help). Obtained from
/// <see cref="IPluginContext.Menu"/>.
///
/// <para>This is part of the plugin <b>trust boundary</b> (see re/17-drm-guard.md): a contribution is
/// only a caption plus a pair of managed callbacks — no raw memory, addresses, or FL internals ever
/// cross it. The host materializes the entries into FL's menus natively and routes clicks and
/// checkmark queries back to your callbacks by an opaque id.</para>
///
/// <para><b>Lifetime:</b> every contribution a plugin adds is removed automatically when the plugin is
/// disabled (and when its assembly is unloaded). Calling <see cref="IDisposable.Dispose"/> on the
/// returned handle removes a single entry early.</para>
///
/// <para><b>Threading:</b> callbacks fire on FL's UI thread. If a handler touches UI that lives on a
/// different thread (e.g. a WPF window on its own dispatcher), marshal to that thread yourself.
/// <see cref="AddToggle"/>'s <c>isChecked</c> is polled while a menu is (re)built, so keep it fast and
/// non-blocking (read a cached flag rather than blocking on another thread).</para>
/// </summary>
public interface IFlMenuRegistrar
{
    /// <summary>
    /// Add a checkable toggle to a native FL dropdown. <paramref name="isChecked"/> is queried when
    /// the menu is (re)built so the checkmark (✓) reflects live state; <paramref name="onToggled"/>
    /// fires when the user clicks the entry. Returns a handle whose <see cref="IDisposable.Dispose"/>
    /// removes the entry (it is also removed automatically when the plugin is disabled).
    /// </summary>
    /// <param name="menu">Which top-level FL dropdown to add the entry to.</param>
    /// <param name="caption">The menu text (no accelerator/&amp; needed).</param>
    /// <param name="isChecked">Returns the current checked state; polled when the menu is built.</param>
    /// <param name="onToggled">Invoked when the user clicks the entry.</param>
    IDisposable AddToggle(FlNativeMenu menu, string caption, Func<bool> isChecked, Action onToggled);

    /// <summary>
    /// Add a plain (non-checkable) command item to a native FL dropdown; <paramref name="onInvoke"/>
    /// fires when the user clicks it. Returns a handle whose <see cref="IDisposable.Dispose"/> removes
    /// the entry (also removed automatically when the plugin is disabled).
    /// </summary>
    IDisposable AddCommand(FlNativeMenu menu, string caption, Action onInvoke);

    /// <summary>
    /// Ask the host to re-render this plugin's menu entries now. Call it when a toggle's checked-state
    /// changed without a menu click (e.g. the window it tracks was closed another way) so the ✓ updates
    /// without waiting for the next natural rebuild. Cheap and safe to call from any thread.
    /// </summary>
    void Refresh();
}
