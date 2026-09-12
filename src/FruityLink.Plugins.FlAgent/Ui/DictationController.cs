using System;
using System.Threading.Tasks;
using FruityLink.Core.Abstractions;

namespace FruityLink.Plugins.FlAgent.Ui;

/// <summary>
/// The Whisper mic flow shared by BOTH chat surfaces (the Avalonia presenter and the legacy WPF
/// window): toggle (one-time model download → start recording), finalize (stop + authoritative final
/// transcription into the composer), and the live-partial handler with its "ignore late partials
/// after recording stopped" guard. It also owns the dictation prefix — what was already typed when
/// dictation began — so live partials append to (not clobber) it.
///
/// <para>All UI access goes through the surface-supplied delegates: <c>setStatus</c> and
/// <c>marshalToUi</c> must be safe to call from any thread (the presenter posts via the Avalonia
/// host, the window via its dispatcher). <c>applyFinalText</c> is awaited so a Send that finalizes
/// dictation first observes the populated composer before reading it (the presenter routes it
/// through its awaited PostAwait + view-model race guard; the window applies synchronously on its
/// UI thread). No <c>ConfigureAwait(false)</c> here on purpose: when called from a UI thread the
/// continuations (composer read, <c>StartRecording</c>, the final apply) stay on that thread,
/// matching the prior per-surface implementations.</para>
/// </summary>
internal sealed class DictationController
{
    private readonly IDictationService _dictation;
    private readonly Func<bool> _isBusy;
    private readonly Action<string?> _setStatus;
    private readonly Func<string, Task> _applyFinalText;
    private readonly Action<string> _applyPartialText;
    private readonly Func<string?> _readComposer;
    private readonly Action<Action> _marshalToUi;

    /// <summary>What was already typed when dictation began, so live partials append to it.</summary>
    private string _prefix = string.Empty;

    public DictationController(
        IDictationService dictation,
        Func<bool> isBusy,
        Action<string?> setStatus,
        Func<string, Task> applyFinalText,
        Action<string> applyPartialText,
        Func<string?> readComposer,
        Action<Action> marshalToUi)
    {
        _dictation = dictation;
        _isBusy = isBusy;
        _setStatus = setStatus;
        _applyFinalText = applyFinalText;
        _applyPartialText = applyPartialText;
        _readComposer = readComposer;
        _marshalToUi = marshalToUi;
    }

    /// <summary>Mic button: start recording, or stop + take the final transcription into the composer.</summary>
    public async Task ToggleAsync()
    {
        if (_isBusy()) return;

        if (_dictation.State == DictationState.Recording)
        {
            await FinalizeAsync();
            return;
        }

        if (_dictation.State != DictationState.Idle) return;

        if (!_dictation.IsModelReady)
        {
            _setStatus("Downloading speech model (~150 MB, one time)…");
            var progress = new Progress<double>(p => _setStatus($"Downloading speech model… {p * 100:0}%"));
            try { await _dictation.EnsureModelAsync(progress); }
            catch (Exception ex) { _setStatus("Model download failed: " + ex.Message); return; }
            _setStatus(null);
        }

        // Remember what's already typed so live partials append to (not clobber) it.
        _prefix = (_readComposer() ?? string.Empty).TrimEnd();
        try { _dictation.StartRecording(); }
        catch (Exception ex) { _setStatus("Microphone unavailable: " + ex.Message); }
    }

    /// <summary>Stops capture and writes the authoritative final transcript into the composer
    /// (via the awaited apply delegate, so the caller sees the text once this returns).</summary>
    public async Task FinalizeAsync()
    {
        string prefix = _prefix;
        string text;
        try { text = await _dictation.StopAndTranscribeAsync(); }
        catch (Exception ex) { _setStatus("Transcription failed: " + ex.Message); return; }
        await _applyFinalText(Combine(prefix, text));
    }

    /// <summary>Live-partial handler: both surfaces' <c>PartialTranscribed</c> subscriptions route here.</summary>
    public void HandlePartialTranscribed(string text)
    {
        _marshalToUi(() =>
        {
            // Ignore late partials that arrive after recording has stopped.
            if (_dictation.State == DictationState.Recording)
                _applyPartialText(Combine(_prefix, text));
        });
    }

    private static string Combine(string prefix, string text)
    {
        if (string.IsNullOrEmpty(prefix)) return text;
        return string.IsNullOrEmpty(text) ? prefix : prefix + " " + text;
    }
}
