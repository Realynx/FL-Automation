using FruityLink.Core.Abstractions;

namespace FruityLink.Plugins.Abstractions;

/// <summary>
/// Host-provided capabilities handed to a plugin on enable. Deliberately exposes ONLY the safe,
/// typed FL control surface (<see cref="INativeFlControl"/>) — never the raw memory/call primitives
/// (poke/peek/call) — so a third-party plugin cannot reach FL's DRM or internals. This is the plugin
/// trust boundary (see re/17-drm-guard.md): no address ever crosses it.
/// </summary>
public interface IPluginContext
{
    /// <summary>Safe, typed FL Studio control. The ONLY way a plugin touches FL.</summary>
    INativeFlControl Fl { get; }

    /// <summary>Host service provider for resolving shared services (may be empty for minimal hosts).</summary>
    IServiceProvider Services { get; }

    /// <summary>
    /// Contribute entries to FL Studio's native top-level menus (e.g. a show/hide toggle in the
    /// <see cref="FlNativeMenu.View"/> dropdown). The returned handles are scoped to THIS plugin:
    /// all of its contributions are removed automatically when it is disabled, and
    /// <see cref="IDisposable.Dispose"/> removes a single entry early. Like the rest of the context
    /// this is inside the trust boundary — only captions + callbacks cross it, never raw memory.
    /// </summary>
    IFlMenuRegistrar Menu { get; }

    /// <summary>
    /// Contribute big square buttons to FL Studio's main toolbar (e.g. a show/hide toggle for a plugin
    /// window, beside FL's own metronome/typing-keyboard toggles). The returned handles are scoped to
    /// THIS plugin: all of its buttons are removed automatically when it is disabled, and
    /// <see cref="IDisposable.Dispose"/> removes a single button early. Like the rest of the context this
    /// is inside the trust boundary — only captions + callbacks cross it, never raw memory.
    /// </summary>
    IFlToolbarRegistrar Toolbar { get; }

    /// <summary>Append a diagnostic line to the host log.</summary>
    void Log(string message);
}
