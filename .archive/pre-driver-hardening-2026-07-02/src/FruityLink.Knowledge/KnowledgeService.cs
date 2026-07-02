using FruityLink.Core.Abstractions;
using FruityLink.Core.Domain;

namespace FruityLink.Knowledge;

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

    /// <summary>Shared chunk → embed → persist pipeline for both web and file sources.</summary>
    private async Task<KnowledgeSource> IngestAsync(string uri, string title, string text, CancellationToken ct)
    {
        // The chunker already trims and drops blanks; filter once more so a malformed source can
        // never send an empty/whitespace chunk to the embedding API (a guaranteed failure for many backends).
        IReadOnlyList<string> chunkTexts = _chunker.Chunk(text)
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .ToList();

        string sourceId = NewSourceId();
        var source = new KnowledgeSource(sourceId, uri, title, DateTimeOffset.UtcNow, chunkTexts.Count);

        if (chunkTexts.Count == 0)
        {
            // Nothing to embed; still register the (empty) source so it appears in listings.
            _store.UpsertSource(source);
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

        _store.UpsertSource(source);
        _store.UpsertChunks(sourceId, records);
        return source;
    }

    private static string NewSourceId() => Guid.NewGuid().ToString("N");
}
