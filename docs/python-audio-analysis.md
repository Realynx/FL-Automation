# Python audio measurements

The dependency-free `fruitylink.analysis.Analysis` API is also available as `fl.analysis` in the Python IDE and MCP. It measures an explicit audio file or planar PCM array. It does not read live FL audio, mute channels, render stems, or change the project.

```python
# A section of a known master render. End times are exclusive.
audio = fl.analysis.wav(r"C:\Music\song.wav", start_seconds=48, end_seconds=56,
                        source_kind="master_render")
result = audio.summary(loudness=True, true_peak=True)

# Transient RMS/crest analysis with overlapping 20 ms windows, paged output.
result = audio.windows(window_seconds=0.020, hop_seconds=0.010, offset=0, limit=32)
# Request the next page using result["next_offset"] until it is None.

# Spectral balance, centroid and dominant frequency bins; these are not pitch labels.
result = audio.spectral(fft_size=4096)
result = audio.spectral_windows(window_seconds=0.25, hop_seconds=0.125, limit=16)
```

Without a DAW connection, use `from fruitylink.analysis import Analysis` and the same `Analysis.wav(...)` or `Analysis.pcm([left, right], sample_rate, source="capture name")` factory. Return the summary or one page to MCP; the local analysis object and raw sample arrays are intentionally unsuitable as a tool response.

## Source and range

Every amplitude response includes the source path/name, caller-supplied source kind, sample format/rate, audio-channel count, total source frames, requested seconds, and measured frame bounds. Start rounds down, exclusive end rounds up and clamps to file duration. This rounding can include at most one extra frame at either boundary. Sample windows report their actual frame-derived durations; amplitude windows discard an incomplete final window. Spectral windows include a final partial window and report its bounds.

Supported files are little-endian RIFF WAV: PCM8/16/24/32 and IEEE float32/64, including matching WAVE_FORMAT_EXTENSIBLE subformats. Unsupported compression, RF64, partial frames, nonfinite samples, inconsistent chunk lengths and unsupported valid-bit alignment fail explicitly. Float amplitudes above 1 are retained. Selecting a smaller range reads only that range's audio data.

`source_kind` describes caller knowledge; it is not proof of isolation. Existing FL MCP rendering produces the project/master mix. A separately exported mixer stem can contain multiple channels, sends, and buses; FL's **Split mixer tracks** export omits Master effects. An arbitrary channel peak meter does not provide timed PCM for RMS, loudness, or PSR. Do not label a master file as per-instrument analysis. [FL Studio export documentation](https://www.image-line.com/fl-studio-learning/fl-studio-online-manual/html/fformats_save_export.htm)

## Amplitude, energy and occupancy

