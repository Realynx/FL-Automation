"""Explicit BS.1770 K-weighting/gates and an opt-in oversampled true-peak estimate.

Mono and stereo only: channel roles of multichannel PCM cannot be inferred safely.
This implementation is tested against analytic EBU signals, not certified as a meter.
"""

from __future__ import annotations

import math
from array import array
from collections.abc import Sequence

from .audio import AudioSource


def db(value: float, multiplier: float = 20) -> float | None:
    return multiplier * math.log10(value) if value > 0 else None


def _coefficients(rate: int) -> tuple[tuple[float, ...], tuple[float, ...]]:
    k = math.tan(math.pi * 1681.974450955533 / rate)
    vh = 10 ** (3.999843853973347 / 20)
    vb = vh ** 0.4996667741545416
    q = 0.7071752369554196
    denominator = 1 + k / q + k * k
    shelf = ((vh + vb * k / q + k * k) / denominator, 2 * (k * k - vh) / denominator,
             (vh - vb * k / q + k * k) / denominator, 2 * (k * k - 1) / denominator,
             (1 - k / q + k * k) / denominator)
    k = math.tan(math.pi * 38.13547087602444 / rate)
    q = 0.5003270373238773
    denominator = 1 + k / q + k * k
    highpass = (1.0, -2.0, 1.0, 2 * (k * k - 1) / denominator,
                (1 - k / q + k * k) / denominator)
    return shelf, highpass


def _filter(samples: Sequence[float], coefficients: tuple[float, ...]) -> array[float]:
    b0, b1, b2, a1, a2 = coefficients
    x1 = x2 = y1 = y2 = 0.0
    result = array("d")
    for x in samples:
        y = b0 * x + b1 * x1 + b2 * x2 - a1 * y1 - a2 * y2
        result.append(y)
        x2, x1, y2, y1 = x1, x, y1, y
    return result


class Loudness:
    def __init__(self, source: AudioSource) -> None:
        if len(source.channels) > 2 or not 8000 <= source.sample_rate <= 192000:
            raise ValueError("Loudness requires mono/stereo PCM at 8000..192000 Hz; no channel layout is inferred.")
        self.rate = source.sample_rate
        energy = array("d", [0]) * source.frame_count
        coefficients = _coefficients(self.rate)
        for channel in source.channels:
            weighted = _filter(_filter(channel, coefficients[0]), coefficients[1])
            for i, value in enumerate(weighted):
                energy[i] += value * value
        self.prefix = array("d", [0])
        total = 0.0
        for value in energy:
            total += value
            self.prefix.append(total)

    def energy(self, begin: int, end: int) -> float:
        return max(0, self.prefix[end] - self.prefix[begin]) / (end - begin)

    def measure(self, begin: int, end: int) -> float | None:
        value = db(self.energy(begin, end), 10)
        return None if value is None else value - 0.691

    def integrated(self, begin: int = 0, end: int | None = None) -> float | None:
        """Gated integrated loudness over selected frames [begin, end); blocks start at begin."""
        block = round(0.4 * self.rate)
        hop = round(0.1 * self.rate)
        stop = len(self.prefix) - 1 if end is None else end
        energies = [self.energy(start, start + block)
                    for start in range(begin, stop + 1 - block, hop)]
        absolute = 10 ** ((-70 + 0.691) / 10)
        retained = [energy for energy in energies if energy > absolute]
        if not retained:
            return None
        relative = sum(retained) / len(retained) / 10
        gated = [energy for energy in retained if energy > relative]
        return -0.691 + 10 * math.log10(sum(gated) / len(gated)) if gated else None


def true_peak_frames(source: AudioSource) -> array[float]:
    """49-tap Hann-windowed sinc interpolation, 4x, with zero outside selected range.

    Each frame stores the maximum interpolated magnitude over its four phases and
    every audio channel. This is an estimate; sample peak remains a separate metric.
    """
    factor, taps, center = 4, 49, 24
    phases: list[list[tuple[int, float]]] = [[] for _ in range(factor)]
    for j in range(taps):
        x = (j - center) / factor
        sinc = 1.0 if x == 0 else math.sin(math.pi * x) / (math.pi * x)
        coefficient = sinc * (0.5 - 0.5 * math.cos(2 * math.pi * j / (taps - 1)))
        if abs(coefficient) > 1e-15:
            phases[j % factor].append((j // factor, coefficient))
    count = source.frame_count
    result = array("d", [0]) * count
    for channel in source.channels:
        for frame in range(count):
            peak = abs(channel[frame])
            position = frame + center // factor
            for phase in phases:
                value = sum(channel[position - delay] * coefficient
                            for delay, coefficient in phase if 0 <= position - delay < count)
                peak = max(peak, abs(value))
            result[frame] = max(result[frame], peak)
    return result
