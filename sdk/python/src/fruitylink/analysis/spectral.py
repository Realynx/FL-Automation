"""Bounded, dependency-free spectral summaries of explicitly supplied PCM.

This uses averaged one-sided modified periodograms, a periodic Hann window and
50% overlap. The final segment is anchored at the end to avoid dropping a tail.
Channel powers are averaged; signals are never summed before the transform.
See docs/python-spectral-analysis.md for definitions and limitations.
"""

from bisect import bisect_right
from collections.abc import Sequence
from dataclasses import dataclass
from math import cos, fsum, isfinite, pi, sin

from ..values import JsonValue
from .audio import AudioSource, finite

_MAX_TRANSFORM_SAMPLES = 4_194_304
_DEFAULT_EDGES = (0.0, 60.0, 250.0, 500.0, 2000.0, 4000.0, 6000.0, 12000.0)


@dataclass(frozen=True)
class _Settings:
    fft_size: int
    edges: tuple[float, ...]
    rolloff_fraction: float
    dominant_bins: int


def spectral(source: AudioSource, *, fft_size: int = 2048,
             band_edges_hz: Sequence[float] | None = None,
             rolloff_fraction: float = 0.85, dominant_bins: int = 5) -> dict[str, JsonValue]:
    """Summarize the source's selected frame range; frequencies are not pitch estimates.

    Bands contain bins whose centers fall in [low, high), with the last upper
    edge included. Fractions use total 0..Nyquist power, even for custom edges.
    Long selections exceeding the work bound must use shorter ranges or pages.
    """
    settings = _settings(source, fft_size, band_edges_hz, rolloff_fraction, dominant_bins)
    _check_work(source, [(0, source.frame_count)], settings)
    return _summarize(source, 0, source.frame_count, settings)


def spectral_windows(source: AudioSource, *, window_seconds: float = 1.0,
                     hop_seconds: float | None = None, offset: int = 0, limit: int = 16,
                     fft_size: int = 2048, band_edges_hz: Sequence[float] | None = None,
                     rolloff_fraction: float = 0.85,
                     dominant_bins: int = 5) -> dict[str, JsonValue]:
    """Page temporal spectral summaries; the last window may be shorter.

    Window starts are relative to the selected source. Returned frame bounds
    and seconds are absolute in the original source, with exclusive ends.
    No frames or windows are silently subsampled to satisfy resource limits.
    """
    settings = _settings(source, fft_size, band_edges_hz, rolloff_fraction, dominant_bins)
    width = _seconds_frames(window_seconds, source.sample_rate, "window_seconds")
    hop = width if hop_seconds is None else _seconds_frames(hop_seconds, source.sample_rate, "hop_seconds")
    _integer(offset, "offset", 0, 2**53 - 1)
    _integer(limit, "limit", 1, 64)
    total = (source.frame_count + hop - 1) // hop
    stop = min(total, offset + limit)
    spans = [(index * hop, min(source.frame_count, index * hop + width)) for index in range(offset, stop)]
    _check_work(source, spans, settings)
    items: list[JsonValue] = [_summarize(source, start, end, settings) for start, end in spans]
    return {"items": items, "total": total, "offset": offset,
            "next_offset": stop if stop < total else None,
            "window_frames": width, "hop_frames": hop,
            "window_seconds": width / source.sample_rate, "hop_seconds": hop / source.sample_rate}


def _integer(value: int, name: str, low: int, high: int) -> None:
    if type(value) is not int or not low <= value <= high:
        raise ValueError(f"{name} must be an integer in {low}..{high}.")


def _seconds_frames(value: float, sample_rate: int, name: str) -> int:
    frames = finite(value, name, positive=True) * sample_rate
    if not isfinite(frames) or frames > 2**53 - 1:
        raise ValueError(f"{name} is too large.")
    result = round(frames)
    if result < 1:
        raise ValueError(f"{name} must span at least one sample frame.")
    return result


