using FruityLink.Core.Abstractions;
using FruityLink.Core.Domain;

namespace FruityLink.Persistence;

/// <summary>
/// File-backed <see cref="IProjectVersionControl"/>. Each commit saves a full-state <c>.flp</c> backup via
/// <see cref="INativeFlControl.SaveCopyAsync"/> (one native call — the hot path) and appends a row to the
/// session's <c>index.json</c> (atomic rewrite). Restore opens the target <c>.flp</c> via
/// <see cref="INativeFlControl.OpenProjectAsync"/> after a best-effort safety backup of the live state.
/// JSON state capture / partial replay is deferred (Phase 3) and kept off the bridge hot path.
/// </summary>
public sealed class JsonProjectVersionControl : IProjectVersionControl
{
    private readonly StoragePaths _paths;
    private readonly INativeFlControl _fl;
    private readonly CommitJournalStore _journal;
    private readonly ProjectVersionIndexStore _index;
    private readonly SemaphoreSlim _gate = new(1, 1);

    // Reads are lock-free: mutations always REPLACE these references (never mutate in place), and a
    // reference read/write is atomic in .NET, so History/Head can be read from the UI thread safely.
    private volatile List<ProjectCommit> _commits = new();
    private volatile string? _headId;
    private volatile ProjectCommit? _recovery;
    private string _sessionId = string.Empty;

    /// <summary>Creates a project version-control store rooted at <paramref name="paths"/>, backing up and
    /// restoring through <paramref name="fl"/> (the host's single FL bridge). <paramref name="registry"/>
    /// enables granular inverse-journal undo/redo; when null, every commit falls back to its <c>.flp</c>
    /// (the pre-journal behavior).</summary>
    public JsonProjectVersionControl(StoragePaths paths, INativeFlControl fl, IInverseOpRegistry? registry = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(fl);
        _paths = paths;
        _fl = fl;
        _journal = new CommitJournalStore(paths, fl, registry);
        _index = new ProjectVersionIndexStore(paths);
    }

    /// <inheritdoc />
    public string SessionId => _sessionId;

    /// <inheritdoc />
    public IReadOnlyList<ProjectCommit> History => _commits.ToArray();

    /// <inheritdoc />
    public ProjectCommit? Head
    {
        get
        {
            string? id = _headId;
            if (id is null) return null;
            List<ProjectCommit> snapshot = _commits;
            for (int i = 0; i < snapshot.Count; i++)
                if (snapshot[i].Id == id) return snapshot[i];
            return null;
        }
    }

    /// <inheritdoc />
    public bool CanUndo
    {
        get
        {
            ProjectCommit? head = Head;
            return head?.ParentId is not null && _commits.Any(c => c.Id == head.ParentId);
        }
    }

    /// <inheritdoc />
    public bool CanRedo
    {
        get
        {
            ProjectCommit? head = Head;
            return head is not null && _commits.Any(c => c.ParentId == head.Id);
        }
    }

    /// <inheritdoc />
    public ProjectCommit? RecoveryCandidate => _recovery;

    /// <inheritdoc />
    public event EventHandler<ProjectVersionChanged>? Changed;

    /// <inheritdoc />
    public async Task OpenSessionAsync(string sessionId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(sessionId);

        bool existingSession;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _sessionId = sessionId;
            PurgeOtherSessions(sessionId);   // undo history is runtime-scoped — drop stale prior-run sessions
            (List<ProjectCommit> commits, string? headId) = await _index.ReadAsync(sessionId, ct).ConfigureAwait(false);
            // Whether this session already had history ON DISK before this run — the only case where crash
            // recovery is meaningful. A fresh session (no prior commits) gets an auto "Session start"
            // baseline below; offering THAT as a recovery candidate is the false "crash recovery available
            // after a normal close" bug.
            existingSession = commits.Count > 0;
            _commits = commits;
            _headId = headId;
            _recovery = null;
        }
        finally { _gate.Release(); }

