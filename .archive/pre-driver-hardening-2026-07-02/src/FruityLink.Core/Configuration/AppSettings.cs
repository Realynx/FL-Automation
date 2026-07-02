namespace FruityLink.Core.Configuration;

/// <summary>UI theme selection.</summary>
public enum AppTheme
{
    Dark,
    Light,
}

/// <summary>Root application settings, persisted as JSON.</summary>
/// <param name="Theme">Active theme.</param>
/// <param name="Llm">Chat backend connection.</param>
/// <param name="Embeddings">RAG embeddings backend (defaults to inheriting the chat backend).</param>
/// <param name="FirstRunCompleted">Whether the setup wizard has been completed.</param>
/// <param name="Debug">When true, exposes debug features in the UI (e.g. the Diagnostics panel).</param>
/// <param name="MaxSubAgents">Maximum number of sub-agents the orchestrator may run in parallel.</param>
/// <param name="Soul">Creative profile biasing the AI's melody/chord idea generation.</param>
public sealed record AppSettings(
    AppTheme Theme = AppTheme.Dark,
    LlmSettings? Llm = null,
    EmbeddingSettings? Embeddings = null,
    bool FirstRunCompleted = false,
    bool Debug = false,
    int MaxSubAgents = 16,
    Soul? Soul = null)
{
    public LlmSettings LlmOrDefault => Llm ?? new LlmSettings();
    public EmbeddingSettings EmbeddingsOrDefault => Embeddings ?? new EmbeddingSettings();
    public Soul SoulOrDefault => Soul ?? new Soul();
}
