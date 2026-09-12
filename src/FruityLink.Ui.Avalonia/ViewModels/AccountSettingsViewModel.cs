using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using FruityLink.Ui.Avalonia.Services;

namespace FruityLink.Ui.Avalonia.ViewModels;

/// <summary>
/// One entry of the model picker: the pseudonymous <see cref="Id"/> persisted to settings and sent
/// to the gateway, plus the <see cref="Label"/> the ComboBox shows. The first entry is always the
/// <see cref="AccountSettingsViewModel.DefaultModelId"/> sentinel ("Auto (plan default)").
/// </summary>
public sealed class ModelOption
{
    public ModelOption(string id, string label)
    {
        Id = id ?? throw new ArgumentNullException(nameof(id));
        Label = string.IsNullOrWhiteSpace(label) ? id : label;
    }

    /// <summary>The model id persisted to settings and sent as <c>model</c> to the gateway.</summary>
    public string Id { get; }

    /// <summary>The friendly label shown in the picker.</summary>
    public string Label { get; }
}

/// <summary>
/// The Settings panel's ACCOUNT card: sign in to FL Automate with email/password, see the signed-in
/// identity + plan, and pick the model from the account's available list — all through an
/// <see cref="IAccountGateway"/>, the seam the FL Agent plugin binds to the real auth service +
/// settings store and which the standalone dev head satisfies with an in-memory stub. The API/
/// gateway base URLs are deliberately NOT on this card any more — they stay in settings.json as
/// dev-only hand-editable overrides. No auth/backend types leak in here: the whole card stays
/// inside the backend-agnostic Avalonia UI lib.
///
/// <para>The password is write-only: it flows into <see cref="IAccountGateway.LoginAsync"/> and the
/// box is cleared on every attempt; it is never stored on this view model past the call.</para>
///
/// <para>The model list refreshes from the gateway after sign-in, on gateway attach (session
/// restore), and whenever the card is (re)loaded while signed in — single-flight and throttled so
/// reopening Settings doesn't spam <c>/v1/models</c>. Offline or error keeps the sentinel + the
/// saved selection and surfaces a non-blocking status line.</para>
/// </summary>
public sealed class AccountSettingsViewModel : ViewModelBase
{
    /// <summary>The sentinel model id meaning "let the gateway pick the plan's default model".</summary>
    public const string DefaultModelId = "default";

    /// <summary>The label of the always-first sentinel entry in the picker.</summary>
    public const string DefaultModelLabel = "Auto (plan default)";

    /// <summary>Successful fetches younger than this are reused instead of re-hitting the gateway
    /// when the card merely reopens (sign-in and attach always force a fresh fetch).</summary>
    private static readonly TimeSpan ModelListMaxAge = TimeSpan.FromSeconds(30);

    private IAccountGateway _gateway;
    private readonly RelayCommand _signInCommand;
    private readonly RelayCommand _signOutCommand;
    private readonly RelayCommand _saveCommand;
    private readonly RelayCommand _testCommand;
    private readonly RelayCommand _openAccountPageCommand;

    private bool _isLoggedIn;
    private string _signedInEmail = string.Empty;
    private string _plan = string.Empty;
    private string _email = string.Empty;
    private string _password = string.Empty;
    private bool _isBusy;
    private string? _status;
    private bool _shareDebugData;

    /// <summary>The persisted model id (what settings.json currently holds, or what the user last
    /// saved). The picker's selection is derived from it; unknown ids stay visible as raw entries.</summary>
    private string _savedModelId = DefaultModelId;

    /// <summary>The last successfully fetched gateway list (null until the first fetch succeeds).</summary>
    private IReadOnlyList<AccountModel>? _fetchedModels;

    private ModelOption? _selectedModelOption;
    private bool _rebuildingOptions;        // suppress the ComboBox's binding echo during rebuilds
    private int _modelsRefreshInFlight;     // 1 while a fetch runs (single-flight gate)
    private DateTimeOffset _lastModelsFetchUtc = DateTimeOffset.MinValue;

