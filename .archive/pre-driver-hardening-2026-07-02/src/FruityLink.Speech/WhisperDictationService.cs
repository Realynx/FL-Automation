using System.IO;
using System.Net.Http;
using System.Text;
using FruityLink.Core.Abstractions;
using NAudio.Wave;
using Whisper.net;

namespace FruityLink.Speech;

/// <summary>
/// Local speech-to-text. Captures the microphone at 16 kHz mono via NAudio and transcribes it
/// with a Whisper GGML model (whisper.cpp via Whisper.net) — fully offline once the model is
/// downloaded. The model is fetched once from the whisper.cpp model repo and cached on disk.
/// </summary>
public sealed class WhisperDictationService : IDictationService, IDisposable
{
    // base.en: ~148 MB, a good speed/accuracy balance for English dictation.
    private const string ModelUrl =
        "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-base.en.bin";

    private readonly string _modelPath;
    private readonly object _gate = new();

    private WaveInEvent? _waveIn;
    private MemoryStream? _buffer;
    private TaskCompletionSource<bool>? _stopped;
    private WhisperFactory? _factory;
    private CancellationTokenSource? _liveCts;
    private Task? _liveLoop;
    private CancellationTokenSource? _opCts;

    public WhisperDictationService(string dataDirectory)
    {
        string dir = Path.Combine(dataDirectory, "models");
        Directory.CreateDirectory(dir);
        _modelPath = Path.Combine(dir, "ggml-base-en.bin");
    }

    public DictationState State { get; private set; } = DictationState.Idle;

    public bool IsModelReady => File.Exists(_modelPath);

    public event EventHandler? StateChanged;

    public event Action<string>? PartialTranscribed;

