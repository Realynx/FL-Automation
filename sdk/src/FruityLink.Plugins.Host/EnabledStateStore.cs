using System.Text.Json;

namespace FruityLink.Plugins.Host;

/// <summary>
/// Persists the user's plugin on/off choices for <see cref="PluginManager"/>: a JSON file (default
/// <c>%LocalAppData%\FruityLink\plugins.json</c>) holding the set of enabled plugin ids; restored at
/// discovery and rewritten on every enable/disable. Holds no plugin state itself — the caller passes
/// the enabled-ids snapshot in. Read/write failures are logged, never thrown.
/// </summary>
internal sealed class EnabledStateStore
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private readonly string _stateFile;
    private readonly Action<string> _log;

    /// <param name="stateFile">JSON file path for the persisted enabled-ids set.</param>
    /// <param name="log">Diagnostic sink (read/write failures are logged here).</param>
    public EnabledStateStore(string stateFile, Action<string> log)
    {
        _stateFile = stateFile;
        _log = log;
    }

    /// <summary>The JSON file persisting the set of enabled plugin ids.</summary>
    public string StateFile => _stateFile;

    public HashSet<string> LoadPersistedEnabled()
    {
        try
        {
            if (!File.Exists(_stateFile)) return new(StringComparer.OrdinalIgnoreCase);
            string json = File.ReadAllText(_stateFile);
            PersistedState? state = JsonSerializer.Deserialize<PersistedState>(json);
            return new HashSet<string>(state?.Enabled ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            _log($"persist: failed to read '{_stateFile}': {ex.Message}");
            return new(StringComparer.OrdinalIgnoreCase);
        }
    }

    public void PersistSafe(List<string> enabled)
    {
        try { Persist(enabled); }
        catch (Exception ex) { _log($"persist: failed to write '{_stateFile}': {ex.Message}"); }
    }

    private void Persist(List<string> enabled)
    {
        string? dir = Path.GetDirectoryName(_stateFile);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(_stateFile, JsonSerializer.Serialize(new PersistedState(enabled), JsonOpts));
    }

    private sealed record PersistedState(List<string> Enabled);
}
