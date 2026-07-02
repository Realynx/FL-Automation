using System.Collections.Concurrent;
using FruityLink.Core.Abstractions;
using FruityLink.Core.Domain;

namespace FruityLink.Agent;

/// <summary>
/// Thread-safe in-memory audit sink. Plugins record FL operations here as they execute;
/// the app drains it when creating a version-tree checkpoint.
/// </summary>
public sealed class OperationAuditSink : IOperationAuditSink
{
    private readonly ConcurrentQueue<FlOperation> _operations = new();

    public void Record(FlOperation operation) => _operations.Enqueue(operation);

    public IReadOnlyList<FlOperation> Drain()
    {
        var drained = new List<FlOperation>();
        while (_operations.TryDequeue(out var op))
            drained.Add(op);
        return drained;
    }
}
