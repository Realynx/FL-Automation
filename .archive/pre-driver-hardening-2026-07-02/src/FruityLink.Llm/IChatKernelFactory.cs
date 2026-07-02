using FruityLink.Core.Configuration;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace FruityLink.Llm;

/// <summary>
/// Builds a Semantic Kernel <see cref="Kernel"/> (and bare <see cref="IChatCompletionService"/>)
/// from <see cref="LlmSettings"/>. Construction is purely local — no network call is made — so
/// callers can build a kernel even while the backend is offline.
/// </summary>
public interface IChatKernelFactory
{
    /// <summary>
    /// Builds a <see cref="Kernel"/> wired to the chat backend described by <paramref name="settings"/>.
    /// </summary>
    /// <param name="settings">The chat connection (backend, endpoint, model, optional deployment).</param>
    /// <param name="apiKey">
    /// Resolved API key (the secret, not the <see cref="LlmSettings.ApiKeyRef"/>). For Ollama a
    /// placeholder is substituted when null, since some connector overloads require a non-empty key.
    /// </param>
    Kernel CreateKernel(LlmSettings settings, string? apiKey = null);

    /// <summary>
    /// Convenience for callers that only need chat completion (no plugins): builds a kernel and
    /// returns its <see cref="IChatCompletionService"/>.
    /// </summary>
    IChatCompletionService CreateChatService(LlmSettings settings, string? apiKey = null);
}
