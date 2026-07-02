using System;
using System.Collections.ObjectModel;
using System.Windows.Input;
using FruityLink.Ui.Avalonia.Services;

namespace FruityLink.Ui.Avalonia.ViewModels;

/// <summary>
/// State + behaviour for the chat shell. Deliberately contains NO LLM logic — it exposes the seams a
/// host (the FL Agent plugin's presenter) wires to the real <c>FlAgent</c> + Whisper dictation, while
/// the standalone dev head runs it with no host at all:
///
///  • <see cref="MessageSubmitted"/> fires when the user sends text. The presenter starts a turn:
///    call <see cref="StartAssistantMessage"/> to add the reply bubble, then stream tokens into its
///    <see cref="ChatMessage.Text"/> / <see cref="ChatMessage.Thoughts"/> / <see cref="ChatMessage.ToolCalls"/>.
///  • <see cref="CancelRequested"/> fires when Send (acting as Cancel) is clicked mid-turn.
///  • <see cref="MicToggleRequested"/> fires when the mic button is clicked (dictation).
///  • <see cref="IsBusy"/> drives the Send↔Cancel affordance; <see cref="IsRecording"/> the mic state.
///
/// When no host is attached (standalone dev run) Send simply notes that no agent is wired, so the
/// themed shell stays visibly interactive.
/// </summary>
public sealed class ChatViewModel : ViewModelBase
{
    private readonly SettingsService _settings = SettingsService.Instance;
    private readonly BackendSettingsViewModel _backend;
    private string _inputText = string.Empty;
    private bool _isBusy;
    private bool _isRecording;
    private bool _canDictate;
    private bool _showThoughts;
    private bool _showToolCalls;
    private bool _isSettingsOpen;
    private string _statusText = string.Empty;
    private string? _transientStatus;

