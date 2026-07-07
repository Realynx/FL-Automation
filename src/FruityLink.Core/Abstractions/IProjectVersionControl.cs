using FruityLink.Core.Domain;

namespace FruityLink.Core.Abstractions;

/// <summary>
/// Version control + backup of the live FL project, grouped by AI work-unit (agent turn). Each commit is
/// a full-state <c>.flp</c> backup (authoritative) plus optional readable JSON state and a link to the
/// chat version node. Restore = open the target commit's <c>.flp</c>; the JSON state is inspection +
/// fallback only. All members are safe to call when the bridge is unavailable (they no-op / return null).
/// </summary>
public interface IProjectVersionControl
{
    /// <summary>The session this VC currently tracks (empty before <see cref="OpenSessionAsync"/>).</summary>
    string SessionId { get; }

    /// <summary>Commits in creation order (oldest → newest) for the session. Bind the UI to this.</summary>
    IReadOnlyList<ProjectCommit> History { get; }

    /// <summary>The commit the live project currently reflects (HEAD), or null before the first commit.</summary>
    ProjectCommit? Head { get; }

    /// <summary>True when HEAD has a parent to move back to.</summary>
    bool CanUndo { get; }

    /// <summary>True when a commit exists ahead of HEAD (a child) to move forward to.</summary>
    bool CanRedo { get; }

    /// <summary>A detected crash-recovery candidate (a backup newer than the user's on-disk project), or
    /// null. Computed on <see cref="OpenSessionAsync"/>; cleared by <see cref="DismissRecovery"/> or any
    /// commit/restore.</summary>
    ProjectCommit? RecoveryCandidate { get; }

    /// <summary>Point the VC at a session (loads its <c>index.json</c> and runs crash-recovery detection).
    /// Call on session open/switch.</summary>
    Task OpenSessionAsync(string sessionId, CancellationToken ct = default);

    /// <summary>Capture a commit now: save the <c>.flp</c> backup, append to history and advance HEAD.
    /// <paramref name="chatNodeId"/> links the chat checkpoint; <paramref name="operations"/> are drained
    /// op summaries (also used to auto-label when <paramref name="label"/> is null). <paramref name="changes"/>
    /// are the drained inverse-journal records for this turn: when supplied and fully invertible they are
    /// written to <c>{commitId}.ops.json</c> to enable granular undo/redo (null/empty ⇒ the commit stays
    /// <c>.flp</c>-only, i.e. today's behavior). Returns null when there was nothing to capture (e.g. bridge
    /// unavailable / no session open).</summary>
    Task<ProjectCommit?> CommitAsync(
        string? label = null,
        string? chatNodeId = null,
        IReadOnlyList<string>? operations = null,
        CommitTrigger trigger = CommitTrigger.Manual,
        IReadOnlyList<ChangeRecord>? changes = null,
        CancellationToken ct = default);

    /// <summary>Move HEAD to its parent and restore that commit (safety-backup, then open its <c>.flp</c>).</summary>
    Task<ProjectCommit?> UndoAsync(CancellationToken ct = default);

    /// <summary>Move HEAD forward to a child commit and restore it.</summary>
    Task<ProjectCommit?> RedoAsync(CancellationToken ct = default);

    /// <summary>Restore an arbitrary commit: (1) safety-backup the live project, (2) open the commit's
    /// <c>.flp</c>, (3) set HEAD. Does NOT create a new commit.</summary>
    Task<ProjectCommit?> RestoreAsync(string commitId, CancellationToken ct = default);

    /// <summary>Deserialized readable snapshot for a commit (UI diff/inspect), or null if none was captured.</summary>
    Task<FlProjectState?> GetStateAsync(string commitId, CancellationToken ct = default);

    /// <summary>Clear the pending <see cref="RecoveryCandidate"/> (user dismissed the recovery prompt).</summary>
    void DismissRecovery();

    /// <summary>Raised (any thread) after History/Head/Can*/recovery change. UI marshals to its thread.</summary>
    event EventHandler<ProjectVersionChanged>? Changed;
}
