namespace FruityLink.Core.Abstractions;

/// <summary>Optional lifetime policy for callers that must acknowledge actual native completion.
/// It exposes no native addresses or raw commands.</summary>
public interface IFlNativeCompletionScope
{
    /// <summary>Requires in-process native calls started in the current asynchronous execution context
    /// to finish before their managed tasks complete, including timeout and cancellation paths.
    /// Dispose the scope after awaiting the operation. Scopes nest and do not change other callers'
    /// timeout policy. Native code cannot be interrupted, so completion can wait indefinitely.</summary>
    IDisposable RequireNativeCompletion();
}
