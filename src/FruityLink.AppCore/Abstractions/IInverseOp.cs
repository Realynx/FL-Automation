using FruityLink.Core.Domain;

namespace FruityLink.Core.Abstractions;

/// <summary>
/// The two directions of one invertible operation, keyed in an <see cref="IInverseOpRegistry"/> by a
/// logical op id (e.g. <c>"channel_volume"</c>). <see cref="ReadAsync"/> captures the BEFORE-state for a
/// target (the "old" token stored in the journal); <see cref="ApplyAsync"/> restores a value token — the
/// journal's <c>Old</c> on undo, its <c>New</c> on redo. For value-set ops apply is a setter call; for
/// create/delete ops it creates or deletes by identity. The SAME entry serves capture and both replay
/// directions, so undo and redo can never diverge.
/// </summary>
public interface IInverseOp
{
    /// <summary>Read the current value of <paramref name="target"/> and return it as the "old" token
    /// (e.g. <c>{ "value": 10000 }</c>). Called during the turn, before the mutation.</summary>
    Task<IReadOnlyDictionary<string, object?>> ReadAsync(
        INativeFlControl fl,
        IReadOnlyDictionary<string, object?> target,
        CancellationToken ct = default);

    /// <summary>Apply a value token to <paramref name="target"/>: the journal's <c>Old</c> on undo, its
    /// <c>New</c> on redo. Tokens arriving from disk are <c>JsonElement</c>s, so read them tolerantly.
    /// A create's undo (<paramref name="value"/> null) deletes by identity; a delete's undo re-creates
    /// from the token. Throw on failure so the caller can fall back to the <c>.flp</c>.</summary>
    Task ApplyAsync(
        INativeFlControl fl,
        IReadOnlyDictionary<string, object?> target,
        IReadOnlyDictionary<string, object?>? value,
        CancellationToken ct = default);
}

/// <summary>
/// Maps a logical op id to its <see cref="IInverseOp"/>. Consulted by the capture seam (read-before-write)
/// and by version control (undo/redo replay). An empty registry makes every commit fall back to the
/// <c>.flp</c> — i.e. the pre-journal behavior — so the engine is safely inert until ops are registered.
/// </summary>
public interface IInverseOpRegistry
{
    /// <summary>Look up the inverse op for <paramref name="op"/>. Returns false when unregistered.</summary>
    bool TryGet(string op, out IInverseOp inverseOp);

    /// <summary>True when <paramref name="record"/>'s op is registered (so it can be inverse-replayed).</summary>
    bool CanInvert(ChangeRecord record);
}
