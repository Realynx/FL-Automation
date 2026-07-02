using FruityLink.Core.Configuration;

namespace FruityLink.Llm;

/// <summary>
/// Lightweight reachability probe for an LLM backend, used by the UI to surface a clear
/// "backend unreachable" state. Implementations must be tolerant: they never throw, returning
/// <c>false</c> for any failure.
/// </summary>
public interface ILlmConnectivity
{
    /// <summary>
    /// Returns <c>true</c> when the backend responds successfully to a cheap probe, otherwise
    /// <c>false</c>. Never throws — connection, timeout, and HTTP errors all map to <c>false</c>.
    /// </summary>
    Task<bool> IsReachableAsync(LlmSettings settings, CancellationToken ct = default);
}
