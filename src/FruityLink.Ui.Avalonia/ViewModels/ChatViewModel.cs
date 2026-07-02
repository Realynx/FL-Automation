using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Threading;
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
    private readonly AccountSettingsViewModel _account;
    private string _inputText = string.Empty;
    private bool _isBusy;
    private bool _isRecording;
    private bool _canDictate;
    private bool _showThoughts;
    private bool _showToolCalls;
    private bool _isSettingsOpen;
    private bool _isVersionsOpen;
    private string _statusText = string.Empty;
    private string? _transientStatus;

    // ---- dictation state machine (Idle → Recording → Finalizing → text-ready) --------------------
    // IsRecording (capturing audio) is driven by the host; IsFinalizing (running the FINAL Whisper
    // render after the mic stops) is a distinct busy phase that lights the mic spinner. _sendInFlight
    // serialises Send so a repeat click/Enter can't fire a second send while one is settling.
    // _dictationConsumed is the RACE GUARD: once a send has consumed the composer, any dictation
    // transcript routed through ApplyDictatedText is dropped so a late Whisper result can't resurrect
    // stale text (reset when a new recording begins).
    private bool _isFinalizing;
    private bool _sendInFlight;
    private bool _dictationConsumed;

    /// <summary>
    /// Creates the chat shell. The optional <paramref name="accountGateway"/> is the seam to the AGENT's
    /// real FL Automate account (auth service + settings.json) that the Settings panel's ACCOUNT card
    /// drives; when omitted (standalone dev head) an in-memory stub is used so the UI stays interactive
    /// with no agent wired. The FL Agent plugin attaches the real one after construction via
    /// <see cref="AttachAccountGateway"/>.
    /// </summary>
    public ChatViewModel(IAccountGateway? accountGateway = null)
    {
        // Seed the debug toggles from the persisted settings (single source of truth = SettingsService).
        _showThoughts = _settings.Current.ShowThoughts;
        _showToolCalls = _settings.Current.ShowToolCalls;

        _account = new AccountSettingsViewModel(accountGateway ?? new InMemoryAccountGateway());
        Versions = new VersionControlViewModel(new InMemoryProjectVersionControl());
        Versions.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(VersionControlViewModel.HasRecovery)) OnPropertyChanged(nameof(HasRecovery));
            if (e.PropertyName == nameof(VersionControlViewModel.RecoveryLabel)) OnPropertyChanged(nameof(RecoveryLabel));
        };

        SendCommand = new RelayCommand(OnSendOrCancel);
        MicCommand = new RelayCommand(OnToggleMic);
        ToggleSettingsCommand = new RelayCommand(() => IsSettingsOpen = !IsSettingsOpen);
        CloseSettingsCommand = new RelayCommand(() => IsSettingsOpen = false);
        ToggleVersionsCommand = new RelayCommand(() => IsVersionsOpen = !IsVersionsOpen);

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

    /// <summary>Show/hide the in-place Version-history panel (history button in the header).</summary>
    public ICommand ToggleVersionsCommand { get; }

    public bool HasMessages => Messages.Count > 0;

    /// <summary>The FL Automate ACCOUNT card shown in the Settings panel (sign in/out, plan display,
    /// model, advanced URLs), backed by the agent's real auth service + settings store.</summary>
    public AccountSettingsViewModel Account => _account;

    /// <summary>The AI edit-history / undo-redo / restore panel (project version control). Backed by the
    /// in-memory stub until the FL Agent plugin attaches the real gateway.</summary>
    public VersionControlViewModel Versions { get; }

    /// <summary>True while the Settings panel is shown in place of the transcript + composer. Opening it
    /// re-reads the persisted backend settings and closes the Version-history panel (single active overlay).</summary>
    public bool IsSettingsOpen
    {
        get => _isSettingsOpen;
        set
        {
            if (SetProperty(ref _isSettingsOpen, value))
            {
                if (value)
                {
                    if (_isVersionsOpen) { _isVersionsOpen = false; OnPropertyChanged(nameof(IsVersionsOpen)); }
                    _account.Reload();
                }
                OnPropertyChanged(nameof(IsChatVisible));
            }
        }
    }

    /// <summary>True while the Version-history panel is shown. Opening it closes Settings (single active
    /// overlay) and refreshes the timeline so it reflects current backend state.</summary>
    public bool IsVersionsOpen
    {
        get => _isVersionsOpen;
        set
        {
            if (SetProperty(ref _isVersionsOpen, value))
            {
                if (value)
                {
                    if (_isSettingsOpen) { _isSettingsOpen = false; OnPropertyChanged(nameof(IsSettingsOpen)); }
                    Versions.Refresh();
                }
                OnPropertyChanged(nameof(IsChatVisible));
            }
        }
    }

    /// <summary>True when the chat surface should show (neither overlay is open).</summary>
    public bool IsChatVisible => !_isSettingsOpen && !_isVersionsOpen;

    /// <summary>Pending crash-recovery flag, read through to the Version-history panel (for a chat-surface banner).</summary>
    public bool HasRecovery => Versions.HasRecovery;

    /// <summary>The pending crash-recovery label, or null.</summary>
    public string? RecoveryLabel => Versions.RecoveryLabel;

    /// <summary>Attach the real account gateway (called by the FL Agent plugin's presenter on the
    /// UI thread once the agent is composed). Until then the card shows in-memory defaults.</summary>
    public void AttachAccountGateway(IAccountGateway gateway) => _account.AttachGateway(gateway);

    /// <summary>Attach the real project version-control gateway (plugin presenter, UI thread), mirroring
    /// <see cref="AttachAccountGateway"/>. Until then the panel shows the in-memory stub.</summary>
    public void AttachVersionControl(IProjectVersionControl vc) => Versions.AttachGateway(vc);

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
            {
                // A fresh recording session re-opens the composer to dictation writes (clears the
                // "consumed by a send" race guard).
                if (value) _dictationConsumed = false;
                UpdateStatus();
            }
        }
    }

    /// <summary>
    /// True while the FINAL Whisper transcription is being produced AFTER the mic is stopped — a busy
    /// phase distinct from <see cref="IsRecording"/>. Drives the mic "busy" spinner and the header's
    /// "Transcribing…" status, and Send waits for it to clear before consuming the composer. Driven
    /// automatically from the host's transient-status hint (see <see cref="SetTransientStatus"/>); an
    /// updated host bridge may also set it directly.
    /// </summary>
    public bool IsFinalizing
    {
        get => _isFinalizing;
        set
        {
            if (SetProperty(ref _isFinalizing, value))
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
    /// falling back to the Working / Listening state.
    /// <para>Also infers <see cref="IsFinalizing"/> from a "Transcribing…" hint so the mic busy-spinner
    /// lights up during the final render even with a host bridge that only reports status text.</para></summary>
    public void SetTransientStatus(string? text)
    {
        _transientStatus = text;
        IsFinalizing = text is not null && text.Contains("Transcrib", StringComparison.OrdinalIgnoreCase);
        UpdateStatus();
    }

    // ---- dictation host seams (optional; wired by an updated presenter) -----

    /// <summary>
    /// Optional host hook that STOPS any in-progress recording and completes ONLY once the final
    /// transcript has been applied to the composer (idempotent — safe to call whether recording,
    /// finalizing, or already idle). When set, "Send while recording" awaits this so the flow becomes
    /// stop → wait (spinner shown) → send. When null, the view-model falls back to
    /// <see cref="MicToggleRequested"/> plus observing <see cref="IsRecording"/>/<see cref="IsFinalizing"/>.
    /// </summary>
    public Func<Task>? DictationFinalizer { get; set; }

    /// <summary>
    /// Apply a dictation transcript to the composer THROUGH the race guard: a transcript that lands
    /// after a send has consumed the composer (or after a newer recording superseded this one) is
    /// dropped, so a late Whisper result can never resurrect stale text. A host bridge should route
    /// BOTH partial and final transcripts here instead of assigning <see cref="InputText"/> directly.
    /// </summary>
    public void ApplyDictatedText(string? text)
    {
        if (_dictationConsumed) return;   // a send already consumed this recording session — drop it
        InputText = text ?? string.Empty;
    }

    // ---- behaviour ---------------------------------------------------------

    private void OnSendOrCancel()
    {
        // Cancel is synchronous + immediate; the actual send may need to await the final transcript.
        if (IsBusy)
        {
            CancelRequested?.Invoke();
            return;
        }

        _ = SendAsync();
    }

    /// <summary>
    /// Send gated on the dictation state machine: NEVER sends while recording or finalizing. If the
    /// user hits Send mid-dictation we (a) stop the mic, (b) await the final render with the mic
    /// spinner showing, (c) let the transcript land in the composer, then (d) send the completed text.
    /// Serialised via <see cref="_sendInFlight"/> so a repeat click/Enter can't double-fire.
    /// </summary>
    private async Task SendAsync()
    {
        if (_sendInFlight) return;
        _sendInFlight = true;
        try
        {
            // Finish any active dictation FIRST so the final transcript is in the box before we read it.
            if (CanDictate && (IsRecording || IsFinalizing))
                await FinishDictationForSendAsync();

            string text = (InputText ?? string.Empty).Trim();
            if (text.Length == 0) return;

            // Consume the composer. Set the race guard BEFORE clearing so any late transcript routed
            // through ApplyDictatedText for this session is discarded rather than left as a duplicate.
            _dictationConsumed = true;
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
        finally
        {
            _sendInFlight = false;
        }
    }

    /// <summary>
    /// Stop dictation and wait until the final transcript has been applied to the composer. Prefers the
    /// host's awaitable <see cref="DictationFinalizer"/>; otherwise asks the host to stop via
    /// <see cref="MicToggleRequested"/> and waits for the recording+final-render to settle back to idle,
    /// then lets any queued transcript post flush before the caller reads the composer.
    /// </summary>
    private async Task FinishDictationForSendAsync()
    {
        Func<Task>? finalizer = DictationFinalizer;
        if (finalizer is not null)
        {
            try { await finalizer().ConfigureAwait(true); }
            catch { /* fall through and send whatever is already in the composer */ }
            return;
        }

        // Fallback for a bridge that only exposes the toggle + state seams.
        if (IsRecording) MicToggleRequested?.Invoke();
        await WaitWhileAsync(() => IsRecording || IsFinalizing, TimeSpan.FromSeconds(60)).ConfigureAwait(true);

        // Let any queued (lower/equal priority) transcript UI post apply before we read the composer.
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
    }

    /// <summary>Await until <paramref name="condition"/> is false (observing property changes on the UI
    /// thread), or the timeout elapses so a stuck backend can never hang Send.</summary>
    private async Task WaitWhileAsync(Func<bool> condition, TimeSpan timeout)
    {
        if (!condition()) return;

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnChanged(object? s, PropertyChangedEventArgs e)
        {
            if (!condition()) tcs.TrySetResult();
        }

        PropertyChanged += OnChanged;
        try
        {
            if (!condition()) return;   // re-check after subscribing (avoids a missed transition)
            using var cts = new CancellationTokenSource(timeout);
            using (cts.Token.Register(() => tcs.TrySetResult()))
                await tcs.Task.ConfigureAwait(true);
        }
        finally
        {
            PropertyChanged -= OnChanged;
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
                   : IsFinalizing ? "Transcribing…"
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
