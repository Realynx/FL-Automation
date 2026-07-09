namespace FruityLink.Plugins.Abstractions;

/// <summary>
/// Read-side view of the host's menu-contribution registry, consumed by the native ↔ managed glue
/// that materializes plugin menu entries into FL's dropdowns. Plugins never use this — they contribute
/// through <see cref="IFlMenuRegistrar"/>. The host publishes the live registry on
/// <see cref="FlMenuRegistryLocator.Current"/>; the glue enumerates it, queries checkmarks, and
/// dispatches clicks by id.
/// </summary>
public interface IFlMenuContributions
{
    /// <summary>
    /// All current contributions as compact JSON, in display order:
    /// <c>[{"id":"..","menu":"View","caption":"..","kind":"toggle"|"command","checked":true|false},
    /// ...]</c>. <c>checked</c> is evaluated live (each toggle's <c>isChecked</c>) at the moment of the
    /// call; command items report <c>false</c>. Only <c>\</c> and <c>"</c> are escaped (the native
    /// parser unescapes just those), so captions may contain any other UTF-8 character.
    /// </summary>
    string ListJson();

    /// <summary>
    /// Invoke the contribution with this id (fire its toggle/command handler). Returns false for an
    /// unknown id. Never throws — a misbehaving handler is caught and reported.
    /// </summary>
    bool Invoke(string id);

    /// <summary>
    /// Live checked-state of a contribution: 1 = checked, 0 = unchecked or a command item, -1 = unknown id.
    /// </summary>
    int Checked(string id);
}

/// <summary>
/// Process-wide accessor for the live <see cref="IFlMenuContributions"/> registry. The plugin host
/// sets <see cref="Current"/> during initialization; the native menu glue (FlClrHost.dll →
/// FlBridge.dll) reads it to enumerate contributions, query checkmarks, and dispatch clicks. It is
/// null before the host initializes — the glue degrades to an empty contribution set. Mirrors
/// <see cref="PluginManagerLocator"/>, decoupling the native layer from the host implementation.
/// </summary>
public static class FlMenuRegistryLocator
{
    /// <summary>The active menu-contribution registry, or null before the host has initialized it.</summary>
    public static IFlMenuContributions? Current { get; set; }
}