def _settings(source: AudioSource, fft_size: int, edges: Sequence[float] | None,
              fraction: float, dominant: int) -> _Settings:
    _integer(fft_size, "fft_size", 32, 16384)
    if fft_size & (fft_size - 1):
        raise ValueError("fft_size must be a power of two.")
    _integer(dominant, "dominant_bins", 0, 16)
    fraction = finite(fraction, "rolloff_fraction")
    if not 0 < fraction <= 1:
        raise ValueError("rolloff_fraction must be finite and in (0, 1].")
    if source.sample_rate <= 0 or not source.channels or source.frame_count <= 0:
        raise ValueError("Spectral analysis requires nonempty audio and a positive sample rate.")
    if any(len(channel) != source.frame_count for channel in source.channels):
        raise ValueError("Audio channels must have equal frame counts.")
    return _Settings(fft_size, _band_edges(edges, source.sample_rate / 2), fraction, dominant)


def _band_edges(edges: Sequence[float] | None, nyquist: float) -> tuple[float, ...]:
    if edges is None:
        return (*[edge for edge in _DEFAULT_EDGES if edge < nyquist], nyquist)
    if not 2 <= len(edges) <= 33:
        raise ValueError("band_edges_hz requires 2..33 increasing edges.")
    values = tuple(finite(edge, "Band edges") for edge in edges)
    if values[0] < 0 or values[-1] > nyquist or any(a >= b for a, b in zip(values, values[1:])):
        raise ValueError("Band edges must increase strictly within 0..Nyquist.")
    return values