    private void SetState(DictationState state)
    {
        State = state;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task EnsureModelAsync(IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (IsModelReady) return;

        _opCts = new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _opCts.Token);

        SetState(DictationState.Downloading);
        try
        {
            await DownloadModelAsync(progress, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Cancelled via Cancel() — leave the model un-downloaded for next time.
        }
        finally
        {
            SetState(DictationState.Idle);
        }
    }

    public void StartRecording()
    {
        lock (_gate)
        {
            if (State != DictationState.Idle) return;
            if (WaveInEvent.DeviceCount == 0)
                throw new InvalidOperationException("No microphone / audio input device was found.");

            _buffer = new MemoryStream();
            _waveIn = new WaveInEvent
            {
                WaveFormat = new WaveFormat(16000, 16, 1),
                BufferMilliseconds = 100,
            };
            _waveIn.DataAvailable += OnDataAvailable;
            _waveIn.RecordingStopped += OnRecordingStopped;
            _waveIn.StartRecording();
            SetState(DictationState.Recording);

            _liveCts = new CancellationTokenSource();
            _opCts = new CancellationTokenSource();
            _liveLoop = Task.Run(() => LiveLoopAsync(_liveCts.Token));
        }
    }

    /// <summary>
    /// Periodically re-transcribes the audio captured so far and raises <see cref="PartialTranscribed"/>,
    /// giving a live preview. Single-flight (each pass completes before the next), so it never piles up.
    /// </summary>
    private async Task LiveLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(1200, ct).ConfigureAwait(false);

                byte[] snapshot;
                lock (_gate)
                    snapshot = _buffer?.ToArray() ?? Array.Empty<byte>();

                if (snapshot.Length < 19200) continue; // ~0.6s @ 16 kHz / 16-bit

                string text = await TranscribeAsync(snapshot, ct).ConfigureAwait(false);
                if (!ct.IsCancellationRequested && !string.IsNullOrWhiteSpace(text))
                    PartialTranscribed?.Invoke(text);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on stop.
        }
        catch
        {
            // The live preview is best-effort; the final transcription is authoritative.
        }
    }

    public async Task<string> StopAndTranscribeAsync(CancellationToken ct = default)
    {
        WaveInEvent? waveIn;
        MemoryStream? buffer;
        lock (_gate)
        {
            if (State != DictationState.Recording || _waveIn is null || _buffer is null)
                return string.Empty;
            waveIn = _waveIn;
            buffer = _buffer;
            _stopped = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        // Stop the live-preview loop first so it doesn't run concurrently with the final pass.
        _liveCts?.Cancel();
        if (_liveLoop is not null)
        {
            try { await _liveLoop.ConfigureAwait(false); } catch { /* ignore */ }
            _liveLoop = null;
        }

        waveIn.StopRecording();
        await (_stopped?.Task ?? Task.CompletedTask).ConfigureAwait(false);

        byte[] pcm;
        lock (_gate)
        {
            waveIn.DataAvailable -= OnDataAvailable;
            waveIn.RecordingStopped -= OnRecordingStopped;
            waveIn.Dispose();
            _waveIn = null;
            pcm = buffer.ToArray();
            buffer.Dispose();
            _buffer = null;
        }

        // Ignore clips shorter than ~0.4s (16000 Hz * 2 bytes * 0.4s).
        if (pcm.Length < 12800 || !IsModelReady)
        {
            SetState(DictationState.Idle);
            return string.Empty;
        }

        SetState(DictationState.Transcribing);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            ct, _opCts?.Token ?? CancellationToken.None);
        try
        {
            return await Task.Run(() => TranscribeAsync(pcm, linked.Token), linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return string.Empty; // Cancelled via Cancel() (e.g. user hit Send).
        }
        finally
        {
            SetState(DictationState.Idle);
        }
    }

    public void Cancel()
    {
        _liveCts?.Cancel();
        _opCts?.Cancel();

        WaveInEvent? toStop = null;
        lock (_gate)
        {
            if (_waveIn is not null)
            {
                _waveIn.DataAvailable -= OnDataAvailable;
                _waveIn.RecordingStopped -= OnRecordingStopped;
                toStop = _waveIn;
                _waveIn = null;
                _buffer?.Dispose();
                _buffer = null;
            }
        }

        try { toStop?.Dispose(); } catch { /* ignore */ }

        if (State != DictationState.Idle)
            SetState(DictationState.Idle);
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        lock (_gate)
            _buffer?.Write(e.Buffer, 0, e.BytesRecorded);
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e) => _stopped?.TrySetResult(true);

    private async Task<string> TranscribeAsync(byte[] pcm16, CancellationToken ct)
    {
        int sampleCount = pcm16.Length / 2;
        var samples = new float[sampleCount];
        for (int i = 0; i < sampleCount; i++)
            samples[i] = BitConverter.ToInt16(pcm16, i * 2) / 32768f;

        _factory ??= WhisperFactory.FromPath(_modelPath);
        await using WhisperProcessor processor = _factory.CreateBuilder().WithLanguage("en").Build();

        var text = new StringBuilder();
        await foreach (SegmentData segment in processor.ProcessAsync(samples, ct).ConfigureAwait(false))
            text.Append(segment.Text);

        return text.ToString().Trim();
    }

    private async Task DownloadModelAsync(IProgress<double>? progress, CancellationToken ct)
    {
        string tempPath = _modelPath + ".part";
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };

        using HttpResponseMessage response = await http
            .GetAsync(ModelUrl, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        long? total = response.Content.Headers.ContentLength;
        await using Stream source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using (var destination = File.Create(tempPath))
        {
            var buffer = new byte[81920];
            long received = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await destination.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                received += read;
                if (total is > 0)
                    progress?.Report((double)received / total.Value);
            }
        }

        File.Move(tempPath, _modelPath, overwrite: true);
    }

    public void Dispose()
    {
        _liveCts?.Cancel();
        _opCts?.Cancel();
        _waveIn?.Dispose();
        _buffer?.Dispose();
        _factory?.Dispose();
    }
}
