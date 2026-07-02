using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace FruityLink.Ui.Avalonia.ViewModels;

/// <summary>
/// Tiny INotifyPropertyChanged base. We hand-roll MVVM (rather than pull in a toolkit) to keep
/// this UI project dependency-light — it only needs property/collection change notification.
/// </summary>
public abstract class ViewModelBase : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    /// <summary>Set-with-notify helper; returns true when the value actually changed.</summary>
    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }
}
