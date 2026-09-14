using Avalonia.Markup.Xaml;
using Avalonia.Styling;

namespace FruityLink.Ui.Avalonia.Theme;

/// <summary>Each product window owns its styles so plugin reload uses its current assembly.</summary>
public partial class ProductStyles : Styles
{
    public ProductStyles() => AvaloniaXamlLoader.Load(this);
}
