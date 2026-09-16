"""Numeric kernels: pure Python by default, numpy when it imports.

Both paths return the same values within floating-point summation tolerance
(about 1e-12 relative). ``USE_NUMPY`` can be set to False to force the pure
path, which is what FL Studio's embedded Python uses when numpy is absent.
Recursive filters stay in pure Python and iterate ``array('d')`` objects
directly; iterating numpy arrays from Python is slower than that.
"""

from __future__ import annotations

import importlib
import importlib.util
import math
from array import array
from collections.abc import Sequence
from operator import mul
from typing import Any

_numpy: Any = None
if importlib.util.find_spec("numpy") is not None:
    try:
        _numpy = importlib.import_module("numpy")
    except Exception:  # pragma: no cover - a broken numpy install must not disable analysis
        _numpy = None
USE_NUMPY = _numpy is not None
_sumprod: Any = getattr(math, "sumprod", None)


def _vector(values: Sequence[float]) -> Any:
    if isinstance(values, array) and values.typecode == "d":
        return _numpy.frombuffer(values, dtype=_numpy.float64)
    return _numpy.asarray(values, dtype=_numpy.float64)


def _pure_dot(a: Sequence[float], b: Sequence[float]) -> float:
    if _sumprod is not None:
        return float(_sumprod(a, b))
    return math.fsum(map(mul, a, b))


def dot(a: Sequence[float], b: Sequence[float]) -> float:
    """Sum of elementwise products of two equally long sequences."""
    if len(a) != len(b):
        raise ValueError("dot requires equal lengths.")
    if USE_NUMPY and _numpy is not None:
        return float(_numpy.dot(_vector(a), _vector(b)))
    return _pure_dot(a, b)


def sum_squares(values: Sequence[float]) -> float:
    return dot(values, values)


def mono_mix(channels: Sequence[Sequence[float]], begin: int, end: int) -> array[float]:
    """Mean of all channels over [begin, end); opposite-polarity content cancels here."""
    if len(channels) == 1:
        return array("d", channels[0][begin:end])
    if USE_NUMPY and _numpy is not None:
        stacked = _numpy.stack([_vector(channel)[begin:end] for channel in channels])
        result = array("d")
        result.frombytes(stacked.mean(axis=0).astype(_numpy.float64).tobytes())
        return result
    scale = 1 / len(channels)
    if len(channels) == 2:
        left, right = channels[0][begin:end], channels[1][begin:end]
        return array("d", [scale * (a + b) for a, b in zip(left, right)])
    parts = [channel[begin:end] for channel in channels]
    return array("d", [scale * sum(values) for values in zip(*parts)])


def box_decimate(values: Sequence[float], factor: int) -> array[float]:
    """Average consecutive groups of ``factor`` samples; a trailing partial group is dropped."""
    if factor == 1:
        return array("d", values)
    count = len(values) // factor
    if USE_NUMPY and _numpy is not None:
        grouped = _vector(values)[:count * factor].reshape(count, factor)
        result = array("d")
        result.frombytes(grouped.mean(axis=1).astype(_numpy.float64).tobytes())
        return result
    scale = 1 / factor
    groups = [iter(values[:count * factor])] * factor
    return array("d", [scale * sum(group) for group in zip(*groups)])


def autocorrelation(values: Sequence[float], min_lag: int, max_lag: int) -> list[float]:
    """Unnormalized autocorrelation sum(x[n] * x[n + lag]) for each lag in min_lag..max_lag."""
    count = len(values)
    if not 0 <= min_lag <= max_lag < count:
        raise ValueError("autocorrelation lags must satisfy 0 <= min_lag <= max_lag < len(values).")
    if USE_NUMPY and _numpy is not None:
        vector = _vector(values)
        full = _numpy.correlate(vector, vector, mode="full")[count - 1:]
        return [float(value) for value in full[min_lag:max_lag + 1]]
    return [_pure_dot(values[:count - lag], values[lag:]) for lag in range(min_lag, max_lag + 1)]


def peak(channels: Sequence[Sequence[float]], begin: int, end: int) -> float:
    """Largest absolute sample over all channels in [begin, end)."""
    if USE_NUMPY and _numpy is not None:
        return max(float(_numpy.abs(_vector(channel)[begin:end]).max()) for channel in channels)
    return max(max(max(channel[begin:end]), -min(channel[begin:end])) for channel in channels)


def mean_channel_power(channels: Sequence[Sequence[float]], begin: int, end: int) -> float:
    """Mean over channels of the mean square; robust to anti-phase stereo content."""
    frames = end - begin
    return sum(sum_squares(channel[begin:end]) for channel in channels) / (frames * len(channels))


