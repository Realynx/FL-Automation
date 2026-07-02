using System;
using System.Windows.Input;

namespace FruityLink.Ui.Avalonia.ViewModels;

/// <summary>
/// One commit row in the version-history timeline. Per-row Jump / Restore commands are row-owned (the
/// shared <see cref="RelayCommand"/> is parameterless) and call back into the parent
/// <see cref="VersionControlViewModel"/>. Display flags (current / ahead / confirming) drive the row's
/// chrome via computed properties so the AXAML needs no value converters.
/// </summary>
public sealed class CommitVm : ViewModelBase
{
    private readonly VersionControlViewModel _owner;
    private bool _isCurrent;
    private bool _isAhead;
    private bool _confirming;

    public CommitVm(VersionControlViewModel owner, Services.VersionCommit model)
    {
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        Model = model ?? throw new ArgumentNullException(nameof(model));

        JumpCommand = new RelayCommand(() => _owner.BeginJump(this), () => !_isCurrent && !_owner.IsBusy);
        ConfirmJumpCommand = new RelayCommand(() => _ = _owner.ConfirmJumpAsync(this), () => !_owner.IsBusy);
        CancelJumpCommand = new RelayCommand(() => _owner.CancelJump(this));
        RestoreBackupCommand = new RelayCommand(
            () => _ = _owner.RestoreBackupAsync(this),
            () => Model.HasBackup && !_isCurrent && !_owner.IsBusy);
    }

    public Services.VersionCommit Model { get; }

    public string Id => Model.Id;
    public string Label => Model.Label;
    public bool HasBackup => Model.HasBackup;

    /// <summary>"2 min ago" etc.; the absolute time shows in a tooltip.</summary>
    public string RelativeTime => Format(DateTimeOffset.Now - Model.Timestamp);
    public string AbsoluteTime => Model.Timestamp.LocalDateTime.ToString("f");

    /// <summary>True for the commit the live project reflects (brand rule + NOW badge).</summary>
    public bool IsCurrent
    {
        get => _isCurrent;
        set { if (SetProperty(ref _isCurrent, value)) { OnPropertyChanged(nameof(ShowActions)); Requery(); } }
    }

    /// <summary>True for a commit ahead of Current (a redoable / "undone" state) — rendered dimmed.</summary>
    public bool IsAhead
    {
        get => _isAhead;
        set { if (SetProperty(ref _isAhead, value)) OnPropertyChanged(nameof(RowOpacity)); }
    }

    /// <summary>True while this row's inline "discard newer edits?" confirm strip is showing.</summary>
    public bool Confirming
    {
        get => _confirming;
        set { if (SetProperty(ref _confirming, value)) OnPropertyChanged(nameof(ShowActions)); }
    }

    /// <summary>Show the row's action buttons (hidden on Current and while confirming).</summary>
    public bool ShowActions => !_isCurrent && !_confirming;

    /// <summary>Dim commits ahead of Current so they read as "undone".</summary>
    public double RowOpacity => _isAhead ? 0.5 : 1.0;

    public ICommand JumpCommand { get; }
    public ICommand ConfirmJumpCommand { get; }
    public ICommand CancelJumpCommand { get; }
    public ICommand RestoreBackupCommand { get; }

    /// <summary>Re-query the row's commands (called when IsCurrent / IsBusy change).</summary>
    public void Requery()
    {
        (JumpCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (ConfirmJumpCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (CancelJumpCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (RestoreBackupCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    private static string Format(TimeSpan d) => d.TotalSeconds < 60 ? "just now"
        : d.TotalMinutes < 60 ? $"{(int)d.TotalMinutes} min ago"
        : d.TotalHours < 24 ? $"{(int)d.TotalHours} hr ago"
        : $"{(int)d.TotalDays} d ago";
}
