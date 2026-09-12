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
    private readonly FruityLink.Llm.Auth.BugReportClient? _bugReports;

    private readonly DictationController? _dictationCtl;

    private CancellationTokenSource? _turnCts;
    private ChatMessage? _current;
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
        FruityLink.Agent.ProjectVersionCoordinator? versionCoordinator = null,
        FruityLink.Llm.Auth.BugReportClient? bugReports = null,
        FruityLink.Llm.Auth.UpdateCheckClient? updateCheck = null,
        string? clientVersion = null)
    {
        _host = host;
        _vm = vm;
        _agent = agent;
        _log = log;
        _dictation = dictation;
        _ownsDictation = ownsDictation;
        _versionCoordinator = versionCoordinator;
        _bugReports = bugReports;

        if (dictation is not null)
            _dictationCtl = new DictationController(
                dictation,
                isBusy: () => _vm.IsBusy,
                setStatus: s => _host.Post(() => _vm.SetTransientStatus(s)),
                // Route the FINAL transcript through the VM race guard (drops a late transcript if a send
                // already consumed the box) and AWAIT the apply so a caller awaiting the finalize (the Send
                // finalizer) sees the populated composer.
                applyFinalText: t => PostAwait(() => _vm.ApplyDictatedText(t)),
                applyPartialText: t => _vm.ApplyDictatedText(t),
                readComposer: () => _vm.InputText,
                marshalToUi: _host.Post);

        _host.Post(() =>
        {
            _vm.CanDictate = _dictation is not null;   // show the mic only when wired
            // Deterministic "finish dictation before Send": Send awaits this (stop → final render → apply)
            // instead of the VM's racy toggle-then-observe fallback that sent before IsFinalizing was set.
            if (_dictation is not null) _vm.DictationFinalizer = FinalizeDictationForSendAsync;
        });

        // Point the Settings ACCOUNT card at the agent's real FL Automate account (auth service +
        // settings.json). Marshalled onto the Avalonia UI thread; until then the card shows
        // in-memory defaults. Mirrors how agent access is wired in above.
        if (accountGateway is not null)
            _host.Post(() => _vm.AttachAccountGateway(accountGateway));

        // Point the Version-history panel at the agent's real project version control (same seam pattern).
        // When null, the panel keeps its in-memory stub (inert but interactive).
        if (versionControl is not null)
            _host.Post(() => _vm.AttachVersionControl(versionControl));

        // Once-per-run update check: ask the gateway whether a newer installer exists and, if so,
        // surface the dismissible banner. Deliberately fire-and-forget with a settling delay so it
        // never competes with the pre-warm/first-paint path, and quiet on every failure — an update
        // notice must never cost a user anything.
        if (updateCheck is not null && !string.IsNullOrWhiteSpace(clientVersion))
            _ = NotifyIfUpdateAvailableAsync(updateCheck, clientVersion);

        _vm.MessageSubmitted += OnMessageSubmitted;
        _vm.CancelRequested += OnCancel;
        _vm.MicToggleRequested += OnMicToggle;
        if (_bugReports is not null) _vm.BugReportRequested += OnBugReportRequested;
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
                        ? ChatTurnText.DoneFallback
                        : ChatTurnText.NoTextFallback;
            });
        }
        catch (OperationCanceledException)
        {
            _host.Post(() => msg.Text += msg.Text.Length > 0 ? "\n\n" + ChatTurnText.Cancelled : ChatTurnText.Cancelled);
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
            _host.Post(() =>
            {
                if (msg.Text.Length > 0)
                {
                    // The stream died AFTER answer text reached the bubble: KEEP the partial and
                    // APPEND a marker (mirroring the cancellation branch above) — overwriting here
                    // would throw away text the user is mid-reading. The runner recorded the same
                    // partial as the assistant turn in history, so "continue" picks up from it.
                    // Not flagged IsError: the partial is real answer content, and the error-style
                    // retry affordance would misrepresent a half-delivered reply as a total loss.
                    msg.Text += ChatTurnText.PartialKeptNotice;
                }
                else
                {
                    msg.Text = message;
                    msg.IsError = true;
                }
            });
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
        string detail = ChatTurnText.BuildDetail(ex);

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

    // ---- bug reports ("Report bug" on a failed turn; fires on the UI thread) ----

    /// <summary>How many trailing transcript turns ride along with a bug report (context for triage).</summary>
    private const int BugReportTranscriptTurns = 12;

    /// <summary>
    /// The once-per-run update check. Waits a few seconds so the boot path (pre-warm, first paint,
    /// session restore) settles first, then asks the gateway's public <c>/v1/client-version</c>
    /// whether a newer installer exists. On "yes" the dismissible banner appears in the chat with
    /// a Download link (the installer handles the actual upgrade); on "no"/offline/any failure
    /// nothing happens — the check is best-effort by contract and must never surface an error.
    /// </summary>
    private async Task NotifyIfUpdateAvailableAsync(
        FruityLink.Llm.Auth.UpdateCheckClient updateCheck, string currentVersion)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(8)).ConfigureAwait(false);
            if (_disposed) return;
            FruityLink.Llm.Auth.UpdateInfo? info =
                await updateCheck.CheckAsync(currentVersion).ConfigureAwait(false);
            if (info is null || _disposed) return;
            _log($"[fl-agent] update available: {info.Value.Version} (installed {currentVersion})");
            _host.Post(() => _vm.ShowUpdateNotice(info.Value.Version, info.Value.DownloadUrl));
        }
        catch (Exception ex)
        {
            _log("[fl-agent] update check skipped: " + ex.Message);
        }
    }

    private void OnBugReportRequested(ChatMessage failed)
    {
        FruityLink.Llm.Auth.BugReportClient? client = _bugReports;
        if (client is null) return;

        // Snapshot the transcript ON the UI thread (Messages is UI-thread-owned), then ship it on a
        // background task — a bug report must never block or break the chat.
        var transcript = new List<FruityLink.Llm.Auth.BugReportTurn>();
        foreach (ChatMessage m in _vm.Messages)
        {
            string role = m.IsUser ? "user" : m.IsAssistant ? "assistant" : "system";
            string content = m.Text;
            if (m.IsAssistant && m.HasToolCalls)
                content = string.Join("\n", m.ToolCalls) + (content.Length > 0 ? "\n" + content : string.Empty);
            if (content.Length > 0)
                transcript.Add(new FruityLink.Llm.Auth.BugReportTurn(role, content));
        }
        if (transcript.Count > BugReportTranscriptTurns)
            transcript.RemoveRange(0, transcript.Count - BugReportTranscriptTurns);

        string errorMessage = failed.Text;
        string clientVersion = typeof(AvaloniaChatPresenter).Assembly.GetName().Version?.ToString() ?? "unknown";

        _ = Task.Run(async () =>
        {
            bool sent = await client.SendAsync(errorMessage, transcript, clientVersion).ConfigureAwait(false);
            if (sent)
            {
                _log("[fl-agent] bug report filed with the gateway.");
            }
            else
            {
                // The VM already flashed an optimistic "sent" — correct the record with a quiet info
                // line rather than a new error bubble (a bug report must never cause a new bug).
                _log("[fl-agent] bug report could not be delivered (offline or signed out).");
                _host.Post(() => _vm.AddInfo(
                    "Couldn't deliver the bug report (offline or signed out). It's safe to try again later."));
            }
        });
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

    // ---- speech-to-text (Whisper) — the flow itself lives in the shared DictationController ----

    private void OnMicToggle()
    {
        if (_dictationCtl is not null) _ = _dictationCtl.ToggleAsync();
    }

    /// <summary>Awaitable "finish dictation for a Send", wired to <see cref="ChatViewModel.DictationFinalizer"/>:
    /// stop the mic, await the FINAL Whisper render, and apply the transcript to the composer BEFORE
    /// returning — so Send waits for the text instead of racing it (the VM's toggle-then-observe fallback
    /// sent before the "Transcribing" state was even set). Idempotent: safe when idle / already finalizing.</summary>
    private async Task FinalizeDictationForSendAsync()
    {
        if (_dictation is null || _dictationCtl is null) return;
        if (_dictation.State == DictationState.Recording)
        {
            await _dictationCtl.FinalizeAsync().ConfigureAwait(false);   // stop + transcribe + (awaited) apply
            return;
        }
        // A finalize is already running (mic toggled off just before Send): wait for it to settle to Idle,
        // then flush so its queued ApplyDictatedText post lands before Send reads the composer.
        for (int i = 0; i < 400 && _dictation.State == DictationState.Transcribing; i++)
            await Task.Delay(25).ConfigureAwait(false);
        await PostAwait(() => { }).ConfigureAwait(false);
    }

    /// <summary>Run <paramref name="action"/> on the UI thread and AWAIT its completion (via the host post).</summary>
    private Task PostAwait(Action action)
    {
        var tcs = new TaskCompletionSource();
        _host.Post(() => { try { action(); } finally { tcs.TrySetResult(); } });
        return tcs.Task;
    }

    private void OnPartialTranscribed(string text) => _dictationCtl?.HandlePartialTranscribed(text);

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
        if (_bugReports is not null) _vm.BugReportRequested -= OnBugReportRequested;
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
