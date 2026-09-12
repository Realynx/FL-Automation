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
        // Corrupt settings should not break the app; fall back to defaults.
        return await JsonFile.TryReadAsync<AppSettings>(_paths.SettingsFile, ct).ConfigureAwait(false)
            ?? new AppSettings();
    }

    /// <inheritdoc />
    public async Task SaveAsync(AppSettings settings, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        await JsonFile.WriteAsync(_paths.SettingsFile, settings, ct).ConfigureAwait(false);
    }
}
