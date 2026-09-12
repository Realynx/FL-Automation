namespace FruityLink.Core.Abstractions;

/// <summary>
/// Produces embedding vectors for text. Implemented in the Llm layer against an
/// OpenAI-compatible <c>/embeddings</c> endpoint (Ollama by default), and consumed by
/// the Knowledge/RAG layer. Provider-agnostic by design so the embeddings backend can
/// differ from, or inherit, the chat backend.
/// </summary>
public interface IEmbeddingClient
{
    /// <summary>Embeds a single input.</summary>
    Task<float[]> EmbedAsync(string input, CancellationToken ct = default);

    /// <summary>Embeds a batch of inputs, preserving order.</summary>
    Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken ct = default);
}
