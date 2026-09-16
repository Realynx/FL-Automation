namespace FruityLink.Plugins.Abstractions;

/// <summary>
/// Optional companion to <see cref="IFlPlugin"/> for plugins with heavy, <b>FL-independent</b> startup
/// work (e.g. a UI toolkit cold-start, a runtime/kernel build). The host calls <see cref="PrepareAsync"/>
/// <b>before</b> FL Studio is fully initialized — concurrently with FL's own UI load — so this work is off
/// the critical path and the later <see cref="IFlPlugin.EnableAsync"/> only has to do the small
/// FL-dependent part (e.g. reparenting an already-built, already-painted window into FL's chrome). The
/// net effect is the plugin's UI appearing <i>with</i> FL's UI instead of seconds after it.
///
/// <para>Only plugins the user has persisted as enabled are pre-warmed. A plugin that does not implement
/// this interface behaves exactly as before (all work in <see cref="IFlPlugin.EnableAsync"/>).</para>
/// </summary>
public interface IFlPreWarmPlugin
{
    /// <summary>
    /// Do the FL-independent portion of startup ahead of FL readiness. Called at most once, before
    /// <see cref="IFlPlugin.EnableAsync"/>, and only for persisted-enabled plugins. Contract:
    /// <list type="bullet">
    ///   <item><b>Must not touch FL state</b> — FL is NOT ready yet (no song/channel/window objects).
    ///   Anything requiring the live FL project or FL's windows belongs in
    ///   <see cref="IFlPlugin.EnableAsync"/>.</item>
    ///   <item><b>Best-effort + fail-safe</b> — a failure here must never take the host down; the host
    ///   proceeds to <see cref="IFlPlugin.EnableAsync"/>, which is expected to build cold as a fallback.</item>
    ///   <item><b>Idempotent</b> — safe if somehow invoked more than once.</item>
    /// </list>
    /// The <paramref name="context"/> is the SAME instance later handed to
    /// <see cref="IFlPlugin.EnableAsync"/>, so state stashed on the plugin instance carries across.
    /// </summary>
    Task PrepareAsync(IPluginContext context, CancellationToken ct = default);
}
