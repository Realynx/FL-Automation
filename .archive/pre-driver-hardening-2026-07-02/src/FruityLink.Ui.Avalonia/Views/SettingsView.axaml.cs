using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace FruityLink.Ui.Avalonia.Views;

/// <summary>
/// The in-place Settings panel. Lives inside <see cref="ChatWindow"/> (shown when
/// <c>ChatViewModel.IsSettingsOpen</c>) and inherits the window's <c>ChatViewModel</c> DataContext, so
/// its rows bind straight to the persisted, settings-backed properties (ShowThoughts / ShowToolCalls /
/// AutoScroll). Adding a setting = one AppSettings property + one ChatViewModel property + one row here.
/// </summary>
public partial class SettingsView : UserControl
{
    public SettingsView() => AvaloniaXamlLoader.Load(this);
}
