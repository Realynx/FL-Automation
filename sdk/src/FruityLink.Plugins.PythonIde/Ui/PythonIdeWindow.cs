using System.Xml;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.Highlighting.Xshd;
using FruityLink.Plugins.PythonIde.Documents;
using FruityLink.Plugins.PythonIde.Execution;

namespace FruityLink.Plugins.PythonIde.Ui;

internal sealed partial class PythonIdeWindow : Window
{
    private readonly IPythonIdeExecution _execution;
    private readonly DraftStore _drafts;
    private readonly Func<DraftSnapshot, Task> _persistDraft;
    private readonly ScriptDocument _document = new();
    private readonly TextEditor _editor = new() { Name = "CodeEditor" };
    private readonly TextBlock _documentLabel = new();
    private readonly TextBlock _status = new() { Text = "Ready · changes affect the current FL project" };
    private readonly TextBlock _position = new();
    private readonly Button _run = new() { Content = "▶  Run all" };
    private readonly Button _selection = new() { Content = "Run selection" };
    private readonly Button _stop = new() { Content = "■  Stop", IsEnabled = false };
    private readonly NumericUpDown _timeout = new() { Name = "TimeoutSeconds", Minimum = 1, Maximum = 300, Value = 60, Width = 114, FormatString = "0" };
    private readonly TabControl _output = new() { Name = "OutputTabs" };
    private readonly TextBox _stdout = OutputBox("StandardOutput");
    private readonly TextBox _stderr = OutputBox("StandardError");
    private readonly TextBox _result = OutputBox("ResultOutput");
    private readonly TextBox _traceback = OutputBox("TracebackOutput");
    private readonly DispatcherTimer _draftTimer = new() { Interval = TimeSpan.FromMilliseconds(750) };
    private readonly List<Button> _fileButtons = [];
    private bool _running;
    private bool _fileOperation;
    private bool _closed;
    private CancellationTokenSource? _runCancellation;
    private TaskCompletionSource? _runCompletion;

    public PythonIdeWindow(IPythonIdeExecution execution, DraftStore drafts, Func<DraftSnapshot, Task>? persistDraft = null)
    {
        _execution = execution;
        _drafts = drafts;
        _persistDraft = persistDraft ?? drafts.SaveAsync;
        Width = 1120;
        Height = 780;
        MinWidth = 820;
        MinHeight = 560;
        RequestedThemeVariant = ThemeVariant.Dark;
        Background = Brush("#171D27");
        Foreground = Brush("#E1E8F3");
        Styles.Add(new FluentTheme());
        Styles.Add(EditorTheme.Create());
        ConfigureEditor();
        AutomationProperties.SetName(_editor, "Python source");
        AutomationProperties.SetName(_run, "Run all Python");
        AutomationProperties.SetName(_selection, "Run selected Python");
        AutomationProperties.SetName(_stop, "Stop Python and wait for FL work");
        AutomationProperties.SetName(_timeout, "Python timeout in seconds");
        Content = BuildLayout();
        var names = new NameScope();
        NameScope.SetNameScope(this, names);
        foreach (Control control in new Control[] { _editor, _output, _stdout, _stderr, _result, _traceback, _timeout })
            names.Register(control.Name!, control);
        WireEvents();
        _document.New(EditorContent.Starter);
        SyncEditor();
        _draftTimer.Stop();
    }

    public async Task RestoreDraftAsync()
    {
        DraftSnapshot? draft = await _drafts.RecoverAsync();
        if (draft is null) return;
        _document.Restore(draft);
        SyncEditor();
        _status.Text = "Recovered the latest script draft";
    }

    public Task PersistDraftAsync() => _persistDraft(_document.Snapshot());

    internal void FocusEditor() => _editor.TextArea.Focus();

    public async Task PrepareCloseAsync()
    {
        _closed = true;
        _draftTimer.Stop();
        await StopAsync();
        await PersistDraftAsync();
    }

    private void ConfigureEditor()
    {
        _editor.ShowLineNumbers = true;
        _editor.FontFamily = new FontFamily("Cascadia Code,Consolas,monospace");
        _editor.FontSize = 14;
        _editor.Background = Brush("#1A2230");
        _editor.Foreground = Brush("#E1E8F3");
        _editor.LineNumbersForeground = Brush("#71829B");
        _editor.Padding = new Thickness(10, 14);
        _editor.Options.ConvertTabsToSpaces = true;
        _editor.Options.IndentationSize = 4;
        using Stream definition = typeof(PythonIdeWindow).Assembly.GetManifestResourceStream(
            "FruityLink.Plugins.PythonIde.Ui.Python.xshd")!;
        using XmlReader reader = XmlReader.Create(definition);
        _editor.SyntaxHighlighting = HighlightingLoader.Load(reader, null);
    }

    private void WireEvents()
    {
        _editor.TextChanged += (_, _) =>
        {
            _document.Text = _editor.Text;
            UpdateDocumentLabel();
            _draftTimer.Stop();
            if (!_closed) _draftTimer.Start();
        };
        _editor.TextArea.Caret.PositionChanged += (_, _) =>
            _position.Text = $"Ln {_editor.TextArea.Caret.Line}, Col {_editor.TextArea.Caret.Column}  ·  UTF-8  ·  Python";
        _draftTimer.Tick += async (_, _) =>
        {
            _draftTimer.Stop();
            await GuardAsync(PersistDraftAsync);
        };
        _run.Click += async (_, _) => await RunAsync(selection: false);
        _selection.Click += async (_, _) => await RunAsync(selection: true);
        _stop.Click += async (_, _) => await StopAsync();
        AddHandler(KeyDownEvent, OnEditorKeyDown, RoutingStrategies.Tunnel);
    }

    private void SyncEditor()
    {
        _editor.Text = _document.Text;
        UpdateDocumentLabel();
    }

    private void UpdateDocumentLabel()
    {
        string marker = _document.IsDirty ? " ●" : "";
        _documentLabel.Text = _document.DisplayName + marker;
        ToolTip.SetTip(_documentLabel, _document.FilePath ?? "Unsaved script · draft recovery is enabled");
        Title = _document.DisplayName + marker + " — FL Python IDE";
    }

    private async void OnEditorKeyDown(object? sender, KeyEventArgs e)
    {
        Func<Task>? action = (e.Key, e.KeyModifiers) switch
        {
            (Key.N, KeyModifiers.Control) => NewAsync,
            (Key.O, KeyModifiers.Control) => OpenAsync,
            (Key.S, KeyModifiers.Control) => async () => { await SaveAsync(false); },
            (Key.Enter, KeyModifiers.Control) => () => RunAsync(false),
            (Key.F5, KeyModifiers.None) => () => RunAsync(false),
            (Key.Enter, KeyModifiers.Shift) => () => RunAsync(true),
            (Key.F5, KeyModifiers.Shift) => StopAsync,
            _ => null
        };
        if (action is null) return;
        e.Handled = true;
        await GuardAsync(action);
    }

    private async Task GuardAsync(Func<Task> action)
    {
        try { await action(); }
        catch (Exception exception)
        {
            _status.Text = exception.Message;
            _stderr.Text = exception.ToString();
            _output.SelectedIndex = 1;
        }
    }

    private static IBrush Brush(string color) => new SolidColorBrush(Color.Parse(color));
}
