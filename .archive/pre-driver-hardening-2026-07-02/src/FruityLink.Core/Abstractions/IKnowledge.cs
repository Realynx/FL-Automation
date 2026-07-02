using FruityLink.Core.Domain;

namespace FruityLink.Core.Abstractions;

/// <summary>Retrieves relevant chunks from the RAG knowledge index for a query.</summary>
public interface IKnowledgeRetriever
{
    /// <summary>Returns up to <paramref name="topK"/> most relevant chunks for the query.</summary>
    Task<IReadOnlyList<KnowledgeHit>> SearchAsync(string query, int topK = 5, CancellationToken ct = default);
}

/// <summary>Ingests and manages RAG knowledge sources (web pages and local files).</summary>
public interface IKnowledgeIngestor
{
    /// <summary>Fetches, extracts, chunks, embeds, and indexes a web URL.</summary>
    Task<KnowledgeSource> AddWebSourceAsync(string url, CancellationToken ct = default);

    /// <summary>Extracts, chunks, embeds, and indexes a local file (HTML/PDF/MD/text).</summary>
    Task<KnowledgeSource> AddFileSourceAsync(string path, CancellationToken ct = default);

    /// <summary>Lists indexed sources.</summary>
    Task<IReadOnlyList<KnowledgeSource>> ListSourcesAsync(CancellationToken ct = default);

    /// <summary>Removes a source and its chunks from the index.</summary>
    Task RemoveSourceAsync(string sourceId, CancellationToken ct = default);
}
