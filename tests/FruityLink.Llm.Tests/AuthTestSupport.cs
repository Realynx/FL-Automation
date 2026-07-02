using System.Text;
using System.Text.Json;
using FruityLink.Core.Abstractions;
using FruityLink.Core.Configuration;
using FruityLink.Llm.Auth;

namespace FruityLink.Llm.Tests;

/// <summary>Shared fakes + builders for the account-auth test suites.</summary>
internal static class AuthTestSupport
{
    /// <summary>
    /// Builds a structurally valid (unsigned) JWT whose payload carries the marketing API's claims.
    /// The client never verifies signatures, so "sig" is fine as the third segment.
    /// </summary>
    public static string MakeJwt(
        string email = "fox@example.com",
        string tier = "pro",
        string plan = "pro",
        bool subActive = true,
        TimeSpan? expiresIn = null)
    {
        long exp = DateTimeOffset.UtcNow.Add(expiresIn ?? TimeSpan.FromMinutes(15)).ToUnixTimeSeconds();
        string payload = JsonSerializer.Serialize(new { email, tier, plan, subActive, exp });
        static string B64Url(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return $"{B64Url("""{"alg":"RS256","typ":"JWT"}""")}.{B64Url(payload)}.sig";
    }

    /// <summary>JSON body of a login/refresh token-pair response.</summary>
    public static string TokenPairJson(string accessToken, string refreshToken)
        => JsonSerializer.Serialize(new { accessToken, refreshToken });
}

/// <summary>In-memory <see cref="ISettingsStore"/> for auth tests (no disk).</summary>
internal sealed class InMemorySettingsStore : ISettingsStore
{
    public AppSettings Current { get; set; } = new();

    public Task<AppSettings> LoadAsync(CancellationToken ct = default) => Task.FromResult(Current);

    public Task SaveAsync(AppSettings settings, CancellationToken ct = default)
    {
        Current = settings;
        return Task.CompletedTask;
    }
}

/// <summary>In-memory <see cref="ISecretStore"/> for auth tests (no DPAPI, no disk).</summary>
internal sealed class InMemorySecretStore : ISecretStore
{
    public Dictionary<string, string> Secrets { get; } = new(StringComparer.Ordinal);

    public Task SetAsync(string name, string secret, CancellationToken ct = default)
    {
        Secrets[name] = secret;
        return Task.CompletedTask;
    }

    public Task<string?> GetAsync(string name, CancellationToken ct = default)
        => Task.FromResult(Secrets.TryGetValue(name, out string? v) ? v : null);

    public Task DeleteAsync(string name, CancellationToken ct = default)
    {
        Secrets.Remove(name);
        return Task.CompletedTask;
    }
}

/// <summary>Scriptable <see cref="IAccountAuth"/> for handler/factory tests.</summary>
internal sealed class FakeAccountAuth : IAccountAuth
{
    public string? AccessToken { get; set; } = "token-1";
    public string? RefreshedToken { get; set; } = "token-2";
    public int RefreshCalls { get; private set; }

    public bool IsLoggedIn => AccessToken is not null;
    public AccountSession? Session { get; set; }
    public event Action? StateChanged { add { } remove { } }

    public Task<AccountSession> LoginAsync(string email, string password, CancellationToken ct = default)
        => throw new NotSupportedException();

    public Task LogoutAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task<string?> GetAccessTokenAsync(CancellationToken ct = default)
        => Task.FromResult(AccessToken);

    public Task<string?> RefreshAccessTokenAsync(CancellationToken ct = default)
    {
        RefreshCalls++;
        AccessToken = RefreshedToken;
        return Task.FromResult(RefreshedToken);
    }

    public Task<bool> TryRestoreAsync(CancellationToken ct = default) => Task.FromResult(IsLoggedIn);
}
