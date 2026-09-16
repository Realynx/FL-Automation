"""Amplitude, energy, occupancy, and paged time-window measurements."""

import math
from collections.abc import Sequence

from .audio import MAX_SAMPLES, AudioSource, finite
from .loudness import Loudness, db, true_peak_frames

MAX_AMPLITUDE_WORK = 8_000_000
MAX_TRUE_PEAK_SAMPLES = 2_000_000
MAX_LOUDNESS_SAMPLES = MAX_SAMPLES


def page_bounds(total: int, offset: int, limit: int) -> tuple[int, int]:
    if isinstance(offset, bool) or not isinstance(offset, int) or offset < 0:
        raise ValueError("offset must be a nonnegative integer.")
    if isinstance(limit, bool) or not isinstance(limit, int) or not 1 <= limit <= 64:
        raise ValueError("limit must be an integer in 1..64.")
    return min(offset, total), min(offset + limit, total)


def window_frames(source: AudioSource, window_seconds: float, hop_seconds: float | None) -> tuple[int, int]:
    if finite(window_seconds, "window_seconds", positive=True) > 86400:
        raise ValueError("window_seconds must not exceed one day.")
    if hop_seconds is not None and finite(hop_seconds, "hop_seconds", positive=True) > 86400:
        raise ValueError("hop_seconds must not exceed one day.")
    width = round(finite(window_seconds, "window_seconds", positive=True) * source.sample_rate)
    hop = width if hop_seconds is None else round(
        finite(hop_seconds, "hop_seconds", positive=True) * source.sample_rate)
    if width < 1 or hop < 1:
        raise ValueError("Window and hop must round to at least one audio frame.")
    return width, hop


def _channel(samples: Sequence[float], rate: int, occupancy_frames: int,
             occupancy_threshold: float) -> dict[str, object]:
    square_sum = math.fsum(value * value for value in samples)
    mean_square = square_sum / len(samples)
    rms = math.sqrt(mean_square)
    peak = max(abs(value) for value in samples)
    active = 0
    for start in range(0, len(samples), occupancy_frames):
        block = samples[start:start + occupancy_frames]
        power = math.fsum(value * value for value in block) / len(block)
        if power >= occupancy_threshold:
            active += len(block)
    return {"rms": rms, "rms_dbfs": db(rms), "mean_square": mean_square,
            "energy": square_sum / rate, "sample_peak": peak, "sample_peak_dbfs": db(peak),
            "crest_db": db(peak / rms) if rms > 0 else None,
            "dc_offset": math.fsum(samples) / len(samples),
            "over_full_scale_frames": sum(abs(value) > 1 for value in samples),
            "audio_occupancy": active / len(samples)}


