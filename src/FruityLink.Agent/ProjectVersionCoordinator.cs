using FruityLink.Core.Abstractions;
using FruityLink.Core.Domain;

namespace FruityLink.Agent;

/// <summary>
/// Turns each AI work-unit (agent turn) into at most one project-state commit. It watches the tools the
/// agent invokes (<see cref="FlAgent.ToolInvoked"/>) and, when the presenter reports a turn finished,
/// commits ONCE — but only if a genuinely MUTATING tool ran during that turn (read-only <c>get_*</c>/
/// <c>list_*</c>, view-state <c>select_*</c> and transport/seek do not count). The op summaries drained
/// from <see cref="IOperationAuditSink"/> (when supplied) auto-label the commit.
///
/// <para>Wire it once where <see cref="FlAgent"/> is composed; the presenter calls
/// <see cref="OnTurnCompletedAsync"/> in its turn <c>finally</c> (fire-and-forget so the native save never
/// blocks the UI thread). <see cref="Dispose"/> unhooks from the long-lived agent.</para>
/// </summary>
public sealed class ProjectVersionCoordinator : IDisposable
{
    private readonly IProjectVersionControl _vc;
    private readonly FlAgent _agent;
    private readonly IOperationAuditSink? _audit;
    private readonly IChangeJournal? _journal;
    private readonly IReadOnlySet<string>? _granularTools;
    private readonly HashSet<string> _mutatingThisTurn = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();
    private bool _disposed;

    /// <summary>Creates a coordinator committing to <paramref name="vc"/> at <paramref name="agent"/>'s turn
    /// boundaries. <paramref name="audit"/> is optional; when present its drained op descriptions label the
    /// commit, otherwise the label falls back to an op count. <paramref name="journal"/> +
    /// <paramref name="granularTools"/> enable granular inverse undo/redo: the journal's records are attached
    /// to the commit only when EVERY mutating tool this turn is one that fully journals (in
    /// <paramref name="granularTools"/>) and no capture was tainted; otherwise the commit stays
    /// <c>.flp</c>-only. Omit both to keep the pre-journal behavior.</summary>
    public ProjectVersionCoordinator(
        IProjectVersionControl vc, FlAgent agent, IOperationAuditSink? audit = null,
        IChangeJournal? journal = null, IReadOnlySet<string>? granularTools = null)
    {
        _vc = vc ?? throw new ArgumentNullException(nameof(vc));
        _agent = agent ?? throw new ArgumentNullException(nameof(agent));
        _audit = audit;
        _journal = journal;
        _granularTools = granularTools;
        _agent.ToolInvoked += OnTool;
    }

    private void OnTool(ToolCallInfo tool)
    {
        if (tool is null || string.IsNullOrEmpty(tool.Function)) return;
        if (!IsMutating(tool.Function)) return;
        lock (_lock) _mutatingThisTurn.Add(tool.Function);
    }

    /// <summary>Call right after the turn's stream loop finishes. Commits once if the turn mutated project
    /// state; a no-op (and cheap) otherwise. Never throws — a commit failure must not break the chat turn.</summary>
    public async Task OnTurnCompletedAsync(string? chatNodeId = null, CancellationToken ct = default)
    {
        string[] mutatedTools;
        lock (_lock)
        {
            mutatedTools = _mutatingThisTurn.ToArray();
            _mutatingThisTurn.Clear();
        }

        // ALWAYS drain the journal at the turn boundary — even on a non-mutating turn — so a stray capture
        // (e.g. a UI-side value change) can never leak into a later turn's commit.
        bool tainted = _journal?.IsTainted ?? false;
        IReadOnlyList<Core.Domain.ChangeRecord> records = _journal?.Drain() ?? Array.Empty<Core.Domain.ChangeRecord>();

        if (mutatedTools.Length == 0) return;   // nothing persistent changed this turn

        // The turn is granularly invertible only when EVERY mutating tool it ran fully journals, no capture
        // was tainted, and we actually recorded ops. Any not-yet-invertible tool (add_note, add_channel,
        // delete_arrangement, …) taints the commit to .flp-only, so a turn is never left half-inverted.
        bool granular =
            _granularTools is not null
            && !tainted
            && records.Count > 0
            && Array.TrueForAll(mutatedTools, _granularTools.Contains);
        IReadOnlyList<Core.Domain.ChangeRecord>? changes = granular ? records : null;

        IReadOnlyList<string> ops = _audit?.Drain().Select(o => o.Description).ToArray() ?? Array.Empty<string>();
        string label = ops.Count > 0
            ? string.Join("; ", ops.Take(3))
            : $"AI edit ({mutatedTools.Length} {(mutatedTools.Length == 1 ? "op" : "ops")})";

        // Bound the whole commit so a wedged native call can never pin this worker indefinitely. The commit
        // already runs off the UI thread (the presenter fires this and-forget), and CommitAsync now backs up
        // via FL's modal-free direct writer (safe even on an untitled template — the old wrapper-save modal
        // that wedged FL's UI thread is no longer on this path), so this timeout is belt-and-suspenders:
        // even a future stuck bridge round-trip cancels out instead of leaking a hung task. Any failure or
        // timeout is swallowed; a backup problem must never surface into (or break) the chat turn.
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(30));
        try { await _vc.CommitAsync(label, chatNodeId, ops, CommitTrigger.Auto, changes, timeoutCts.Token).ConfigureAwait(false); }
        catch { /* backup failure/timeout is never surfaced into the chat turn */ }
    }

    /// <summary>Classifies a tool name: read-only (<c>get_*</c>/<c>list_*</c>), view-state (<c>select_*</c>)
    /// and transport/seek are NOT mutating; everything else (add/set/create/clear/delete/move/…) is.</summary>
    public static bool IsMutating(string function)
    {
        if (string.IsNullOrEmpty(function)) return false;
        if (function.StartsWith("get_", StringComparison.OrdinalIgnoreCase)) return false;
        if (function.StartsWith("list_", StringComparison.OrdinalIgnoreCase)) return false;
        if (function.StartsWith("select_", StringComparison.OrdinalIgnoreCase)) return false;

        return function.ToLowerInvariant() switch
        {
            "seek" or "transport_play" or "transport_stop" or "transport_record"
                or "get_status" or "is_available" => false,
            _ => true,
        };
    }

    /// <summary>Unhooks from the long-lived agent so the coordinator can be collected.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _agent.ToolInvoked -= OnTool;
    }
}
