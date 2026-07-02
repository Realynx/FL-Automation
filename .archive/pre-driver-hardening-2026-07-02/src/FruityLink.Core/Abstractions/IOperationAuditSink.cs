using FruityLink.Core.Domain;

namespace FruityLink.Core.Abstractions;

/// <summary>
/// Collects the FL Studio operations the agent performs so they can be attached to a
/// version-tree checkpoint and described/reverted later.
/// </summary>
public interface IOperationAuditSink
{
    /// <summary>Records an operation that was just performed.</summary>
    void Record(FlOperation operation);

    /// <summary>Returns and clears the operations accumulated since the last drain.</summary>
    IReadOnlyList<FlOperation> Drain();
}
