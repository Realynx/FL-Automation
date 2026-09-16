"""Monophonic fundamental-frequency tracking by normalized autocorrelation.

The mono mix is low-passed and box-decimated so that ``fmax`` stays below a
quarter of the analysis rate, then each frame (two periods of ``fmin``) is
mean-removed and correlated with itself over lags between ``1/fmax`` and
``1/fmin``. The first local maximum within 90% of the strongest one is chosen
(this prefers the shortest period and resists octave-down errors) and refined
by parabolic interpolation. Confidence is the normalized correlation at that
lag. Frames below -60 dBFS or under ``min_confidence`` are unvoiced. This is a
monophonic tracker for an isolated voice; a full mix gives meaningless values.
"""

from __future__ import annotations

import math
from collections.abc import Sequence
from dataclasses import dataclass
from statistics import median

from ._kernels import autocorrelation, box_decimate, mono_mix
from .audio import AudioSource, finite
from .bands import BandFilter, BandSpec, span_frames

MAX_PITCH_FRAMES = 4096
MAX_PITCH_PAGE = 256
MAX_PITCH_WORK = 1_500_000_000
SILENCE_RMS = 10 ** (-60 / 20)
PEAK_FRACTION = 0.9
PITCH_METHOD = ("normalized autocorrelation on the low-passed, decimated mono mix; first peak within 90% of "
                "the maximum; parabolic lag refinement; monophonic only")


@dataclass(frozen=True)
class _Track:
    settings: dict[str, object]
    items: list[dict[str, object]]
    summary: dict[str, object]


def _decimation(sample_rate: int, fmax: float) -> int:
    return max(1, math.floor(sample_rate / (6 * fmax)))


def _validate(source: AudioSource, hop_seconds: float, fmin: float, fmax: float,
              min_confidence: float) -> tuple[float, float, float, float]:
    hop = finite(hop_seconds, "hop_seconds", positive=True)
    low = finite(fmin, "fmin", positive=True)
    high = finite(fmax, "fmax", positive=True)
    confidence = finite(min_confidence, "min_confidence")
    if not 0 <= confidence <= 1:
        raise ValueError("min_confidence must be in 0..1.")
    if low >= high or high >= source.sample_rate / 4 or low < 1:
        raise ValueError("Require 1 <= fmin < fmax < sample_rate / 4.")
    if hop > 60:
        raise ValueError("hop_seconds must not exceed 60.")
    return hop, low, high, confidence


def _prepare(source: AudioSource, begin: int, end: int, factor: int) -> Sequence[float]:
    mono = mono_mix(source.channels, begin, end)
    if factor == 1:
        return mono
    cutoff = 0.4 * source.sample_rate / factor
    filtered = BandFilter(BandSpec(None, cutoff, "antialias"), source.sample_rate).process(mono)
    return box_decimate(filtered, factor)


def _frame_pitch(frame: Sequence[float], rate: float, min_lag: int, max_lag: int) -> tuple[float | None, float]:
    """Return (f0, confidence) for one mean-removed frame, or (None, 0) when silent."""
    width = len(frame)
    mean = sum(frame) / width
    x = [value - mean for value in frame]
    prefix = [0.0]
    total = 0.0
    for value in x:
        total += value * value
        prefix.append(total)
    if math.sqrt(prefix[width] / width) < SILENCE_RMS:
        return None, 0.0
    scores: list[float] = []
    for lag, product in zip(range(min_lag, max_lag + 1), autocorrelation(x, min_lag, max_lag)):
        energy = prefix[width - lag] * (prefix[width] - prefix[lag])
        scores.append(product / math.sqrt(energy) if energy > 0 else 0.0)
    best = max(scores)
    if best <= 0:
        return None, 0.0
    chosen = None
    for index in range(1, len(scores) - 1):
        if scores[index] >= PEAK_FRACTION * best and scores[index - 1] < scores[index] >= scores[index + 1]:
            chosen = index
            break
    if chosen is None:
        chosen = scores.index(best)
    period = float(min_lag + chosen)
    value = scores[chosen]
    if 0 < chosen < len(scores) - 1:
        left, centre, right = scores[chosen - 1], scores[chosen], scores[chosen + 1]
        curvature = left - 2 * centre + right
        if curvature < 0:
            shift = 0.5 * (left - right) / curvature
            period += shift
            value = centre - 0.25 * (left - right) * shift
    return rate / period, max(0.0, min(1.0, value))


def _midi(f0: float) -> float:
    return round(69 + 12 * math.log2(f0 / 440), 2)


