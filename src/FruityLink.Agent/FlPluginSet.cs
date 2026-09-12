using FruityLink.Agent.Plugins;
using FruityLink.Core.Abstractions;

namespace FruityLink.Agent;

/// <summary>
/// Aggregates the agent's tool plugins so the host registers them once and the agent
/// adds them all to its kernel. FL control is native (injected DLL) via <see cref="NativeControlPlugin"/>.
/// </summary>
public sealed class FlPluginSet(
    MusicTheoryPlugin musicTheory,
    NativeControlPlugin nativeControl,
    KnowledgePlugin knowledge,
    OrchestrationPlugin orchestration,
    // Optional with a default so composition roots outside this assembly (which construct the set
    // positionally) keep compiling; hosts without project version control just omit it and the
    // Versioning tools stay off the surface entirely.
    VersioningPlugin? versioning = null)
{
    /// <summary>All plugins with the names they are exposed under to the model. Versioning is
    /// main-agent-only (like Orchestration): sub-agents do ONE scoped task and never need to browse
    /// past-turn history, so its two tools would be pure schema cost on their kernels.</summary>
    public IReadOnlyList<(string Name, object Instance)> All { get; } =
        SubAgentPlugins(musicTheory, nativeControl, knowledge)
            .Append(("Orchestration", (object)orchestration))
            .Concat(versioning is null
                ? Array.Empty<(string, object)>()
                : new[] { ("Versioning", (object)versioning) })
            .ToArray();

    /// <summary>Late-binds the project version store into the Versioning tools (list_versions /
    /// get_version_changes). Late because the store is composed after the plugin set — it needs the
    /// live bridge. No-op when the set was built without a Versioning plugin.</summary>
    public void AttachVersionControl(IProjectVersionControl versionControl) =>
        versioning?.Attach(versionControl);

    /// <summary>
    /// Builds the sub-agent plugin list from its three plugins. Exists as a static helper (rather
    /// than only the instance view) because <c>SubAgentService</c> can't take an <see cref="FlPluginSet"/>:
    /// the set contains the orchestration plugin, which itself wraps the sub-agent service — a
    /// construction cycle. Both paths share this one definition of "the sub-agent tool surface".
    /// </summary>
    internal static IReadOnlyList<(string Name, object Instance)> SubAgentPlugins(
        MusicTheoryPlugin musicTheory,
        NativeControlPlugin nativeControl,
        KnowledgePlugin knowledge) => new (string, object)[]
    {
        ("MusicTheory", musicTheory),
        ("NativeControl", nativeControl),
        ("Knowledge", knowledge),
    };
}
