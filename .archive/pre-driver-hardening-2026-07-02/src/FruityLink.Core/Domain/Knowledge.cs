namespace FruityLink.Core.Domain;

/// <summary>A registered knowledge source (web page or local file) for the RAG index.</summary>
/// <param name="Id">Stable source id.</param>
/// <param name="Uri">URL or file path.</param>
/// <param name="Title">Display title.</param>
/// <param name="AddedAt">When it was ingested.</param>
/// <param name="ChunkCount">Number of chunks produced.</param>
public sealed record KnowledgeSource(
    string Id,
    string Uri,
    string Title,
    DateTimeOffset AddedAt,
    int ChunkCount);

/// <summary>A retrieved chunk of source text with its relevance score.</summary>
/// <param name="SourceId">Owning source id.</param>
/// <param name="SourceTitle">Owning source title (for citation).</param>
/// <param name="Text">The chunk text.</param>
/// <param name="Score">Similarity score (higher = more relevant).</param>
public sealed record KnowledgeHit(string SourceId, string SourceTitle, string Text, double Score);
