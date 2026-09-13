using Avalonia;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Styling;

namespace FruityLink.Plugins.PythonIde.Ui;

internal static class EditorTheme
{
    public static Styles Create()
    {
        // The packaged Fluent aliases resolve StaticResource against Application. Supply the
        // same editor tokens locally so a neutral or another plugin's Application also works.
        var styles = new Styles();
        styles.Resources["SearchPanelFontSize"] = 13d;
        styles.Resources["SearchPanelFontFamily"] = FontFamily.Default;
        styles.Resources["CompletionToolTipBorderThickness"] = new Thickness(1);
        SetBrush(styles, "CompletionToolTipBackground", "#253247");
        SetBrush(styles, "CompletionToolTipForeground", "#E1E8F3");
        SetBrush(styles, "CompletionToolTipBorderBrush", "#516784");
        SetBrush(styles, "OverloadViewerBackground", "#253247");
        SetBrush(styles, "OverloadViewerForeground", "#E1E8F3");
        SetBrush(styles, "OverloadViewerBorderBrush", "#516784");
        SetBrush(styles, "SearchPanelBackgroundBrush", "#253247");
        SetBrush(styles, "SearchPanelBorderBrush", "#516784");
        SetBrush(styles, "TextAreaSelectionBrush", "#426B82");
        styles.Resources.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://FruityLink.Plugins.PythonIde/"))
        { Source = new Uri("avares://AvaloniaEdit/Themes/Base.xaml") });
        return styles;
    }

    private static void SetBrush(Styles styles, string key, string color) =>
        styles.Resources[key] = new SolidColorBrush(Color.Parse(color));
}