    /// <summary>
    /// Creates the chat shell. The optional <paramref name="backendGateway"/> is the seam to the AGENT's
    /// real backend config store (settings.json / encrypted secrets.json) that the Settings panel's
    /// "AI CONNECTION" card edits; when omitted (standalone dev head) an in-memory stub is used so the
    /// UI stays interactive with no agent wired. The FL Agent plugin attaches the real one after
    /// construction via <see cref="AttachBackendGateway"/>.
    /// </summary>
    public ChatViewModel(IBackendSettingsGateway? backendGateway = null)
    {
        // Seed the debug toggles from the persisted settings (single source of truth = SettingsService).
        _showThoughts = _settings.Current.ShowThoughts;
        _showToolCalls = _settings.Current.ShowToolCalls;

        _backend = new BackendSettingsViewModel(backendGateway ?? new InMemoryBackendSettingsGateway());

        SendCommand = new RelayCommand(OnSendOrCancel);
        MicCommand = new RelayCommand(OnToggleMic);
        ToggleSettingsCommand = new RelayCommand(() => IsSettingsOpen = !IsSettingsOpen);
        CloseSettingsCommand = new RelayCommand(() => IsSettingsOpen = false);

        Messages.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasMessages));

        AddInfo("FL Automate ready. Ask me to chop samples, organize your session, route the mixer, or set "
              + "up sidechains — I drive FL Studio natively. Enter sends; Shift+Enter for a new line.");
    }

    public ObservableCollection<ChatMessage> Messages { get; } = new();

    /// <summary>Raised (on the UI thread) when the user submits text. The agent host subscribes here.</summary>
    public event Action<string>? MessageSubmitted;

    /// <summary>Raised (on the UI thread) when the user clicks Send while a turn is streaming (= Cancel).</summary>
    public event Action? CancelRequested;

    /// <summary>Raised (on the UI thread) when the user clicks the mic button (start/stop dictation).</summary>
    public event Action? MicToggleRequested;

    public ICommand SendCommand { get; }
    public ICommand MicCommand { get; }

    /// <summary>Show/hide the in-place Settings panel (gear button in the header).</summary>
    public ICommand ToggleSettingsCommand { get; }

    /// <summary>Return from Settings to the chat (back arrow in the Settings panel).</summary>
    public ICommand CloseSettingsCommand { get; }

    public bool HasMessages => Messages.Count > 0;

    /// <summary>The AI-backend connection card shown in the Settings panel (provider / endpoint / model /
    /// API key / Azure deployment), backed by the agent's real settings + secret stores.</summary>
    public BackendSettingsViewModel Backend => _backend;

    /// <summary>True while the Settings panel is shown in place of the transcript + composer. Opening it
    /// re-reads the persisted backend settings so the fields always reflect what's on disk.</summary>
    public bool IsSettingsOpen
    {
        get => _isSettingsOpen;
        set
        {
            if (SetProperty(ref _isSettingsOpen, value) && value)
                _backend.Reload();
        }
    }

    /// <summary>Attach the real backend-settings gateway (called by the FL Agent plugin's presenter on the
    /// UI thread once the agent is composed). Until then the card shows in-memory defaults.</summary>
    public void AttachBackendGateway(IBackendSettingsGateway gateway) => _backend.AttachGateway(gateway);

    /// <summary>Where the settings JSON lives (surfaced in the Settings footer).</summary>
    public string SettingsFilePath => _settings.FilePath;

    public string InputText
    {
        get => _inputText;
        set => SetProperty(ref _inputText, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(SendButtonText));
                UpdateStatus();
            }
        }
    }

    /// <summary>The Send button doubles as Cancel while a turn is streaming.</summary>
    public string SendButtonText => _isBusy ? "Cancel" : "Send";

    public bool IsRecording
    {
        get => _isRecording;
        set
        {
            if (SetProperty(ref _isRecording, value))
                UpdateStatus();
        }
    }

    /// <summary>True when a dictation service is wired — controls the mic button's visibility.</summary>
    public bool CanDictate
    {
        get => _canDictate;
        set => SetProperty(ref _canDictate, value);
    }

    /// <summary>Show the streamed "thinking" block on assistant turns. Backed by (and persisted to)
    /// the settings file; the Settings panel binds the same property, so a change there updates the chat
    /// live and survives a restart.</summary>
    public bool ShowThoughts
    {
        get => _showThoughts;
        set
        {
            if (SetProperty(ref _showThoughts, value))
            {
                _settings.Current.ShowThoughts = value;
                _settings.Save();
                RefreshDebugVisibility();
            }
        }
    }

    /// <summary>Show the tool-call chips on assistant turns. Persisted like <see cref="ShowThoughts"/>.</summary>
    public bool ShowToolCalls
    {
        get => _showToolCalls;
        set
        {
            if (SetProperty(ref _showToolCalls, value))
            {
                _settings.Current.ShowToolCalls = value;
                _settings.Save();
                RefreshDebugVisibility();
            }
        }
    }

    /// <summary>Keep the transcript pinned to the newest message. Persisted; the view reads this when
    /// deciding whether to auto-scroll.</summary>
    public bool AutoScroll
    {
        get => _settings.Current.AutoScroll;
        set
        {
            if (_settings.Current.AutoScroll == value) return;
            _settings.Current.AutoScroll = value;
            _settings.Save();
            OnPropertyChanged();
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set
        {
            if (SetProperty(ref _statusText, value))
            {
                OnPropertyChanged(nameof(HasStatus));
                OnPropertyChanged(nameof(HeaderStatus));
            }
        }
    }

    public bool HasStatus => !string.IsNullOrEmpty(_statusText);

    /// <summary>The header status pill's text: the current activity when busy/recording, else "READY".</summary>
    public string HeaderStatus => string.IsNullOrEmpty(_statusText) ? "READY" : _statusText;

    // ---- host-facing helpers (called on the UI thread) ---------------------

    /// <summary>Add an assistant reply bubble and return it so the host can stream into it. Applies the
    /// current Thoughts / Tool-calls toggle state so streamed reasoning + chips honour the toggles.</summary>
    public ChatMessage StartAssistantMessage()
    {
        var msg = new ChatMessage(MessageRole.Assistant)
        {
            ThoughtsVisible = _showThoughts,
            ToolCallsVisible = _showToolCalls,
        };
        Messages.Add(msg);
        return msg;
    }

    /// <summary>Add a quiet system / info bubble (e.g. the ready banner or a backend note).</summary>
    public ChatMessage AddInfo(string text)
    {
        var msg = new ChatMessage(MessageRole.Info, text);
        Messages.Add(msg);
        return msg;
    }

    /// <summary>Show a transient status line (e.g. dictation model-download progress). Null clears it,
    /// falling back to the Working / Listening state.</summary>
    public void SetTransientStatus(string? text)
    {
        _transientStatus = text;
        UpdateStatus();
    }

    // ---- behaviour ---------------------------------------------------------

    private void OnSendOrCancel()
    {
        if (IsBusy)
        {
            CancelRequested?.Invoke();
            return;
        }

        string text = (InputText ?? string.Empty).Trim();
        if (text.Length == 0) return;

        Messages.Add(new ChatMessage(MessageRole.User, text));
        InputText = string.Empty;

        Action<string>? handler = MessageSubmitted;
        handler?.Invoke(text);

        // Standalone dev run (no agent host): keep the shell visibly interactive.
        if (handler is null)
        {
            var m = StartAssistantMessage();
            m.Text = "This is the themed UI shell — no agent is wired in standalone mode. "
                   + "Inside FL Studio your message streams back here with reasoning and tool calls.";
        }
    }

    private void OnToggleMic()
    {
        Action? handler = MicToggleRequested;
        if (handler is not null) handler();
        else IsRecording = !IsRecording;   // standalone: just toggle the visual state
    }

    private void UpdateStatus()
    {
        StatusText = _transientStatus
                   ?? (IsRecording ? "Listening…"
                   : IsBusy ? "Working…"
                   : string.Empty);
    }

    /// <summary>Push the global Thoughts / Tool-calls toggle state onto every message.</summary>
    private void RefreshDebugVisibility()
    {
        foreach (ChatMessage m in Messages)
        {
            m.ThoughtsVisible = _showThoughts;
            m.ToolCallsVisible = _showToolCalls;
        }
    }
}
