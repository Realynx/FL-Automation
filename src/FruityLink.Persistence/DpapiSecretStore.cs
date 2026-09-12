using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using FruityLink.Core.Abstractions;

namespace FruityLink.Persistence;

/// <summary>
/// Windows DPAPI-backed <see cref="ISecretStore"/>. Secrets are stored in <c>secrets.json</c>
/// as a logical-name → base64 map, where each value is the UTF-8 secret encrypted with
/// <see cref="ProtectedData.Protect(byte[], byte[], DataProtectionScope)"/> under
/// <see cref="DataProtectionScope.CurrentUser"/>. Secrets are never logged.
/// </summary>
/// <remarks>DPAPI is Windows-only; this assembly targets <c>net9.0-windows</c>.</remarks>
[SupportedOSPlatform("windows")]
public sealed class DpapiSecretStore : ISecretStore
{
    private readonly StoragePaths _paths;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Creates a secret store rooted at the supplied <paramref name="paths"/>.</summary>
    /// <param name="paths">Shared storage layout (DI: register one instance for all stores).</param>
    public DpapiSecretStore(StoragePaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _paths = paths;
    }

    /// <inheritdoc />
    public async Task SetAsync(string name, string secret, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(secret);

        byte[] protectedBytes = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(secret),
            optionalEntropy: null,
            scope: DataProtectionScope.CurrentUser);
        string encoded = Convert.ToBase64String(protectedBytes);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Dictionary<string, string> map = await ReadMapAsync(ct).ConfigureAwait(false);
            map[name] = encoded;
            await WriteMapAsync(map, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<string?> GetAsync(string name, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Dictionary<string, string> map = await ReadMapAsync(ct).ConfigureAwait(false);
            if (!map.TryGetValue(name, out string? encoded))
            {
                return null;
            }

            try
            {
                byte[] plain = ProtectedData.Unprotect(
                    Convert.FromBase64String(encoded),
                    optionalEntropy: null,
                    scope: DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(plain);
            }
            catch (Exception ex) when (ex is CryptographicException or FormatException)
            {
                // Value is unreadable (e.g. encrypted by another user/machine); treat as absent.
                return null;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task DeleteAsync(string name, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Dictionary<string, string> map = await ReadMapAsync(ct).ConfigureAwait(false);
            if (map.Remove(name))
            {
                await WriteMapAsync(map, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<Dictionary<string, string>> ReadMapAsync(CancellationToken ct)
    {
        Dictionary<string, string>? map = await JsonFile
            .TryReadAsync<Dictionary<string, string>>(_paths.SecretsFile, ct)
            .ConfigureAwait(false);
        return map is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(map, StringComparer.Ordinal);
    }

    private async Task WriteMapAsync(Dictionary<string, string> map, CancellationToken ct) =>
        await JsonFile.WriteAsync(_paths.SecretsFile, map, ct).ConfigureAwait(false);
}