        // A fresh session's first commit is its ROOT (ParentId = null), so the FIRST AI edit would have
        // nothing to undo TO and Undo stays disabled. Snapshot the session-start state as the root now, so
        // the first edit gets a parent to revert to. Best-effort — needs the bridge; only when empty.
        if (_commits.Count == 0 && await _fl.IsAvailableAsync(ct).ConfigureAwait(false))
        {
            try { await CommitAsync(label: "Session start", trigger: CommitTrigger.Initial, ct: ct).ConfigureAwait(false); }
            catch { /* baseline is best-effort; only the very first edit would then lack an undo target */ }
        }

        await DetectRecoveryAsync(existingSession, ct).ConfigureAwait(false);   // best-effort, off the lock
        Raise(ProjectVersionChangeKind.SessionOpened, Head);
    }

    /// <summary>Undo history is scoped to the CURRENT FL runtime. On a new run we delete every OTHER
    /// session's project-version data: a prior run's inverse-ops assume that run's project state, so undoing
    /// across runs applies stale ops onto a different project (crash) — and once FL closes those changes are
    /// gone or already saved into the .flp, so keeping them is pointless. Best-effort; never blocks open.</summary>
    private void PurgeOtherSessions(string currentSessionId)
    {
        try
        {
            string? root = Path.GetDirectoryName(_paths.ProjectVersionDir(currentSessionId));
            if (root is null || !Directory.Exists(root)) return;
            foreach (string dir in Directory.EnumerateDirectories(root))
            {
                if (string.Equals(Path.GetFileName(dir), currentSessionId, StringComparison.OrdinalIgnoreCase)) continue;
                try { Directory.Delete(dir, recursive: true); } catch { /* locked/in-use → leave it */ }
            }
        }
        catch { /* best-effort — never let cleanup break session open */ }
    }

    /// <inheritdoc />
    public async Task<ProjectCommit?> CommitAsync(
        string? label = null,
        string? chatNodeId = null,
        IReadOnlyList<string>? operations = null,
        CommitTrigger trigger = CommitTrigger.Manual,
        IReadOnlyList<ChangeRecord>? changes = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_sessionId)) return null;
        if (!await _fl.IsAvailableAsync(ct).ConfigureAwait(false)) return null;   // no bridge → skip silently

        // Commit even on an UNTITLED project (FL templates load as "…\untitled.flp", so the user's normal
        // starting state IS untitled — skipping it meant version control never recorded anything). This is
        // safe now: SaveCopyAsync uses FL's low-level DIRECT writer (FLproj_WriteFlpFile flag 0), which does
        // NOT go through the SaveProjectToFlp wrapper whose untitled temp→move failure popped a blocking
        // modal on FL's main thread (the post-turn UI freeze). The direct writer is modal-free and does not
        // touch the project path/title, so the user's project stays "untitled" in FL.
        string id = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(_paths.ProjectVersionDir(_sessionId));
        string flp = _paths.ProjectFlpBackup(_sessionId, id);

        await _fl.SaveCopyAsync(flp, ct).ConfigureAwait(false);   // ONE native call — authoritative backup

        // Inverse journal (granular undo/redo). Written ONCE here, read only when THIS commit is
        // undone/redone — never held resident. Absent/invertible:false ⇒ the commit uses the .flp.
        await _journal.WriteAsync(_sessionId, id, changes, ct).ConfigureAwait(false);

        string? original = await TryReadProjectPathAsync(ct).ConfigureAwait(false);

        IReadOnlyList<string> ops = operations ?? Array.Empty<string>();
        string finalLabel =
            !string.IsNullOrWhiteSpace(label) ? label!
            : ops.Count > 0 ? string.Join("; ", ops.Take(3))
            : "AI edit";

        var commit = new ProjectCommit(
            id, _headId, finalLabel, DateTimeOffset.UtcNow, _sessionId, chatNodeId,
            flp, null, original, ops.ToArray(), trigger);

        await SetHeadAsync(id, ct, append: commit).ConfigureAwait(false);

        Raise(ProjectVersionChangeKind.Committed, commit);
        return commit;
    }

    /// <inheritdoc />
    public async Task<ProjectCommit?> UndoAsync(CancellationToken ct = default)
    {
        ProjectCommit? head = Head;
        if (head?.ParentId is null) return null;
        ProjectCommit? parent = _commits.FirstOrDefault(c => c.Id == head.ParentId);
        if (parent is null) return null;
        if (!await _fl.IsAvailableAsync(ct).ConfigureAwait(false)) return null;

        // Granular path: replay HEAD's ops in REVERSE, applying each op's OLD value to land on the parent.
        // Falls back to the parent's .flp when the journal is missing/invertible:false or a replay step
        // fails (RestoreCore then fully overwrites live state — FL is never left half-inverted).
        if (await _journal.TryReplayAsync(_sessionId, head, undo: true, ct).ConfigureAwait(false))
        {
            // No .flp touch — the granular path restored state via inverse ops; just move HEAD.
            await SetHeadAsync(parent.Id, ct).ConfigureAwait(false);
            Raise(ProjectVersionChangeKind.Undone, parent);
            return parent;
        }
        return await RestoreCoreAsync(parent, ProjectVersionChangeKind.Undone, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<ProjectCommit?> RedoAsync(CancellationToken ct = default)
    {
        ProjectCommit? head = Head;
        if (head is null) return null;
        ProjectCommit? child = _commits
            .Where(c => c.ParentId == head.Id)
            .OrderByDescending(c => c.CreatedAt)
            .FirstOrDefault();
        if (child is null) return null;
        if (!await _fl.IsAvailableAsync(ct).ConfigureAwait(false)) return null;

        // Granular path: replay the CHILD's ops FORWARD, applying each op's NEW value. Same .flp fallback.
        if (await _journal.TryReplayAsync(_sessionId, child, undo: false, ct).ConfigureAwait(false))
        {
            await SetHeadAsync(child.Id, ct).ConfigureAwait(false);
            Raise(ProjectVersionChangeKind.Redone, child);
            return child;
        }
        return await RestoreCoreAsync(child, ProjectVersionChangeKind.Redone, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<ProjectCommit?> RestoreAsync(string commitId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(commitId);
        ProjectCommit? target = _commits.FirstOrDefault(c => c.Id == commitId);
        return target is null
            ? Task.FromResult<ProjectCommit?>(null)
            : RestoreCoreAsync(target, ProjectVersionChangeKind.Restored, ct);
    }

    /// <inheritdoc />
    public async Task<FlProjectState?> GetStateAsync(string commitId, CancellationToken ct = default)
    {
        ProjectCommit? c = _commits.FirstOrDefault(x => x.Id == commitId);
        if (c?.StateJsonPath is null) return null;
        return await JsonFile
            .TryReadAsync<FlProjectState>(c.StateJsonPath, ct, swallowIoErrors: true)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void DismissRecovery()
    {
        if (_recovery is null) return;
        _recovery = null;
        Raise(ProjectVersionChangeKind.SessionOpened, Head);
    }

    // ── restore core ─────────────────────────────────────────────────────────────────────────────
    private async Task<ProjectCommit?> RestoreCoreAsync(
        ProjectCommit target, ProjectVersionChangeKind kind, CancellationToken ct)
    {
        if (!await _fl.IsAvailableAsync(ct).ConfigureAwait(false)) return null;
        if (!File.Exists(target.FlpBackupPath)) return null;   // JSON replay fallback deferred (Phase 3)

        // 1) safety net: capture the CURRENT live state before we overwrite it (best-effort, own slot).
        await SafetyBackupAsync(ct).ConfigureAwait(false);

        // 2) authoritative restore.
        await _fl.OpenProjectAsync(target.FlpBackupPath, ct).ConfigureAwait(false);

        await SetHeadAsync(target.Id, ct).ConfigureAwait(false);

        Raise(kind, target);
        return target;
    }

    private async Task SafetyBackupAsync(CancellationToken ct)
    {
        try
        {
            // Safe on untitled projects too — SaveCopyAsync uses FL's modal-free direct writer (see
            // CommitAsync). The pre-restore safety copy is best-effort; a failure never blocks the restore.
            Directory.CreateDirectory(_paths.ProjectVersionDir(_sessionId));
            string stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss-fff");
            string path = Path.Combine(_paths.ProjectVersionDir(_sessionId), $"pre-restore-{stamp}.flp");
            await _fl.SaveCopyAsync(path, ct).ConfigureAwait(false);
        }
        catch { /* the safety backup is a net, not a guarantee — never block a restore on it */ }
    }

    /// <summary>The single index-mutation path shared by commit/restore/undo/redo: under the gate,
    /// optionally append a new commit, move HEAD to <paramref name="newHeadId"/>, clear any recovery
    /// candidate, and persist the index. Never touches a <c>.flp</c>.</summary>
    private async Task SetHeadAsync(string? newHeadId, CancellationToken ct, ProjectCommit? append = null)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (append is not null) _commits = new List<ProjectCommit>(_commits) { append };
            _headId = newHeadId;
            _recovery = null;
            await _index.SaveAsync(_sessionId, _commits, _headId, ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    // ── crash recovery detection ──────────────────────────────────────────────────────────────────
    private async Task DetectRecoveryAsync(bool existingSession, CancellationToken ct)
    {
        try
        {
            // Recovery is only meaningful for a session that ALREADY had history on disk before this run
            // (i.e. a prior run left unsaved AI work). A fresh session's only commit is the auto "Session
            // start" baseline we just created, and the default FL template loads UNTITLED — which the
            // untitled branch below would treat as recoverable — so without this gate every normal launch
            // falsely reports "crash recovery available". NOTE: with today's per-run session id +
            // PurgeOtherSessions, sessions are always fresh, so this correctly keeps recovery OFF until a
            // stable (project-keyed) session id exists to carry real cross-run recovery.
            if (!existingSession) return;

            ProjectCommit? latest = _commits.LastOrDefault();
            // The auto baseline is not "work to recover"; only a real edit past it is.
            if (latest is null || latest.Trigger == CommitTrigger.Initial) return;
            if (!File.Exists(latest.FlpBackupPath)) return;
            if (!await _fl.IsAvailableAsync(ct).ConfigureAwait(false)) return;

            (string? path, bool untitled) = FlProjectInfoParser.Parse(await _fl.GetProjectInfoAsync(ct).ConfigureAwait(false));

            bool recoverable;
            if (untitled)
            {
                // FL opened blank / never saved after a crash — a matching session backup exists → offer.
                recoverable = true;
            }
            else if (!string.IsNullOrEmpty(path)
                     && string.Equals(path, latest.OriginalProjectPath, StringComparison.OrdinalIgnoreCase)
                     && File.Exists(path))
            {
                // Same project the AI edited, but the user's on-disk .flp is OLDER than our last backup →
                // the AI's edits were never persisted by the user → offer recovery.
                recoverable = File.GetLastWriteTimeUtc(path) < latest.CreatedAt.UtcDateTime;
            }
            else
            {
                recoverable = false;
            }

            if (recoverable) _recovery = latest;
        }
        catch { /* recovery detection is best-effort; never throw on startup */ }
    }

    private async Task<string?> TryReadProjectPathAsync(CancellationToken ct)
    {
        try { return FlProjectInfoParser.Parse(await _fl.GetProjectInfoAsync(ct).ConfigureAwait(false)).Path; }
        catch { return null; }
    }

    private void Raise(ProjectVersionChangeKind kind, ProjectCommit? commit) =>
        Changed?.Invoke(this, new ProjectVersionChanged(kind, commit));
}
