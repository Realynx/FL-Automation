using FruityLink.Persistence;
using Shouldly;
using Xunit;

namespace FruityLink.Persistence.Tests;

public sealed class DpapiSecretStoreTests : TempStorageFixture
{
    private readonly DpapiSecretStore _store;

    public DpapiSecretStoreTests() => _store = new DpapiSecretStore(Paths);

    [Fact]
    public async Task Set_then_Get_returns_original_secret()
    {
        await _store.SetAsync("openai", "sk-secret-value-123");

        string? value = await _store.GetAsync("openai");

        value.ShouldBe("sk-secret-value-123");
    }

    [Fact]
    public async Task Get_missing_returns_null()
    {
        (await _store.GetAsync("not-set")).ShouldBeNull();
    }

    [Fact]
    public async Task Delete_removes_the_secret()
    {
        await _store.SetAsync("anthropic", "claude-key");
        (await _store.GetAsync("anthropic")).ShouldBe("claude-key");

        await _store.DeleteAsync("anthropic");

        (await _store.GetAsync("anthropic")).ShouldBeNull();
    }

    [Fact]
    public async Task Set_overwrites_existing_value()
    {
        await _store.SetAsync("key", "first");
        await _store.SetAsync("key", "second");

        (await _store.GetAsync("key")).ShouldBe("second");
    }

    [Fact]
    public async Task Multiple_secrets_coexist()
    {
        await _store.SetAsync("a", "alpha");
        await _store.SetAsync("b", "bravo");

        (await _store.GetAsync("a")).ShouldBe("alpha");
        (await _store.GetAsync("b")).ShouldBe("bravo");
    }

    [Fact]
    public async Task Secret_is_not_stored_in_plaintext()
    {
        await _store.SetAsync("key", "super-secret-plaintext");

        string fileContents = await File.ReadAllTextAsync(Paths.SecretsFile);

        fileContents.ShouldNotContain("super-secret-plaintext");
    }

    [Fact]
    public async Task Delete_missing_does_not_throw()
    {
        await Should.NotThrowAsync(() => _store.DeleteAsync("never-set"));
    }
}
