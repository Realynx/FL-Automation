using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace FruityLink.Ui.Avalonia.Theme;

/// <summary>Loads tokens from this product instance, independent of the process-wide asset cache.</summary>
public partial class ProductTokens : ResourceDictionary
{
    public ProductTokens() => AvaloniaXamlLoader.Load(this);
}
