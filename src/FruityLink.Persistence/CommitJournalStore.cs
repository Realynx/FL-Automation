using System.Text.Json;
using FruityLink.Core.Abstractions;
using FruityLink.Core.Domain;

namespace FruityLink.Persistence;

/// <summary>
/// Owns a session's per-commit inverse journals (<c>{commitId}.ops.json</c>) for
/// <see cref="JsonProjectVersionControl"/>: writes one journal at commit time and replays it through the
/// <see cref="IInverseOpRegistry"/> when that commit is undone/redone. Journals are an optimization over
/// the authoritative <c>.flp</c> backups, so every failure path reports "not replayable" rather than throwing.
/// </summary>
internal sealed class CommitJournalStore
{
    private readonly StoragePaths _paths;
    private readonly INativeFlControl _fl;
    private readonly IInverseOpRegistry? _registry;

    public CommitJournalStore(StoragePaths paths, INativeFlControl fl, IInverseOpRegistry? registry)
    {
        _paths = paths;
        _fl = fl;
        _registry = registry;
    }

    /// <summary>Write <c>{commitId}.ops.json</c> when there are records to record. <c>invertible</c> is true
    /// only when a registry is wired and EVERY record's op is registered — otherwise the file marks itself
    /// non-invertible so undo/redo falls back to the <c>.flp</c>. Best-effort: the journal is an
    /// optimization, so a write failure just leaves the commit on the <c>.flp</c> path.</summary>
    public async Task WriteAsync(string sessionId, string commitId, IReadOnlyList<ChangeRecord>? changes, CancellationToken ct)
    {
        if (changes is not { Count: > 0 }) return;   // nothing granular this turn ⇒ .flp-only (as before)
        bool invertible = _registry is not null && changes.All(_registry.CanInvert);
        var journal = new CommitJournal(1, commitId, DateTimeOffset.UtcNow, invertible, changes);
        try
        {
            await JsonFile.WriteAsync(_paths.ProjectOpsFile(sessionId, commitId), journal, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or JsonException) { /* .flp fallback stays available */ }
    }

    /// <summary>Replay <paramref name="commit"/>'s journal: undo applies each op's OLD value in REVERSE
    /// order; redo applies each op's NEW value FORWARD. Returns true only when the WHOLE journal replayed
    /// cleanly; a missing/invertible:false journal, an absent registry, or ANY apply error returns false so
    /// the caller uses the authoritative <c>.flp</c> (which fully overwrites live state, so a partially
    /// applied replay is wiped — FL is never left half-inverted).</summary>
    public async Task<bool> TryReplayAsync(string sessionId, ProjectCommit commit, bool undo, CancellationToken ct)
    {
        if (_registry is null) return false;
        CommitJournal? journal = await ReadAsync(sessionId, commit.Id, ct).ConfigureAwait(false);
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

    /// <summary>Load a commit's <c>{commitId}.ops.json</c>, or null when absent/unreadable.</summary>
    private Task<CommitJournal?> ReadAsync(string sessionId, string commitId, CancellationToken ct) =>
        JsonFile.TryReadAsync<CommitJournal>(_paths.ProjectOpsFile(sessionId, commitId), ct, swallowIoErrors: true);
}
