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

## Mix checks: bars, bands, sections, transitions, pitch and masking

These helpers replace the per-revision ffmpeg loops recorded in the Ember Tides friction log. They run on the same `AudioAnalysis` object (`fl.analysis.wav(...)` or `Analysis.wav(...)`), need no third-party package (numpy is used for a few sums when it happens to import, with identical results), and return small JSON-safe dictionaries: pages or summaries, never sample arrays. The bar grid is explicit and constant: bar `start_bar` (default 1) begins at file time `grid_offset_seconds` (default 0) and every bar lasts `beats_per_bar * 60 / bpm` seconds; tempo automation is not inferred. Result times are absolute file seconds and dB values are rounded to 0.01.

### Per-bar anomaly scan

```python
audio = fl.analysis.wav(r"C:\Music\song-v017.wav", source_kind="master_render")
scan = audio.scan_bars(bpm=128)          # bands (None, 90) and (4000, None); step 6 dB
result = {"summary": scan["summary"], "anomalies": scan["anomalies"]}
# scan["bars"]: page of {bar, start_seconds, end_seconds, partial, full_db, bands: {"<90": .., ">4000": ..}, flags}
```

`scan_bars(bpm, *, beats_per_bar=4, bands=((None, 90), (4000, None)), step_db=6.0, start_bar=1, grid_offset_seconds=0, offset=0, limit=128)` measures every bar overlapping the selected range. `full_db` is the mean of channel powers as RMS dBFS (amplitude reference 1); each band is the RMS of the channel-mean mono mix after the band filter described below. A bar is flagged when any measure moves more than `step_db` from the previous bar, or enters or leaves exact silence. `anomalies` and `summary` (flagged, loudest, quietest and silent bars) cover the whole scan; `bars` is paged with up to 256 rows, and the scan is cached on the analysis object so further pages cost nothing. A scan accepts at most 8 bands and 4096 bars; a bar cut by the selected range is marked `partial`. A synthetic 200 s stereo 48 kHz render scanned in 5.5 s with the default bands in pure Python (2.3 s more to load the file).

### Band energy and revision comparison

```python
bands = ((2000, 4000), (5000, None))                                 # presence and air
chorus = audio.band_energy(bands, start_seconds=96, end_seconds=112)
# chorus["bands"] == {"2000-4000": -21.4, ">5000": -27.9}; chorus["full_db"], chorus["peak_db"]
delta = fl.analysis.compare_bands(r"C:\Music\song-v016.wav", r"C:\Music\song-v017.wav", bands,
                                  start_seconds=96, end_seconds=112)
# delta["bands"][">5000"]["delta_db"] is b minus a; delta["largest_increase"] names the band that grew most
```

Bands are `(low_hz, high_hz)` pairs; `None` leaves that side open and `(None, None)` is the unfiltered mix. Labels read `"<90"`, `"2000-4000"`, `">5000"` or `"full"`. Each edge is a fourth-order Butterworth (two cascaded RBJ biquads, 24 dB per octave) run forward only over the channel-mean mono mix, preceded by a 0.5 s pre-roll so short windows are not dominated by start-up transients. The value is the time-domain RMS of the filtered signal, so a band includes its skirts and a silent bar after a loud one carries a few milliseconds of filter decay. Anti-phase stereo content cancels in the mono mix, which is why `full_db` averages channel powers instead. Edges must lie below Nyquist, a call takes up to 16 bands, and filtering is bounded at 300 million filter samples per call (choose a shorter window or fewer bands to continue). The default bands split at 90, 250, 2000 and 4000 Hz. The two-source helpers accept WAV paths, `AudioSource` objects or analysis objects; paths load only the requested window.

### Section descriptions

```python
sections = {"intro": (1, 16), "chorus": (33, 48), "drop": (65, 72)}   # inclusive bar ranges
report = audio.describe_sections(bpm=128, sections=sections)
# per section: rms_db, peak_db, crest_db, centroid_hz, width {correlation, side_mid_db}, bands, integrated_lufs
```

`describe_sections` reports the mean-channel-power RMS, sample peak, crest, spectral centroid, stereo width, band levels and BS.1770 integrated loudness of each named section. The centroid is the power-weighted centroid of the mono mix from 2048-point Hann periodograms; a section longer than 16 FFT frames is sampled with 16 evenly spaced frames combined by their power. Width is the zero-lag L/R correlation and `10*log10(sum((L-R)^2) / sum((L+R)^2))`; both are null unless the audio has exactly two channels. Integrated loudness reuses the existing K-weighting and gates, with filter state from the selected start and blocks starting at the section start; it is cached on the analysis object, unavailable for more than two channels, and skipped with `loudness=False`. A section cut by the selected range is marked `partial`; one entirely outside it raises an error. Up to 32 sections per call.

