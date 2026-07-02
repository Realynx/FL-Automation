using FruityLink.Core.Configuration;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace FruityLink.Llm;

/// <summary>
/// Builds a Semantic Kernel <see cref="Kernel"/> (and bare <see cref="IChatCompletionService"/>)
/// pointed at the FL Automate AI gateway described by <see cref="AccountSettings"/>. Construction
/// is purely local — no network call is made — so callers can build a kernel even while offline or
/// logged out; auth failures surface on the first turn.
/// </summary>
public interface IChatKernelFactory
{
    /// <summary>Builds a <see cref="Kernel"/> wired to the gateway (endpoint <c>{gateway}/v1</c>,
    /// model <see cref="AccountSettings.ModelOrDefault"/>). The Bearer token is attached
    /// per-request by the factory's auth handler, not here.</summary>
    Kernel CreateKernel(AccountSettings settings);

    /// <summary>Convenience for callers that only need chat completion (no plugins): builds a
    /// kernel and returns its <see cref="IChatCompletionService"/>.</summary>
    IChatCompletionService CreateChatService(AccountSettings settings);
}
