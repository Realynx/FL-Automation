using System.Text.Json;
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
    private readonly IInverseOpRegistry? _registry;
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
        _registry = registry;
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

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _sessionId = sessionId;
            PurgeOtherSessions(sessionId);   // undo history is runtime-scoped — drop stale prior-run sessions
            ProjectVersionIndex idx = await ReadIndexAsync(sessionId, ct).ConfigureAwait(false);
            _commits = idx.Commits ?? new List<ProjectCommit>();
            _headId = idx.HeadId;
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

        await DetectRecoveryAsync(ct).ConfigureAwait(false);   // best-effort, off the lock
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
        await WriteOpsJournalAsync(id, changes, ct).ConfigureAwait(false);

        string? original = await TryReadProjectPathAsync(ct).ConfigureAwait(false);

        IReadOnlyList<string> ops = operations ?? Array.Empty<string>();
        string finalLabel =
            !string.IsNullOrWhiteSpace(label) ? label!
            : ops.Count > 0 ? string.Join("; ", ops.Take(3))
            : "AI edit";

        var commit = new ProjectCommit(
            id, _headId, finalLabel, DateTimeOffset.UtcNow, _sessionId, chatNodeId,
            flp, null, original, ops.ToArray(), trigger);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _commits = new List<ProjectCommit>(_commits) { commit };
            _headId = id;
            _recovery = null;
            await SaveIndexAsync(ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }

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
        if (await TryReplayJournalAsync(head, undo: true, ct).ConfigureAwait(false))
        {
            await MoveHeadAsync(parent.Id, ct).ConfigureAwait(false);
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
        if (await TryReplayJournalAsync(child, undo: false, ct).ConfigureAwait(false))
        {
            await MoveHeadAsync(child.Id, ct).ConfigureAwait(false);
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
        if (c?.StateJsonPath is null || !File.Exists(c.StateJsonPath)) return null;
        try
        {
            await using FileStream stream = File.OpenRead(c.StateJsonPath);
            return await JsonSerializer
                .DeserializeAsync<FlProjectState>(stream, JsonDefaults.Options, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is JsonException or IOException) { return null; }
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

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _headId = target.Id;
            _recovery = null;
            await SaveIndexAsync(ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }

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

    // ── inverse journal (granular undo/redo) ────────────────────────────────────────────────────────

    /// <summary>Write <c>{commitId}.ops.json</c> when there are records to record. <c>invertible</c> is true
    /// only when a registry is wired and EVERY record's op is registered — otherwise the file marks itself
    /// non-invertible so undo/redo falls back to the <c>.flp</c>. Best-effort: the journal is an
    /// optimization, so a write failure just leaves the commit on the <c>.flp</c> path.</summary>
    private async Task WriteOpsJournalAsync(string commitId, IReadOnlyList<ChangeRecord>? changes, CancellationToken ct)
    {
        if (changes is not { Count: > 0 }) return;   // nothing granular this turn ⇒ .flp-only (as before)
        bool invertible = _registry is not null && changes.All(_registry.CanInvert);
        var journal = new CommitJournal(1, commitId, DateTimeOffset.UtcNow, invertible, changes);
        try
        {
            string json = JsonSerializer.Serialize(journal, JsonDefaults.Options);
            await AtomicFile.WriteAllTextAsync(_paths.ProjectOpsFile(_sessionId, commitId), json, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or JsonException) { /* .flp fallback stays available */ }
    }

    /// <summary>Load a commit's <c>{commitId}.ops.json</c>, or null when absent/unreadable.</summary>
    private async Task<CommitJournal?> ReadOpsJournalAsync(string commitId, CancellationToken ct)
    {
        string path = _paths.ProjectOpsFile(_sessionId, commitId);
        if (!File.Exists(path)) return null;
        try
        {
            await using FileStream stream = File.OpenRead(path);
            return await JsonSerializer
                .DeserializeAsync<CommitJournal>(stream, JsonDefaults.Options, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is JsonException or IOException) { return null; }
    }

    /// <summary>Replay <paramref name="commit"/>'s journal: undo applies each op's OLD value in REVERSE
    /// order; redo applies each op's NEW value FORWARD. Returns true only when the WHOLE journal replayed
    /// cleanly; a missing/invertible:false journal, an absent registry, or ANY apply error returns false so
    /// the caller uses the authoritative <c>.flp</c> (which fully overwrites live state, so a partially
    /// applied replay is wiped — FL is never left half-inverted).</summary>
    private async Task<bool> TryReplayJournalAsync(ProjectCommit commit, bool undo, CancellationToken ct)
    {
        if (_registry is null) return false;
        CommitJournal? journal = await ReadOpsJournalAsync(commit.Id, ct).ConfigureAwait(false);
        if (journal is null || !journal.Invertible || journal.Ops.Count == 0) return false;

        // Bulk ops are one record per element, so ordered replay is element-exact.
        IEnumerable<ChangeRecord> ordered = undo
            ? journal.Ops.OrderByDescending(o => o.Seq)
            : journal.Ops.OrderBy(o => o.Seq);
        try
        {
            foreach (ChangeRecord rec in ordered)
            {
                if (!_registry.TryGet(rec.Op, out IInverseOp inv)) return false;   // guarded by Invertible
                IReadOnlyDictionary<string, object?>? value = undo ? rec.Old : rec.New;
                await inv.ApplyAsync(_fl, rec.Target, value, ct).ConfigureAwait(false);
            }
            return true;
        }
        catch { return false; }
    }

    /// <summary>Advance HEAD to <paramref name="newHeadId"/> and persist the index (no .flp touch). Used by
    /// the granular undo/redo path, which restores state via inverse ops rather than reopening a project.</summary>
    private async Task MoveHeadAsync(string newHeadId, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _headId = newHeadId;
            _recovery = null;
            await SaveIndexAsync(ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    // ── crash recovery detection ──────────────────────────────────────────────────────────────────
    private async Task DetectRecoveryAsync(CancellationToken ct)
    {
        try
        {
            ProjectCommit? latest = _commits.LastOrDefault();
            if (latest is null || !File.Exists(latest.FlpBackupPath)) return;
            if (!await _fl.IsAvailableAsync(ct).ConfigureAwait(false)) return;

            (string? path, bool untitled) = ParseProjectInfo(await _fl.GetProjectInfoAsync(ct).ConfigureAwait(false));

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
        try { return ParseProjectInfo(await _fl.GetProjectInfoAsync(ct).ConfigureAwait(false)).Path; }
        catch { return null; }
    }

    /// <summary>Parses the multi-line string from <see cref="INativeFlControl.GetProjectInfoAsync"/>
    /// ("Title: …\nPath: …\nSaved: …") into the on-disk path (null when untitled) and an untitled flag.</summary>
    private static (string? Path, bool Untitled) ParseProjectInfo(string info)
    {
        string? path = null;
        bool untitled = false;
        foreach (string raw in info.Split('\n'))
        {
            string line = raw.Trim();
            if (line.StartsWith("Path:", StringComparison.OrdinalIgnoreCase))
            {
                string value = line[5..].Trim();
                path = (value.Length == 0 || value == "(none)") ? null : value;
            }
            else if (line.StartsWith("Saved:", StringComparison.OrdinalIgnoreCase))
            {
                untitled = line.Contains("no", StringComparison.OrdinalIgnoreCase);
            }
        }
        if (path is null) untitled = true;
        return (path, untitled);
    }

    // ── index persistence ─────────────────────────────────────────────────────────────────────────
    private async Task<ProjectVersionIndex> ReadIndexAsync(string sessionId, CancellationToken ct)
    {
        string path = _paths.ProjectVersionIndexFile(sessionId);
        if (!File.Exists(path)) return new ProjectVersionIndex();
        try
        {
            await using FileStream stream = File.OpenRead(path);
            return await JsonSerializer
                .DeserializeAsync<ProjectVersionIndex>(stream, JsonDefaults.Options, ct)
                .ConfigureAwait(false) ?? new ProjectVersionIndex();
        }
        catch (Exception ex) when (ex is JsonException or IOException) { return new ProjectVersionIndex(); }
    }

    private async Task SaveIndexAsync(CancellationToken ct)
    {
        var idx = new ProjectVersionIndex
        {
            SchemaVersion = 1,
            Commits = _commits.ToList(),
            HeadId = _headId,
        };
        string json = JsonSerializer.Serialize(idx, JsonDefaults.Options);
        await AtomicFile.WriteAllTextAsync(_paths.ProjectVersionIndexFile(_sessionId), json, ct).ConfigureAwait(false);
    }

    private void Raise(ProjectVersionChangeKind kind, ProjectCommit? commit) =>
        Changed?.Invoke(this, new ProjectVersionChanged(kind, commit));

    /// <summary>On-disk shape of a session's <c>index.json</c>: the commit DAG + HEAD + schema.</summary>
    private sealed class ProjectVersionIndex
    {
        public int SchemaVersion { get; set; } = 1;
        public List<ProjectCommit> Commits { get; set; } = new();
        public string? HeadId { get; set; }
    }
}
