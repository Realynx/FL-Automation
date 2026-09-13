using Avalonia.Platform.Storage;

namespace FruityLink.Plugins.PythonIde.Ui;

internal sealed partial class PythonIdeWindow
{
    private static readonly FilePickerFileType PythonFiles = new("Python scripts") { Patterns = ["*.py"] };

    private async Task NewAsync()
    {
        if (_fileOperation) return;
        SetFileBusy(true);
        try
        {
            if (!await ConfirmReplaceAsync()) return;
            _document.New("# `fl` is connected to this FL Studio project.\n\n");
            SyncEditor();
            await PersistDraftAsync();
            _editor.Focus();
        }
        finally { SetFileBusy(false); }
    }

    private async Task OpenAsync()
    {
        if (_fileOperation) return;
        SetFileBusy(true);
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            { Title = "Open Python script", AllowMultiple = false, FileTypeFilter = [PythonFiles, FilePickerFileTypes.All] });
            string? path = files.FirstOrDefault()?.TryGetLocalPath();
            if (path is null || !await ConfirmReplaceAsync()) return;
            await _document.OpenAsync(path);
            SyncEditor();
            await PersistDraftAsync();
            _status.Text = "Opened " + path;
        }
        finally { SetFileBusy(false); }
    }

    private async Task<bool> SaveAsync(bool saveAs)
    {
        if (_fileOperation || _closed) return false;
        SetFileBusy(true);
        try { return await SaveCoreAsync(saveAs); }
        finally { SetFileBusy(false); }
    }

    private async Task<bool> SaveCoreAsync(bool saveAs)
    {
        string? path = saveAs ? null : _document.FilePath;
        if (path is null)
        {
            IStorageFile? file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            { Title = "Save Python script", SuggestedFileName = _document.DisplayName,
                DefaultExtension = "py", FileTypeChoices = [PythonFiles], ShowOverwritePrompt = true });
            path = file?.TryGetLocalPath();
        }
        if (path is null) return false;
        await _document.SaveAsync(path);
        UpdateDocumentLabel();
        await PersistDraftAsync();
        _status.Text = "Saved " + path;
        return true;
    }

    private async Task<bool> ConfirmReplaceAsync()
    {
        if (!_document.IsDirty) return true;
        UnsavedChoice choice = await UnsavedChangesDialog.AskAsync(this, _document.DisplayName);
        return await DocumentReplacement.CanReplaceAsync(choice, () => SaveCoreAsync(false));
    }

    private void SetFileBusy(bool busy)
    {
        _fileOperation = busy;
        _editor.IsReadOnly = busy;
        foreach (var button in _fileButtons) button.IsEnabled = !busy;
    }
}

internal enum UnsavedChoice { Cancel, Discard, Save }

internal static class DocumentReplacement
{
    public static Task<bool> CanReplaceAsync(UnsavedChoice choice, Func<Task<bool>> save) => choice switch
    {
        UnsavedChoice.Discard => Task.FromResult(true),
        UnsavedChoice.Save => save(),
        _ => Task.FromResult(false)
    };
}
