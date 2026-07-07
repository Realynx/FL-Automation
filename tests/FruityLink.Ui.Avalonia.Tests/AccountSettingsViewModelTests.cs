using FruityLink.Ui.Avalonia.Services;
using FruityLink.Ui.Avalonia.ViewModels;
using Shouldly;
using Xunit;

namespace FruityLink.Ui.Avalonia.Tests;

/// <summary>
/// Selection-mapping and refresh-discipline tests for the ACCOUNT card's model picker. The fake
/// gateway completes synchronously, so the view model's fire-and-forget refreshes finish inline
/// and every assertion below is deterministic without an Avalonia dispatcher.
/// </summary>
public sealed class AccountSettingsViewModelTests
{
    /// <summary>Scriptable <see cref="IAccountGateway"/>: canned snapshot + model list, counts
    /// <c>/v1/models</c> fetches, records saves, and can be told to fail the list call.</summary>
    private sealed class FakeGateway : IAccountGateway
    {
        public AccountSnapshot Snapshot { get; set; } = new();
        public IReadOnlyList<AccountModel> Models { get; set; } = Array.Empty<AccountModel>();
        public Exception? ListModelsError { get; set; }
        public int ListModelsCalls { get; private set; }
        public string? LastSavedModel { get; private set; }

        public string AccountPageUrl => "https://fl-automate.test/account";

        public AccountSnapshot Load() => Snapshot;

        public Task<AccountSnapshot> LoginAsync(string email, string password, CancellationToken ct = default)
        {
            Snapshot.IsLoggedIn = true;
            Snapshot.Email = email;
            Snapshot.Plan = "pro";
            return Task.FromResult(Snapshot);
        }

        public Task<AccountSnapshot> LogoutAsync(CancellationToken ct = default)
        {
            Snapshot.IsLoggedIn = false;
            Snapshot.Email = string.Empty;
            Snapshot.Plan = string.Empty;
            return Task.FromResult(Snapshot);
        }

        public Task SaveModelAsync(string model, CancellationToken ct = default)
        {
            LastSavedModel = model;
            Snapshot.Model = model;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<AccountModel>> ListModelsAsync(CancellationToken ct = default)
        {
            ListModelsCalls++;
            if (ListModelsError is not null) throw ListModelsError;
            return Task.FromResult(Models);
        }

        public Task<IReadOnlyList<string>> TestConnectionAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<string>>(Models.Select(m => m.DisplayName).ToList());
    }

    private static FakeGateway SignedIn(string savedModel = "default") => new()
    {
        Snapshot = new AccountSnapshot
        {
            IsLoggedIn = true,
            Email = "fox@foxmanager.pw",
            Plan = "pro",
            Model = savedModel,
        },
        Models = new[]
        {
            new AccountModel("swift-1", "FL Swift"),
            new AccountModel("deep-9", "FL Deep"),
        },
    };

    [Fact]
    public void Signed_out_the_picker_holds_just_the_auto_sentinel()
    {
        var vm = new AccountSettingsViewModel(new FakeGateway());

        vm.ModelOptions.Select(o => o.Id).ShouldBe(new[] { "default" });
        vm.ModelOptions[0].Label.ShouldBe("Auto (plan default)");
        vm.SelectedModelOption!.Id.ShouldBe("default");
    }

    [Fact]
    public void Signed_in_the_fetched_models_follow_the_sentinel_in_gateway_order()
    {
        var vm = new AccountSettingsViewModel(SignedIn());

        vm.ModelOptions.Select(o => o.Id).ShouldBe(new[] { "default", "swift-1", "deep-9" });
        vm.ModelOptions.Select(o => o.Label).ShouldBe(new[] { "Auto (plan default)", "FL Swift", "FL Deep" });
        vm.SelectedModelOption!.Id.ShouldBe("default");
        vm.Status.ShouldBeNull();
    }

    [Fact]
    public void A_saved_id_the_gateway_lists_is_selected_under_its_display_name()
    {
        var vm = new AccountSettingsViewModel(SignedIn(savedModel: "deep-9"));

        vm.SelectedModelOption!.Id.ShouldBe("deep-9");
        vm.SelectedModelOption.Label.ShouldBe("FL Deep");
        vm.ModelOptions.Count.ShouldBe(3);   // no duplicate raw-id entry
    }