def block_mean_squares(values: Sequence[float], block: int) -> array[float]:
    """Mean square of consecutive ``block``-frame groups; the final partial group keeps its own length."""
    if block < 1:
        raise ValueError("block must be at least one frame.")
    count = len(values)
    full = count // block
    if USE_NUMPY and _numpy is not None:
        squares = _vector(values) ** 2
        result = array("d")
        if full:
            result.frombytes(squares[:full * block].reshape(full, block).mean(axis=1)
                             .astype(_numpy.float64).tobytes())
        if count > full * block:
            result.append(float(squares[full * block:].mean()))
        return result
    result = array("d")
    for start in range(0, count, block):
        chunk = values[start:start + block]
        result.append(_pure_dot(chunk, chunk) / len(chunk))
    return result


def active_bounds(channels: Sequence[Sequence[float]], threshold: float) -> tuple[int, int] | None:
    """``(first, last + 1)`` frames where any channel exceeds ``threshold`` in magnitude; None when none does."""
    first: int | None = None
    last: int | None = None
    for channel in channels:
        if USE_NUMPY and _numpy is not None:
            hits = _numpy.flatnonzero(_numpy.abs(_vector(channel)) > threshold)
            if hits.size == 0:
                continue
            begin, end = int(hits[0]), int(hits[-1])
        else:
            begin = next((index for index, value in enumerate(channel) if abs(value) > threshold), -1)
            if begin < 0:
                continue
            end = next(index for index in range(len(channel) - 1, begin - 1, -1) if abs(channel[index]) > threshold)
        first = begin if first is None else min(first, begin)
        last = end if last is None else max(last, end)
    if first is None or last is None:
        return None
    return first, last + 1


def hann(frames: int) -> list[float]:
    """Periodic Hann window, matching the spectral module; a single frame is rectangular."""
    if frames == 1:
        return [1.0]
    return [0.5 - 0.5 * math.cos(2 * math.pi * index / frames) for index in range(frames)]


def mean_periodogram(values: Sequence[float], starts: Sequence[int], segment_frames: int,
                     size: int) -> list[float]:
    """Average one-sided modified periodogram over Hann-windowed segments (spectral module conventions).

    Each segment covers ``values[start:start + segment_frames]`` and is zero-padded to ``size``.
    Powers are normalized so that their sum equals the windowed mean square of the audio.
    """
    if not starts:
        raise ValueError("mean_periodogram needs at least one segment start.")
    window = hann(segment_frames)
    if USE_NUMPY and _numpy is not None:
        vector = _vector(values)
        weights = _numpy.asarray(window)
        frames = _numpy.stack([vector[start:start + segment_frames] for start in starts]) * weights
        if not _numpy.isfinite(frames).all():
            raise ValueError("Audio contains nonfinite samples or exceeds spectral numeric range.")
        spectrum = _numpy.fft.rfft(frames, n=size, axis=1)
        powers = (spectrum.real ** 2 + spectrum.imag ** 2) / (size * float(_numpy.dot(weights, weights)))
        powers[:, 1:size // 2] *= 2
        return [float(value) for value in powers.mean(axis=0)]
    from .spectral import _periodogram
    total = [0.0] * (size // 2 + 1)
    for start in starts:
        for index, power in enumerate(_periodogram(values, start, window, size)):
            total[index] += power / len(starts)
    return total


def fir_filter(values: Sequence[float], taps: Sequence[float]) -> array[float]:
    """Causal zero-state FIR convolution truncated to ``len(values)``; numpy path only.

    Overlap-add with FFT blocks. Callers on the pure path keep their recursive filters.
    """
    if not (USE_NUMPY and _numpy is not None):
        raise RuntimeError("fir_filter requires numpy; use the recursive filters on the pure path.")
    vector = _vector(values)
    kernel = _numpy.asarray(taps, dtype=_numpy.float64)
    count, width = len(vector), len(kernel)
    block = 1
    while block < max(4 * width, 65536):
        block *= 2
    step = block - width + 1
    kernel_spectrum = _numpy.fft.rfft(kernel, n=block)
    output = _numpy.zeros(count + block, dtype=_numpy.float64)
    for start in range(0, count, step):
        chunk = vector[start:start + step]
        output[start:start + block] += _numpy.fft.irfft(_numpy.fft.rfft(chunk, n=block) * kernel_spectrum, n=block)
    result = array("d")
    result.frombytes(output[:count].tobytes())
    return result
