namespace FruityLink.Core.Domain;

/// <summary>What triggered a project-state commit.</summary>
public enum CommitTrigger
{
    /// <summary>The first commit for a session (a baseline snapshot).</summary>
    Initial,

    /// <summary>Auto-captured at an AI work-unit (turn) boundary that mutated project state.</summary>
    Auto,

    /// <summary>Explicitly requested by the user (UI button).</summary>
    Manual,

    /// <summary>A safety backup taken just before a restore, so a mis-click stays recoverable.</summary>
    PreRestoreSafety,
}

/// <summary>
/// One AI work-unit committed to project history. A commit = a full-state backup (<c>.flp</c>) plus an
/// optional readable snapshot (<c>state.json</c>) and a link to the chat version node. Restore = open the
/// commit's <c>.flp</c> (authoritative); the JSON state is inspection + fallback only.
/// </summary>
/// <param name="Id">Stable commit id (GUID "N").</param>
/// <param name="ParentId">Previous commit id, or null for the root; forms a DAG mirroring the chat tree.</param>
/// <param name="Label">Human-readable summary (auto from ops/user message, or manual).</param>
/// <param name="CreatedAt">When the commit was captured (UTC).</param>
/// <param name="SessionId">Owning chat session id.</param>
/// <param name="ChatNodeId">Linked <c>VersionNode.Id</c> in the chat version tree, or null.</param>
/// <param name="FlpBackupPath">Absolute path to the <c>{commitId}.flp</c> backup (authoritative restore).</param>
/// <param name="StateJsonPath">Absolute path to the <c>{commitId}.state.json</c> snapshot, or null.</param>
/// <param name="OriginalProjectPath">FL's own project path at commit time (for crash detection), or null.</param>
/// <param name="Operations">Human-readable op summaries drained from the audit sink.</param>
/// <param name="Trigger">What caused this commit.</param>
public sealed record ProjectCommit(
    string Id,
    string? ParentId,
    string Label,
    DateTimeOffset CreatedAt,
    string SessionId,
    string? ChatNodeId,
    string FlpBackupPath,
    string? StateJsonPath,
    string? OriginalProjectPath,
    IReadOnlyList<string> Operations,
    CommitTrigger Trigger);

/// <summary>
/// A plain serializable snapshot of the readable FL project state — typed scalars where the SDK exposes
/// them, string blobs otherwise (honest about the round-trip gaps: the <c>.flp</c> stays authoritative).
/// Captured off the bridge hot path; used for UI inspection and last-resort partial replay.
/// </summary>
public sealed record FlProjectState(
    int SchemaVersion,
    DateTimeOffset CapturedAt,
    double Tempo,
    int Ppq,
    string ProjectInfo,
    string SongState,
    string Patterns,
    string Channels,
    string PlaylistTracks,
    string Clips,
    string Arrangements,
    string Markers,
    IReadOnlyList<FlChannelState> ChannelDetail,
    IReadOnlyList<string> MixerTracks);

/// <summary>Per-channel readable detail inside an <see cref="FlProjectState"/>.</summary>
public sealed record FlChannelState(int Index, string Name, string PluginDescription, string? Notes);

/// <summary>The kind of change a <see cref="ProjectVersionChanged"/> notification carries.</summary>
public enum ProjectVersionChangeKind
{
    Committed,
    Restored,
    Undone,
    Redone,
    SessionOpened,
    Pruned,
}

/// <summary>Raised when project version-control state changes (History/Head/Can*/recovery).</summary>
/// <param name="Kind">What changed.</param>
/// <param name="Commit">The commit involved, when applicable.</param>
public sealed record ProjectVersionChanged(ProjectVersionChangeKind Kind, ProjectCommit? Commit);
