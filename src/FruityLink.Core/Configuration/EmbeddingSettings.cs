namespace FruityLink.Core.Configuration;

/// <summary>
/// Settings for the RAG embeddings backend. STANDALONE by design: chat goes through the FL Automate
/// gateway (see <see cref="AccountSettings"/>), but embeddings stay a local OpenAI-compatible
/// endpoint (default: an Ollama daemon). Embeddings-through-the-gateway is out of scope for now.
/// </summary>
/// <param name="Endpoint">OpenAI-compatible base URL (e.g. http://localhost:11434/v1 for Ollama).</param>
/// <param name="Model">Embedding model id (e.g. "nomic-embed-text").</param>
/// <param name="Dimensions">Vector dimensionality (nomic-embed-text = 768). Stored with the index.</param>
public sealed record EmbeddingSettings(
    string Endpoint = "http://localhost:11434/v1",
    string Model = "nomic-embed-text",
    int Dimensions = 768);
