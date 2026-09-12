namespace FruityLink.Core.Domain;

/// <summary>
/// The shape of a single journaled change, which decides how its inverse is applied.
/// </summary>
public enum ChangeOpKind
{
    /// <summary>A value was set on an existing object. Undo = set <c>Old</c>; redo = set <c>New</c>.</summary>
    SetScalar,

    /// <summary>An object was created. <c>Old</c> is null; undo deletes by <c>Target</c> identity,
    /// redo re-creates from <c>New</c>. (Phase 2+.)</summary>
    Create,

    /// <summary>An object was deleted. <c>New</c> is null; undo re-creates from <c>Old</c>,
    /// redo deletes by <c>Target</c> identity. (Phase 2+.)</summary>
    Delete,

    /// <summary>A multi-step edit whose inverse is a bespoke sequence (e.g. slice). (Phase 3+.)</summary>
    Composite,
}

/// <summary>
/// One inverse-invertible change captured during an AI turn. <c>Old</c> drives UNDO and <c>New</c>
/// drives REDO; both are applied through the <c>IInverseOp</c> registered under <see cref="Op"/>.
/// <see cref="Target"/> addresses the affected object by STABLE IDENTITY (e.g. a clip is
/// pattern+track+start, never a volatile slot index) so replay resolves it against live state.
/// Bulk edits are recorded as one <see cref="ChangeRecord"/> per element so reverse-order replay is
/// element-exact. Dictionaries carry small JSON-friendly scalars (numbers, strings, bools); after a
/// disk round-trip the values arrive as <c>JsonElement</c>, so inverse ops read them through the
/// tolerant accessors rather than casting directly.
/// </summary>
/// <param name="Seq">0-based order within the commit (assigned by the journal at append time).</param>
/// <param name="Kind">How the inverse is applied (see <see cref="ChangeOpKind"/>).</param>
/// <param name="Op">Registry key selecting the <c>IInverseOp</c> (read/apply pair) for this change.</param>
/// <param name="Target">Stable identity of the changed object (empty for global scalars like tempo).</param>
/// <param name="Old">Value token restored on undo, or null for a <see cref="ChangeOpKind.Create"/>.</param>
/// <param name="New">Value token restored on redo, or null for a <see cref="ChangeOpKind.Delete"/>.</param>
public sealed record ChangeRecord(
    int Seq,
    ChangeOpKind Kind,
    string Op,
    IReadOnlyDictionary<string, object?> Target,
    IReadOnlyDictionary<string, object?>? Old,
    IReadOnlyDictionary<string, object?>? New);

/// <summary>
/// The on-disk inverse journal for ONE commit (<c>{commitId}.ops.json</c>), written once at commit and
/// read only when that commit is undone/redone — never held resident for any other commit.
/// <see cref="Invertible"/> is false when the turn contained any op with no registry entry (or any
/// non-granular mutation): such a commit's undo/redo falls back to its full-state <c>.flp</c>, which
/// keeps correctness simple (a turn is never left half-inverted).
/// </summary>
/// <param name="SchemaVersion">On-disk schema version (currently 1).</param>
/// <param name="CommitId">The commit this journal belongs to.</param>
/// <param name="CreatedAt">When the journal was written (UTC).</param>
/// <param name="Invertible">True when every op in <see cref="Ops"/> can be inverse-replayed.</param>
/// <param name="Ops">The changes in the order they were performed (replayed reversed for undo).</param>
public sealed record CommitJournal(
    int SchemaVersion,
    string CommitId,
    DateTimeOffset CreatedAt,
    bool Invertible,
    IReadOnlyList<ChangeRecord> Ops);
