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
/// Edits the AGENT's LLM backend connection (provider · endpoint · model · API key · Azure deployment)
/// and persists it through an <see cref="IBackendSettingsGateway"/> — the seam the FL Agent plugin binds
/// to the real settings/secret stores, and which the standalone dev head satisfies with an in-memory
/// stub. Mirrors the fields the legacy WPF <c>SettingsViewModel</c> exposed. No backend types leak in
/// here: the whole card stays inside the backend-agnostic Avalonia UI lib.
///
/// <para>The API key is write-only: the raw value is never read back, only a "set" flag
/// (<see cref="HasApiKey"/>) so the UI shows a masked "•••• set" hint; leaving the box blank keeps the
/// stored key.</para>
/// </summary>
public sealed class BackendSettingsViewModel : ViewModelBase
{
    private IBackendSettingsGateway _gateway;
    private readonly RelayCommand _saveCommand;
    private readonly RelayCommand _testCommand;

    private string _selectedProvider = "Ollama";
    private string _endpoint = string.Empty;
    private string _model = string.Empty;
    private string _deployment = string.Empty;
    private string _newApiKey = string.Empty;
    private bool _hasApiKey;
    private bool _isBusy;
    private string? _status;

    public BackendSettingsViewModel(IBackendSettingsGateway gateway)
    {
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        Providers = new ObservableCollection<string>(gateway.Providers);
        _saveCommand = new RelayCommand(() => _ = SaveAsync(), () => !_isBusy);
        _testCommand = new RelayCommand(() => _ = TestAsync(), () => !_isBusy);
        Reload();
    }

    /// <summary>The selectable providers for the combo (from the gateway; mirrors the agent's backend enum).</summary>
    public ObservableCollection<string> Providers { get; private set; }

    public ICommand SaveCommand => _saveCommand;
    public ICommand TestCommand => _testCommand;

    /// <summary>Selected provider. Drives which extra fields (API key / Azure deployment) are relevant.</summary>
    public string SelectedProvider
    {
        get => _selectedProvider;
        set
        {
            if (SetProperty(ref _selectedProvider, value))
            {
                OnPropertyChanged(nameof(ShowApiKey));
                OnPropertyChanged(nameof(ShowDeployment));
            }
        }
    }

    /// <summary>Backend base URL.</summary>
    public string Endpoint { get => _endpoint; set => SetProperty(ref _endpoint, value); }

    /// <summary>Model id (must support tool calling).</summary>
    public string Model { get => _model; set => SetProperty(ref _model, value); }

    /// <summary>Azure OpenAI deployment name (Azure only).</summary>
    public string Deployment { get => _deployment; set => SetProperty(ref _deployment, value); }

    /// <summary>The API key the user is entering. Write-only: blank on save means "keep the stored key".</summary>
    public string NewApiKey { get => _newApiKey; set => SetProperty(ref _newApiKey, value); }

    /// <summary>True when a key is already stored (controls the masked "set" hint).</summary>
    public bool HasApiKey
    {
        get => _hasApiKey;
        private set
        {
            if (SetProperty(ref _hasApiKey, value))
                OnPropertyChanged(nameof(ApiKeyWatermark));
        }
    }

    /// <summary>Watermark for the API key box: signals a key is already stored without revealing it.</summary>
    public string ApiKeyWatermark => _hasApiKey
        ? "•••• set — leave blank to keep"
        : "Paste API key";

    /// <summary>API key applies to cloud backends; Ollama (local or proxied) doesn't need one here.</summary>
    public bool ShowApiKey => !string.Equals(SelectedProvider, "Ollama", StringComparison.OrdinalIgnoreCase);

    /// <summary>Deployment name is an Azure OpenAI concept only.</summary>
    public bool ShowDeployment => string.Equals(SelectedProvider, "AzureOpenAI", StringComparison.OrdinalIgnoreCase);

    /// <summary>True while a save / test is in flight (disables the buttons).</summary>
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(IsNotBusy));
                _saveCommand.RaiseCanExecuteChanged();
                _testCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>Convenience inverse of <see cref="IsBusy"/> for enabling inputs in XAML.</summary>
    public bool IsNotBusy => !_isBusy;

    /// <summary>Inline status / validation line under the Save + Test buttons.</summary>
    public string? Status { get => _status; private set => SetProperty(ref _status, value); }

    /// <summary>
    /// Swaps in the real gateway (the plugin calls this on the UI thread once the agent is wired) and
    /// re-reads the persisted settings. Before this runs the card shows the in-memory defaults.
    /// </summary>
    public void AttachGateway(IBackendSettingsGateway gateway)
    {
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        Providers = new ObservableCollection<string>(gateway.Providers);
        OnPropertyChanged(nameof(Providers));
        Reload();
    }

    /// <summary>Re-reads the current persisted settings into the fields (called when the panel opens).</summary>
    public void Reload()
    {
        BackendSettings s;
        try { s = _gateway.Load(); }
        catch (Exception ex) { Status = "Couldn't load backend settings: " + ex.Message; return; }

        SelectedProvider = Providers.Contains(s.Provider) ? s.Provider : (Providers.FirstOrDefault() ?? s.Provider);
        Endpoint = s.Endpoint;
        Model = s.Model;
        Deployment = s.Deployment;
        HasApiKey = s.HasApiKey;
        NewApiKey = string.Empty;
        Status = null;
    }

    private BackendSettings Capture() => new()
    {
        Provider = SelectedProvider,
        Endpoint = (Endpoint ?? string.Empty).Trim(),
        Model = (Model ?? string.Empty).Trim(),
        Deployment = (Deployment ?? string.Empty).Trim(),
        HasApiKey = HasApiKey,
    };

    private async Task SaveAsync()
    {
        if (_isBusy) return;

        BackendSettings dto = Capture();
        if (string.IsNullOrWhiteSpace(dto.Endpoint)) { Status = "Endpoint is required."; return; }
        if (string.IsNullOrWhiteSpace(dto.Model)) { Status = "Model is required."; return; }

        IsBusy = true;
        Status = "Saving…";
        string? newKey = string.IsNullOrWhiteSpace(NewApiKey) ? null : NewApiKey;
        try
        {
            await _gateway.SaveAsync(dto, newKey, CancellationToken.None).ConfigureAwait(true);
            if (newKey is not null) { HasApiKey = true; NewApiKey = string.Empty; }
            Status = "Saved and backend reconfigured.";
        }
        catch (Exception ex)
        {
            // The on-disk save happens before the live re-configure inside the gateway, so a failure here
            // means "persisted, but the new backend couldn't be brought up" — mirrors the legacy app.
            if (newKey is not null) { HasApiKey = true; NewApiKey = string.Empty; }
            Status = "Saved, but the backend couldn't be configured: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task TestAsync()
    {
        if (_isBusy) return;

        BackendSettings dto = Capture();
        IsBusy = true;
        Status = "Testing connection…";
        try
        {
            bool ok = await _gateway.TestConnectionAsync(dto, CancellationToken.None).ConfigureAwait(true);
            Status = ok
                ? $"Reachable: {dto.Provider} at {dto.Endpoint}"
                : $"Not reachable at {dto.Endpoint}. Is the backend running?";
        }
        catch (Exception ex)
        {
            Status = "Connection test failed: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }
}
