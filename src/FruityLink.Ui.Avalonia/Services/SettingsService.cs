using System;
using System.IO;
using System.Text.Json;

namespace FruityLink.Ui.Avalonia.Services;

/// <summary>
/// Loads + persists the interior UI's <see cref="AppSettings"/> as JSON on disk. Process-singleton so
/// every window / view-model (the embedded FL plugin path AND the standalone dev head) shares ONE
/// authoritative settings instance and a single file.
///
/// <para>The file lives at <c>%APPDATA%\FLAutomate\ui-settings.json</c> (roaming app data — always writable
/// for the current user). The directory is created on first save. All I/O is best-effort: a missing or
/// corrupt file just yields defaults, and a failed write is swallowed (settings persistence must never
/// take down the UI, least of all when embedded inside FL Studio).</para>
/// </summary>
public sealed class SettingsService
{
    private static readonly Lazy<SettingsService> _lazy = new(() => new SettingsService());

    /// <summary>The process-wide settings store.</summary>
    public static SettingsService Instance => _lazy.Value;

    private static readonly JsonSerializerOptions _json = new() { WriteIndented = true };

    private readonly string _dir;
    private readonly string _path;

    /// <summary>The live settings. Mutate its properties then call <see cref="Save"/> to persist.</summary>
    public AppSettings Current { get; private set; }

    private SettingsService()
    {
        _dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FLAutomate");
        // IMPORTANT: must NOT be "settings.json" — that file is OWNED by FruityLink.Persistence.JsonSettingsStore
        // (the agent's backend/model config, see StoragePaths.SettingsFile). Sharing the name made this UI
        // service overwrite the user's AI settings with the 3-field UI schema. Keep UI prefs in a distinct file.
        _path = Path.Combine(_dir, "ui-settings.json");
        Current = Load();
    }

    /// <summary>The absolute path of the settings file (surfaced in the Settings view footer).</summary>
    public string FilePath => _path;

    private AppSettings Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                string json = File.ReadAllText(_path);
                AppSettings? loaded = JsonSerializer.Deserialize<AppSettings>(json, _json);
                if (loaded is not null) return loaded;
            }
        }
        catch
        {
            // Corrupt / unreadable file → fall back to defaults rather than failing the UI.
        }
        return new AppSettings();
    }

    /// <summary>Persist <see cref="Current"/> to disk (best-effort; never throws).</summary>
    public void Save()
    {
        try
        {
            Directory.CreateDirectory(_dir);
            File.WriteAllText(_path, JsonSerializer.Serialize(Current, _json));
        }
        catch
        {
            // Best-effort: a failed write must not break the UI.
        }
    }
}
