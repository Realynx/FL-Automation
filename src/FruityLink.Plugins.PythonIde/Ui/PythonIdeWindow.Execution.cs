using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

namespace FruityLink.Plugins.PythonIde.Ui;

internal sealed partial class PythonIdeWindow
{
    internal async Task RunAsync(bool selection)
    {
        if (_running || _closed || _fileOperation) return;
        string code = selection ? _editor.SelectedText : _editor.Text;
        if (string.IsNullOrWhiteSpace(code))
        {
            _status.Text = selection ? "Select Python code before running a selection." : "Write Python code before running.";
            return;
        }
        using var cancellation = new CancellationTokenSource();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _runCancellation = cancellation;
        _runCompletion = completion;
        SetRunning(true);
        _status.Text = selection ? "Running selection…" : "Running script…";
        ClearOutput();
        try
        {
            await PersistDraftAsync();
            cancellation.Token.ThrowIfCancellationRequested();
            JsonElement response = await _execution.ExecuteAsync(code, (int)(_timeout.Value ?? 60), cancellation.Token);
            PresentResponse(response);
        }
        catch (OperationCanceledException) { _status.Text = "Stopped · Python and FL work have finished"; }
        catch (Exception exception)
        {
            _stderr.Text = exception.Message;
            _traceback.Text = exception.ToString();
            _output.SelectedIndex = 1;
            _status.Text = "Execution failed · see Errors";
        }
        finally
        {
            _runCancellation = null;
            _runCompletion = null;
            SetRunning(false);
            completion.TrySetResult();
        }
    }

    internal async Task StopAsync()
    {
        if (!_running) return;
        Task completion = _runCompletion!.Task;
        _runCancellation?.Cancel();
        _stop.IsEnabled = false;
        _status.Text = "Stopping · waiting for active Python and FL work to finish…";
        await GuardAsync(async () =>
        {
            try { await _execution.CancelAsync(); }
            finally { await completion; }
        });
    }

    private void SetRunning(bool running)
    {
        _running = running;
        _run.IsEnabled = !running;
        _selection.IsEnabled = !running;
        _stop.IsEnabled = running;
        _timeout.IsEnabled = !running;
    }

    private void ClearOutput()
    {
        _stdout.Text = "";
        _stderr.Text = "";
        _result.Text = "";
        _traceback.Text = "";
    }

    private void PresentResponse(JsonElement response)
    {
        _stdout.Text = StreamText(response, "stdout");
        _stderr.Text = StreamText(response, "stderr");
        _result.Text = OutputText(response, "result");
        _traceback.Text = OutputText(response, "traceback");
        bool ok = response.TryGetProperty("ok", out JsonElement value) && value.ValueKind == JsonValueKind.True;
        string error = OutputText(response, "error");
        if (error.Length > 0) _stderr.Text += Environment.NewLine + error;
        _status.Text = ok ? "Finished · changes are applied to the current project" : "Python error · see Errors / Traceback";
        _output.SelectedIndex = ok ? (string.IsNullOrEmpty(_stdout.Text) ? 2 : 0)
            : (string.IsNullOrEmpty(_traceback.Text) ? 1 : 3);
    }

    private static string OutputText(JsonElement response, string name)
    {
        if (!response.TryGetProperty(name, out JsonElement value) || value.ValueKind == JsonValueKind.Null) return "";
        return value.ValueKind == JsonValueKind.String ? value.GetString()! : JsonSerializer.Serialize(value,
            new JsonSerializerOptions { WriteIndented = true });
    }

    private static string StreamText(JsonElement response, string name)
    {
        string text = OutputText(response, name);
        bool truncated = response.TryGetProperty(name + "Truncated", out JsonElement flag) && flag.ValueKind == JsonValueKind.True;
        return truncated ? text + "\n[Output truncated by the Python runtime]" : text;
    }

    private async Task BrowseApiAsync()
    {
        JsonElement catalog = await _execution.GetCatalogAsync();
        var dialog = new Window { Title = "FL Python API", Width = 780, Height = 650,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, RequestedThemeVariant = ThemeVariant.Dark };
        dialog.Styles.Add(new FluentTheme());
        var search = new TextBox { Watermark = "Filter operations, e.g. query_notes or tempo", Margin = new Thickness(12) };
        TextBox details = OutputBox("ApiCatalog");
        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,*"), Children = { search, details } };
        Grid.SetRow(details, 1);
        void Update() => details.Text = FilterCatalog(catalog, search.Text);
        search.TextChanged += (_, _) => Update();
        Update();
        dialog.Content = grid;
        await dialog.ShowDialog(this);
    }

    internal static string FilterCatalog(JsonElement catalog, string? filter)
    {
        var options = new JsonSerializerOptions { WriteIndented = true };
        if (catalog.ValueKind != JsonValueKind.Object || !catalog.TryGetProperty("operations", out JsonElement operations))
            return JsonSerializer.Serialize(catalog, options);
        var matches = operations.EnumerateArray().Where(item => string.IsNullOrWhiteSpace(filter)
            || item.ToString().Contains(filter, StringComparison.OrdinalIgnoreCase)).ToArray();
        JsonObject result = JsonNode.Parse(catalog.GetRawText())!.AsObject();
        result["operations"] = JsonSerializer.SerializeToNode(matches);
        return result.ToJsonString(options);
    }
}
