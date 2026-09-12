namespace FruityLink.Core.Domain;

/// <summary>The kind of FL Studio change an agent operation performed (for audit/revert).</summary>
public enum FlOperationKind
{
    CreatePattern,
    RenamePattern,
    WriteNotes,
    SetChannelProperty,
    SetMixerProperty,
    SetPluginParam,
    Transport,
    Other,
}

/// <summary>
/// A recorded FL Studio operation the agent performed, with enough detail to describe
/// it and, where the API allows, attempt a best-effort revert.
/// </summary>
/// <param name="Kind">Operation category.</param>
/// <param name="Description">Human-readable summary, e.g. "Wrote 12 notes to pattern 3".</param>
/// <param name="Target">Affected entity (e.g. "pattern:3", "channel:0").</param>
/// <param name="UndoHint">Optional data to drive a revert (e.g. a prior value), or null if irreversible.</param>
/// <param name="Timestamp">When it happened.</param>
public sealed record FlOperation(
    FlOperationKind Kind,
    string Description,
    string Target,
    string? UndoHint,
    DateTimeOffset Timestamp);

/// <summary>
/// A node in a chat's version tree: a checkpoint snapshotting the conversation plus
/// the FL operations applied to reach it. Branching = creating a child from any node.
/// </summary>
/// <param name="Id">Stable node id.</param>
/// <param name="ParentId">Parent node id, or null for the root.</param>
/// <param name="Label">User/auto label, e.g. "before drum loop".</param>
/// <param name="Messages">Conversation snapshot at this checkpoint.</param>
/// <param name="Operations">FL operations performed since the parent.</param>
/// <param name="CreatedAt">Creation time.</param>
public sealed record VersionNode(
    string Id,
    string? ParentId,
    string Label,
    IReadOnlyList<ChatMessage> Messages,
    IReadOnlyList<FlOperation> Operations,
    DateTimeOffset CreatedAt);
