using FruityLink.Core.Domain;
using Microsoft.Data.Sqlite;

namespace FruityLink.Knowledge;

/// <summary>A chunk row paired with its embedding vector, as streamed from the store.</summary>
/// <param name="SourceId">Owning source id.</param>
/// <param name="SourceTitle">Owning source title (for citation).</param>
/// <param name="Text">Chunk text.</param>
/// <param name="Vector">Embedding vector (decoded from the stored BLOB).</param>
internal sealed record ChunkVector(string SourceId, string SourceTitle, string Text, float[] Vector);

/// <summary>A chunk to be persisted, prior to vector encoding.</summary>
/// <param name="Id">Chunk primary key.</param>
/// <param name="Ordinal">Zero-based position within the source.</param>
/// <param name="Text">Chunk text.</param>
/// <param name="Vector">Embedding vector.</param>
internal sealed record ChunkRecord(string Id, int Ordinal, string Text, float[] Vector);

/// <summary>
/// Plain SQLite-backed store for knowledge sources and their embedded chunks. Vectors are
/// persisted as little-endian <c>float[]</c> BLOBs; similarity search is performed in C# by
/// the caller. Isolated behind this internal class so a dedicated vector store can replace it
/// later without touching the service.
/// </summary>
internal sealed class SqliteVectorStore
{
    private readonly string _connectionString;

