using FruityLink.Plugins.PythonIde.Documents;
using FruityLink.Plugins.PythonIde.Ui;
using Xunit;

namespace FruityLink.Plugins.PythonIde.Ui.Tests;

public sealed class DocumentTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "fruitylink-ide-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task SaveAndOpenPreserveUnicodeAndTrackOnlyUnsavedChanges()
    {
        var document = new ScriptDocument();
        document.New("print('初めまして 🎹')\n");
        document.Text += "result = 128\n";
        Assert.True(document.IsDirty);
        string path = Path.Combine(_directory, "script.py");
        await document.SaveAsync(path);
        Assert.False(document.IsDirty);
        var opened = new ScriptDocument();
        await opened.OpenAsync(path);
        Assert.Equal(document.Text, opened.Text);
        Assert.False(opened.IsDirty);
        opened.Text += "# modified";
        Assert.True(opened.IsDirty);
        Assert.Equal("script.py", opened.DisplayName);
    }

    [Fact]
    public async Task FailedOpenKeepsTheCurrentUnsavedDocument()
    {
        var document = new ScriptDocument();
        document.New("original");
        document.Text = "unsaved";
        await Assert.ThrowsAnyAsync<IOException>(() => document.OpenAsync(Path.Combine(_directory, "missing.py")));
        Assert.Equal("unsaved", document.Text);
        Assert.True(document.IsDirty);
        Assert.Null(document.FilePath);
    }

    [Fact]
    public async Task DraftRecoveryRetainsOriginalSavedBaselineAndSeparateSessions()
    {
        var document = new ScriptDocument();
        document.New("saved");
        await document.SaveAsync(Path.Combine(_directory, "script.py"));
        document.Text = "unsaved draft 🎹";
        var first = new DraftStore(_directory);
        await first.SaveAsync(document.Snapshot());
        var second = new DraftStore(_directory);
        DraftSnapshot? snapshot = await second.RecoverAsync();
        Assert.NotNull(snapshot);
        var restored = new ScriptDocument();
        restored.Restore(snapshot);
        Assert.Equal(document.Text, restored.Text);
        Assert.Equal(document.FilePath, restored.FilePath);
        Assert.True(restored.IsDirty);
        await second.SaveAsync(restored.Snapshot());
        Assert.Equal(2, Directory.GetFiles(_directory, "draft-*.json").Length);
    }

    [Fact]
    public async Task CorruptNewerDraftDoesNotHideValidRecovery()
    {
        var document = new ScriptDocument();
        document.New("recover me");
        var drafts = new DraftStore(_directory);
        await drafts.SaveAsync(document.Snapshot());
        string bad = Path.Combine(_directory, "draft-bad.json");
        await File.WriteAllTextAsync(bad, "{broken");
        File.SetLastWriteTimeUtc(bad, DateTime.UtcNow.AddSeconds(1));
        Assert.Equal("recover me", (await drafts.RecoverAsync())!.Text);
    }

    [Theory]
    [InlineData(0, true, false, 0)]
    [InlineData(1, false, true, 0)]
    [InlineData(2, false, false, 1)]
    [InlineData(2, true, true, 1)]
    public async Task ReplacementHonorsCancelAndFailedSave(int choice, bool saved, bool expected, int expectedCalls)
    {
        int calls = 0;
        bool replace = await DocumentReplacement.CanReplaceAsync((UnsavedChoice)choice, () => { calls++; return Task.FromResult(saved); });
        Assert.Equal(expected, replace);
        Assert.Equal(expectedCalls, calls);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
