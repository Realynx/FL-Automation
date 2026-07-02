using FruityLink.Agent.Plugins;

namespace FruityLink.Agent;

/// <summary>
/// Aggregates the agent's tool plugins so the host registers them once and the agent
/// adds them all to its kernel. FL control is native (injected DLL) via <see cref="NativeControlPlugin"/>.
/// </summary>
public sealed class FlPluginSet(
    MusicTheoryPlugin musicTheory,
    NativeControlPlugin nativeControl,
    KnowledgePlugin knowledge,
    OrchestrationPlugin orchestration)
{
    /// <summary>All plugins with the names they are exposed under to the model.</summary>
    public IReadOnlyList<(string Name, object Instance)> All { get; } = new (string, object)[]
    {
        ("MusicTheory", musicTheory),
        ("NativeControl", nativeControl),
        ("Knowledge", knowledge),
        ("Orchestration", orchestration),
    };
}