### Transition A/B

```python
check = fl.analysis.transition(r"C:\Music\song-v017.wav", r"C:\Music\song-v018.wav", bpm=128, bar=65,
                               window_seconds=0.43)
# check["before"]["delta"] and check["after"]["delta"]: b minus a for rms_db, peak_db, centroid_hz
# check["jump"]["b"]["rms_db"]: how much render b rises across the bar line
```

`transition(a, b, bpm, bar, *, window_seconds=0.43, beats_per_bar=4, start_bar=1, grid_offset_seconds=0)` measures the window that ends on the bar line and the window that starts on it, in both renders: RMS (mean channel power), sample peak and mono centroid, with `b - a` deltas per window and each render's own jump across the line. 0.43 s is about one beat at 140 bpm. Both windows must exist in both sources.

### Pitch tracking

```python
lead = fl.analysis.wav(r"C:\Music\lead-only.wav", start_seconds=120, end_seconds=132)
track = lead.pitch_track(hop_seconds=0.05, fmin=60, fmax=1500)
result = {"summary": track["summary"], "items": track["items"]}   # page items with offset/limit
```

`pitch_track` is a monophonic tracker for an isolated render; a full mix gives meaningless values. The mono mix is low-passed and decimated so that `fmax` stays below a quarter of the analysis rate, then each frame (two periods of `fmin`, spaced `hop_seconds`) is mean-removed and correlated with itself over lags between `1/fmax` and `1/fmin`. The first local maximum within 90% of the strongest one is chosen, which prefers the shortest period and resists octave-down errors, and the lag is refined by parabolic interpolation. `confidence` is the normalised correlation at that lag; frames below -60 dBFS or under `min_confidence` (default 0.5) report `f0_hz` and `midi` as null. The summary gives the first and last voiced `f0` with their MIDI numbers, `glide_semitones`, min/max/median and the voiced fraction. A call produces at most 4096 frames, pages hold up to 256, and the track is cached on the analysis object. Vibrato, polyphony and noisy attacks lower the confidence; they do not raise errors. Lower `fmax` for a faster, more decimated analysis.

### Masking between two instruments

```python
report = fl.analysis.masking_report(r"C:\Music\lead-only.wav", r"C:\Music\pad-only.wav",
                                    ((250, 500), (500, 1600), (1600, 4000)),
                                    start_seconds=96, end_seconds=112, clash_threshold_db=6)
# report["bands"]["500-1600"] == {"a_db": .., "b_db": .., "louder": "a", "overlap_db": -2.1}
# report["clashes"] lists the bands where both parts compete
```

`overlap_db` is the quieter source's level minus the louder one's in each band: 0 means equal energy (full overlap) and more negative values mean more separation. A band is a clash when both sources exceed `floor_db` (default -60) and their separation is within `clash_threshold_db`; the numbers say how many dB of ducking or EQ would reach the threshold. Both inputs should be isolated renders over the same window.

### Mix check recipe

1. Before every master, run `scan_bars(bpm)` on the full mix and read `summary["flagged_bars"]` and `anomalies` first. A `<90` drop is a missing sub or a sidechain that swallowed the kick, a `>4000` rise is a harsh entry, and a `full` step is a level jump between sections. Re-scan with `bands=((None, 90), (2000, 4000), (5000, None))` when presence is the concern.
2. When swapping or adding a voice, render the previous and new revisions and run `compare_bands(previous, current, ((2000, 4000), (5000, None)), start_seconds=..., end_seconds=...)` over the section that changed. An increase of a few dB in `2000-4000` or `>5000` means the new voice added harshness; `describe_sections` on both renders shows whether loudness or width moved too.
3. When fixing a drop, run `transition(previous, current, bpm, bar)` at the bar line. `jump["b"]["rms_db"]` should exceed `jump["a"]["rms_db"]`, while the `before` deltas stay near zero so the build-up is unchanged.
4. When two parts fight, render each alone and run `masking_report`; duck or EQ the background part in the listed `clashes`, then re-run to confirm the separation exceeds the threshold.
5. When automating pitch or filters, render the affected voice alone and confirm with `pitch_track` (`glide_semitones`) or `spectral_windows` (centroid); the full mix hides both.

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
