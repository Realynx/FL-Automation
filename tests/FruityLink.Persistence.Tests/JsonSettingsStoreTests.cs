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
        settings.AccountOrDefault.ApiBaseUrl.ShouldBe("https://fl-automate.com/api");
        settings.AccountOrDefault.GatewayBaseUrl.ShouldBe("https://ai.fl-automate.com");
        settings.AccountOrDefault.Model.ShouldBe("default");
        settings.AccountOrDefault.Email.ShouldBeNull();
    }

    [Fact]
    public async Task Save_then_Load_round_trips_non_default_settings()
    {
        AppSettings original = new(
            Theme: AppTheme.Light,
            Account: new AccountSettings(
                ApiBaseUrl: "http://localhost:3001/api",
                GatewayBaseUrl: "http://localhost:8787",
                Model: "big-brain")
            {
                Email = "fox@foxmanager.pw",
                Plan = "pro",
            },
            FirstRunCompleted: true);

        await _store.SaveAsync(original);
        AppSettings loaded = await _store.LoadAsync();

        loaded.Theme.ShouldBe(AppTheme.Light);
        loaded.FirstRunCompleted.ShouldBeTrue();
        loaded.Account.ShouldNotBeNull();
        loaded.Account.ApiBaseUrl.ShouldBe("http://localhost:3001/api");
        loaded.Account.GatewayBaseUrl.ShouldBe("http://localhost:8787");
        loaded.Account.Model.ShouldBe("big-brain");
        loaded.Account.Email.ShouldBe("fox@foxmanager.pw");
        loaded.Account.Plan.ShouldBe("pro");
    }

    [Fact]
    public async Task AllowParallelToolCalls_opt_out_survives_save_load_and_a_settings_card_style_merge()
    {
        // The flag is a manual settings.json opt-out (not surfaced in any UI), so every persistence
        // hop must carry it: (1) plain save/load, (2) the record-`with` merge the settings-card
        // gateway (AgentAccountGateway.SaveAsync) applies on every save — a positional
        // AccountSettings rebuild there would silently reset the flag to its default (true).
        AppSettings original = new(Account: new AccountSettings() { AllowParallelToolCalls = false });

        await _store.SaveAsync(original);
        AppSettings loaded = await _store.LoadAsync();
        loaded.Account!.AllowParallelToolCalls.ShouldBeFalse();

        AccountSettings merged = loaded.Account with
        {
            Model = "big-brain",
            ApiBaseUrl = "http://localhost:3001/api",
            GatewayBaseUrl = "http://localhost:8787",
        };
        await _store.SaveAsync(loaded with { Account = merged });

        AppSettings reloaded = await _store.LoadAsync();
        reloaded.Account!.Model.ShouldBe("big-brain");
        reloaded.Account.AllowParallelToolCalls.ShouldBeFalse();
    }

    [Fact]
    public async Task Load_with_corrupt_file_returns_defaults()
    {
        await File.WriteAllTextAsync(Paths.SettingsFile, "{ this is not valid json ");

        AppSettings settings = await _store.LoadAsync();

        settings.Theme.ShouldBe(AppTheme.Dark);
    }

    [Fact]
    public async Task Load_with_legacy_multi_provider_file_falls_back_to_account_defaults()
    {
        // A pre-account settings.json (provider/endpoint/apiKeyRef era). Unknown members are
        // ignored by System.Text.Json, so the file loads with Account = null → gateway defaults —
        // the documented migration story (the user just signs in again).
        await File.WriteAllTextAsync(Paths.SettingsFile, """
        {
          "theme": "Light",
          "llm": { "backend": "OpenAI", "endpoint": "https://api.openai.com/v1", "model": "gpt-4o", "apiKeyRef": "llm:apikey" },
          "firstRunCompleted": true
        }
        """);

        AppSettings settings = await _store.LoadAsync();

        settings.Theme.ShouldBe(AppTheme.Light);           // known members still load
        settings.FirstRunCompleted.ShouldBeTrue();
        settings.Account.ShouldBeNull();                   // legacy "llm" is simply dropped
        settings.AccountOrDefault.Model.ShouldBe("default");
    }
}
