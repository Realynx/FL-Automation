using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using AvaloniaEdit;
using FruityLink.Plugins.PythonIde.Documents;
using FruityLink.Plugins.PythonIde.Execution;
using FruityLink.Plugins.PythonIde.Ui;
using FruityLink.Ui.Avalonia.Hosting;
using Xunit;

[assembly: AvaloniaTestApplication(typeof(FruityLink.Plugins.PythonIde.Ui.Tests.TestApplication))]

namespace FruityLink.Plugins.PythonIde.Ui.Tests;

public static class TestApplication
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<Application>().UseSkia().WithInterFont()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

public sealed class WindowTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "fruitylink-ide-ui-tests", Guid.NewGuid().ToString("N"));

    [AvaloniaFact]
    public async Task EditorHasLocalThemePythonHighlightingAndRendersPreview()
    {
        var execution = new FakeExecution();
        var window = CreateWindow(execution);
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            TextEditor editor = window.FindControl<TextEditor>("CodeEditor")!;
            Assert.True(editor.ShowLineNumbers);
            Assert.Equal("Python", editor.SyntaxHighlighting.Name);
            Assert.Equal(2, window.Styles.Count);
            Assert.Empty(Application.Current!.Styles);
            Assert.Contains("fl.project.info", editor.Text);
            Assert.Equal(60, window.FindControl<NumericUpDown>("TimeoutSeconds")!.Value);
            string? preview = Environment.GetEnvironmentVariable("FRUITYLINK_IDE_PREVIEW");
            if (preview is not null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(preview))!);
                using var bitmap = new RenderTargetBitmap(new PixelSize(1120, 780), new Vector(96, 96));
                bitmap.Render(window);
                bitmap.Save(preview);
            }
            Assert.Equal(0, execution.Calls);
        }
        finally { await window.PrepareCloseAsync(); window.Close(); }
    }

    [AvaloniaFact]
    public async Task RunsExactSelectionAndDisplaysStructuredOutput()
    {
        var execution = new FakeExecution();
        var window = CreateWindow(execution);
        try
        {
            window.Show();
            var editor = window.FindControl<TextEditor>("CodeEditor")!;
            editor.Text = "print('not selected')\nresult = 128\n";
            editor.Select(editor.Text.IndexOf("result", StringComparison.Ordinal), "result = 128".Length);
            var key = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter, KeyModifiers = KeyModifiers.Shift };
            editor.RaiseEvent(key);
            await execution.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(key.Handled);
            Assert.Equal("result = 128", execution.Code);
            Assert.Contains("128", window.FindControl<TextBox>("ResultOutput")!.Text);
            Assert.Contains("Hello 🎹", window.FindControl<TextBox>("StandardOutput")!.Text);
        }
        finally { await window.PrepareCloseAsync(); window.Close(); }
    }

    [AvaloniaFact]
    public async Task NativeFrameUsesEditorContentDimensionsAndRestoresTypingFocus()
    {
        var window = CreateWindow(new FakeExecution());
        var view = new EmbeddedAvaloniaView(window);
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var options = view.GetWindowOptions("FL Python IDE");
            Assert.Equal("FL Python IDE", options.Caption);
            Assert.Equal(1120, options.Width);
            Assert.Equal(780, options.Height);
            Assert.Equal(820, options.MinimumWidth);
            Assert.Equal(560, options.MinimumHeight);
            var editor = window.FindControl<TextEditor>("CodeEditor")!;
            editor.Text = "result = 42";
            editor.CaretOffset = 5;
            window.FocusEditor();
            Assert.True(editor.IsKeyboardFocusWithin);
            Assert.Equal(5, editor.CaretOffset);
        }
        finally { await window.PrepareCloseAsync(); view.Close(); }
    }

    [AvaloniaFact]
    public async Task EmptySelectionDoesNotRunWholeDocument()
    {
        var execution = new FakeExecution();
        var window = CreateWindow(execution);
        try { await window.RunAsync(true); Assert.Equal(0, execution.Calls); }
        finally { await window.PrepareCloseAsync(); window.Close(); }
    }

    [AvaloniaFact]
    public async Task StopAndNextRunWaitUntilUnderlyingWorkActuallyDrains()
    {
        var execution = new FakeExecution { Pending = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var window = CreateWindow(execution);
        try
        {
            Task run = window.RunAsync(false);
            await execution.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Task stop = window.StopAsync();
            Assert.True(execution.CancelRequested);
            Assert.False(stop.IsCompleted);
            await window.RunAsync(false);
            Assert.Equal(1, execution.Calls);
            execution.Pending.SetResult(FakeExecution.Success);
            await Task.WhenAll(run, stop);
            await window.RunAsync(false);
            Assert.Equal(2, execution.Calls);
        }
        finally { await window.PrepareCloseAsync(); window.Close(); }
    }

    [AvaloniaFact]
    public async Task StopDuringDraftPersistencePreventsPythonFromStarting()
    {
        var persistence = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var execution = new FakeExecution();
        var window = new PythonIdeWindow(execution, new DraftStore(_directory), _ => persistence.Task);
        Task run = window.RunAsync(false);
        Task stop = window.StopAsync();
        Assert.False(stop.IsCompleted);
        Assert.Equal(0, execution.Calls);
        persistence.SetResult();
        await Task.WhenAll(run, stop);
        Assert.Equal(0, execution.Calls);
        await window.PrepareCloseAsync();
        window.Close();
    }

    [AvaloniaFact]
    public async Task ErrorOnlyResponseSelectsTheErrorTab()
    {
        var execution = new FakeExecution { Response = JsonSerializer.SerializeToElement(new
        { ok = false, result = (object?)null, error = "Embedded response exceeds 1 MiB." }) };
        var window = CreateWindow(execution);
        try
        {
            await window.RunAsync(false);
            Assert.Contains("1 MiB", window.FindControl<TextBox>("StandardError")!.Text);
            Assert.Equal(1, window.FindControl<TabControl>("OutputTabs")!.SelectedIndex);
        }
        finally { await window.PrepareCloseAsync(); window.Close(); }
    }

    [AvaloniaFact]
    public async Task PythonExceptionKeepsStdoutErrorAndTracebackVisible()
    {
        var execution = new FakeExecution { Response = JsonSerializer.SerializeToElement(new
        { ok = false, result = (object?)null, stdout = "before failure", stderr = "warning", error = "ValueError: bad", traceback = "Traceback: line 2" }) };
        var window = CreateWindow(execution);
        try
        {
            await window.RunAsync(false);
            Assert.Equal("before failure", window.FindControl<TextBox>("StandardOutput")!.Text);
            Assert.Contains("ValueError", window.FindControl<TextBox>("StandardError")!.Text);
            Assert.Contains("line 2", window.FindControl<TextBox>("TracebackOutput")!.Text);
            Assert.Equal(3, window.FindControl<TabControl>("OutputTabs")!.SelectedIndex);
        }
        finally { await window.PrepareCloseAsync(); window.Close(); }
    }

    [Fact]
    public void CatalogFilteringUsesTheRealEnvelopeAndPreservesVersion()
    {
        JsonElement catalog = JsonSerializer.SerializeToElement(new
        { apiVersion = 1, operations = new[] { new { name = "get_tempo" }, new { name = "query_notes" } } });
        using JsonDocument filtered = JsonDocument.Parse(PythonIdeWindow.FilterCatalog(catalog, "TEMPO"));
        Assert.Equal(1, filtered.RootElement.GetProperty("apiVersion").GetInt32());
        Assert.Equal("get_tempo", Assert.Single(filtered.RootElement.GetProperty("operations").EnumerateArray()).GetProperty("name").GetString());
    }

    private PythonIdeWindow CreateWindow(FakeExecution execution) => new(execution, new DraftStore(_directory));

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private sealed class FakeExecution : IPythonIdeExecution
    {
        public static readonly JsonElement Success = JsonSerializer.SerializeToElement(new
        { ok = true, stdout = "Hello 🎹", stderr = "", result = new { tempo = 128 } });
        public JsonElement Response { get; init; } = Success;
        public TaskCompletionSource<JsonElement>? Pending { get; init; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls { get; private set; }
        public string? Code { get; private set; }
        public bool CancelRequested { get; private set; }
        public Task<JsonElement> ExecuteAsync(string code, int timeoutSeconds = 60, CancellationToken ct = default)
        {
            Calls++;
            Code = code;
            Started.TrySetResult();
            return Pending?.Task ?? Task.FromResult(Response);
        }
        public Task<JsonElement> GetCatalogAsync(string? filter = null, CancellationToken ct = default) => Task.FromResult(Response);
        public async Task CancelAsync() { CancelRequested = true; if (Pending is not null) await Pending.Task; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
