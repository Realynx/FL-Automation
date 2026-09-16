namespace FruityLink.Plugins.Abstractions;

/// <summary>
/// Read-side view of the host's toolbar-button registry, consumed by the native ↔ managed glue that
/// materializes plugin buttons onto FL's main toolbar. Plugins never use this — they contribute through
/// <see cref="IFlToolbarRegistrar"/>. The host publishes the live registry on
/// <see cref="FlToolbarRegistryLocator.Current"/>; the glue enumerates it, queries lit state, and
/// dispatches clicks by id.
/// </summary>
public interface IFlToolbarContributions
{
    /// <summary>
    /// All current buttons as compact JSON, in display order:
    /// <c>[{"id":"..","caption":"..","kind":"toggle"|"button","active":true|false,"order":0}, ...]</c>.
    /// <c>active</c> is evaluated live (each toggle's <c>isActive</c>) at the moment of the call;
    /// momentary ("button") items report <c>false</c>. Only <c>\</c> and <c>"</c> are escaped (the native
    /// parser unescapes just those), so captions may contain any other UTF-8 character.
    /// </summary>
    string ListJson();

    /// <summary>
    /// Invoke the button with this id (fire its toggle/click handler). Returns false for an unknown id.
    /// Never throws — a misbehaving handler is caught and reported.
    /// </summary>
    bool Invoke(string id);

    /// <summary>
    /// Live lit-state of a button: 1 = active, 0 = inactive or a momentary item, -1 = unknown id.
    /// </summary>
    int Active(string id);
}

/// <summary>
/// Process-wide accessor for the live <see cref="IFlToolbarContributions"/> registry. The plugin host
/// sets <see cref="Current"/> during initialization; the native toolbar glue (FlClrHost.dll →
/// FlBridge.dll) reads it to enumerate buttons, query lit state, and dispatch clicks. It is null before
/// the host initializes — the glue degrades to an empty button set. Mirrors
/// <see cref="FlMenuRegistryLocator"/>, decoupling the native layer from the host implementation.
/// </summary>
public static class FlToolbarRegistryLocator
{
    /// <summary>The active toolbar-button registry, or null before the host has initialized it.</summary>
    public static IFlToolbarContributions? Current { get; set; }
}
