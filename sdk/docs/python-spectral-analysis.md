# Spectral sample analysis

The Python SDK can summarize frequency distribution in explicitly supplied PCM or a
selected WAV range, using only the standard library. It does not capture FL audio,
identify a musical instrument, or infer pitch from the strongest frequency bin.
Mixer and channel attribution must come from the provenance of the supplied audio.

```python
audio = fl.analysis.wav("kick.wav", start_seconds=0.0, end_seconds=1.0)
spectrum = audio.spectral(fft_size=2048)
changes = audio.spectral_windows(window_seconds=0.1, hop_seconds=0.05, limit=16)
result = {"spectrum": spectrum, "changes": changes}
```

The standalone functions are `fruitylink.analysis.spectral.spectral(source, ...)`
and `spectral_windows(source, ...)`, where `source` is the common `AudioSource`
returned by `load_wav` or `from_pcm`. No FL connection is required.

## What is measured

Each segment uses a periodic Hann window and a one-sided power periodogram.
Segments overlap by 50%; if the range ends between regular segment starts, an
additional segment is anchored at its end. Periodograms are averaged equally
over segments and audio channels. Averaging **channel powers** prevents opposite
polarity stereo channels from cancelling in a downmix. No DC removal, perceptual
weighting, sample-rate conversion or pitch interpolation is applied.

For an FFT of size `N`, window `w`, and unnormalized transform `X`, bin power is
`abs(X[k])**2 / (N * sum(w**2))`. Interior positive-frequency bins are doubled;
DC and Nyquist are not. This is power spectral density integrated over each bin,
with units of normalized amplitude squared. The segment averaging and one-sided
scaling follow the [SciPy Welch reference](https://docs.scipy.org/doc/scipy/reference/generated/scipy.signal.welch.html).

- `bands`: explicit Hz edges and each band's fraction of total 0..Nyquist power.
  Bins are assigned by center frequency to `[low, high)`, with the last upper edge
  included. Custom edges may cover only part of the spectrum, so their fractions
  need not sum to one. Defaults split at 0, 60, 250, 500, 2000, 4000, 6000 and
  12000 Hz, followed by Nyquist; edges beyond Nyquist are omitted.
- `centroid_hz`: the power-weighted mean frequency, `sum(f[k]*P[k])/sum(P[k])`.
  It is a numerical brightness descriptor; it does not establish sound quality
  or instrument identity. The weighting convention is explicit because magnitude
  and power centroids differ. [Centroid definition](https://www.mathworks.com/help/audio/ref/spectralcentroid.html)
- `rolloff_hz`: the first bin reaching `rolloff_fraction` of cumulative power;
  the default fraction is 0.85. This is quantized to FFT bins.
  [Rolloff definition](https://www.mathworks.com/help/audio/ug/spectral-descriptors.html)
- `dominant_bins`: up to 16 strongest bins with bin number, Hz and power fraction.
  Several can belong to one Hann main lobe. They are not separate tones or notes.
- `windowed_mean_square`: total averaged periodogram power. Hann weighting and
  overlapping segments make this an estimate, not the exact unwindowed energy or
  RMS of a transient. Use the amplitude analyzer for those measurements.

Centroid, rolloff and normalized band fractions are `null` when spectral power is
zero; dominant bins are empty. `silence` reports whether the actual selected PCM
is all zero, while `zero_spectral_power` reports the transform result. These can
differ for an impulse at a zero-weight Hann endpoint. No epsilon threshold turns
quiet audio into silence, and no NaN/Infinity values are emitted.

## Ranges, resolution and bounds

The common loader selects the source time range before analysis. Every spectral
summary reports absolute source frame bounds with an exclusive end, actual start
and end seconds, the requested source range, sample format/rate and channel count.
Temporal window durations and hops round to the nearest sample frame; returned
frame counts and seconds state the actual values. The last temporal window can be
shorter. Paging uses `offset`, `limit`, `total` and `next_offset`.

`fft_size` must be a power of two from 32 through 16384. Bin spacing is
`sample_rate / fft_size`. A short segment is zero-padded; its separately reported
`segment_resolution_hz` is `sample_rate / segment_frames`. Padding creates a finer
frequency grid without improving the underlying resolving power. Hann windowing
also spreads narrow tones across neighboring bins. One-sample input is explicitly
marked `single_sample` and cannot locate a tonal frequency.

Each call allows at most 4,194,304 FFT input samples, counting overlap and channels.
There are at most 32 bands, 16 dominant bins and 64 windows per page; the default
page contains at most 16 windows. An excessive workload raises an error before
transforms start. Select a shorter source range or smaller page to continue;
the analyzer never silently downsamples or skips segments to meet this limit.

Synthetic tests cover analytical tone power, multiband ratios, Parseval energy,
DC/Nyquist scaling, opposite-polarity stereo, silence, transient/window changes,
tail inclusion, source-relative timestamps and resource limits. These verify the
offline analyzer; they do not claim live FL audio capture or per-channel stems.
