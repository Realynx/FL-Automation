using System;
using System.Threading;
using System.Threading.Tasks;
using FruityLink.Agent;
using FruityLink.Core.Abstractions;
using FruityLink.Ui.Avalonia.Hosting;
using FruityLink.Ui.Avalonia.Services;
using FruityLink.Ui.Avalonia.ViewModels;

namespace FruityLink.Plugins.FlAgent.Ui;

/// <summary>
/// Wires the agent-agnostic Avalonia <see cref="ChatViewModel"/> to the REUSED
/// <see cref="FruityLink.Agent.FlAgent"/> and the Whisper <see cref="IDictationService"/> — the Avalonia
/// analog of the wiring baked into the WPF <c>FlAgentChatWindow</c>. No LLM logic lives here: it just
/// streams <see cref="FruityLink.Agent.FlAgent.StreamAsync"/> deltas into the current assistant bubble,
/// surfaces <see cref="FruityLink.Agent.FlAgent.ToolInvoked"/> as tool chips, and drives dictation.
///
/// <para>All agent/dictation callbacks arrive on background threads and are marshalled onto the Avalonia
/// UI thread via <see cref="EmbeddedAvaloniaHost.Post"/>. The view-model's own events
/// (<see cref="ChatViewModel.MessageSubmitted"/> etc.) fire on the UI thread.</para>
/// </summary>
internal sealed class AvaloniaChatPresenter : IDisposable
{
    private readonly EmbeddedAvaloniaHost _host;
    private readonly ChatViewModel _vm;
    private readonly FruityLink.Agent.FlAgent _agent;
    private readonly IDictationService? _dictation;
    private readonly bool _ownsDictation;
    private readonly Action<string> _log;

    private CancellationTokenSource? _turnCts;
    private ChatMessage? _current;
    private string _dictationPrefix = string.Empty;
    private bool _disposed;

    public AvaloniaChatPresenter(
        EmbeddedAvaloniaHost host,
        ChatViewModel vm,
        FruityLink.Agent.FlAgent agent,
        Action<string> log,
        IDictationService? dictation,
        bool ownsDictation,
        IBackendSettingsGateway? backendSettings = null)
    {
        _host = host;
        _vm = vm;
        _agent = agent;
        _log = log;
        _dictation = dictation;
        _ownsDictation = ownsDictation;

        _host.Post(() => _vm.CanDictate = _dictation is not null);   // show the mic only when wired

        // Point the Settings "AI CONNECTION" card at the agent's real settings/secret stores (settings.json
        // + encrypted secrets.json). Marshalled onto the Avalonia UI thread; until then the card shows
        // in-memory defaults. Mirrors how agent access is wired in above.
        if (backendSettings is not null)
            _host.Post(() => _vm.AttachBackendGateway(backendSettings));

        _vm.MessageSubmitted += OnMessageSubmitted;
        _vm.CancelRequested += OnCancel;
        _vm.MicToggleRequested += OnMicToggle;
        _agent.ToolInvoked += OnToolInvoked;
        if (_dictation is not null)
        {
            _dictation.StateChanged += OnDictationStateChanged;
            _dictation.PartialTranscribed += OnPartialTranscribed;
        }
    }

    // ---- send / stream / cancel (MessageSubmitted fires on the UI thread) ----

    private void OnMessageSubmitted(string text)
    {
        _current = _vm.StartAssistantMessage();
        _vm.IsBusy = true;
        var cts = new CancellationTokenSource();
        _turnCts = cts;
        ChatMessage msg = _current;
        _ = Task.Run(() => RunTurnAsync(text, msg, cts.Token));
    }

    private async Task RunTurnAsync(string text, ChatMessage msg, CancellationToken ct)
    {
        try
        {
            await foreach (AgentDelta d in _agent.StreamAsync(text, ct).ConfigureAwait(false))
            {
                AgentDelta delta = d;
                _host.Post(() =>
                {
                    if (delta.Kind == AgentDeltaKind.Thought)
                        msg.Thoughts = (msg.Thoughts ?? string.Empty) + delta.Text;
                    else
                        msg.Text += delta.Text;
                });
            }

            _host.Post(() =>
            {
                if (msg.Text.Length == 0)
                    msg.Text = msg.HasToolCalls || msg.HasThoughts
                        ? "Done."
                        : "(The model returned no text. If it never calls tools, try a tool-capable model.)";
            });
        }
        catch (OperationCanceledException)
        {
            _host.Post(() => msg.Text += msg.Text.Length > 0 ? "\n\n(cancelled)" : "(cancelled)");
            _log("[fl-agent] turn cancelled by user.");
        }
        catch (Exception ex)
        {
            string detail = ex.Message;
            if (ex.InnerException is not null && ex.InnerException.Message != ex.Message)
                detail += " — " + ex.InnerException.Message;
            _host.Post(() => { msg.Text = "⚠ " + detail; msg.IsError = true; });
            _log("[fl-agent] turn error: " + ex.Message);
        }
        finally
        {
            CancellationTokenSource? cts = _turnCts;
            _turnCts = null;
            try { cts?.Dispose(); } catch { /* already disposed */ }
            _host.Post(() => _vm.IsBusy = false);
        }
    }

