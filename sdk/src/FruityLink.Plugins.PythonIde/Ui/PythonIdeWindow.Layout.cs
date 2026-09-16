using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using FruityLink.Plugins.PythonIde.Documents;

namespace FruityLink.Plugins.PythonIde.Ui;

internal sealed partial class PythonIdeWindow
{
    private Control BuildLayout()
    {
        var layout = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto") };
        Add(layout, BuildHeader(), 0);
        Add(layout, BuildToolbar(), 1);
        Add(layout, BuildWorkspace(), 2);
        Add(layout, BuildFooter(), 3);
        return layout;
    }

    private Control BuildHeader()
    {
        var title = new TextBlock { Text = "FL  /  PYTHON IDE", FontSize = 17, FontWeight = FontWeight.SemiBold };
        var subtitle = new TextBlock { Text = "Script the project in front of you.", FontSize = 12, Foreground = Brush("#9EAEC5") };
        var titles = new StackPanel { Spacing = 4, Children = { title, subtitle } };
        var tag = new TextBlock { Text = "IN-PROCESS PYTHON", FontSize = 10, FontWeight = FontWeight.Bold,
            Foreground = Brush("#84E5C6"), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Children = { titles, tag } };
        Grid.SetColumn(tag, 1);
        return new Border { Padding = new Thickness(22, 18), Background = Brush("#121924"), Child = grid };
    }

    private Control BuildToolbar()
    {
        var files = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        _fileButtons.Add(ActionButton("New", NewAsync));
        _fileButtons.Add(ActionButton("Open…", OpenAsync));
        _fileButtons.Add(ActionButton("Save", async () => { await SaveAsync(false); }));
        _fileButtons.Add(ActionButton("Save as…", async () => { await SaveAsync(true); }));
        foreach (Button button in _fileButtons) files.Children.Add(button);
        _run.Background = Brush("#77DFC0");
        _run.Foreground = Brush("#102920");
        _run.FontWeight = FontWeight.SemiBold;
        var runs = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7,
            Children = { _run, _selection, _stop, _timeout,
                new TextBlock { Text = "sec", VerticalAlignment = VerticalAlignment.Center, Foreground = Brush("#9EAEC5") } } };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Children = { files, runs } };
        Grid.SetColumn(runs, 1);
        return new Border { Padding = new Thickness(14, 10), Child = grid };
    }

    private Control BuildWorkspace()
    {
        var panes = new Grid { ColumnDefinitions = new ColumnDefinitions("*,245") };
        var codeAndOutput = new Grid { RowDefinitions = new RowDefinitions("Auto,3*,5,2*") };
        Add(codeAndOutput, new Border { Padding = new Thickness(18, 9), Background = Brush("#222C3C"), Child = _documentLabel }, 0);
        Add(codeAndOutput, _editor, 1);
        Add(codeAndOutput, new GridSplitter { ResizeDirection = GridResizeDirection.Rows,
            HorizontalAlignment = HorizontalAlignment.Stretch, Background = Brush("#35445B") }, 2);
        _output.ItemsSource = new[]
        {
            new TabItem { Header = "Output", Content = _stdout, FontSize = 14 },
            new TabItem { Header = "Errors", Content = _stderr, FontSize = 14 },
            new TabItem { Header = "Result", Content = _result, FontSize = 14 },
            new TabItem { Header = "Traceback", Content = _traceback, FontSize = 14 }
        };
        Add(codeAndOutput, _output, 3);
        panes.Children.Add(codeAndOutput);
        Control help = BuildHelp();
        Grid.SetColumn(help, 1);
        panes.Children.Add(help);
        return panes;
    }

    private Control BuildHelp()
    {
        var contents = new StackPanel { Spacing = 16 };
        contents.Children.Add(new TextBlock { Text = "QUICK REFERENCE", FontSize = 11,
            FontWeight = FontWeight.Bold, Foreground = Brush("#84E5C6") });
        contents.Children.Add(ActionButton("Browse API…", BrowseApiAsync));
        contents.Children.Add(new TextBlock { Text = EditorContent.Help, FontSize = 12,
            LineHeight = 19, Foreground = Brush("#B9C7DA"), TextWrapping = TextWrapping.Wrap });
        return new Border { Padding = new Thickness(18), Background = Brush("#151D29"),
            Child = new ScrollViewer { Content = contents } };
    }

    private Control BuildFooter()
    {
        _status.FontSize = 11;
        _status.TextTrimming = TextTrimming.CharacterEllipsis;
        _status.Foreground = Brush("#B9C7DA");
        _position.FontSize = 11;
        _position.Foreground = Brush("#93A5BF");
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 16,
            Children = { _status, _position } };
        Grid.SetColumn(_position, 1);
        return new Border { Padding = new Thickness(16, 10), Child = grid };
    }

    private Button ActionButton(string caption, Func<Task> action)
    {
        var button = new Button { Content = caption };
        AutomationProperties.SetName(button, caption);
        button.Click += async (_, _) => await GuardAsync(action);
        return button;
    }

    private static TextBox OutputBox(string name)
    {
        var box = new TextBox
        {
        Name = name, IsReadOnly = true, AcceptsReturn = true, AcceptsTab = true,
        TextWrapping = TextWrapping.NoWrap, FontFamily = new FontFamily("Cascadia Code,Consolas,monospace"),
        FontSize = 12, Background = Brush("#141B25"), BorderThickness = new Thickness(0),
        Padding = new Thickness(14), Watermark = "Run a script to see output here."
        };
        AutomationProperties.SetName(box, name);
        return box;
    }

    private static void Add(Grid grid, Control control, int row)
    {
        Grid.SetRow(control, row);
        grid.Children.Add(control);
    }
}
