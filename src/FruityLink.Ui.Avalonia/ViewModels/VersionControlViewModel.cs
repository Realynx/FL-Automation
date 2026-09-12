using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using FruityLink.Ui.Avalonia.Hosting;
using FruityLink.Ui.Avalonia.Services;

namespace FruityLink.Ui.Avalonia.ViewModels;

/// <summary>
/// The version-history panel. Binds to an <see cref="IProjectVersionControl"/> seam (the in-memory stub in
/// the dev head; the real backend gateway inside FL). Rebuilds a newest-first row list from the backend's
/// <see cref="IProjectVersionControl.History"/> on every <see cref="IProjectVersionControl.Changed"/>
/// (marshalled to the UI thread), exposes prominent Undo/Redo, a per-row jump with an inline destructive
/// confirm, and a crash-recovery banner.
/// </summary>
public sealed class VersionControlViewModel : ViewModelBase
{
    private IProjectVersionControl _vc;
    private readonly RelayCommand _undo;
    private readonly RelayCommand _redo;
    private readonly RelayCommand _restoreRecovery;
    private readonly RelayCommand _dismissRecovery;
    private readonly RelayCommand _refresh;
    private bool _isBusy;
    private string? _status;
    private CommitVm? _current;
    private CommitVm? _pendingJump;

    public VersionControlViewModel(IProjectVersionControl vc)
    {
        _vc = vc ?? throw new ArgumentNullException(nameof(vc));
        _undo = new RelayCommand(() => _ = UndoAsync(), () => CanUndo && !_isBusy);
        _redo = new RelayCommand(() => _ = RedoAsync(), () => CanRedo && !_isBusy);
        _restoreRecovery = new RelayCommand(() => _ = AcceptRecoveryAsync(), () => HasRecovery && !_isBusy);
        _dismissRecovery = new RelayCommand(DismissRecovery, () => HasRecovery);
        _refresh = new RelayCommand(Rebuild);
        Subscribe();
        Rebuild();
    }

    /// <summary>The timeline rows, newest first (display order).</summary>
    public ObservableCollection<CommitVm> Commits { get; } = new();

    public ICommand UndoCommand => _undo;
    public ICommand RedoCommand => _redo;
    public ICommand RestoreRecoveryCommand => _restoreRecovery;
    public ICommand DismissRecoveryCommand => _dismissRecovery;
    public ICommand RefreshCommand => _refresh;

    public bool IsEmpty => Commits.Count == 0;

    public CommitVm? Current { get => _current; private set => SetProperty(ref _current, value); }

    public bool CanUndo => _vc.Current?.ParentId is not null;
    public bool CanRedo => Commits.Any(c => c.IsAhead);

    public bool IsBusy { get => _isBusy; private set { if (SetProperty(ref _isBusy, value)) RequeryAll(); } }
    public string? Status { get => _status; private set => SetProperty(ref _status, value); }

    public string Summary => $"{Commits.Count} states · backups: {Commits.Count(c => c.HasBackup)}";

    public bool HasRecovery => _vc.PendingRecovery is not null;
    public string? RecoveryLabel => _vc.PendingRecovery?.Label;

    /// <summary>Re-read backend state (called when the panel opens).</summary>
    public void Refresh() => Rebuild();

    /// <summary>Swap in the real gateway (plugin presenter, UI thread), re-subscribing cleanly.</summary>
    public void AttachGateway(IProjectVersionControl vc)
    {
        Unsubscribe();
        _vc = vc ?? throw new ArgumentNullException(nameof(vc));
        Subscribe();
        Rebuild();
    }

    private void Subscribe() => _vc.Changed += OnBackendChanged;
    private void Unsubscribe() => _vc.Changed -= OnBackendChanged;

    // Fires off-thread → marshal onto the UI thread before touching the ObservableCollection.
    private void OnBackendChanged() => UiThread.RunOrPost(Rebuild);

    /// <summary>Rebuild the row list from backend History (newest first) + recompute current/ahead flags.</summary>
    public void Rebuild()
    {
        IReadOnlyList<VersionCommit> history = _vc.History;   // oldest → newest
        string? curId = _vc.Current?.Id;
        int curIdx = curId is null ? -1 : IndexOf(history, curId);

        Commits.Clear();
        for (int i = history.Count - 1; i >= 0; i--)          // newest first
        {
            Commits.Add(new CommitVm(this, history[i])
            {
                IsCurrent = history[i].Id == curId,
                IsAhead = curIdx >= 0 && i > curIdx,          // later than Current ⇒ redoable
            });
        }
        Current = Commits.FirstOrDefault(c => c.IsCurrent);
        _pendingJump = null;

        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
        OnPropertyChanged(nameof(HasRecovery));
        OnPropertyChanged(nameof(RecoveryLabel));
        RequeryAll();
    }

    private async Task UndoAsync() => await Run(() => _vc.UndoAsync());
    private async Task RedoAsync() => await Run(() => _vc.RedoAsync());

    /// <summary>Begin a per-row jump. Jumping forward (redo) is non-destructive and runs immediately;
    /// jumping back opens a two-step inline confirm.</summary>
    public void BeginJump(CommitVm row)
    {
        CancelJump(_pendingJump);
        if (row.IsAhead) { _ = ConfirmJumpAsync(row); return; }
        _pendingJump = row;
        row.Confirming = true;
    }

    public void CancelJump(CommitVm? row)
    {
        if (row is not null) row.Confirming = false;
        if (ReferenceEquals(row, _pendingJump)) _pendingJump = null;
    }

    public async Task ConfirmJumpAsync(CommitVm row)
    {
        row.Confirming = false;
        _pendingJump = null;
        await Run(() => _vc.RestoreAsync(row.Id));
    }

    public async Task RestoreBackupAsync(CommitVm row) => await Run(() => _vc.RestoreBackupAsync(row.Id));

    private async Task AcceptRecoveryAsync() => await Run(() => _vc.AcceptRecoveryAsync());
    public void DismissRecovery() { _vc.DismissRecovery(); Rebuild(); }

    private async Task Run(Func<Task> op)
    {
        if (_isBusy) return;
        IsBusy = true;
        Status = "Working…";
        try { await op().ConfigureAwait(true); Status = null; }
        catch (Exception ex) { Status = "Failed: " + ex.Message; }
        finally { IsBusy = false; }   // Changed → Rebuild refreshes the list
    }

    private void RequeryAll()
    {
        _undo.RaiseCanExecuteChanged();
        _redo.RaiseCanExecuteChanged();
        _restoreRecovery.RaiseCanExecuteChanged();
        _dismissRecovery.RaiseCanExecuteChanged();
        foreach (CommitVm c in Commits) c.Requery();
    }

    private static int IndexOf(IReadOnlyList<VersionCommit> h, string id)
    {
        for (int i = 0; i < h.Count; i++) if (h[i].Id == id) return i;
        return -1;
    }
}