    private void OnCancel()
    {
        try { _turnCts?.Cancel(); }
        catch { /* already disposed / cancelled */ }
    }

    private void OnToolInvoked(ToolCallInfo info)
    {
        // Raised on the SK execution thread — marshal to the Avalonia UI thread.
        ChatMessage? m = _current;
        if (m is null) return;
        _host.Post(() =>
        {
            string args = string.IsNullOrEmpty(info.Arguments) ? string.Empty : "(" + info.Arguments + ")";
            m.ToolCalls.Add("→ " + info.Function + args);
        });
    }

    // ---- speech-to-text (Whisper) — mirrors the WPF chat window ----

    private void OnMicToggle() => _ = ToggleMicAsync();

    private async Task ToggleMicAsync()
    {
        if (_dictation is null || _vm.IsBusy) return;

        if (_dictation.State == DictationState.Recording) { await FinalizeDictationAsync(); return; }
        if (_dictation.State != DictationState.Idle) return;

        if (!_dictation.IsModelReady)
        {
            _host.Post(() => _vm.SetTransientStatus("Downloading speech model (~150 MB, one time)…"));
            var progress = new Progress<double>(p =>
                _host.Post(() => _vm.SetTransientStatus($"Downloading speech model… {p * 100:0}%")));
            try { await _dictation.EnsureModelAsync(progress).ConfigureAwait(false); }
            catch (Exception ex) { _host.Post(() => _vm.SetTransientStatus("Model download failed: " + ex.Message)); return; }
            _host.Post(() => _vm.SetTransientStatus(null));
        }

        _dictationPrefix = (_vm.InputText ?? string.Empty).TrimEnd();
        try { _dictation.StartRecording(); }
        catch (Exception ex) { _host.Post(() => _vm.SetTransientStatus("Microphone unavailable: " + ex.Message)); }
    }

    private async Task FinalizeDictationAsync()
    {
        if (_dictation is null) return;
        string prefix = _dictationPrefix;
        string text;
        try { text = await _dictation.StopAndTranscribeAsync().ConfigureAwait(false); }
        catch (Exception ex) { _host.Post(() => _vm.SetTransientStatus("Transcription failed: " + ex.Message)); return; }
        _host.Post(() => _vm.InputText = Combine(prefix, text));
    }

    private static string Combine(string prefix, string text)
    {
        if (string.IsNullOrEmpty(prefix)) return text;
        return string.IsNullOrEmpty(text) ? prefix : prefix + " " + text;
    }

    private void OnPartialTranscribed(string text)
    {
        _host.Post(() =>
        {
            if (_dictation is not null && _dictation.State == DictationState.Recording)
                _vm.InputText = Combine(_dictationPrefix, text);
        });
    }

    private void OnDictationStateChanged(object? sender, EventArgs e)
    {
        _host.Post(() =>
        {
            DictationState state = _dictation?.State ?? DictationState.Idle;
            _vm.IsRecording = state == DictationState.Recording;
            _vm.SetTransientStatus(state == DictationState.Transcribing ? "Transcribing…" : null);
        });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try { _turnCts?.Cancel(); } catch { /* best-effort */ }

        _vm.MessageSubmitted -= OnMessageSubmitted;
        _vm.CancelRequested -= OnCancel;
        _vm.MicToggleRequested -= OnMicToggle;
        _agent.ToolInvoked -= OnToolInvoked;

        if (_dictation is not null)
        {
            _dictation.StateChanged -= OnDictationStateChanged;
            _dictation.PartialTranscribed -= OnPartialTranscribed;
            try { _dictation.Cancel(); } catch { /* best-effort */ }
            if (_ownsDictation && _dictation is IDisposable disposable)
                try { disposable.Dispose(); } catch { /* best-effort */ }
        }
    }
}