    public AccountSettingsViewModel(IAccountGateway gateway)
    {
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        _signInCommand = new RelayCommand(() => _ = SignInAsync(), () => !_isBusy);
        _signOutCommand = new RelayCommand(() => _ = SignOutAsync(), () => !_isBusy);
        _saveCommand = new RelayCommand(() => _ = SaveAsync(), () => !_isBusy);
        _testCommand = new RelayCommand(() => _ = TestAsync(), () => !_isBusy);
        _openAccountPageCommand = new RelayCommand(OpenAccountPage);
        ModelOptions = new ObservableCollection<ModelOption> { new(DefaultModelId, DefaultModelLabel) };
        _selectedModelOption = ModelOptions[0];
        Reload();
    }

    public ICommand SignInCommand => _signInCommand;
    public ICommand SignOutCommand => _signOutCommand;
    public ICommand SaveCommand => _saveCommand;
    public ICommand TestCommand => _testCommand;

    /// <summary>Opens the marketing site's account page (manage/upgrade/downgrade the
    /// subscription) in the system browser. Deliberately not busy-gated — it's a plain link.</summary>
    public ICommand OpenAccountPageCommand => _openAccountPageCommand;

    /// <summary>True when a session is live — flips the card between its sign-in and account faces.</summary>
    public bool IsLoggedIn
    {
        get => _isLoggedIn;
        private set
        {
            if (SetProperty(ref _isLoggedIn, value))
            {
                OnPropertyChanged(nameof(IsLoggedOut));
                OnPropertyChanged(nameof(SignedInText));
            }
        }
    }

    /// <summary>Inverse of <see cref="IsLoggedIn"/> for XAML visibility bindings.</summary>
    public bool IsLoggedOut => !_isLoggedIn;

    /// <summary>"Signed in as email — Plan plan" header line for the logged-in face.</summary>
    public string SignedInText
    {
        get
        {
            if (!_isLoggedIn) return string.Empty;
            string plan = string.IsNullOrWhiteSpace(_plan)
                ? string.Empty
                : $" — {char.ToUpperInvariant(_plan[0])}{_plan[1..]} plan";
            return $"Signed in as {_signedInEmail}{plan}";
        }
    }

    /// <summary>
    /// The "Share debug data" opt-in (upload per-turn debug transcripts — tool calls + LLM
    /// responses — to FL Automate for remote review). Default off. Persists immediately on toggle
    /// (no Save button round-trip: a privacy switch must never sit unsaved), reverting the checkbox
    /// if the save fails so the UI never claims an opt-in state that didn't land on disk.
    /// </summary>
    public bool ShareDebugData
    {
        get => _shareDebugData;
        set
        {
            if (!SetProperty(ref _shareDebugData, value)) return;
            _ = PersistShareDebugDataAsync(value);
        }
    }

    private async Task PersistShareDebugDataAsync(bool enabled)
    {
        try
        {
            await _gateway.SaveShareDebugDataAsync(enabled, CancellationToken.None).ConfigureAwait(true);
            Status = enabled
                ? "Debug data sharing is ON — each AI turn's debug log uploads to FL Automate."
                : "Debug data sharing is off.";
        }
        catch (Exception ex)
        {
            _shareDebugData = !enabled;   // the save didn't land — the checkbox must not lie
            OnPropertyChanged(nameof(ShareDebugData));
            Status = "Couldn't save the debug-data preference: " + ex.Message;
        }
    }

    /// <summary>Email box (sign-in face).</summary>
    public string Email { get => _email; set => SetProperty(ref _email, value); }

    /// <summary>Password box (sign-in face; cleared on every attempt, never stored).</summary>
    public string Password { get => _password; set => SetProperty(ref _password, value); }

    /// <summary>The picker's entries: the "Auto (plan default)" sentinel first, then the account's
    /// fetched models, then (if the saved id matches none of them) the raw saved id itself — so a
    /// stale/unknown selection is never silently dropped.</summary>
    public ObservableCollection<ModelOption> ModelOptions { get; }

    /// <summary>The picked entry (never null in practice; a null push from a mid-rebuild ComboBox
    /// echo is ignored). Persisted by Save.</summary>
    public ModelOption? SelectedModelOption
    {
        get => _selectedModelOption;
        set
        {
            if (_rebuildingOptions || value is null) return;   // ComboBox echoes during item swaps
            SetProperty(ref _selectedModelOption, value);
        }
    }

