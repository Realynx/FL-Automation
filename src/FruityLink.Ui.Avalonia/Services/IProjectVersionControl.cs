using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FruityLink.Ui.Avalonia.Services;

/// <summary>One AI work-unit commit. UI-local mirror of the backend record (plain primitives only, so the
/// UI lib never references <c>FruityLink.Core</c>) — exactly how <see cref="BackendSettings"/> mirrors
/// <c>AppSettings</c>.</summary>
public sealed record VersionCommit(
    string Id,
    DateTimeOffset Timestamp,
    string Label,
    bool HasBackup,
    string? ParentId);

/// <summary>Crash-recovery info surfaced at startup (a backup newer than the user's on-disk project).</summary>
public sealed record RecoveryInfo(string CommitId, string Label, DateTimeOffset Timestamp);

/// <summary>
/// The seam the version-control panel binds to. Mirrors the backend's
/// <c>FruityLink.Core.Abstractions.IProjectVersionControl</c>; the FL Agent plugin adapts the real Core
/// interface onto this. The standalone dev head uses <see cref="InMemoryProjectVersionControl"/>.
/// </summary>
public interface IProjectVersionControl
{
    /// <summary>Commits in chronological order (oldest → newest).</summary>
    IReadOnlyList<VersionCommit> History { get; }

    /// <summary>The state the live FL project currently reflects (HEAD), or null.</summary>
    VersionCommit? Current { get; }

    /// <summary>Capture the current state as a commit (manual). Unused by the panel today (the agent
    /// commits automatically) but kept for parity with the contract.</summary>
    Task CommitAsync(string label, CancellationToken ct = default);

    /// <summary>Move back one state.</summary>
    Task UndoAsync(CancellationToken ct = default);

    /// <summary>Move forward one state.</summary>
    Task RedoAsync(CancellationToken ct = default);

    /// <summary>Jump to an arbitrary state.</summary>
    Task RestoreAsync(string commitId, CancellationToken ct = default);

    /// <summary>Re-open a commit's <c>.flp</c> backup.</summary>
    Task RestoreBackupAsync(string commitId, CancellationToken ct = default);

    /// <summary>Pending crash-recovery, or null. The UI shows a banner when non-null.</summary>
    RecoveryInfo? PendingRecovery { get; }

    /// <summary>Accept the pending recovery (restore the newer backup).</summary>
    Task AcceptRecoveryAsync(CancellationToken ct = default);

    /// <summary>Dismiss the pending recovery.</summary>
    void DismissRecovery();

    /// <summary>Raised (possibly off-thread) whenever History/Current/PendingRecovery change.</summary>
    event Action? Changed;
}

/// <summary>
/// No-backend version control for the standalone Avalonia dev head (<c>dotnet run</c>): seeds a few sample
/// commits and implements undo/redo/jump by moving a cursor, so the history panel renders populated and
/// stays fully interactive with no agent wired. Never touches disk or FL.
/// </summary>
public sealed class InMemoryProjectVersionControl : IProjectVersionControl
{
    private readonly List<VersionCommit> _history = new();
    private int _cursor;
    private RecoveryInfo? _recovery;

    /// <summary>Seeds ~5 sample commits mirroring the design mock.</summary>
    public InMemoryProjectVersionControl()
    {
        DateTimeOffset now = DateTimeOffset.Now;
        (string Label, bool Backup, int MinsAgo)[] seeds =
        {
            ("Set project tempo to 140 & key to F minor", false, 24),
            ("Created 4 mixer tracks, named them",         true, 20),
            ("Routed drums to Bus 1, added glue comp",     true, 12),
            ("Chopped the vocal into 8 slices",            true,  6),
            ("Added reverb send on the vocal bus",         true,  2),
        };

        string? parent = null;
        foreach ((string label, bool backup, int minsAgo) in seeds)
        {
            string id = Guid.NewGuid().ToString("N");
            _history.Add(new VersionCommit(id, now.AddMinutes(-minsAgo), label, backup, parent));
            parent = id;
        }
        _cursor = _history.Count - 1;
    }

    public IReadOnlyList<VersionCommit> History => _history.ToArray();

    public VersionCommit? Current =>
        _cursor >= 0 && _cursor < _history.Count ? _history[_cursor] : null;

    public RecoveryInfo? PendingRecovery => _recovery;

    public event Action? Changed;

    public Task CommitAsync(string label, CancellationToken ct = default)
    {
        // Discard-newer, then append (mirrors the backend's branch-on-commit-after-undo, linearized).
        if (_cursor < _history.Count - 1)
            _history.RemoveRange(_cursor + 1, _history.Count - _cursor - 1);
        string? parent = Current?.Id;
        _history.Add(new VersionCommit(Guid.NewGuid().ToString("N"), DateTimeOffset.Now, label, true, parent));
        _cursor = _history.Count - 1;
        Raise();
        return Task.CompletedTask;
    }

    public Task UndoAsync(CancellationToken ct = default)
    {
        if (_cursor > 0) { _cursor--; Raise(); }
        return Task.CompletedTask;
    }

    public Task RedoAsync(CancellationToken ct = default)
    {
        if (_cursor < _history.Count - 1) { _cursor++; Raise(); }
        return Task.CompletedTask;
    }

    public Task RestoreAsync(string commitId, CancellationToken ct = default)
    {
        int i = _history.FindIndex(c => c.Id == commitId);
        if (i >= 0) { _cursor = i; Raise(); }
        return Task.CompletedTask;
    }

    public Task RestoreBackupAsync(string commitId, CancellationToken ct = default) => RestoreAsync(commitId, ct);

    public Task AcceptRecoveryAsync(CancellationToken ct = default)
    {
        _recovery = null;
        Raise();
        return Task.CompletedTask;
    }

    public void DismissRecovery()
    {
        _recovery = null;
        Raise();
    }

    private void Raise() => Changed?.Invoke();
}
