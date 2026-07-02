using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace FruityLink.Ui.Avalonia.Views;

/// <summary>
/// The in-place version-history panel. Lives inside <see cref="ChatWindow"/> (shown when
/// <c>ChatViewModel.IsVersionsOpen</c>) and inherits the window's <c>ChatViewModel</c> DataContext, so its
/// rows bind through <c>Versions.*</c> — exactly how <see cref="SettingsView"/> binds through <c>Account.*</c>.
/// </summary>
public partial class VersionControlView : UserControl
{
    public VersionControlView() => AvaloniaXamlLoader.Load(this);
}