Measurements are per audio channel; opposite-polarity stereo signals are not summed or cancelled. `rms` is the square root of mean squared normalized samples. `rms_dbfs` uses full-scale **amplitude 1** as its reference: a full-scale sine has approximately -3.0103 dBFS RMS. This explicitly differs from meters that calibrate sine RMS to zero. `sample_peak_dbfs` is the largest stored sample magnitude in dBFS. `crest_db` is sample peak divided by RMS in decibels. [RMS definition](https://www.mathworks.com/help/matlab/ref/rms.html)

`mean_square` has normalized amplitude-squared units. `energy` is sum of squared samples divided by sample rate, in normalized amplitude-squared seconds, **not joules**. `dc_offset` is the sample mean. `over_full_scale_frames` counts magnitudes above 1; this does not prove a floating-point signal was clipped by a device.

`audio_occupancy` is the fraction of frames belonging to RMS blocks at or above `occupancy_threshold_dbfs` (default -60). The block width defaults to 50 ms, and a final partial block is included with its actual frame count. It is threshold-based audio occupancy, not musical note density or a subjective measure of mix density. Silence produces `None` for undefined logarithms and ratios, which becomes JSON `null`.

## Loudness, true peak and PSR

`loudness=True` enables mono/stereo K-weighting with unit channel weights. Filter state starts at the selected range's beginning. Integrated loudness uses 400 ms blocks with 100 ms hops, an absolute -70 LUFS gate, then a relative -10 LU gate. Incomplete final blocks are discarded. Momentary and short-term loudness use ungated trailing 400 ms and 3 s windows. They remain unavailable until that much audio exists inside the selected range. Multichannel loudness is refused because channel-role weights cannot be inferred safely. [ITU-R BS.1770-5](https://www.itu.int/rec/R-REC-BS.1770-5-202311-I), [EBU Tech 3341](https://tech.ebu.ch/docs/tech/tech3341.pdf)

`true_peak=True` enables an explicitly labelled **true-peak estimate** using 4x, 49-tap Hann-windowed sinc interpolation. It is separate from sample peak, uses surrounding selected audio for interpolation, and treats audio outside the selected range as zero. Analytic EBU calibration signals are tested; the implementation is not presented as a certified meter. Numerical weighting coefficients are cross-checked against the primary [libebur128 implementation](https://github.com/jiixyj/libebur128/blob/master/ebur128/ebur128.c).

When both options are enabled, `psr_db` is estimated true peak minus short-term LUFS **over the same trailing 3 s**. It is never peak-minus-RMS. A 20 ms transient window still needs a complete trailing 3 s context for PSR; `short_term_range_frames` identifies that context. In `summary()`, amplitude and integrated loudness describe the whole selected range, while PSR describes only its final 3 s. PSR is unavailable for shorter ranges or silence. [NUGEN MasterCheck PSR definition](https://nugenaudio.com/files/manuals/MasterCheck%20Manual.pdf)

## Work and output bounds

The reader and whole-range amplitude/loudness analysis allow up to 32 million scalar samples (frames times channels). True-peak interpolation is limited to 2 million scalar samples; choose shorter sections for that expensive option. A page permits at most 64 windows, 256 channel-window records, and 8 million amplitude input samples including repeated overlap. Spectral transforms have a separate 4,194,304 input-sample work bound. Exceeding a bound raises an actionable error; no samples or windows are silently subsampled. Loudness and true-peak intermediate results are cached on the local analysis object. Computation is offline and may take seconds; it is not a real-time meter.

## Symbolic note density and constant tempo

```python
from fruitylink.analysis import note_density, tick_range_seconds
notes = fl.patterns[1].notes.list()  # pattern-local, not expanded playlist playback
result = note_density(notes, start_tick=0, end_tick=1536, ppq=96)
start, end = tick_range_seconds(1536, 3072, ppq=96, bpm=150)
```

Note density reports onsets per beat, union occupancy, and average polyphony for the supplied pattern-local notes. Muted notes are excluded by default; boundary-crossing notes contribute only their intersection. Playlist repetitions, clip cropping, mute/routing state, and overlapping instances are **not** inferred from a pattern's notes. Tick conversion assumes the explicitly supplied constant BPM: tempo automation requires an actual tempo map and cannot be recovered from one current BPM value.

See [spectral analysis](python-spectral-analysis.md) for frequency-domain definitions and limits.

## Live verification and embedded performance

The installed framework was exercised through MCP on FL 26.1.3.5570. An
eight-second project with mixer-volume automation rendered audible kicks in its
first half and exact silence in its second half. The embedded measurement API
confirmed both ranges, returned short transient windows and a compact spectrum
for a real drum sample, and emitted finite JSON with nulls for undefined values.

Installer 0.1.22 reduces expensive cancellation ancestry checks and managed
callbacks to one per 1024 Python trace events. Script entry and native SDK calls
still check immediately. The same embedded numerical test dropped from 101.44 to
11.00 seconds (one measured comparison, about 9.2×), with identical output. A
one-second infinite-loop deadline terminated correctly, and a subsequent Python
and native mixer query succeeded in the same FL instance.

Standalone validation also compared sample peak and RMS against FFmpeg within
0.00001 dB on a rendered fixture, and integrated loudness within its displayed
precision. The full 192-second Glass Satellites render measured -18.863 LUFS in
15.5 seconds for loading, amplitude statistics and integrated loudness, without
true-peak interpolation. These are validation examples, not latency guarantees.
