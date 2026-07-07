using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace FruityLink.Ui.Avalonia.Controls;

/// <summary>The FL Automate brand mark (marketing favicon's 5-bar equalizer) as a reusable,
/// resolution-independent control. Size it via Width/Height at the use site.</summary>
public partial class LogoMark : UserControl
{
    public LogoMark()
    {
        // Same direct-load pattern as the Views (robust regardless of name-generator config).
        AvaloniaXamlLoader.Load(this);
    }
}
