using FruityLink.Core.Domain;

namespace FruityLink.Persistence;

/// <summary>
/// Reads and writes a session's project-version <c>index.json</c> (the commit DAG + HEAD) for
/// <see cref="JsonProjectVersionControl"/>. A missing or unreadable index yields an empty history.
/// </summary>
internal sealed class ProjectVersionIndexStore
{
    private readonly StoragePaths _paths;

    public ProjectVersionIndexStore(StoragePaths paths) => _paths = paths;

    /// <summary>Loads the session's commit list + HEAD, or an empty history when absent/unreadable.</summary>
    public async Task<(List<ProjectCommit> Commits, string? HeadId)> ReadAsync(string sessionId, CancellationToken ct)
    {
        ProjectVersionIndex idx = await JsonFile
            .TryReadAsync<ProjectVersionIndex>(_paths.ProjectVersionIndexFile(sessionId), ct, swallowIoErrors: true)
            .ConfigureAwait(false) ?? new ProjectVersionIndex();
        return (idx.Commits ?? new List<ProjectCommit>(), idx.HeadId);
    }

    /// <summary>Atomically rewrites the session's <c>index.json</c> with the given commits + HEAD.</summary>
    public Task SaveAsync(string sessionId, List<ProjectCommit> commits, string? headId, CancellationToken ct)
    {
        var idx = new ProjectVersionIndex
        {
            SchemaVersion = 1,
            Commits = commits.ToList(),
            HeadId = headId,
        };
        return JsonFile.WriteAsync(_paths.ProjectVersionIndexFile(sessionId), idx, ct);
    }

    /// <summary>On-disk shape of a session's <c>index.json</c>: the commit DAG + HEAD + schema.</summary>
    private sealed class ProjectVersionIndex
    {
        public int SchemaVersion { get; set; } = 1;
        public List<ProjectCommit> Commits { get; set; } = new();
        public string? HeadId { get; set; }
    }
}
