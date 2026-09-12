using FruityLink.Core.Configuration;
using FruityLink.Core.Domain;

namespace FruityLink.Core.Abstractions;

/// <summary>Export formats for a saved chat.</summary>
public enum ChatExportFormat
{
    Markdown,
    Json,
}

/// <summary>Stores and retrieves chat sessions; supports export.</summary>
/// <remarks>Parked roadmap surface (chat save/branching UI): implemented by
/// <c>JsonChatStore</c> and covered by tests, but not yet registered in the
/// composition root — chat history currently lives in memory only.</remarks>
public interface IChatStore
{
    Task<IReadOnlyList<ChatSession>> ListAsync(CancellationToken ct = default);
    Task<ChatSession?> GetAsync(string id, CancellationToken ct = default);
    Task SaveAsync(ChatSession session, CancellationToken ct = default);
    Task DeleteAsync(string id, CancellationToken ct = default);

    /// <summary>Renders a session to the requested format and returns the content.</summary>
    Task<string> ExportAsync(string id, ChatExportFormat format, CancellationToken ct = default);
}

/// <summary>Persists a per-chat version tree (branch/checkpoint DAG) for undo/branching.</summary>
/// <remarks>Parked roadmap surface: implemented by <c>JsonVersionTree</c> and covered by tests,
/// but not yet registered in the composition root — project versioning currently ships via
/// <c>IProjectVersionControl</c> (the ProjectCommit DAG) instead.</remarks>
public interface IVersionTree
{
    /// <summary>Creates a checkpoint child of <paramref name="parentId"/> (or a root when null).</summary>
    Task<VersionNode> CreateCheckpointAsync(
        string sessionId,
        string? parentId,
        string label,
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<FlOperation> operations,
        CancellationToken ct = default);

    /// <summary>Returns all nodes for a session (the full tree).</summary>
    Task<IReadOnlyList<VersionNode>> GetTreeAsync(string sessionId, CancellationToken ct = default);

    /// <summary>Returns a single node, or null if absent.</summary>
    Task<VersionNode?> GetNodeAsync(string sessionId, string nodeId, CancellationToken ct = default);
}

/// <summary>Loads and saves root application settings.</summary>
public interface ISettingsStore
{
    Task<AppSettings> LoadAsync(CancellationToken ct = default);
    Task SaveAsync(AppSettings settings, CancellationToken ct = default);
}

/// <summary>Stores secrets (API keys) encrypted at rest, keyed by a logical name.</summary>
public interface ISecretStore
{
    Task SetAsync(string name, string secret, CancellationToken ct = default);
    Task<string?> GetAsync(string name, CancellationToken ct = default);
    Task DeleteAsync(string name, CancellationToken ct = default);
}
