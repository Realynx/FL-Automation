using System.Text;

namespace FruityLink.Plugins.PythonIde.Documents;

internal sealed class ScriptDocument
{
    internal const int MaximumFileBytes = 4 * 1024 * 1024;
    private string _savedText = "";
    public string Text { get; set; } = "";
    public string? FilePath { get; private set; }
    public bool IsDirty => !string.Equals(Text, _savedText, StringComparison.Ordinal);
    public string DisplayName => FilePath is null ? "Untitled.py" : Path.GetFileName(FilePath);

    public void New(string text = "")
    {
        Text = text;
        _savedText = text;
        FilePath = null;
    }

    public async Task OpenAsync(string path)
    {
        string absolute = Path.GetFullPath(path);
        if (new FileInfo(absolute).Length > MaximumFileBytes)
            throw new IOException("This editor accepts Python files up to 4 MiB.");
        string text = await File.ReadAllTextAsync(absolute, Encoding.UTF8);
        Text = text;
        _savedText = text;
        FilePath = absolute;
    }

    public async Task SaveAsync(string path)
    {
        string absolute = Path.GetFullPath(path);
        string snapshot = Text;
        await AtomicTextFile.WriteAsync(absolute, snapshot);
        FilePath = absolute;
        _savedText = snapshot;
    }

    public DraftSnapshot Snapshot() => new(1, FilePath, Text, _savedText);

    public void Restore(DraftSnapshot draft)
    {
        Text = draft.Text;
        _savedText = draft.SavedText;
        FilePath = draft.FilePath;
    }
}

internal sealed record DraftSnapshot(int Version, string? FilePath, string Text, string SavedText);

internal static class AtomicTextFile
{
    public static async Task WriteAsync(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, text, new UTF8Encoding(false));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