def _summary(items: Sequence[dict[str, object]]) -> dict[str, object]:
    voiced = [item for item in items if isinstance(item["f0_hz"], float)]
    values = [float(str(item["f0_hz"])) for item in voiced]
    if not values:
        return {"frame_count": len(items), "voiced_frames": 0, "voiced_fraction": 0.0, "start_f0_hz": None,
                "end_f0_hz": None, "glide_semitones": None, "min_f0_hz": None, "max_f0_hz": None,
                "median_f0_hz": None}
    return {"frame_count": len(items), "voiced_frames": len(voiced),
            "voiced_fraction": round(len(voiced) / len(items), 3),
            "start_f0_hz": values[0], "end_f0_hz": values[-1],
            "start_time": voiced[0]["time"], "end_time": voiced[-1]["time"],
            "start_midi": _midi(values[0]), "end_midi": _midi(values[-1]),
            "glide_semitones": round(12 * math.log2(values[-1] / values[0]), 2),
            "min_f0_hz": min(values), "max_f0_hz": max(values), "median_f0_hz": round(median(values), 2)}


def _track(source: AudioSource, begin: int, end: int, hop_seconds: float, fmin: float, fmax: float,
           min_confidence: float) -> _Track:
    factor = _decimation(source.sample_rate, fmax)
    rate = source.sample_rate / factor
    max_lag = math.ceil(rate / fmin)
    min_lag = max(2, math.floor(rate / fmax))
    width = 2 * max_lag
    hop = max(1, round(hop_seconds * rate))
    signal = _prepare(source, begin, end, factor)
    if len(signal) < width:
        raise ValueError(f"pitch_track needs at least {width / rate:.3f} s (two periods of fmin) of audio.")
    count = 1 + (len(signal) - width) // hop
    if count > MAX_PITCH_FRAMES:
        raise ValueError(f"pitch_track produces at most {MAX_PITCH_FRAMES} frames; increase hop_seconds or "
                         "select a shorter range.")
    if count * (max_lag - min_lag + 1) * width > MAX_PITCH_WORK:
        raise ValueError("pitch_track work bound exceeded; narrow fmin..fmax, increase hop_seconds, or select "
                         "a shorter range.")
    origin = (source.origin_frame + begin) / source.sample_rate
    items: list[dict[str, object]] = []
    for index in range(count):
        start = index * hop
        f0, confidence = _frame_pitch(signal[start:start + width], rate, min_lag, max_lag)
        voiced = f0 is not None and confidence >= min_confidence
        items.append({"time": round(origin + (start + width / 2) / rate, 4),
                      "f0_hz": round(f0, 2) if voiced and f0 is not None else None,
                      "midi": _midi(f0) if voiced and f0 is not None else None,
                      "confidence": round(confidence, 3)})
    settings: dict[str, object] = {
        "start_seconds": origin, "end_seconds": (source.origin_frame + end) / source.sample_rate,
        "hop_seconds": hop / rate, "frame_seconds": width / rate, "analysis_rate_hz": rate,
        "decimation": factor, "fmin_hz": fmin, "fmax_hz": fmax, "min_confidence": min_confidence,
        "silence_dbfs": -60, "time_reference": "frame centre, absolute file seconds", "method": PITCH_METHOD}
    return _Track(settings, items, _summary(items))


def _page(source: AudioSource, track: _Track, offset: int, limit: int) -> dict[str, object]:
    if type(offset) is not int or offset < 0 or type(limit) is not int or not 1 <= limit <= MAX_PITCH_PAGE:
        raise ValueError(f"offset must be a nonnegative integer and limit an integer in 1..{MAX_PITCH_PAGE}.")
    total = len(track.items)
    last = min(offset + limit, total)
    return {"provenance": source.provenance(), **track.settings, "summary": track.summary, "offset": offset,
            "total": total, "next_offset": last if last < total else None,
            "items": track.items[min(offset, total):last]}


def pitch_track(source: AudioSource, *, start_seconds: float | None = None, end_seconds: float | None = None,
                hop_seconds: float = 0.05, fmin: float = 30.0, fmax: float = 2000.0,
                min_confidence: float = 0.5, offset: int = 0, limit: int = 64) -> dict[str, object]:
    """Paged ``{time, f0_hz, midi, confidence}`` frames plus a start/end/glide summary.

    Lower ``fmax`` for more decimation and speed; raise ``fmin`` for shorter
    frames. Unvoiced frames keep their confidence but report ``f0_hz`` as None.
    """
    hop, low, high, confidence = _validate(source, hop_seconds, fmin, fmax, min_confidence)
    begin, end = span_frames(source, start_seconds, end_seconds)
    return _page(source, _track(source, begin, end, hop, low, high, confidence), offset, limit)
