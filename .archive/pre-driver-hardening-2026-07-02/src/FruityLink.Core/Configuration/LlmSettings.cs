namespace FruityLink.Core.Configuration;

/// <summary>Selectable LLM / embeddings backends. All are reached through one factory.</summary>
public enum LlmBackendKind
{
    /// <summary>Ollama's OpenAI-compatible API. May be a local daemon or a cloud proxy.</summary>
    Ollama,
    OpenAI,
    AzureOpenAI,
    Anthropic,
}

/// <summary>
/// Connection settings for a chat backend. The same shape works for a local Ollama
/// daemon and a cloud-proxied Ollama endpoint — only the labels differ.
/// </summary>
/// <param name="Backend">Which provider to use.</param>
/// <param name="Endpoint">Base URL (e.g. http://localhost:11434/v1 for Ollama).</param>
/// <param name="Model">Model id (e.g. "qwen2.5", "gpt-4o").</param>
/// <param name="ApiKeyRef">Logical key name resolved via <c>ISecretStore</c> (not the secret itself).</param>
/// <param name="Deployment">Azure OpenAI deployment name (Azure only).</param>
public sealed record LlmSettings(
    LlmBackendKind Backend = LlmBackendKind.Ollama,
    string Endpoint = "http://localhost:11434/v1",
    string Model = "qwen2.5",
    string? ApiKeyRef = null,
    string? Deployment = null);

/// <summary>
/// Settings for the RAG embeddings backend. By default it inherits the selected chat
/// backend (e.g. Ollama's <c>nomic-embed-text</c>), but can be pointed at any provider.
/// </summary>
/// <param name="InheritFromChat">When true, use the chat backend's connection for embeddings.</param>
/// <param name="Override">Connection to use when not inheriting.</param>
/// <param name="Model">Embedding model id (e.g. "nomic-embed-text").</param>
/// <param name="Dimensions">Vector dimensionality (nomic-embed-text = 768). Stored with the index.</param>
public sealed record EmbeddingSettings(
    bool InheritFromChat = true,
    LlmSettings? Override = null,
    string Model = "nomic-embed-text",
    int Dimensions = 768)
{
    /// <summary>
    /// Resolves the effective connection for embeddings, falling back to the chat
    /// backend's endpoint/key when inheriting. The embedding <see cref="Model"/> always wins.
    /// </summary>
    public LlmSettings Resolve(LlmSettings chat)
    {
        LlmSettings baseline = InheritFromChat || Override is null ? chat : Override;
        return baseline with { Model = Model };
    }
}
