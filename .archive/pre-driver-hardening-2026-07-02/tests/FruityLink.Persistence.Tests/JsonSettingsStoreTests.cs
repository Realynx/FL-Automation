using FruityLink.Core.Configuration;
using FruityLink.Persistence;
using Shouldly;
using Xunit;

namespace FruityLink.Persistence.Tests;

public sealed class JsonSettingsStoreTests : TempStorageFixture
{
    private readonly JsonSettingsStore _store;

    public JsonSettingsStoreTests() => _store = new JsonSettingsStore(Paths);

    [Fact]
    public async Task Load_with_no_file_returns_defaults()
    {
        AppSettings settings = await _store.LoadAsync();

        settings.Theme.ShouldBe(AppTheme.Dark);
        settings.FirstRunCompleted.ShouldBeFalse();
        settings.LlmOrDefault.Backend.ShouldBe(LlmBackendKind.Ollama);
    }

    [Fact]
    public async Task Save_then_Load_round_trips_non_default_settings()
    {
        AppSettings original = new(
            Theme: AppTheme.Light,
            Llm: new LlmSettings(
                Backend: LlmBackendKind.OpenAI,
                Endpoint: "https://api.openai.com/v1",
                Model: "gpt-4o",
                ApiKeyRef: "openai-key"),
            FirstRunCompleted: true);

        await _store.SaveAsync(original);
        AppSettings loaded = await _store.LoadAsync();

        loaded.Theme.ShouldBe(AppTheme.Light);
        loaded.FirstRunCompleted.ShouldBeTrue();
        loaded.Llm.ShouldNotBeNull();
        loaded.Llm.Backend.ShouldBe(LlmBackendKind.OpenAI);
        loaded.Llm.Model.ShouldBe("gpt-4o");
        loaded.Llm.ApiKeyRef.ShouldBe("openai-key");
    }

    [Fact]
    public async Task Load_with_corrupt_file_returns_defaults()
    {
        await File.WriteAllTextAsync(Paths.SettingsFile, "{ this is not valid json ");

        AppSettings settings = await _store.LoadAsync();

        settings.Theme.ShouldBe(AppTheme.Dark);
    }
}
