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
