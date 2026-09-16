using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

namespace FruityLink.Plugins.PythonIde.Ui;

internal static class UnsavedChangesDialog
{
    public static Task<UnsavedChoice> AskAsync(Window owner, string document)
    {
        var dialog = new Window { Title = "Unsaved script", Width = 440, SizeToContent = SizeToContent.Height,
            CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner, RequestedThemeVariant = ThemeVariant.Dark };
        dialog.Styles.Add(new FluentTheme());
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Right };
        foreach (var choice in new[] { UnsavedChoice.Cancel, UnsavedChoice.Discard, UnsavedChoice.Save })
        {
            var button = new Button { Content = choice.ToString() };
            button.Click += (_, _) => dialog.Close(choice);
            buttons.Children.Add(button);
        }
        dialog.Content = new StackPanel { Margin = new Thickness(24), Spacing = 22,
            Children = { new TextBlock { Text = $"Save changes to {document}?", FontSize = 17, TextWrapping = TextWrapping.Wrap }, buttons } };
        return dialog.ShowDialog<UnsavedChoice>(owner);
    }
}