    /// <summary>Creates a store over the SQLite database at <paramref name="dbPath"/>.</summary>
    /// <param name="dbPath">
    /// Filesystem path to the SQLite database file. The file is created on first use.
    /// </param>
    public SqliteVectorStore(string dbPath)
    {
        if (string.IsNullOrWhiteSpace(dbPath))
            throw new ArgumentException("Database path is required.", nameof(dbPath));

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            // Disable pooling so each connection fully closes on Dispose. Pooled native SQLite
            // handles otherwise keep the test host (and process) from exiting cleanly, and the
            // perf cost is negligible for this app's occasional RAG operations.
            Pooling = false,
        }.ToString();
    }

    /// <summary>Creates the schema if it does not already exist (and upgrades older DBs).</summary>
    public void CreateSchema()
    {
        using SqliteConnection connection = Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS sources(
                id TEXT PRIMARY KEY,
                uri TEXT NOT NULL,
                title TEXT NOT NULL,
                added_at TEXT NOT NULL,
                chunk_count INTEGER NOT NULL,
                content_hash TEXT NOT NULL DEFAULT ''
            );
            CREATE TABLE IF NOT EXISTS chunks(
                id TEXT PRIMARY KEY,
                source_id TEXT NOT NULL,
                ordinal INTEGER NOT NULL,
                text TEXT NOT NULL,
                dim INTEGER NOT NULL,
                vector BLOB NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_chunks_source ON chunks(source_id);
            """;
        command.ExecuteNonQuery();

        // Upgrade a DB created before content_hash existed (idempotent, incremental
        // re-embedding depends on it). SQLite has no "ADD COLUMN IF NOT EXISTS".
        EnsureColumn(connection, "sources", "content_hash", "TEXT NOT NULL DEFAULT ''");
    }

    private static void EnsureColumn(
        SqliteConnection connection, string table, string column, string definition)
    {
        using (SqliteCommand check = connection.CreateCommand())
        {
            check.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = $c;";
            check.Parameters.AddWithValue("$c", column);
            if (Convert.ToInt64(check.ExecuteScalar()) > 0) return;
        }
        using SqliteCommand alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition};";
        alter.ExecuteNonQuery();
    }

    /// <summary>
    /// Inserts or replaces a source row. <paramref name="contentHash"/> records a fingerprint of
    /// the ingested text so re-ingestion can skip unchanged sources (incremental embedding).
    /// </summary>
    public void UpsertSource(KnowledgeSource source, string contentHash = "")
    {
        ArgumentNullException.ThrowIfNull(source);

        using SqliteConnection connection = Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO sources(id, uri, title, added_at, chunk_count, content_hash)
            VALUES($id, $uri, $title, $added_at, $chunk_count, $content_hash)
            ON CONFLICT(id) DO UPDATE SET
                uri = excluded.uri,
                title = excluded.title,
                added_at = excluded.added_at,
                chunk_count = excluded.chunk_count,
                content_hash = excluded.content_hash;
            """;
        command.Parameters.AddWithValue("$id", source.Id);
        command.Parameters.AddWithValue("$uri", source.Uri);
        command.Parameters.AddWithValue("$title", source.Title);
        command.Parameters.AddWithValue("$added_at", source.AddedAt.ToString("O"));
        command.Parameters.AddWithValue("$chunk_count", source.ChunkCount);
        command.Parameters.AddWithValue("$content_hash", contentHash ?? "");
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Returns the stored content hash + chunk count for a source id, or null when the source is
    /// not present. Used to decide whether a re-ingested source's text changed.
    /// </summary>
    public (string ContentHash, int ChunkCount)? GetSourceMeta(string sourceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);

        using SqliteConnection connection = Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT content_hash, chunk_count FROM sources WHERE id = $id;";
        command.Parameters.AddWithValue("$id", sourceId);
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        return (reader.IsDBNull(0) ? "" : reader.GetString(0), reader.GetInt32(1));
    }

    /// <summary>
    /// Inserts (or replaces) a batch of chunks for a source. Existing chunks for the source are
    /// removed first so re-ingestion is idempotent.
    /// </summary>
    public void UpsertChunks(string sourceId, IReadOnlyList<ChunkRecord> chunks)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        ArgumentNullException.ThrowIfNull(chunks);

        using SqliteConnection connection = Open();
        using SqliteTransaction transaction = connection.BeginTransaction();

        DeleteChunksForSource(connection, transaction, sourceId);

        using (SqliteCommand insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText =
                """
                INSERT INTO chunks(id, source_id, ordinal, text, dim, vector)
                VALUES($id, $source_id, $ordinal, $text, $dim, $vector);
                """;
            SqliteParameter id = insert.Parameters.Add("$id", Microsoft.Data.Sqlite.SqliteType.Text);
            insert.Parameters.AddWithValue("$source_id", sourceId);
            SqliteParameter ordinal = insert.Parameters.Add("$ordinal", Microsoft.Data.Sqlite.SqliteType.Integer);
            SqliteParameter text = insert.Parameters.Add("$text", Microsoft.Data.Sqlite.SqliteType.Text);
            SqliteParameter dim = insert.Parameters.Add("$dim", Microsoft.Data.Sqlite.SqliteType.Integer);
            SqliteParameter vector = insert.Parameters.Add("$vector", Microsoft.Data.Sqlite.SqliteType.Blob);

            foreach (ChunkRecord chunk in chunks)
            {
                id.Value = chunk.Id;
                ordinal.Value = chunk.Ordinal;
                text.Value = chunk.Text;
                dim.Value = chunk.Vector.Length;
                vector.Value = EncodeVector(chunk.Vector);
                insert.ExecuteNonQuery();
            }
        }

        transaction.Commit();
    }

    /// <summary>Deletes a source and all of its chunks. Returns <c>true</c> if the source existed.</summary>
    public bool DeleteSource(string sourceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);

        using SqliteConnection connection = Open();
        using SqliteTransaction transaction = connection.BeginTransaction();

        DeleteChunksForSource(connection, transaction, sourceId);

        int affected;
        using (SqliteCommand deleteSource = connection.CreateCommand())
        {
            deleteSource.Transaction = transaction;
            deleteSource.CommandText = "DELETE FROM sources WHERE id = $source_id;";
            deleteSource.Parameters.AddWithValue("$source_id", sourceId);
            affected = deleteSource.ExecuteNonQuery();
        }

        transaction.Commit();
        return affected > 0;
    }

    /// <summary>Deletes every chunk belonging to <paramref name="sourceId"/> within the transaction.</summary>
    private static void DeleteChunksForSource(
        SqliteConnection connection, SqliteTransaction transaction, string sourceId)
    {
        using SqliteCommand delete = connection.CreateCommand();
        delete.Transaction = transaction;
        delete.CommandText = "DELETE FROM chunks WHERE source_id = $source_id;";
        delete.Parameters.AddWithValue("$source_id", sourceId);
        delete.ExecuteNonQuery();
    }

    /// <summary>
    /// Streams every chunk together with its decoded embedding vector, joined to its source
    /// title. Materialised lazily so the whole index need not be held in memory at once.
    /// </summary>
    public IEnumerable<ChunkVector> GetAllChunkVectors()
    {
        using SqliteConnection connection = Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT c.source_id, s.title, c.text, c.dim, c.vector
            FROM chunks c
            JOIN sources s ON s.id = c.source_id
            ORDER BY c.source_id, c.ordinal;
            """;

        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            string sourceId = reader.GetString(0);
            string title = reader.GetString(1);
            string text = reader.GetString(2);
            int dim = reader.GetInt32(3);
            byte[] blob = (byte[])reader[4];
            float[] vector = DecodeVector(blob, dim);
            yield return new ChunkVector(sourceId, title, text, vector);
        }
    }

    /// <summary>Lists all registered sources, newest first.</summary>
    public IReadOnlyList<KnowledgeSource> ListSources()
    {
        var sources = new List<KnowledgeSource>();

        using SqliteConnection connection = Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, uri, title, added_at, chunk_count
            FROM sources
            ORDER BY added_at DESC;
            """;

        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            sources.Add(new KnowledgeSource(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                DateTimeOffset.Parse(reader.GetString(3), null, System.Globalization.DateTimeStyles.RoundtripKind),
                reader.GetInt32(4)));
        }

        return sources;
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    /// <summary>Encodes a vector as a little-endian <c>dim*4</c>-byte BLOB.</summary>
    private static byte[] EncodeVector(float[] vector)
    {
        var bytes = new byte[vector.Length * sizeof(float)];
        System.Buffer.BlockCopy(vector, 0, bytes, 0, bytes.Length);
        // BlockCopy preserves machine byte order; normalise to little-endian for portability.
        if (!BitConverter.IsLittleEndian)
            ReverseFloats(bytes);
        return bytes;
    }

    /// <summary>Decodes a little-endian BLOB of <paramref name="dim"/> floats.</summary>
    private static float[] DecodeVector(byte[] blob, int dim)
    {
        var vector = new float[dim];
        int byteCount = Math.Min(blob.Length, dim * sizeof(float));
        if (BitConverter.IsLittleEndian)
        {
            System.Buffer.BlockCopy(blob, 0, vector, 0, byteCount);
        }
        else
        {
            var copy = (byte[])blob.Clone();
            ReverseFloats(copy);
            System.Buffer.BlockCopy(copy, 0, vector, 0, byteCount);
        }

        return vector;
    }

    private static void ReverseFloats(byte[] bytes)
    {
        for (int i = 0; i + sizeof(float) <= bytes.Length; i += sizeof(float))
            Array.Reverse(bytes, i, sizeof(float));
    }
}
