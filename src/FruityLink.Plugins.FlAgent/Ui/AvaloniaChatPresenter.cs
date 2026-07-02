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
    private readonly FruityLink.Agent.ProjectVersionCoordinator? _versionCoordinator;

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
        IAccountGateway? accountGateway = null,
        FruityLink.Ui.Avalonia.Services.IProjectVersionControl? versionControl = null,
        FruityLink.Agent.ProjectVersionCoordinator? versionCoordinator = null)
    {
        _host = host;
        _vm = vm;
        _agent = agent;
        _log = log;
        _dictation = dictation;
        _ownsDictation = ownsDictation;
        _versionCoordinator = versionCoordinator;

        _host.Post(() => _vm.CanDictate = _dictation is not null);   // show the mic only when wired

        // Point the Settings ACCOUNT card at the agent's real FL Automate account (auth service +
        // settings.json). Marshalled onto the Avalonia UI thread; until then the card shows
        // in-memory defaults. Mirrors how agent access is wired in above.
        if (accountGateway is not null)
            _host.Post(() => _vm.AttachAccountGateway(accountGateway));

        // Point the Version-history panel at the agent's real project version control (same seam pattern).
        // When null, the panel keeps its in-memory stub (inert but interactive).
        if (versionControl is not null)
            _host.Post(() => _vm.AttachVersionControl(versionControl));

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
            // A failed turn no longer costs the user their context: the agent's turn runner keeps the
            // user's message + the ENTIRE prior conversation in history, so every failure is
            // recoverable — the user just says "try again" and the model resumes with full context.
            // Surface a clear, reassuring message (with the backend's HTTP status when it's a
            // transient 5xx/network failure worth retrying) instead of a raw exception string.
            string message = BuildTurnErrorMessage(ex);
            _host.Post(() => { msg.Text = message; msg.IsError = true; });
            _log("[fl-agent] turn error: " + ex.Message);
        }
        finally
        {
            CancellationTokenSource? cts = _turnCts;
            _turnCts = null;
            try { cts?.Dispose(); } catch { /* already disposed */ }
            _host.Post(() => _vm.IsBusy = false);

            // One project-state commit per AI work-unit, gated on a mutating tool having fired this turn.
            // Fire-and-forget on a background task so the .flp SaveCopy never blocks the UI thread; the
            // coordinator swallows any failure so a backup can't break the chat turn.
            FruityLink.Agent.ProjectVersionCoordinator? coordinator = _versionCoordinator;
            if (coordinator is not null)
                _ = Task.Run(() => coordinator.OnTurnCompletedAsync(null, CancellationToken.None));
        }
    }

    /// <summary>
    /// Builds the user-facing text for a failed turn. Because a failed turn now KEEPS the user's
    /// message and the full prior conversation (see <c>FruityLink.Agent.AgentTurnRunner</c>), every
    /// failure is recoverable: the message reassures the user their context is safe and tells them how
    /// to resume. A transient backend failure (HTTP 5xx / timeout / connection drop, or a null status
    /// from a network/budget abort) is flagged as an "AI backend error" with its status so the user
    /// knows a retry is worth it; a genuine 4xx (bad request) is labelled distinctly.
    /// </summary>
    private static string BuildTurnErrorMessage(Exception ex)
    {
        int? status = ExtractHttpStatus(ex);
        string detail = ex.Message;
        if (ex.InnerException is not null && ex.InnerException.Message != ex.Message)
            detail += " — " + ex.InnerException.Message;

        bool transient = status is null or >= 500;   // null = network/timeout/budget abort
        string header = transient
            ? (status is int s ? $"⚠ AI backend error ({s})" : "⚠ AI backend error")
            : $"⚠ Request error ({status})";

        string tail = "\n\nYour message and the conversation are kept" +
            (transient ? " — say \"try again\" to retry." : ".");
        return header + ": " + detail + tail;
    }

    /// <summary>
    /// Best-effort HTTP status extraction from an exception chain, without a compile-time dependency
    /// on Semantic Kernel's exception types: SK's <c>HttpOperationException</c> and
    /// <see cref="System.Net.Http.HttpRequestException"/> both expose a nullable
    /// <see cref="System.Net.HttpStatusCode"/> <c>StatusCode</c> property. Returns null when no link
    /// in the chain carries one (a network drop, timeout, or the per-turn request-budget abort).
    /// </summary>
    private static int? ExtractHttpStatus(Exception ex)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            object? value = e.GetType().GetProperty("StatusCode")?.GetValue(e);
            if (value is System.Net.HttpStatusCode code)
                return (int)code;
        }
        return null;
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
        // Route through the VM race guard: if a send already consumed the composer, this late final
        // transcript is dropped instead of resurrecting stale text as a duplicate.
        _host.Post(() => _vm.ApplyDictatedText(Combine(prefix, text)));
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
                _vm.ApplyDictatedText(Combine(_dictationPrefix, text));
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
        try { _versionCoordinator?.Dispose(); } catch { /* best-effort */ }

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
