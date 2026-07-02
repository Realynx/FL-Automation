namespace FruityLink.Persistence;

/// <summary>
/// Resolves and owns the on-disk layout for all FruityLink persistence. A single instance
/// is shared by every store so that tests can redirect everything to a temp directory by
/// supplying a custom <paramref name="baseDirectory"/> to the constructor.
/// </summary>
public sealed class StoragePaths
{
    /// <summary>
    /// Creates a layout rooted at <paramref name="baseDirectory"/>, defaulting to
    /// <c>%APPDATA%\FLAutomate</c> when none is supplied. The base directory and the
    /// chats/versions subdirectories are created eagerly so stores can write immediately.
    /// </summary>
    /// <param name="baseDirectory">Root directory for all data, or null for the per-user default.</param>
    public StoragePaths(string? baseDirectory = null)
    {
        // On-disk data folder is branded "FLAutomate" (the product is FL Automate). This is only the
        // data-directory NAME — the assembly/namespace identifiers stay FruityLink.* by design.
        BaseDirectory = baseDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "FLAutomate");

        ChatsDirectory = Path.Combine(BaseDirectory, "chats");
        VersionsDirectory = Path.Combine(BaseDirectory, "versions");
        ProjectVersionsDirectory = Path.Combine(VersionsDirectory, "project");
        SettingsFile = Path.Combine(BaseDirectory, "settings.json");
        SecretsFile = Path.Combine(BaseDirectory, "secrets.json");

        EnsureDirectories();
    }

    /// <summary>Root directory containing all FruityLink data.</summary>
    public string BaseDirectory { get; }

    /// <summary>Directory holding one <c>{id}.json</c> file per chat session.</summary>
    public string ChatsDirectory { get; }

    /// <summary>Directory holding one <c>{sessionId}.json</c> version-tree file per session.</summary>
    public string VersionsDirectory { get; }

    /// <summary>Directory (<c>versions/project</c>) holding per-session project-state backups + index.</summary>
    public string ProjectVersionsDirectory { get; }

    /// <summary>Full path to the application settings JSON file.</summary>
    public string SettingsFile { get; }

    /// <summary>Full path to the encrypted secrets JSON file.</summary>
    public string SecretsFile { get; }

    /// <summary>Returns the path to a chat session's JSON file.</summary>
    /// <param name="sessionId">Chat session id.</param>
    public string ChatFile(string sessionId) =>
        Path.Combine(ChatsDirectory, sessionId + ".json");

    /// <summary>Returns the path to a session's version-tree JSON file.</summary>
    /// <param name="sessionId">Chat session id.</param>
    public string VersionFile(string sessionId) =>
        Path.Combine(VersionsDirectory, sessionId + ".json");

    /// <summary>Per-session project-version directory (<c>versions/project/{sessionId}</c>).</summary>
    /// <param name="sessionId">Owning chat session id.</param>
    public string ProjectVersionDir(string sessionId) =>
        Path.Combine(ProjectVersionsDirectory, sessionId);

    /// <summary>The session's commit index (<c>.../index.json</c>): commit DAG + HEAD + recovery metadata.</summary>
    public string ProjectVersionIndexFile(string sessionId) =>
        Path.Combine(ProjectVersionDir(sessionId), "index.json");

    /// <summary>The authoritative <c>.flp</c> backup for a commit (<c>.../{commitId}.flp</c>).</summary>
    public string ProjectFlpBackup(string sessionId, string commitId) =>
        Path.Combine(ProjectVersionDir(sessionId), commitId + ".flp");

    /// <summary>The readable state snapshot for a commit (<c>.../{commitId}.state.json</c>).</summary>
    public string ProjectStateFile(string sessionId, string commitId) =>
        Path.Combine(ProjectVersionDir(sessionId), commitId + ".state.json");

    /// <summary>Creates the base, chats and versions directories if they do not yet exist.</summary>
    public void EnsureDirectories()
    {
        Directory.CreateDirectory(BaseDirectory);
        Directory.CreateDirectory(ChatsDirectory);
        Directory.CreateDirectory(VersionsDirectory);
        Directory.CreateDirectory(ProjectVersionsDirectory);
    }
}
