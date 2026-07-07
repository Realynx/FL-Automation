using FruityLink.Core.Domain;

namespace FruityLink.Core.Abstractions;

/// <summary>
/// The per-turn inverse-change recorder. Mirrors <see cref="IOperationAuditSink"/>: a thread-safe
/// in-memory queue that only ever holds the CURRENT turn's records (a handful), drained and written
/// to disk at the commit boundary, then cleared. Past commits never occupy RAM — the on-disk
/// <c>{commitId}.ops.json</c> is the only durable copy and is read only on demand for that commit's
/// undo/redo.
///
/// <para>Inert by construction: when no inverse ops are wired (no capture seam), nothing is appended,
/// <see cref="Drain"/> returns empty, and version control behaves exactly as before (full-<c>.flp</c>
/// undo/redo).</para>
/// </summary>
public interface IChangeJournal
{
    /// <summary>Buffer one captured change for the current turn. The journal assigns its
    /// <see cref="ChangeRecord.Seq"/> in append order.</summary>
    void Append(ChangeRecord record);

    /// <summary>True when at least one record is buffered for the current turn.</summary>
    bool HasChanges { get; }

    /// <summary>True when a mutation happened this turn that could NOT be cleanly captured (unregistered
    /// op or a failed read-before-write). A tainted turn must fall back to the <c>.flp</c> so FL is never
    /// left half-inverted. Cleared by <see cref="Drain"/>.</summary>
    bool IsTainted { get; }

    /// <summary>Mark the current turn as not cleanly invertible (see <see cref="IsTainted"/>).</summary>
    void Taint();

    /// <summary>Return and clear the current turn's records (and the taint flag). Called by the
    /// version coordinator at the turn/commit boundary.</summary>
    IReadOnlyList<ChangeRecord> Drain();
}
