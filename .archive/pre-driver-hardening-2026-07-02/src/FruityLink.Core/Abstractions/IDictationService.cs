namespace FruityLink.Core.Abstractions;

/// <summary>Lifecycle of a local speech-to-text session.</summary>
public enum DictationState
{
    /// <summary>Ready; not recording.</summary>
    Idle,

    /// <summary>The speech model is being downloaded (one-time).</summary>
    Downloading,

    /// <summary>Capturing microphone audio.</summary>
    Recording,

    /// <summary>Running the captured audio through Whisper.</summary>
    Transcribing,
}

/// <summary>
/// Local (offline) speech-to-text: captures the microphone and transcribes it with Whisper,
/// so the user can dictate into the chat composer instead of typing.
/// </summary>
public interface IDictationService
{
    /// <summary>Current session state.</summary>
    DictationState State { get; }

    /// <summary>True once the Whisper model file is present locally.</summary>
    bool IsModelReady { get; }

    /// <summary>Raised (on a background thread) whenever <see cref="State"/> changes.</summary>
    event EventHandler? StateChanged;

    /// <summary>
    /// Raised (on a background thread) with a live, best-effort transcript of the audio captured
    /// so far while recording. Each event supersedes the previous one; the value returned by
    /// <see cref="StopAndTranscribeAsync"/> is the final, authoritative transcript.
    /// </summary>
    event Action<string>? PartialTranscribed;

    /// <summary>Downloads the Whisper model if it isn't already present.</summary>
    /// <param name="progress">Receives 0..1 download progress, when the length is known.</param>
    Task EnsureModelAsync(IProgress<double>? progress = null, CancellationToken ct = default);

    /// <summary>Begins capturing microphone audio. Throws if no input device is available.</summary>
    void StartRecording();

    /// <summary>Stops capture and returns the transcribed text (empty if the clip was too short).</summary>
    Task<string> StopAndTranscribeAsync(CancellationToken ct = default);

    /// <summary>
    /// Immediately aborts any in-progress dictation (recording, live preview, transcription, or
    /// model download) and returns to <see cref="DictationState.Idle"/> without transcribing.
    /// </summary>
    void Cancel();
}
