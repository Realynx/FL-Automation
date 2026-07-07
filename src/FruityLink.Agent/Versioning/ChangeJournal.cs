using FruityLink.Core.Abstractions;
using FruityLink.Core.Domain;

namespace FruityLink.Agent.Versioning;

/// <summary>
/// Thread-safe in-memory <see cref="IChangeJournal"/>: holds only the CURRENT turn's captured changes
/// (mirrors <see cref="OperationAuditSink"/>), assigns each record its append-order <see cref="ChangeRecord.Seq"/>,
/// and clears on <see cref="Drain"/>. Never retains past commits — the durable copy is each commit's
/// on-disk <c>{commitId}.ops.json</c>, read only when that commit is undone/redone.
/// </summary>
public sealed class ChangeJournal : IChangeJournal
{
    private readonly object _lock = new();
    private readonly List<ChangeRecord> _records = new();
    private bool _tainted;

    /// <inheritdoc />
    public void Append(ChangeRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        lock (_lock)
            _records.Add(record with { Seq = _records.Count });
    }

    /// <inheritdoc />
    public bool HasChanges
    {
        get { lock (_lock) return _records.Count > 0; }
    }

    /// <inheritdoc />
    public bool IsTainted
    {
        get { lock (_lock) return _tainted; }
    }

    /// <inheritdoc />
    public void Taint()
    {
        lock (_lock) _tainted = true;
    }

    /// <inheritdoc />
    public IReadOnlyList<ChangeRecord> Drain()
    {
        lock (_lock)
        {
            var drained = _records.ToArray();
            _records.Clear();
            _tainted = false;
            return drained;
        }
    }
}