def _segment_count(frames: int, size: int) -> int:
    if frames <= size:
        return 1
    return (frames - size + size // 2 - 1) // (size // 2) + 1


def _check_work(source: AudioSource, spans: Sequence[tuple[int, int]], settings: _Settings) -> None:
    transforms = sum(_segment_count(end - start, settings.fft_size) for start, end in spans)
    if transforms * settings.fft_size * len(source.channels) > _MAX_TRANSFORM_SAMPLES:
        raise ValueError("Spectral work limit exceeded; select a shorter source range, reduce page limit, "
                         "or analyze fewer channels. No samples were skipped.")


def _segment_starts(start: int, end: int, size: int) -> list[int]:
    if end - start <= size:
        return [start]
    starts = list(range(start, end - size + 1, size // 2))
    if starts[-1] != end - size:
        starts.append(end - size)
    return starts


def _fft(values: list[complex]) -> None:
    """In-place radix-2 forward DFT, with no normalization."""
    size = len(values)
    reversed_index = 0
    for index in range(1, size):
        bit = size >> 1
        while reversed_index & bit:
            reversed_index ^= bit
            bit >>= 1
        reversed_index ^= bit
        if index < reversed_index:
            values[index], values[reversed_index] = values[reversed_index], values[index]
    width = 2
    while width <= size:
        half = width // 2
        angle = -2 * pi / width
        rotation = complex(cos(angle), sin(angle))
        for start in range(0, size, width):
            factor = complex(1.0)
            for index in range(start, start + half):
                even, odd = values[index], factor * values[index + half]
                values[index], values[index + half] = even + odd, even - odd
                factor *= rotation
        width *= 2


def _periodogram(channel: Sequence[float], start: int, window: list[float], size: int) -> list[float]:
    values = [complex(channel[start + index] * weight) for index, weight in enumerate(window)]
    if any(not isfinite(value.real) for value in values):
        raise ValueError("Audio contains nonfinite samples or exceeds spectral numeric range.")
    values.extend([0j] * (size - len(values)))
    _fft(values)
    normalization = size * fsum(weight * weight for weight in window)
    powers = [(value.real * value.real + value.imag * value.imag) / normalization
              for value in values[:size // 2 + 1]]
    for index in range(1, size // 2):
        powers[index] *= 2
    if any(not isfinite(power) for power in powers):
        raise ValueError("Audio exceeds spectral numeric range.")
    return powers


def _average_power(source: AudioSource, start: int, end: int, size: int) -> tuple[list[float], int, int]:
    segment_frames = min(size, end - start)
    window = ([1.0] if segment_frames == 1 else
              [0.5 - 0.5 * cos(2 * pi * index / segment_frames) for index in range(segment_frames)])
    starts = _segment_starts(start, end, size)
    count = len(starts) * len(source.channels)
    powers = [0.0] * (size // 2 + 1)
    for channel in source.channels:
        for segment_start in starts:
            part = _periodogram(channel, segment_start, window, size)
            for index, power in enumerate(part):
                powers[index] += power / count
    return powers, len(starts), segment_frames


def _bands(powers: list[float], resolution: float, edges: tuple[float, ...],
           total: float) -> list[JsonValue]:
    amounts = [0.0] * (len(edges) - 1)
    for index, power in enumerate(powers):
        frequency = index * resolution
        band = bisect_right(edges, frequency) - 1
        if frequency == edges[-1]:
            band -= 1
        if 0 <= band < len(amounts):
            amounts[band] += power
    return [{"low_hz": low, "high_hz": high, "upper_inclusive": index == len(amounts) - 1,
             "power_fraction": amount / total if total else None}
            for index, (low, high, amount) in enumerate(zip(edges, edges[1:], amounts))]


def _descriptors(powers: list[float], resolution: float, total: float,
                 settings: _Settings) -> dict[str, JsonValue]:
    if not total:
        return {"centroid_hz": None, "rolloff_hz": None, "dominant_bins": []}
    centroid = fsum((power / total) * index * resolution for index, power in enumerate(powers))
    cumulative = 0.0
    rolloff = (len(powers) - 1) * resolution
    for index, power in enumerate(powers):
        cumulative += power
        if cumulative >= total * settings.rolloff_fraction:
            rolloff = index * resolution
            break
    ordered = sorted(range(len(powers)), key=lambda index: (-powers[index], index))
    dominant: list[JsonValue] = [{"bin": index, "frequency_hz": index * resolution,
                                  "power_fraction": powers[index] / total}
                                 for index in ordered[:settings.dominant_bins] if powers[index] > 0]
    return {"centroid_hz": centroid, "rolloff_hz": rolloff, "dominant_bins": dominant}


def _summarize(source: AudioSource, start: int, end: int, settings: _Settings) -> dict[str, JsonValue]:
    powers, segments, segment_frames = _average_power(source, start, end, settings.fft_size)
    total = fsum(powers)
    resolution = source.sample_rate / settings.fft_size
    first, last = source.origin_frame + start, source.origin_frame + end
    result: dict[str, JsonValue] = {
        "source": source.source, "source_kind": source.source_kind,
        "sample_format": source.sample_format, "sample_rate_hz": source.sample_rate,
        "channels": len(source.channels), "start_frame": first, "end_frame": last,
        "start_seconds": first / source.sample_rate, "end_seconds": last / source.sample_rate,
        "requested_start_seconds": source.requested_start_seconds,
        "requested_end_seconds": source.requested_end_seconds,
        "frame_count": end - start, "total_source_frames": source.total_source_frames,
        "channel_aggregation": "mean_channel_power", "method": "averaged_modified_periodogram",
        "window": "periodic_hann" if segment_frames > 1 else "single_sample",
        "detrend": "none", "fft_size": settings.fft_size, "segment_frames": segment_frames,
        "segment_count": segments, "segment_hop_frames": settings.fft_size // 2,
        "tail_policy": "anchor_final_segment", "zero_padded": segment_frames < settings.fft_size,
        "bin_spacing_hz": resolution, "segment_resolution_hz": source.sample_rate / segment_frames,
        "nyquist_hz": source.sample_rate / 2, "weighting": "power",
        "power_units": "normalized_amplitude_squared", "windowed_mean_square": total,
        "rolloff_fraction": settings.rolloff_fraction, "zero_spectral_power": total == 0,
        "silence": not any(channel[index] != 0 for channel in source.channels for index in range(start, end)),
        "band_assignment": "bin_center", "bands": _bands(powers, resolution, settings.edges, total),
    }
    result.update(_descriptors(powers, resolution, total, settings))
    return result
