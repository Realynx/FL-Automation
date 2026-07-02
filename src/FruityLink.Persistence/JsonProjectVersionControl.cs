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
    private readonly SemaphoreSlim _gate = new(1, 1);

    // Reads are lock-free: mutations always REPLACE these references (never mutate in place), and a
    // reference read/write is atomic in .NET, so History/Head can be read from the UI thread safely.
    private volatile List<ProjectCommit> _commits = new();
    private volatile string? _headId;
    private volatile ProjectCommit? _recovery;
    private string _sessionId = string.Empty;

    /// <summary>Creates a project version-control store rooted at <paramref name="paths"/>, backing up and
    /// restoring through <paramref name="fl"/> (the host's single FL bridge).</summary>
    public JsonProjectVersionControl(StoragePaths paths, INativeFlControl fl)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(fl);
        _paths = paths;
        _fl = fl;
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
            ProjectVersionIndex idx = await ReadIndexAsync(sessionId, ct).ConfigureAwait(false);
            _commits = idx.Commits ?? new List<ProjectCommit>();
            _headId = idx.HeadId;
            _recovery = null;
        }
        finally { _gate.Release(); }

        await DetectRecoveryAsync(ct).ConfigureAwait(false);   // best-effort, off the lock
        Raise(ProjectVersionChangeKind.SessionOpened, Head);
    }

    /// <inheritdoc />
    public async Task<ProjectCommit?> CommitAsync(
        string? label = null,
        string? chatNodeId = null,
        IReadOnlyList<string>? operations = null,
        CommitTrigger trigger = CommitTrigger.Manual,
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
    public Task<ProjectCommit?> UndoAsync(CancellationToken ct = default)
    {
        ProjectCommit? head = Head;
        if (head?.ParentId is null) return Task.FromResult<ProjectCommit?>(null);
        ProjectCommit? parent = _commits.FirstOrDefault(c => c.Id == head.ParentId);
        return parent is null
            ? Task.FromResult<ProjectCommit?>(null)
            : RestoreCoreAsync(parent, ProjectVersionChangeKind.Undone, ct);
    }

    /// <inheritdoc />
    public Task<ProjectCommit?> RedoAsync(CancellationToken ct = default)
    {
        ProjectCommit? head = Head;
        if (head is null) return Task.FromResult<ProjectCommit?>(null);
        ProjectCommit? child = _commits
            .Where(c => c.ParentId == head.Id)
            .OrderByDescending(c => c.CreatedAt)
            .FirstOrDefault();
        return child is null
            ? Task.FromResult<ProjectCommit?>(null)
            : RestoreCoreAsync(child, ProjectVersionChangeKind.Redone, ct);
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
