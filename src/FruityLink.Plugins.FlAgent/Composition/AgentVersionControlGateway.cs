using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FruityLink.Core.Domain;
using FruityLink.Ui.Avalonia.Services;
using CoreVc = FruityLink.Core.Abstractions.IProjectVersionControl;

namespace FruityLink.Plugins.FlAgent.Composition;

/// <summary>
/// Bridges the backend-agnostic Avalonia version-history panel
/// (<see cref="FruityLink.Ui.Avalonia.Services.IProjectVersionControl"/>) to the AGENT's real
/// <see cref="FruityLink.Core.Abstractions.IProjectVersionControl"/> — the exact parallel of
/// <see cref="AgentBackendSettingsGateway"/>. It maps the Core <see cref="ProjectCommit"/> records onto the
/// flat UI <see cref="VersionCommit"/> DTO (no FruityLink.Core type crosses the seam) and re-raises the
/// backend's <c>Changed</c> event as the UI's parameterless <see cref="Changed"/>.
/// </summary>
internal sealed class AgentVersionControlGateway : IProjectVersionControl
{
    private readonly CoreVc _backend;

    public AgentVersionControlGateway(CoreVc backend)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _backend.Changed += OnBackendChanged;
    }

    public IReadOnlyList<VersionCommit> History => _backend.History.Select(Map).ToList();

    public VersionCommit? Current => _backend.Head is { } h ? Map(h) : null;

    public Task CommitAsync(string label, CancellationToken ct = default) =>
        _backend.CommitAsync(label, null, null, CommitTrigger.Manual, ct);

    public Task UndoAsync(CancellationToken ct = default) => _backend.UndoAsync(ct);

    public Task RedoAsync(CancellationToken ct = default) => _backend.RedoAsync(ct);

    public Task RestoreAsync(string commitId, CancellationToken ct = default) => _backend.RestoreAsync(commitId, ct);

    // Restore = open the target commit's .flp (the backend's authoritative restore path).
    public Task RestoreBackupAsync(string commitId, CancellationToken ct = default) => _backend.RestoreAsync(commitId, ct);

    public RecoveryInfo? PendingRecovery =>
        _backend.RecoveryCandidate is { } c ? new RecoveryInfo(c.Id, c.Label, c.CreatedAt) : null;

    public Task AcceptRecoveryAsync(CancellationToken ct = default) =>
        _backend.RecoveryCandidate is { } c ? _backend.RestoreAsync(c.Id, ct) : Task.CompletedTask;

    public void DismissRecovery() => _backend.DismissRecovery();

    public event Action? Changed;

    private void OnBackendChanged(object? sender, ProjectVersionChanged e) => Changed?.Invoke();

    private static VersionCommit Map(ProjectCommit c) =>
        new(c.Id, c.CreatedAt, c.Label, !string.IsNullOrEmpty(c.FlpBackupPath), c.ParentId);
}
