using System.Security.Cryptography;
using System.Text;
using FruityLink.Core.Abstractions;
using FruityLink.Core.Domain;

namespace FruityLink.Knowledge;

/// <summary>
/// Outcome of an incremental text ingest. <see cref="Reused"/> is true when the source's content
/// was unchanged since the last build and its existing embeddings were kept (no embedding call).
/// </summary>
/// <param name="SourceId">Stable source id (derived from the URI).</param>
/// <param name="Uri">Source URI.</param>
/// <param name="Title">Source title.</param>
/// <param name="ChunkCount">Number of chunks now indexed for the source.</param>
/// <param name="Reused">True when embeddings were reused (content unchanged); false when embedded.</param>
public sealed record TextIngestResult(
    string SourceId,
    string Uri,
    string Title,
    int ChunkCount,
    bool Reused);

/// <summary>
/// RAG knowledge engine: ingests web and file sources, embeds their chunks, persists them to a
/// SQLite-backed vector store, and answers similarity queries via brute-force cosine search.
/// </summary>
public sealed class KnowledgeService : IKnowledgeIngestor, IKnowledgeRetriever
{
    private readonly IEmbeddingClient _embeddings;
    private readonly SqliteVectorStore _store;
    private readonly ISourceTextExtractor _extractor;
    private readonly HttpClient _httpClient;
    private readonly TextChunker _chunker;

    /// <summary>
    /// Creates the service. Intended composition root wiring (DI):
    /// register <see cref="IEmbeddingClient"/> (from the Llm layer), a database path string, and
    /// an <see cref="HttpClient"/> (e.g. via <c>IHttpClientFactory</c>), then resolve
    /// <see cref="KnowledgeService"/> as both <see cref="IKnowledgeIngestor"/> and
    /// <see cref="IKnowledgeRetriever"/>.
    /// </summary>
    /// <param name="embeddings">Embedding backend (consumed, not implemented here).</param>
    /// <param name="dbPath">Filesystem path to the SQLite knowledge database.</param>
    /// <param name="httpClient">HTTP client used to fetch web sources.</param>
    public KnowledgeService(IEmbeddingClient embeddings, string dbPath, HttpClient httpClient)
        : this(embeddings, new SqliteVectorStore(dbPath), new SourceTextExtractor(), httpClient)
    {
    }

    /// <summary>
    /// Full-control constructor used by tests and advanced composition: supply the store and
    /// extractor explicitly.
    /// </summary>
    internal KnowledgeService(
        IEmbeddingClient embeddings,
        SqliteVectorStore store,
        ISourceTextExtractor extractor,
        HttpClient httpClient,
        TextChunker? chunker = null)
    {
        _embeddings = embeddings ?? throw new ArgumentNullException(nameof(embeddings));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _extractor = extractor ?? throw new ArgumentNullException(nameof(extractor));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _chunker = chunker ?? new TextChunker();
        _store.CreateSchema();
    }

    /// <inheritdoc />
    public async Task<KnowledgeSource> AddWebSourceAsync(string url, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);

