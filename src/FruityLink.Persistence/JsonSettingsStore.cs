using System.Text.Json;
using FruityLink.Core.Abstractions;
using FruityLink.Core.Configuration;

namespace FruityLink.Persistence;

/// <summary>
/// File-backed <see cref="ISettingsStore"/> persisting <see cref="AppSettings"/> to
/// <c>settings.json</c>. Loading a missing or unparseable file yields defaults rather than
/// throwing, so a corrupt settings file never blocks startup. Saves are atomic.
/// </summary>
public sealed class JsonSettingsStore : ISettingsStore
{
    private readonly StoragePaths _paths;

    /// <summary>Creates a settings store rooted at the supplied <paramref name="paths"/>.</summary>
    /// <param name="paths">Shared storage layout (DI: register one instance for all stores).</param>
    public JsonSettingsStore(StoragePaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _paths = paths;
    }

    /// <inheritdoc />
    public async Task<AppSettings> LoadAsync(CancellationToken ct = default)
    {
        string path = _paths.SettingsFile;
        if (!File.Exists(path))
        {
            return new AppSettings();
        }

        try
        {
            await using FileStream stream = File.OpenRead(path);
            AppSettings? settings = await JsonSerializer
                .DeserializeAsync<AppSettings>(stream, JsonDefaults.Options, ct)
                .ConfigureAwait(false);
            return settings ?? new AppSettings();
        }
        catch (JsonException)
        {
            // Corrupt settings should not break the app; fall back to defaults.
            return new AppSettings();
        }
    }

    /// <inheritdoc />
    public async Task SaveAsync(AppSettings settings, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        string json = JsonSerializer.Serialize(settings, JsonDefaults.Options);
        await AtomicFile.WriteAllTextAsync(_paths.SettingsFile, json, ct).ConfigureAwait(false);
    }
}
