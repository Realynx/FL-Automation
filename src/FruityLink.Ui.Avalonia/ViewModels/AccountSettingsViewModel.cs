using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using FruityLink.Ui.Avalonia.Services;

namespace FruityLink.Ui.Avalonia.ViewModels;

/// <summary>
/// The Settings panel's ACCOUNT card: sign in to FL Automate with email/password, see the signed-in
/// identity + plan, pick the model, and (under Advanced) point at a non-default API/gateway — all
/// through an <see cref="IAccountGateway"/>, the seam the FL Agent plugin binds to the real auth
/// service + settings store and which the standalone dev head satisfies with an in-memory stub.
/// No auth/backend types leak in here: the whole card stays inside the backend-agnostic Avalonia
/// UI lib.
///
/// <para>The password is write-only: it flows into <see cref="IAccountGateway.LoginAsync"/> and the
/// box is cleared on every attempt; it is never stored on this view model past the call.</para>
/// </summary>
public sealed class AccountSettingsViewModel : ViewModelBase
{
    private IAccountGateway _gateway;
    private readonly RelayCommand _signInCommand;
    private readonly RelayCommand _signOutCommand;
    private readonly RelayCommand _saveCommand;
    private readonly RelayCommand _testCommand;

    private bool _isLoggedIn;
    private string _signedInEmail = string.Empty;
    private string _plan = string.Empty;
    private string _email = string.Empty;
    private string _password = string.Empty;
    private string _model = "default";
    private string _apiBaseUrl = string.Empty;
    private string _gatewayBaseUrl = string.Empty;
    private bool _showAdvanced;
    private bool _isBusy;
    private string? _status;

    public AccountSettingsViewModel(IAccountGateway gateway)
    {
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        _signInCommand = new RelayCommand(() => _ = SignInAsync(), () => !_isBusy);
        _signOutCommand = new RelayCommand(() => _ = SignOutAsync(), () => !_isBusy);
        _saveCommand = new RelayCommand(() => _ = SaveAsync(), () => !_isBusy);
        _testCommand = new RelayCommand(() => _ = TestAsync(), () => !_isBusy);
        Reload();
    }

    public ICommand SignInCommand => _signInCommand;
    public ICommand SignOutCommand => _signOutCommand;
    public ICommand SaveCommand => _saveCommand;
    public ICommand TestCommand => _testCommand;

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

    /// <summary>Email box (sign-in face).</summary>
    public string Email { get => _email; set => SetProperty(ref _email, value); }

    /// <summary>Password box (sign-in face; cleared on every attempt, never stored).</summary>
    public string Password { get => _password; set => SetProperty(ref _password, value); }

    /// <summary>Model id sent to the gateway. "default" uses the plan's default model.</summary>
    public string Model { get => _model; set => SetProperty(ref _model, value); }

    /// <summary>Advanced: marketing/auth API base URL.</summary>
    public string ApiBaseUrl { get => _apiBaseUrl; set => SetProperty(ref _apiBaseUrl, value); }

    /// <summary>Advanced: AI gateway base URL.</summary>
    public string GatewayBaseUrl { get => _gatewayBaseUrl; set => SetProperty(ref _gatewayBaseUrl, value); }

    /// <summary>Show/hide the advanced URL fields (collapsed by default).</summary>
    public bool ShowAdvanced
    {
        get => _showAdvanced;
        set
        {
            if (SetProperty(ref _showAdvanced, value))
                OnPropertyChanged(nameof(AdvancedToggleText));
        }
    }

    /// <summary>Chevron label for the advanced-section toggle.</summary>
    public string AdvancedToggleText => _showAdvanced ? "Advanced ▾" : "Advanced ▸";

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
    /// </summary>
    public void AttachGateway(IAccountGateway gateway)
    {
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        Reload();
    }

    /// <summary>Re-reads the current account state into the fields (called when the panel opens).</summary>
    public void Reload()
    {
        AccountSnapshot s;
        try { s = _gateway.Load(); }
        catch (Exception ex) { Status = "Couldn't load account settings: " + ex.Message; return; }

        Apply(s);
        Status = null;
    }

    private void Apply(AccountSnapshot s)
    {
        _signedInEmail = s.Email;
        _plan = s.Plan;
        IsLoggedIn = s.IsLoggedIn;
        OnPropertyChanged(nameof(SignedInText));
        Model = string.IsNullOrWhiteSpace(s.Model) ? "default" : s.Model;
        ApiBaseUrl = s.ApiBaseUrl;
        GatewayBaseUrl = s.GatewayBaseUrl;
        if (s.IsLoggedIn) { Email = string.Empty; Password = string.Empty; }
    }

    private async Task SignInAsync()
    {
        if (_isBusy) return;

        string email = (Email ?? string.Empty).Trim();
        string password = Password ?? string.Empty;
        if (email.Length == 0) { Status = "Enter your account email."; return; }
        if (password.Length == 0) { Status = "Enter your password."; return; }

        IsBusy = true;
        Status = "Signing in…";
        try
        {
            AccountSnapshot s = await _gateway.LoginAsync(email, password, CancellationToken.None).ConfigureAwait(true);
            Apply(s);
            Status = "Signed in.";
        }
        catch (Exception ex)
        {
            Status = ex.Message;
        }
        finally
        {
            Password = string.Empty;   // never keep the password around past the attempt
            IsBusy = false;
        }
    }

    private async Task SignOutAsync()
    {
        if (_isBusy) return;

        IsBusy = true;
        Status = "Signing out…";
        try
        {
            AccountSnapshot s = await _gateway.LogoutAsync(CancellationToken.None).ConfigureAwait(true);
            Apply(s);
            Status = "Signed out.";
        }
        catch (Exception ex)
        {
            Status = "Sign-out hit a snag (session cleared locally): " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task SaveAsync()
    {
        if (_isBusy) return;

        string gateway = (GatewayBaseUrl ?? string.Empty).Trim();
        string api = (ApiBaseUrl ?? string.Empty).Trim();
        if (api.Length == 0) { Status = "API URL is required (see Advanced)."; return; }
        if (gateway.Length == 0) { Status = "Gateway URL is required (see Advanced)."; return; }

        IsBusy = true;
        Status = "Saving…";
        try
        {
            string model = (Model ?? string.Empty).Trim();
            await _gateway.SaveAsync(model.Length == 0 ? "default" : model, api, gateway, CancellationToken.None)
                .ConfigureAwait(true);
            Status = "Saved and applied.";
        }
        catch (Exception ex)
        {
            // The on-disk save happens before the live re-configure inside the gateway, so a failure
            // here means "persisted, but the agent couldn't be brought up on the new settings".
            Status = "Saved, but the agent couldn't be reconfigured: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task TestAsync()
    {
        if (_isBusy) return;

        IsBusy = true;
        Status = "Testing connection…";
        try
        {
            var models = await _gateway.TestConnectionAsync(CancellationToken.None).ConfigureAwait(true);
            Status = models.Count == 0
                ? "Gateway reachable — no models listed for your plan."
                : $"Gateway reachable — models: {string.Join(", ", models)}";
        }
        catch (Exception ex)
        {
            Status = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }
}
