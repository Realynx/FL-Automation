using System.Text.Json;

namespace FruityLink.Plugins.PythonIde.Documents;

internal sealed class DraftStore(string directory)
{
    private readonly string _path = Path.Combine(directory, $"draft-{Guid.NewGuid():N}.json");
    private readonly SemaphoreSlim _writes = new(1, 1);

    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FruityLink", "python-ide", "drafts");

    public async Task SaveAsync(DraftSnapshot snapshot)
    {
        await _writes.WaitAsync();
        try { await AtomicTextFile.WriteAsync(_path, JsonSerializer.Serialize(snapshot)); }
        finally { _writes.Release(); }
    }

    public async Task<DraftSnapshot?> RecoverAsync()
    {
        if (!Directory.Exists(directory)) return null;
        var candidates = Directory.EnumerateFiles(directory, "draft-*.json")
            .OrderByDescending(File.GetLastWriteTimeUtc).Take(20);
        foreach (string path in candidates)
        {
            DraftSnapshot? draft = await ReadCandidateAsync(path);
            if (draft is not null) return draft;
        }
        return null;
    }

    private static async Task<DraftSnapshot?> ReadCandidateAsync(string path)
    {
        try
        {
            if (new FileInfo(path).Length > ScriptDocument.MaximumFileBytes * 3) return null;
            DraftSnapshot? draft = JsonSerializer.Deserialize<DraftSnapshot>(await File.ReadAllTextAsync(path));
            return draft is { Version: 1, Text: not null, SavedText: not null } ? draft : null;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        catch (JsonException) { return null; }
    }
}