    [Fact]
    public void A_stale_saved_id_stays_visible_and_selected_as_a_raw_entry()
    {
        var vm = new AccountSettingsViewModel(SignedIn(savedModel: "retired-model"));

        vm.SelectedModelOption!.Id.ShouldBe("retired-model");
        vm.SelectedModelOption.Label.ShouldBe("retired-model");   // labeled with its raw id
        vm.ModelOptions.Select(o => o.Id).ShouldBe(new[] { "default", "swift-1", "deep-9", "retired-model" });
    }

    [Fact]
    public void An_empty_model_list_is_not_an_error_just_the_sentinel()
    {
        FakeGateway gateway = SignedIn();
        gateway.Models = Array.Empty<AccountModel>();

        var vm = new AccountSettingsViewModel(gateway);

        vm.ModelOptions.Select(o => o.Id).ShouldBe(new[] { "default" });
        vm.SelectedModelOption!.Id.ShouldBe("default");
        vm.Status.ShouldBeNull();   // unconfigured plan is a quiet state, not a failure
    }

    [Fact]
    public void A_gateway_default_entry_is_not_duplicated_under_the_sentinel()
    {
        FakeGateway gateway = SignedIn();
        gateway.Models = new[] { new AccountModel("default", "Default"), new AccountModel("swift-1", "FL Swift") };

        var vm = new AccountSettingsViewModel(gateway);

        vm.ModelOptions.Select(o => o.Id).ShouldBe(new[] { "default", "swift-1" });
        vm.ModelOptions[0].Label.ShouldBe("Auto (plan default)");
    }

    [Fact]
    public void A_failed_fetch_keeps_the_saved_selection_and_reports_quietly()
    {
        FakeGateway gateway = SignedIn(savedModel: "deep-9");
        gateway.ListModelsError = new InvalidOperationException("gateway unreachable");

        var vm = new AccountSettingsViewModel(gateway);

        vm.ModelOptions.Select(o => o.Id).ShouldBe(new[] { "default", "deep-9" });   // sentinel + saved id
        vm.SelectedModelOption!.Id.ShouldBe("deep-9");
        vm.Status.ShouldNotBeNull();
        vm.Status.ShouldContain("gateway unreachable");
    }

    [Fact]
    public async Task Reopening_the_card_reuses_a_fresh_list_but_force_refetches()
    {
        FakeGateway gateway = SignedIn();
        var vm = new AccountSettingsViewModel(gateway);
        gateway.ListModelsCalls.ShouldBe(1);   // constructor load kicked the first fetch

        vm.Reload();                            // card reopened seconds later
        gateway.ListModelsCalls.ShouldBe(1);    // throttled — no /v1/models spam

        await vm.RefreshModelsAsync(force: true);
        gateway.ListModelsCalls.ShouldBe(2);
    }

    [Fact]
    public void Sign_in_force_fetches_the_new_accounts_models()
    {
        var gateway = new FakeGateway
        {
            Models = new[] { new AccountModel("swift-1", "FL Swift") },
        };
        var vm = new AccountSettingsViewModel(gateway);
        gateway.ListModelsCalls.ShouldBe(0);    // logged out — nothing to fetch

        vm.Email = "fox@foxmanager.pw";
        vm.Password = "hunter2";
        vm.SignInCommand.Execute(null);         // fake completes synchronously

        gateway.ListModelsCalls.ShouldBe(1);
        vm.ModelOptions.Select(o => o.Id).ShouldBe(new[] { "default", "swift-1" });
        vm.Password.ShouldBe(string.Empty);
    }

    [Fact]
    public void Save_persists_the_selected_options_id()
    {
        FakeGateway gateway = SignedIn();
        var vm = new AccountSettingsViewModel(gateway);

        vm.SelectedModelOption = vm.ModelOptions.Single(o => o.Id == "swift-1");
        vm.SaveCommand.Execute(null);           // fake completes synchronously

        gateway.LastSavedModel.ShouldBe("swift-1");
        vm.Status.ShouldBe("Saved and applied.");
    }

    [Fact]
    public void A_null_selection_push_from_a_mid_rebuild_combobox_echo_is_ignored()
    {
        var vm = new AccountSettingsViewModel(SignedIn(savedModel: "swift-1"));

        vm.SelectedModelOption = null;          // what the ComboBox pushes while items swap

        vm.SelectedModelOption.ShouldNotBeNull();
        vm.SelectedModelOption!.Id.ShouldBe("swift-1");
    }
}