class AmplitudeAnalysis:
    def __init__(self, source: AudioSource) -> None:
        self.source = source
        self._loudness: Loudness | None = None
        self._true_peaks: Sequence[float] | None = None

    def _measure(self, begin: int, end: int, occupancy_seconds: float,
                 occupancy_threshold_dbfs: float) -> dict[str, object]:
        source = self.source
        threshold = finite(occupancy_threshold_dbfs, "occupancy_threshold_dbfs")
        if not -300 <= threshold <= 240:
            raise ValueError("occupancy_threshold_dbfs must be in -300..240.")
        occupancy, _ = window_frames(source, occupancy_seconds, None)
        channels = [_channel(c[begin:end], source.sample_rate, occupancy, 10 ** (threshold / 10))
                    for c in source.channels]
        return {"start_frame": source.origin_frame + begin, "end_frame": source.origin_frame + end,
                "start_seconds": (source.origin_frame + begin) / source.sample_rate,
                "end_seconds": (source.origin_frame + end) / source.sample_rate,
                "channels": channels,
                "rms_reference": "full-scale amplitude 1; a full-scale sine is -3.0103 dBFS RMS",
                "energy_unit": "normalized_amplitude_squared_seconds_per_channel",
                "occupancy_threshold_dbfs": threshold, "occupancy_block_frames": occupancy,
                "occupancy_method": "fraction of frames in RMS blocks at/above threshold; includes final partial block"}

    def _check_extended_work(self, loudness: bool, true_peak: bool) -> None:
        samples = self.source.frame_count * len(self.source.channels)
        if loudness and samples > MAX_LOUDNESS_SAMPLES:
            raise ValueError("Loudness work exceeds 32 million samples; select a smaller time range.")
        if true_peak and samples > MAX_TRUE_PEAK_SAMPLES:
            raise ValueError("True-peak FIR work exceeds 2 million samples; select a smaller time range.")

    def _extended(self, begin: int, end: int, loudness: bool, true_peak: bool) -> dict[str, object]:
        source = self.source
        result: dict[str, object] = {}
        if loudness and self._loudness is None:
            self._loudness = Loudness(source)
        if true_peak and self._true_peaks is None:
            self._true_peaks = true_peak_frames(source)
        if true_peak and self._true_peaks is not None:
            result["true_peak_estimate_dbtp"] = db(max(self._true_peaks[begin:end]))
            result["true_peak_method"] = "4x 49-tap Hann-windowed sinc; zero outside selected range; estimate"
        if loudness and self._loudness is not None:
            result["loudness_method"] = "BS.1770 K-weighting; mono/stereo unit channel weights; reset at selected start"
            for name, seconds in (("momentary_lufs", 0.4), ("short_term_lufs", 3.0)):
                frames = round(seconds * source.sample_rate)
                result[name] = self._loudness.measure(end - frames, end) if end >= frames else None
            frames = 3 * source.sample_rate
            result["short_term_range_frames"] = (
                [source.origin_frame + end - frames, source.origin_frame + end] if end >= frames else None)
            if true_peak and self._true_peaks is not None:
                short = result["short_term_lufs"]
                peak = db(max(self._true_peaks[end - frames:end])) if end >= frames else None
                result["psr_db"] = peak - short if peak is not None and isinstance(short, float) else None
                result["psr_method"] = "estimated true peak minus ungated LUFS over the same trailing 3 seconds"
                result["psr_unavailable_reason"] = (
                    "requires 3 seconds within selected range" if end < frames else
                    "silent 3-second window" if short is None else None)
        return result

    def summary(self, *, occupancy_seconds: float = 0.05, occupancy_threshold_dbfs: float = -60,
                loudness: bool = False, true_peak: bool = False) -> dict[str, object]:
        """Whole range amplitude/integrated loudness; PSR refers only to its trailing three seconds."""
        if self.source.frame_count * len(self.source.channels) > MAX_SAMPLES:
            raise ValueError("Amplitude work exceeds 32 million samples; select a smaller time range.")
        self._check_extended_work(loudness, true_peak)
        result = self._measure(0, self.source.frame_count, occupancy_seconds, occupancy_threshold_dbfs)
        result.update(self._extended(0, self.source.frame_count, loudness, true_peak))
        if loudness and self._loudness is not None:
            result["integrated_lufs"] = self._loudness.integrated()
            result["integrated_gate"] = "400 ms blocks, 100 ms hop; -70 LUFS absolute, -10 LU relative"
        return {"provenance": self.source.provenance(), "measurements": result}

    def windows(self, *, window_seconds: float = 0.05, hop_seconds: float | None = None,
                offset: int = 0, limit: int = 32, occupancy_seconds: float = 0.05,
                occupancy_threshold_dbfs: float = -60, loudness: bool = False,
                true_peak: bool = False) -> dict[str, object]:
        """Full RMS/crest windows only. Loudness/PSR use standard trailing 0.4/3-second windows."""
        width, hop = window_frames(self.source, window_seconds, hop_seconds)
        total = max(0, 1 + (self.source.frame_count - width) // hop)
        first, last = page_bounds(total, offset, limit)
        if (last - first) * len(self.source.channels) > 256:
            raise ValueError("A page may contain at most 256 channel windows; reduce limit.")
        if (last - first) * width * len(self.source.channels) > MAX_AMPLITUDE_WORK:
            raise ValueError("Overlapping window work exceeds 8 million samples; reduce limit or window duration.")
        self._check_extended_work(loudness, true_peak)
        rows = []
        for index in range(first, last):
            begin, end = index * hop, index * hop + width
            row = self._measure(begin, end, occupancy_seconds, occupancy_threshold_dbfs)
            row.update(self._extended(begin, end, loudness, true_peak))
            rows.append(row)
        return {"provenance": self.source.provenance(), "window_frames": width, "hop_frames": hop,
                "window_seconds": width / self.source.sample_rate, "hop_seconds": hop / self.source.sample_rate,
                "partial_windows": "discarded", "offset": offset, "total": total,
                "next_offset": last if last < total else None, "items": rows}
