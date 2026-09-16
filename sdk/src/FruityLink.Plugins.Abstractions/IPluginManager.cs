namespace FruityLink.Plugins.Abstractions;

/// <summary>Immutable view of one plugin for the manager UI / toolbar dropdown.</summary>
/// <param name="Id">Stable unique id.</param>
/// <param name="Name">Display name.</param>
/// <param name="Description">One-line description.</param>
/// <param name="Version">Version string.</param>
/// <param name="Enabled">Whether the user has it switched on (persisted).</param>
/// <param name="Loaded">Whether its assembly is currently loaded/active in-process.</param>
public sealed record PluginInfo(
    string Id,
    string Name,
    string Description,
    string Version,
    bool Enabled,
    bool Loaded);

/// <summary>
/// The host's plugin registry: discovers installed plugins, reports their state, and toggles them.
/// The native "Plugins" toolbar dropdown drives this via <see cref="PluginManagerLocator.Current"/>.
/// </summary>
public interface IPluginManager
{
    /// <summary>All discovered/installed plugins + their current state.</summary>
    IReadOnlyList<PluginInfo> List();

    /// <summary>Enable a plugin by id (load + activate it; persist the choice). Returns success.</summary>
    Task<bool> EnableAsync(string id, CancellationToken ct = default);

    /// <summary>Disable a plugin by id (deactivate it; persist the choice). Returns success.</summary>
    Task<bool> DisableAsync(string id, CancellationToken ct = default);

    /// <summary>Whether a plugin is currently enabled.</summary>
    bool IsEnabled(string id);
}

/// <summary>
/// Process-wide accessor for the live <see cref="IPluginManager"/>. The host
/// (FruityLink.Plugins.Host) sets <see cref="Current"/> during bootstrap; the native toolbar glue
/// reads it to list/toggle plugins. Decouples the UI + native layers from the host implementation so
/// each can be built independently.
/// </summary>
public static class PluginManagerLocator
{
    /// <summary>The active plugin manager, or null before the host has initialized it.</summary>
    public static IPluginManager? Current { get; set; }
}
