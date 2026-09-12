namespace FruityLink.Core.Configuration;

/// <summary>UI theme selection.</summary>
public enum AppTheme
{
    Dark,
    Light,
}

/// <summary>Root application settings, persisted as JSON.</summary>
/// <param name="Theme">Active theme.</param>
/// <param name="Account">FL Automate account connection (API/gateway bases, model, display identity).</param>
/// <param name="Embeddings">RAG embeddings backend (standalone local endpoint; not the gateway).</param>
/// <param name="FirstRunCompleted">Whether the setup wizard has been completed.</param>
/// <param name="Debug">When true, exposes debug features in the UI (e.g. the Diagnostics panel).</param>
/// <param name="MaxSubAgents">Maximum number of sub-agents the orchestrator may run in parallel.</param>
/// <param name="Soul">Creative profile biasing the AI's melody/chord idea generation.</param>
public sealed record AppSettings(
    AppTheme Theme = AppTheme.Dark,
    AccountSettings? Account = null,
    EmbeddingSettings? Embeddings = null,
    bool FirstRunCompleted = false,
    bool Debug = false,
    int MaxSubAgents = 16,
    Soul? Soul = null)
{
    public AccountSettings AccountOrDefault => Account ?? new AccountSettings();
    public EmbeddingSettings EmbeddingsOrDefault => Embeddings ?? new EmbeddingSettings();
    public Soul SoulOrDefault => Soul ?? new Soul();
}