        using HttpResponseMessage response = await _httpClient.GetAsync(url, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        string html = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        (string text, string? title) = await _extractor.ExtractHtmlAsync(html, ct).ConfigureAwait(false);

        string displayTitle = string.IsNullOrWhiteSpace(title) ? url : title!;
        return await IngestAsync(uri: url, title: displayTitle, text: text, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<KnowledgeSource> AddFileSourceAsync(string path, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        string text = await _extractor.ExtractFileAsync(path, ct).ConfigureAwait(false);
        string title = Path.GetFileName(path);
        return await IngestAsync(uri: path, title: title, text: text, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Ingests pre-extracted text (already plain/markdown) under an explicit citation URI and
    /// title — the chunk → embed → persist pipeline, skipping HTML/PDF extraction. Used by the
    /// offline manual-corpus builder.
    ///
    /// <para>INCREMENTAL: the source id is derived deterministically from <paramref name="uri"/>
    /// and the text is fingerprinted. If a source with the same URI and identical content is
    /// already indexed, its embeddings are REUSED and no embedding call is made
    /// (<see cref="TextIngestResult.Reused"/> = true). This makes re-builds cheap (only changed
    /// pages are re-embedded) and makes a long first build resumable after a failure.</para>
    /// </summary>
    /// <param name="uri">Canonical source URI recorded for citation (e.g. the online manual URL).</param>
    /// <param name="title">Human-readable source title.</param>
    /// <param name="text">The source text to chunk and embed.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<TextIngestResult> AddTextSourceAsync(
        string uri, string title, string text, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uri);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        text ??= string.Empty;

        string sourceId = StableSourceId(uri);
        string hash = ContentHash(text);

        // Skip unchanged content (a non-empty hash match) — reuse existing embeddings.
        if (_store.GetSourceMeta(sourceId) is { } meta && hash.Length > 0 && meta.ContentHash == hash)
            return new TextIngestResult(sourceId, uri, title, meta.ChunkCount, Reused: true);

        KnowledgeSource source = await EmbedAndPersistAsync(sourceId, uri, title, text, hash, ct)
            .ConfigureAwait(false);
        return new TextIngestResult(sourceId, uri, title, source.ChunkCount, Reused: false);
    }

    /// <summary>
    /// Removes every indexed source whose URI is NOT in <paramref name="keepUris"/> — used after a
    /// re-crawl to drop pages that no longer exist upstream. Returns the number pruned. Intended
    /// for a DEDICATED corpus database (it will delete any source not in the keep set).
    /// </summary>
    public Task<int> PruneSourcesNotInAsync(IEnumerable<string> keepUris, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(keepUris);
        var keepIds = new HashSet<string>(keepUris.Select(StableSourceId), StringComparer.Ordinal);

        int pruned = 0;
        foreach (KnowledgeSource source in _store.ListSources())
        {
            ct.ThrowIfCancellationRequested();
            if (!keepIds.Contains(source.Id) && _store.DeleteSource(source.Id))
                pruned++;
        }
        return Task.FromResult(pruned);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<KnowledgeSource>> ListSourcesAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(_store.ListSources());
    }

    /// <inheritdoc />
    public Task RemoveSourceAsync(string sourceId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        ct.ThrowIfCancellationRequested();
        _store.DeleteSource(sourceId);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<KnowledgeHit>> SearchAsync(string query, int topK = 5, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query) || topK <= 0)
            return Array.Empty<KnowledgeHit>();

        float[] queryVector = await _embeddings.EmbedAsync(query, ct).ConfigureAwait(false);
        if (queryVector.Length == 0)
            return Array.Empty<KnowledgeHit>();

        var scored = new List<KnowledgeHit>();
        foreach (ChunkVector chunk in _store.GetAllChunkVectors())
        {
            ct.ThrowIfCancellationRequested();

            // Skip vectors whose dimension does not match the query embedding.
            if (chunk.Vector.Length != queryVector.Length)
                continue;

            double score = VectorMath.Cosine(queryVector, chunk.Vector);
            scored.Add(new KnowledgeHit(chunk.SourceId, chunk.SourceTitle, chunk.Text, score));
        }

        if (scored.Count == 0)
            return Array.Empty<KnowledgeHit>();

        return scored
            .OrderByDescending(hit => hit.Score)
            .Take(topK)
            .ToList();
    }

    /// <summary>Shared chunk → embed → persist pipeline for web/file sources (random source id).</summary>
    private Task<KnowledgeSource> IngestAsync(string uri, string title, string text, CancellationToken ct)
        => EmbedAndPersistAsync(NewSourceId(), uri, title, text, contentHash: "", ct);

    /// <summary>
    /// Chunk → embed → persist for an EXPLICIT source id and content hash. Replaces any existing
    /// chunks for the id (idempotent), so a changed source is re-embedded cleanly in place.
    /// </summary>
    private async Task<KnowledgeSource> EmbedAndPersistAsync(
        string sourceId, string uri, string title, string text, string contentHash, CancellationToken ct)
    {
        // The chunker already trims and drops blanks; filter once more so a malformed source can
        // never send an empty/whitespace chunk to the embedding API (a guaranteed failure for many backends).
        IReadOnlyList<string> chunkTexts = _chunker.Chunk(text)
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .ToList();

        var source = new KnowledgeSource(sourceId, uri, title, DateTimeOffset.UtcNow, chunkTexts.Count);

        if (chunkTexts.Count == 0)
        {
            // Nothing to embed; still register the (empty) source so it appears in listings.
            _store.UpsertSource(source, contentHash);
            _store.UpsertChunks(sourceId, Array.Empty<ChunkRecord>());
            return source;
        }

        IReadOnlyList<float[]> vectors = await _embeddings.EmbedAsync(chunkTexts, ct).ConfigureAwait(false);
        if (vectors.Count != chunkTexts.Count)
        {
            throw new InvalidOperationException(
                $"Embedding client returned {vectors.Count} vectors for {chunkTexts.Count} chunks.");
        }

        var records = new List<ChunkRecord>(chunkTexts.Count);
        for (int i = 0; i < chunkTexts.Count; i++)
            records.Add(new ChunkRecord(Id: $"{sourceId}:{i}", Ordinal: i, Text: chunkTexts[i], Vector: vectors[i]));

        _store.UpsertSource(source, contentHash);
        _store.UpsertChunks(sourceId, records);
        return source;
    }

    private static string NewSourceId() => Guid.NewGuid().ToString("N");

    /// <summary>A deterministic source id derived from the URI (stable across rebuilds).</summary>
    private static string StableSourceId(string uri) => Sha256Hex(uri);

    /// <summary>A content fingerprint of the source text (empty string for empty text).</summary>
    private static string ContentHash(string text) => text.Length == 0 ? "" : Sha256Hex(text);

    /// <summary>
    /// Lowercase hex SHA-256 of the UTF-8 bytes of <paramref name="value"/>. The casing and
    /// encoding are load-bearing: the result is persisted as the stable source id and in
    /// <c>sources.content_hash</c>.
    /// </summary>
    private static string Sha256Hex(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
