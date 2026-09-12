namespace FruityLink.Knowledge.Tests;

/// <summary>
/// Shared fixture for DB-backed knowledge tests: a Guid-named temp work directory holding the
/// SQLite database, a <see cref="KnowledgeService"/> factory over that database, and best-effort
/// cleanup on dispose.
/// </summary>
public abstract class KnowledgeDbTestBase : IDisposable
{
    protected readonly string _dbPath;
    protected readonly string _workDir;

    protected KnowledgeDbTestBase(string workDirPrefix, string dbFileName)
    {
        _workDir = Path.Combine(Path.GetTempPath(), workDirPrefix, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workDir);
        _dbPath = Path.Combine(_workDir, dbFileName);
    }

    private protected KnowledgeService CreateService(
        FakeEmbeddingClient? embeddings = null, HttpClient? httpClient = null)
        => new(
            embeddings ?? new FakeEmbeddingClient(),
            new SqliteVectorStore(_dbPath),
            new SourceTextExtractor(),
            httpClient ?? new HttpClient());

    protected string WriteTextFile(string name, string content)
    {
        string path = Path.Combine(_workDir, name);
        File.WriteAllText(path, content);
        return path;
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(_workDir))
                Directory.Delete(_workDir, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup of the temp directory.
        }
    }
}
