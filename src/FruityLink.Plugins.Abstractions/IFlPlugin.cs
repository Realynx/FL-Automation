namespace FruityLink.Plugins.Abstractions;

/// <summary>
/// A FruityLink plugin. Discovered + managed by the host; the user turns it on/off from the
/// "Plugins" toolbar dropdown. Implementations MUST expose a public parameterless constructor so the
/// host can instantiate them after loading the assembly. Keep construction cheap + side-effect-free —
/// do the real work in <see cref="EnableAsync"/>, and undo ALL of it in <see cref="DisableAsync"/>.
/// </summary>
public interface IFlPlugin
{
    /// <summary>Stable unique id (e.g. "fl-agent"); used for persistence + enable/disable.</summary>
    string Id { get; }

    /// <summary>Display name shown in the Plugins dropdown (e.g. "FL Automate").</summary>
    string Name { get; }

    /// <summary>One-line description shown in the plugin manager.</summary>
    string Description { get; }

    /// <summary>Plugin version string (e.g. "1.0.0").</summary>
    string Version { get; }

    /// <summary>
    /// Activate the plugin: start services / show UI / register behaviour. Called when the user enables
    /// it, or at startup if it was persisted enabled. Must be idempotent and must never take the host
    /// down — catch + report your own failures.
    /// </summary>
    Task EnableAsync(IPluginContext context, CancellationToken ct = default);

    /// <summary>
    /// Deactivate the plugin: stop everything started in <see cref="EnableAsync"/> and release
    /// resources, leaving FL Studio untouched. Must be idempotent.
    /// </summary>
    Task DisableAsync(CancellationToken ct = default);
}