    /// <summary>True while a sign-in / save / test is in flight (disables the buttons).</summary>
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(IsNotBusy));
                _signInCommand.RaiseCanExecuteChanged();
                _signOutCommand.RaiseCanExecuteChanged();
                _saveCommand.RaiseCanExecuteChanged();
                _testCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>Convenience inverse of <see cref="IsBusy"/> for enabling inputs in XAML.</summary>
    public bool IsNotBusy => !_isBusy;

    /// <summary>Inline status / validation line on the card.</summary>
    public string? Status { get => _status; private set => SetProperty(ref _status, value); }

    /// <summary>
    /// Swaps in the real gateway (the plugin calls this on the UI thread once the agent is wired)
    /// and re-reads the persisted state. Before this runs the card shows the in-memory defaults.
    /// A restored session forces a fresh model-list fetch.
    /// </summary>
    public void AttachGateway(IAccountGateway gateway)
    {
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        _fetchedModels = null;                            // the old gateway's list is meaningless now
        _lastModelsFetchUtc = DateTimeOffset.MinValue;
        Reload();
    }

    /// <summary>Re-reads the current account state into the fields (called when the panel opens).
    /// While signed in this also kicks a background model-list refresh (throttled).</summary>
    public void Reload()
    {
        AccountSnapshot s;
        try { s = _gateway.Load(); }
        catch (Exception ex) { Status = "Couldn't load account settings: " + ex.Message; return; }

        Apply(s);
        Status = null;
        if (s.IsLoggedIn)
            _ = RefreshModelsAsync();   // fire-and-forget; failures land in Status, never throw
    }

    /// <summary>
    /// Fetches the account's model list from the gateway and rebuilds the picker. Single-flight
    /// (a second call while one runs is a no-op) and, unless <paramref name="force"/>, throttled to
    /// one fetch per <see cref="ModelListMaxAge"/>. Failures keep the current entries (sentinel +
    /// saved id) and set a non-blocking status line. Public so the FL host and tests can await it.
    /// </summary>
    public async Task RefreshModelsAsync(bool force = false)
    {
        if (!_isLoggedIn) return;
        if (!force && _fetchedModels is not null
            && DateTimeOffset.UtcNow - _lastModelsFetchUtc < ModelListMaxAge)
        {
            return;   // fresh enough — reopening the card shouldn't spam /v1/models
        }
        if (Interlocked.CompareExchange(ref _modelsRefreshInFlight, 1, 0) != 0)
            return;   // one fetch at a time

        try
        {
            IReadOnlyList<AccountModel> models =
                await _gateway.ListModelsAsync(CancellationToken.None).ConfigureAwait(true);
            _fetchedModels = models;
            _lastModelsFetchUtc = DateTimeOffset.UtcNow;
            RebuildModelOptions();
        }
        catch (Exception ex)
        {
            // Offline / auth hiccup: keep the sentinel + saved selection, tell the user quietly.
            Status = "Couldn't load the model list — using your saved selection. (" + ex.Message + ")";
        }
        finally
        {
            Interlocked.Exchange(ref _modelsRefreshInFlight, 0);
        }
    }

    private void Apply(AccountSnapshot s)
    {
        _signedInEmail = s.Email;
        _plan = s.Plan;
        IsLoggedIn = s.IsLoggedIn;
        OnPropertyChanged(nameof(SignedInText));
        // Seed the persisted value through the FIELD (not the property) — the property setter
        // persists on change, and applying a loaded snapshot must never trigger a redundant save.
        _shareDebugData = s.ShareDebugData;
        OnPropertyChanged(nameof(ShareDebugData));
        _savedModelId = string.IsNullOrWhiteSpace(s.Model) ? DefaultModelId : s.Model.Trim();
        RebuildModelOptions();
        if (s.IsLoggedIn) { Email = string.Empty; Password = string.Empty; }
    }

    /// <summary>
    /// Rebuilds the picker from the last fetched list + the saved id, then re-selects:
    /// sentinel first, fetched models next (in gateway order — default model first), and, when the
    /// saved id matches none of them, the raw saved id as a trailing extra entry so a stale saved
    /// selection stays visible and selected instead of being silently lost. An EMPTY fetched list
    /// (plan not configured yet) is not an error — the picker just shows the sentinel.
    /// </summary>
    private void RebuildModelOptions()
    {
        _rebuildingOptions = true;
        try
        {
            ModelOptions.Clear();
            var sentinel = new ModelOption(DefaultModelId, DefaultModelLabel);
            ModelOptions.Add(sentinel);

            if (_fetchedModels is not null)
            {
                foreach (AccountModel m in _fetchedModels)
                {
                    if (string.Equals(m.Id, DefaultModelId, StringComparison.OrdinalIgnoreCase)) continue;
                    if (ModelOptions.Any(o => string.Equals(o.Id, m.Id, StringComparison.Ordinal))) continue;
                    ModelOptions.Add(new ModelOption(m.Id, m.DisplayName));
                }
            }

            ModelOption? selected = ModelOptions.FirstOrDefault(
                o => string.Equals(o.Id, _savedModelId, StringComparison.Ordinal));
            if (selected is null)
            {
                // Saved id the gateway no longer lists (plan change, stale settings.json): keep it
                // pickable under its raw id rather than snapping the user to another model.
                selected = new ModelOption(_savedModelId, _savedModelId);
                ModelOptions.Add(selected);
            }

            _selectedModelOption = selected;
        }
        finally
        {
            _rebuildingOptions = false;
        }
        OnPropertyChanged(nameof(SelectedModelOption));
    }

    private void OpenAccountPage()
    {
        try
        {
            string url = _gateway.AccountPageUrl;
            // UseShellExecute hands the URL to the OS's default browser — required inside the
            // embedded FL host, where there is no Avalonia TopLevel launcher to ask.
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Status = "Couldn't open the browser: " + ex.Message;
        }
    }

    /// <summary>
    /// Shared scaffold for the card's async operations: busy-gate (a re-entrant call is a no-op),
    /// busy flag + "…ing" status line, error mapping into <see cref="Status"/> (defaults to the raw
    /// <see cref="Exception.Message"/>), and an always-run <paramref name="beforeUnbusy"/> hook that
    /// fires in the finally BEFORE the busy flag drops (e.g. clearing the password).
    /// </summary>
    private async Task RunGuardedAsync(string busyStatus, Func<Task> body,
        Func<Exception, string>? mapError = null, Action? beforeUnbusy = null)
    {
        if (_isBusy) return;

        IsBusy = true;
        Status = busyStatus;
        try
        {
            await body().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Status = mapError is null ? ex.Message : mapError(ex);
        }
        finally
        {
            beforeUnbusy?.Invoke();
            IsBusy = false;
        }
    }

    private async Task SignInAsync()
    {
        if (_isBusy) return;

        string email = (Email ?? string.Empty).Trim();
        string password = Password ?? string.Empty;
        if (email.Length == 0) { Status = "Enter your account email."; return; }
        if (password.Length == 0) { Status = "Enter your password."; return; }

        await RunGuardedAsync("Signing in…", async () =>
        {
            AccountSnapshot s = await _gateway.LoginAsync(email, password, CancellationToken.None).ConfigureAwait(true);
            Apply(s);
            Status = "Signed in.";
            await RefreshModelsAsync(force: true).ConfigureAwait(true);   // fresh account ⇒ fresh list
        },
        beforeUnbusy: () => Password = string.Empty);   // never keep the password around past the attempt
    }

    private Task SignOutAsync() => RunGuardedAsync("Signing out…", async () =>
    {
        AccountSnapshot s = await _gateway.LogoutAsync(CancellationToken.None).ConfigureAwait(true);
        _fetchedModels = null;                            // the list belongs to the old session
        _lastModelsFetchUtc = DateTimeOffset.MinValue;
        Apply(s);
        Status = "Signed out.";
    },
    mapError: ex => "Sign-out hit a snag (session cleared locally): " + ex.Message);

    private Task SaveAsync() => RunGuardedAsync("Saving…", async () =>
    {
        string model = _selectedModelOption?.Id ?? DefaultModelId;
        await _gateway.SaveModelAsync(model, CancellationToken.None).ConfigureAwait(true);
        _savedModelId = model;
        Status = "Saved and applied.";
    },
    // The on-disk save happens before the live re-configure inside the gateway, so a failure
    // here means "persisted, but the agent couldn't be brought up on the new settings".
    mapError: ex => "Saved, but the agent couldn't be reconfigured: " + ex.Message);

    private Task TestAsync() => RunGuardedAsync("Testing connection…", async () =>
    {
        var models = await _gateway.TestConnectionAsync(CancellationToken.None).ConfigureAwait(true);
        Status = models.Count == 0
            ? "Gateway reachable — no models listed for your plan."
            : $"Gateway reachable — models: {string.Join(", ", models)}";
    });
}
