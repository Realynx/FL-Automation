"""LLM-oriented audio descriptions: what a sample, loop, stem or mix "looks like", as compact text.

``describe_audio`` turns a WAV path, planar PCM or an analysis object into an
``AudioDescription``: a JSON-safe ``data`` dict plus a ``text`` rendering of
roughly 20-40 lines that an agent can read instead of listening. Numbers use
the conventions of the rest of this package (dBFS re amplitude 1, BS.1770
LUFS, absolute file seconds); bar positions need an explicit ``bpm``.
``compare_audio`` reports two descriptions' differences in the same vocabulary
and ``describe_samples`` browses many files with a content-hash cache.
Thresholds behind the character tags follow the project's taste rules: harsh
2-4 kHz is flagged early, and "wide" and "bright" are reported separately so
"wide but not bright" can be checked.
"""

from __future__ import annotations

import hashlib
import json
import math
import os
from array import array
from collections.abc import Iterable, Sequence
from dataclasses import dataclass
from pathlib import Path
from statistics import median
from typing import Any

from ._kernels import active_bounds, block_mean_squares, mean_channel_power, mean_periodogram, mono_mix, peak
from .audio import AudioSource, finite, from_pcm
from .bands import coerce_source
from .bars import stereo_width
from .loudness import db, integrated_loudness
from .pitch import _decimation, _frame_pitch, _prepare

DESCRIBE_VERSION = 1
DETAILS: dict[str, tuple[int, int, int, int]] = {
    # slices in the envelope sketch, equal spectral segments for long audio, onsets listed, FFT segments
    "brief": (16, 0, 6, 16), "normal": (32, 4, 8, 32), "full": (64, 8, 24, 64)}
SILENCE_DBFS = -60.0
HOP_SECONDS = 0.005
FFT_SIZE = 4096
SHORT_SECONDS = 5.0
BANDS: tuple[tuple[str, float, float | None], ...] = (
    ("sub", 0.0, 60.0), ("low", 60.0, 250.0), ("lowmid", 250.0, 500.0), ("mid", 500.0, 2000.0),
    ("pres", 2000.0, 4000.0), ("high", 4000.0, 8000.0), ("air", 8000.0, None))
BAND_LEGEND = "sub<60 low60-250 lowmid250-500 mid500-2k pres2-4k high4-8k air>8k"
NOTE_NAMES = ("C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B")
SKETCH_RAMP = " .:-=+*#%@"
PURE_LOUDNESS_LIMIT = 4_000_000
METHOD = ("5 ms RMS envelope (channel-mean power); onsets = rises of 8 dB or more over 30 ms above -50 dBFS, "
          "50 ms hold; 4096-point Hann periodograms sampled across each segment; band dB relative to the "
          "loudest band; tilt = octave-band slope with pink noise at 0; flatness = geometric/arithmetic mean "
          "40 Hz-16 kHz; pitch = normalized autocorrelation on up to five frames; width = L/R correlation")


@dataclass(frozen=True)
class AudioDescription:
    """A description record; ``data`` is JSON-safe and ``text`` is the compact rendering."""

    data: dict[str, Any]
    text: str

    @property
    def tags(self) -> list[str]:
        return [str(tag) for tag in self.data["tags"]]

    def __str__(self) -> str:
        return self.text


@dataclass(frozen=True)
class AudioComparison:
    data: dict[str, Any]
    text: str

    def __str__(self) -> str:
        return self.text


@dataclass(frozen=True)
class SampleTable:
    rows: list[dict[str, Any]]
    descriptions: dict[str, AudioDescription]
    text: str

    def __str__(self) -> str:
        return self.text


# ---------------------------------------------------------------------------
# Input coercion


def _planar(samples: Any) -> list[Sequence[float]]:
    if hasattr(samples, "ndim") and hasattr(samples, "tolist"):
        if samples.ndim == 1:
            return [samples.tolist()]
        if samples.ndim == 2:
            planar = samples if samples.shape[0] <= 32 < samples.shape[1] or samples.shape[0] <= samples.shape[1] \
                else samples.T
            return [row.tolist() for row in planar]
        raise TypeError("PCM arrays must be one- or two-dimensional.")
    if not isinstance(samples, Sequence) or isinstance(samples, (str, bytes)) or not samples:
        raise TypeError("PCM samples must be a nonempty sequence (mono) or a sequence of channels.")
    first = samples[0]
    if isinstance(first, (int, float)):
        return [samples]
    return list(samples)


def as_source(source: object) -> AudioSource:
    """Accept a WAV path, ``(samples, sample_rate)``, an AudioSource, or an analysis object."""
    if isinstance(source, tuple) and len(source) == 2 and isinstance(source[1], int) \
            and not isinstance(source[1], bool):
        return from_pcm(_planar(source[0]), source[1])
    return coerce_source(source, None, None)


def _bar_grid(bpm: float | None, ppq: int | None, start_bar: int | None,
              beats_per_bar: int) -> dict[str, Any] | None:
    if bpm is None:
        if ppq is not None or start_bar is not None:
            raise ValueError("ppq and start_bar need a bpm.")
        return None
    tempo = finite(bpm, "bpm", positive=True)
    if tempo > 1000:
        raise ValueError("bpm must not exceed 1000.")
    if ppq is not None and (type(ppq) is not int or not 1 <= ppq <= 100000):
        raise ValueError("ppq must be an integer in 1..100000.")
    if type(beats_per_bar) is not int or not 1 <= beats_per_bar <= 64:
        raise ValueError("beats_per_bar must be an integer in 1..64.")
    first = 1 if start_bar is None else start_bar
    if type(first) is not int:
        raise ValueError("start_bar must be an integer.")
    return {"bpm": tempo, "ppq": ppq, "start_bar": first, "beats_per_bar": beats_per_bar,
            "bar_seconds": beats_per_bar * 60 / tempo}


def _position(seconds: float, grid: dict[str, Any] | None) -> dict[str, Any]:
    item: dict[str, Any] = {"time": round(seconds, 3)}
    if grid is None:
        return item
    beats = seconds * grid["bpm"] / 60
    bar = math.floor(beats / grid["beats_per_bar"])
    item["bar_beat"] = f"{grid['start_bar'] + bar}:{beats - bar * grid['beats_per_bar'] + 1:.2f}"
    if grid["ppq"] is not None:
        item["tick"] = round(beats * grid["ppq"])
    return item


# ---------------------------------------------------------------------------
# Envelope, onsets and decay


def _envelope_db(source: AudioSource, hop: int) -> list[float]:
    total: array[float] | None = None
    for channel in source.channels:
        squares = block_mean_squares(channel, hop)
        if total is None:
            total = squares
        else:
            for index, value in enumerate(squares):
                total[index] += value
    assert total is not None
    scale = 1 / len(source.channels)
    return [10 * math.log10(value * scale) if value > 0 else -120.0 for value in total]


def _onsets(env: list[float], hop_seconds: float) -> list[tuple[float, float, float]]:
    """(onset time, peak time, rise dB) for every rise of 8 dB or more within 30 ms; 50 ms hold."""
    look, hold = 6, max(1, round(0.05 / hop_seconds))
    found: list[tuple[float, float, float]] = []
    index, count = 0, len(env)
    while index < count:
        base = min(env[max(0, index - look):index], default=-120.0)  # audio starting hot is an onset at 0
        if env[index] - base >= 8 and env[index] >= -50:
            start = index
            while start - 1 >= max(0, index - look) and env[start - 1] > base + 1:
                start -= 1
            top = index
            while top + 1 < count and env[top + 1] > env[top]:
                top += 1
            found.append((start * hop_seconds, top * hop_seconds, min(60.0, env[top] - base)))
            index = top + hold
        index += 1
    return found


def _attack_ms(source: AudioSource, mono: array[float], onset_seconds: float) -> float | None:
    rate = source.sample_rate
    begin = round(onset_seconds * rate)
    end = min(source.frame_count, begin + round(0.1 * rate))
    block = max(1, round(0.001 * rate))
    if end - begin < 2 * block:
        return None
    fine = block_mean_squares(mono[begin:end], block)
    best = max(range(len(fine)), key=lambda index: (fine[index], -index))
    return round(best * block / rate * 1000, 1)


def _decay(env: list[float], hop_seconds: float) -> dict[str, Any]:
    top = max(range(len(env)), key=lambda index: (env[index], -index))
    result: dict[str, Any] = {"peak_time": round(top * hop_seconds, 3)}
    for drop in (20, 40):
        crossing = next((index for index in range(top + 1, len(env)) if env[index] <= env[top] - drop), None)
        result[f"to_minus_{drop}_db_seconds"] = None if crossing is None else round((crossing - top) * hop_seconds, 3)
    return result


def _sketch(env_ms: list[float], slices: int) -> dict[str, Any]:
    per_slice = max(1, math.ceil(len(env_ms) / slices))
    levels: list[float | None] = []
    for start in range(0, len(env_ms), per_slice):
        chunk = env_ms[start:start + per_slice]
        mean = sum(chunk) / len(chunk)
        levels.append(10 * math.log10(mean) if mean > 0 else None)
    loudest = max((level for level in levels if level is not None), default=None)
    relative = [None if level is None or loudest is None else level - loudest for level in levels]
    chars = "".join(" " if rel is None or rel <= -60 else SKETCH_RAMP[min(9, int((rel + 60) / 6))] for rel in relative)
    return {"slice_count": len(levels), "loudest_slice_dbfs": None if loudest is None else round(loudest, 1),
            "slices_dbfs": [None if level is None else round(level) for level in levels], "sketch": chars,
            "legend": "one char per slice, RMS relative to the loudest slice: ' '<-60 dB, '.'-60..-54 ... '@' 0"}


# ---------------------------------------------------------------------------
# Spectrum


def _segment_starts(begin: int, end: int, cap: int) -> tuple[list[int], int]:
    frames = end - begin
    if frames <= FFT_SIZE:
        return [begin], frames
    hop = FFT_SIZE // 2
    count = (frames - FFT_SIZE) // hop + 1
    if count <= cap:
        return [begin + index * hop for index in range(count)], FFT_SIZE
    step = (frames - FFT_SIZE) / (cap - 1)
    return [begin + round(index * step) for index in range(cap)], FFT_SIZE


def _band_fractions(powers: list[float], resolution: float, total: float) -> dict[str, float]:
    amounts = {name: 0.0 for name, _, _ in BANDS}
    for index, power in enumerate(powers):
        frequency = index * resolution
        for name, low, high in BANDS:
            if frequency >= low and (high is None or frequency < high):
                amounts[name] += power
                break
    return {name: amount / total for name, amount in amounts.items()}


def _tilt(powers: list[float], resolution: float, nyquist: float, total: float) -> float | None:
    points: list[tuple[float, float]] = []
    low = 62.5
    while low * 2 <= nyquist and low < 20000:
        first, last = math.ceil(low / resolution), math.ceil(low * 2 / resolution)
        power = sum(powers[first:last])
        if power > total * 1e-9:
            points.append((math.log2(low / 62.5), 10 * math.log10(power)))
        low *= 2
    if len(points) < 3:
        return None
    mean_x = sum(x for x, _ in points) / len(points)
    mean_y = sum(y for _, y in points) / len(points)
    denominator = sum((x - mean_x) ** 2 for x, _ in points)
    return round(sum((x - mean_x) * (y - mean_y) for x, y in points) / denominator, 2)


def _flatness(powers: list[float], resolution: float, nyquist: float, total: float) -> float:
    first, last = math.ceil(40 / resolution), math.floor(min(16000, nyquist) / resolution)
    floor = total * 1e-12
    values = [max(floor, power) for power in powers[first:last + 1]]
    if not values:
        return 0.0
    geometric = math.exp(sum(math.log(value) for value in values) / len(values))
    return round(geometric / (sum(values) / len(values)), 4)


def _spectrum(source: AudioSource, mono: array[float], name: str, begin: int, end: int, cap: int,
              grid: dict[str, Any] | None) -> dict[str, Any]:
    if name == "attack":
        # Centre the transient under the window: a periodic Hann is zero at the segment start.
        pad = array("d", [0.0]) * ((end - begin) // 2)
        buffer = pad + mono[begin:end] + pad
        powers = mean_periodogram(buffer, [0], len(buffer), FFT_SIZE)
        starts = [begin]
    else:
        starts, segment_frames = _segment_starts(begin, end, cap)
        powers = mean_periodogram(mono, starts, segment_frames, FFT_SIZE)
    total = math.fsum(powers)
    rate = source.sample_rate
    resolution, nyquist = rate / FFT_SIZE, rate / 2
    item: dict[str, Any] = {"name": name, "start": _position(begin / rate, grid), "end": _position(end / rate, grid),
                            "segments": len(starts), "level_dbfs": None if total <= 0 else round(10 * math.log10(total), 1),
                            "centroid_hz": None, "tilt_db_per_octave": None, "flatness": None,
                            "bands_rel_db": {}, "bands_dbfs": {}}
    if total <= 0:
        return item
    fractions = _band_fractions(powers, resolution, total)
    loudest = max(fractions.values())
    item["centroid_hz"] = round(math.fsum(index * resolution * power for index, power in enumerate(powers)) / total)
    item["tilt_db_per_octave"] = _tilt(powers, resolution, nyquist, total)
    item["flatness"] = _flatness(powers, resolution, nyquist, total)
    item["bands_rel_db"] = {name: (round(10 * math.log10(fraction / loudest), 1) if fraction > 0 else None)
                            for name, fraction in fractions.items()}
    item["bands_dbfs"] = {name: (round(10 * math.log10(fraction * total), 1) if fraction > 0 else None)
                          for name, fraction in fractions.items()}
    return item


def _segment_spans(source: AudioSource, active: tuple[int, int], onsets: Sequence[tuple[float, float, float]],
                   long_segments: int, grid: dict[str, Any] | None) -> list[tuple[str, int, int]]:
    rate = source.sample_rate
    begin, end = active
    spans = [("whole", begin, end)]
    if (end - begin) / rate <= SHORT_SECONDS:
        if not onsets:
            return spans
        onset = max(begin, round(onsets[0][0] * rate))
        marks = [onset, onset + round(0.025 * rate), onset + round(0.25 * rate), end]
        for name, first, last in zip(("attack", "body", "tail"), marks, marks[1:]):
            if min(last, end) - first >= round(0.005 * rate):
                spans.append((name, first, min(last, end)))
        return spans
    step = (end - begin) / long_segments if long_segments else 0
    for index in range(long_segments):
        first, last = begin + round(index * step), begin + round((index + 1) * step)
        if grid is not None:
            label = f"{_position(first / rate, grid)['bar_beat'].split(':')[0]}-" \
                    f"{_position(last / rate, grid)['bar_beat'].split(':')[0]}"
            name = f"bars {label}"
        else:
            name = f"{_clock(first / rate)}-{_clock(last / rate)}"
        spans.append((name, first, last))
    return spans


# ---------------------------------------------------------------------------
# Pitch and tonality


def _pitch(source: AudioSource, active: tuple[int, int], onsets: Sequence[tuple[float, float, float]]) -> dict[str, Any] | None:
    rate = source.sample_rate
    fmin, fmax = 30.0, 1000.0
    if fmax >= rate / 4:
        return None
    factor = _decimation(rate, fmax)
    analysis_rate = rate / factor
    max_lag, min_lag = math.ceil(analysis_rate / fmin), max(2, math.floor(analysis_rate / fmax))
    width = 2 * max_lag
    preroll = round(0.02 * rate)
    need = width * factor + preroll
    first = max(active[0], round((onsets[0][0] + 0.01) * rate) if onsets else active[0])
    last = active[1] - need
    if last < first:
        return None
    positions = [first + round(index * (last - first) / 4) for index in range(5)] if last > first else [first]
    frames: list[tuple[float, float]] = []
    for position in dict.fromkeys(positions):
        signal = _prepare(source, position, position + need, factor)
        frame = signal[preroll // factor:preroll // factor + width]
        if len(frame) < width:
            continue
        f0, confidence = _frame_pitch(frame, analysis_rate, min_lag, max_lag)
        # A pick at the shortest lag is the anti-alias low-pass correlating with itself, not a pitch.
        if f0 is not None and confidence >= 0.5 and fmin * 1.05 <= f0 <= analysis_rate / (min_lag + 1):
            frames.append((f0, confidence))
    if not frames:
        return None
    centre = median(f0 for f0, _ in frames)
    agreeing = [(f0, confidence) for f0, confidence in frames if abs(1200 * math.log2(f0 / centre)) <= 50]
    agreement = len(agreeing) / len(positions)
    if agreement < 0.5:
        return None
    midi = 69 + 12 * math.log2(centre / 440)
    nearest = round(midi)
    return {"f0_hz": round(centre, 1), "midi": round(midi, 2), "note": f"{NOTE_NAMES[nearest % 12]}{nearest // 12 - 1}",
            "cents": round((midi - nearest) * 100), "confidence": round(median(c for _, c in agreeing), 2),
            "agreement": round(agreement, 2), "frames": len(positions)}


def _tonality(flatness: float | None, pitch: dict[str, Any] | None) -> str:
    if flatness is None:
        return "silent"
    if flatness >= 0.3:
        return "noisy"
    if pitch is not None and pitch["confidence"] >= 0.7 and flatness < 0.15:
        return "tonal"
    if pitch is not None or flatness < 0.1:
        return "mixed"
    return "noisy"


def _width_label(width: dict[str, Any], channel_count: int) -> str:
    if channel_count == 1:
        return "mono"
    correlation, side = width.get("correlation"), width.get("side_mid_db")
    if correlation is None:
        return "unknown"
    if side is not None and side <= -40 or correlation >= 0.99:
        return "mono"
    if correlation >= 0.9:
        return "narrow"
    if correlation >= 0.6:
        return "medium"
    if correlation >= 0.2:
        return "wide"
    if correlation >= -0.2:
        return "very wide"
    return "anti-phase"


# ---------------------------------------------------------------------------
# Tags


def _level_tags(level: dict[str, Any]) -> list[str]:
    tags: list[str] = []
    if level["over_full_scale"]:
        tags.append("over full scale")
    elif level["peak_dbfs"] is not None and level["peak_dbfs"] >= -0.3:
        tags.append("hot peak")
    if level["peak_dbfs"] is not None and level["peak_dbfs"] < -18:
        tags.append("quiet")
    if level["crest_db"] is not None and level["crest_db"] >= 18:
        tags.append("high crest")
    elif level["crest_db"] is not None and level["crest_db"] <= 8:
        tags.append("dense/low crest")
    if abs(level["dc_offset"]) > 0.01:
        tags.append("dc offset")
    return tags


def _spectral_tags(whole: dict[str, Any], attack: dict[str, Any] | None, body: dict[str, Any] | None,
                   attack_ms: float | None) -> list[str]:
    tags: list[str] = []
    rel = whole["bands_rel_db"]
    if not rel:
        return tags
    level = {name: (-200.0 if value is None else value) for name, value in rel.items()}
    loudest = max(level, key=lambda name: level[name])
    if level["sub"] >= -3:
        tags.append("sub-heavy")
    elif level["sub"] <= -30:
        tags.append("no sub")
    if loudest == "lowmid":
        tags.append("lowmid-heavy 250-500 Hz")
    body_level = max(level["low"], level["lowmid"], level["mid"])
    harshness = level["pres"] - body_level + 3  # presence against the body; pink noise sits at 0
    if harshness >= 3:
        tags.append("harsh 2-4 kHz")
    elif harshness >= 0:
        tags.append("forward 2-4 kHz")
    centroid, tilt = whole["centroid_hz"], whole["tilt_db_per_octave"]
    if centroid is not None and (centroid >= 3000 or (tilt is not None and tilt >= 1.0)):
        tags.append("bright")
    elif centroid is not None and tilt is not None and centroid <= 800 and tilt <= -3:
        tags.append("dark")
    if level["air"] >= -6:
        tags.append("airy")
    if attack_ms is not None and attack_ms <= 3 and attack is not None and attack["centroid_hz"] is not None:
        body_centroid = body["centroid_hz"] if body is not None else None
        # An attack much brighter than the body, or a bare transient with nothing after it, reads as clicky.
        if not body_centroid or attack["centroid_hz"] >= max(2500, 1.5 * body_centroid):
            tags.append("clicky attack")
    if attack_ms is not None and attack_ms >= 30:
        tags.append("soft attack")
    return tags


def _shape_tags(onsets: dict[str, Any], decay: dict[str, Any], loop: dict[str, Any], stereo: dict[str, Any],
                tonality: str, silence: dict[str, Any]) -> list[str]:
    tags: list[str] = [tonality] if tonality in ("tonal", "noisy") else []
    count = onsets["count"]
    to40 = decay["to_minus_40_db_seconds"]
    if count == 1 and to40 is not None and not loop["steady_state"]:
        tags.append("one-shot")
        tags.append("long tail" if to40 >= 1.0 else "short" if to40 <= 0.15 else "medium tail")
    elif decay["short"] and decay["tail_seconds"] is not None and decay["tail_seconds"] >= 1.5:
        tags.append("long tail")
    if onsets["regular"]:
        tags.append(f"rhythmic ({count} hits, ~{onsets['median_spacing_ms']:.0f} ms apart)")
    if loop["steady_state"]:
        tags.append("steady")
    if loop["loopable"]:
        tags.append("loopable")
    label = stereo["width"]
    if label in ("wide", "very wide"):
        tags.append("wide")
    elif label == "mono":
        tags.append("mono")
    elif label == "anti-phase":
        tags.append("anti-phase")
    if silence["head_seconds"] >= 0.01:
        tags.append(f"leading silence {silence['head_seconds'] * 1000:.0f} ms")
    return tags


# ---------------------------------------------------------------------------
# Assembly


def _clock(seconds: float) -> str:
    minutes, rest = divmod(seconds, 60)
    return f"{int(minutes)}:{rest:05.2f}"


def _silent_description(source: AudioSource, grid: dict[str, Any] | None) -> AudioDescription:
    duration = source.frame_count / source.sample_rate
    data: dict[str, Any] = {"version": DESCRIBE_VERSION, "source": _source_record(source), "grid": grid,
                            "silent": True, "tags": ["silent"], "method": METHOD}
    text = f"{Path(source.source).name} - {duration:.3f} s, silent (no sample above {SILENCE_DBFS:g} dBFS)\ntags: silent"
    return AudioDescription(data, text)


def _source_record(source: AudioSource) -> dict[str, Any]:
    return {"path": source.source, "kind": source.source_kind, "sample_rate": source.sample_rate,
            "channels": len(source.channels), "sample_format": source.sample_format,
            "frames": source.frame_count, "duration_seconds": round(source.frame_count / source.sample_rate, 4)}


def _loudness(source: AudioSource, detail: str) -> tuple[float | None, str | None]:
    from . import _kernels
    if len(source.channels) > 2:
        return None, "unavailable for more than two channels"
    if source.frame_count < round(0.4 * source.sample_rate):
        return None, "shorter than one 400 ms block"
    samples = source.frame_count * len(source.channels)
    if not (_kernels.USE_NUMPY and _kernels._numpy is not None) and samples > PURE_LOUDNESS_LIMIT and detail != "full":
        return None, "skipped on the pure-Python path for long audio; pass detail='full' or install numpy"
    value = integrated_loudness(source)
    return (None if value is None else round(value, 1)), None


def _onset_record(found: Sequence[tuple[float, float, float]], grid: dict[str, Any] | None,
                  duration: float) -> dict[str, Any]:
    spacings = [b[0] - a[0] for a, b in zip(found, found[1:])]
    regular = False
    spacing = None
    if len(spacings) >= 3:
        spacing = median(spacings)
        regular = all(abs(gap - spacing) <= 0.12 * spacing for gap in spacings)
    per_bar = None if grid is None or duration < grid["bar_seconds"] else \
        round(len(found) / (duration / grid["bar_seconds"]), 2)
    return {"count": len(found), "items": [{**_position(start, grid), "rise_db": round(rise, 1)}
                                           for start, _, rise in found[:4096]],
            "regular": regular, "median_spacing_ms": None if spacing is None else round(spacing * 1000, 1),
            "per_bar": per_bar}


def _loop_record(source: AudioSource, active: tuple[int, int], sketch: dict[str, Any], env: list[float],
                 hop: int, grid: dict[str, Any] | None) -> dict[str, Any]:
    rate = source.sample_rate
    first = max(abs(channel[0]) for channel in source.channels)
    last = max(abs(channel[-1]) for channel in source.channels)
    edge_ok = first < 0.001 and last < 0.001
    begin_block, end_block = active[0] // hop, max(active[0] // hop + 1, math.ceil(active[1] / hop))
    per_slice = max(1, math.ceil(len(env) / sketch["slice_count"]))
    inner = [level for index, level in enumerate(sketch["slices_dbfs"])
             if level is not None and index * per_slice >= begin_block and (index + 1) * per_slice <= end_block]
    span = (max(inner) - min(inner)) if len(inner) >= 2 else None
    window = max(1, round(0.05 * rate))
    head = mean_channel_power(source.channels, active[0], min(active[1], active[0] + window))
    tail = mean_channel_power(source.channels, max(active[0], active[1] - window), active[1])
    match = None if head <= 0 or tail <= 0 else round(10 * math.log10(tail / head), 1)
    steady = span is not None and span <= 6
    bars = None if grid is None else round(source.frame_count / rate / grid["bar_seconds"], 3)
    whole_bars = bars is not None and bars >= 0.99 and abs(bars - round(bars)) <= 0.02
    loopable = steady and (edge_ok or (match is not None and abs(match) <= 3)) and (grid is None or whole_bars)
    return {"edge_start_dbfs": round(max(-120.0, db(first) or -120.0)),
            "edge_end_dbfs": round(max(-120.0, db(last) or -120.0)), "zero_crossing_edges": edge_ok,
            "steady_state": steady, "active_level_range_db": span, "head_tail_level_match_db": match,
            "bars": bars, "whole_bars": whole_bars, "loopable": loopable}


def _analyze(source: AudioSource, grid: dict[str, Any] | None, detail: str) -> dict[str, Any]:
    slices, long_segments, _, cap = DETAILS[detail]
    rate, frames = source.sample_rate, source.frame_count
    duration = frames / rate
    active = active_bounds(source.channels, 10 ** (SILENCE_DBFS / 20))
    if active is None:
        return {"silent": True}
    mono = mono_mix(source.channels, 0, frames)
    peak_value = peak(source.channels, 0, frames)
    power = mean_channel_power(source.channels, 0, frames)
    rms = math.sqrt(power)
    lufs, lufs_note = _loudness(source, detail)
    level: dict[str, Any] = {"peak_dbfs": round(db(peak_value) or 0.0, 1) if peak_value > 0 else None,
                             "rms_dbfs": None if rms <= 0 else round(20 * math.log10(rms), 1),
                             "crest_db": None if rms <= 0 else round(20 * math.log10(peak_value / rms), 1),
                             "integrated_lufs": lufs, "lufs_note": lufs_note,
                             "dc_offset": round(math.fsum(mono) / frames, 4), "over_full_scale": peak_value > 1}
    hop = max(1, round(HOP_SECONDS * rate))
    env = _envelope_db(source, hop)
    env_ms = [10 ** (value / 10) if value > -120 else 0.0 for value in env]
    found = _onsets(env, hop / rate)
    onsets = _onset_record(found, grid, duration)
    short = (active[1] - active[0]) / rate <= SHORT_SECONDS
    attack_ms = _attack_ms(source, mono, found[0][0]) if found and short else None
    decay = _decay(env, hop / rate)
    decay["short"] = short
    decay["attack_ms"] = attack_ms
    decay["release_end"] = round(active[1] / rate, 3)
    decay["tail_seconds"] = round(active[1] / rate - found[-1][0], 3) if found else None
    silence = {"head_seconds": round(active[0] / rate, 4), "tail_seconds": round((frames - active[1]) / rate, 4),
               "threshold_dbfs": SILENCE_DBFS}
    sketch = _sketch(env_ms, slices)
    sketch["slice_seconds"] = round(max(1, math.ceil(len(env_ms) / slices)) * hop / rate, 4)
    spans = _segment_spans(source, active, found, long_segments, grid)
    segments = [_spectrum(source, mono, name, begin, end, cap, grid) for name, begin, end in spans]
    by_name = {segment["name"]: segment for segment in segments}
    pitch = _pitch(source, active, found)
    body = by_name.get("body")
    reference = body if body is not None and body["flatness"] is not None else by_name["whole"]
    tonality = _tonality(reference["flatness"], pitch)
    width = stereo_width(source, 0, frames)
    stereo = {"channels": len(source.channels), "correlation": width["correlation"],
              "side_mid_db": width["side_mid_db"], "width": _width_label(width, len(source.channels))}
    loop = _loop_record(source, active, sketch, env, hop, grid)
    tags = _level_tags(level) + _spectral_tags(by_name["whole"], by_name.get("attack"), by_name.get("body"), attack_ms)
    tags += _shape_tags(onsets, decay, loop, stereo, tonality, silence)
    if "wide" in tags and "bright" in tags:
        tags.append("wide AND bright")
    return {"version": DESCRIBE_VERSION, "source": _source_record(source), "grid": grid, "detail": detail,
            "silent": False, "level": level, "envelope": sketch, "onsets": onsets, "decay": decay,
            "silence": silence, "spectral": {"legend": BAND_LEGEND, "segments": segments},
            "tonality": {"kind": tonality, "flatness": reference["flatness"], "pitch": pitch},
            "stereo": stereo, "loop": loop, "tags": tags, "method": METHOD}


# ---------------------------------------------------------------------------
# Text rendering


def _num(value: Any, digits: int = 1, unit: str = "") -> str:
    if value is None:
        return "n/a"
    return f"{value:.{digits}f}{unit}"


def _bands_line(segment: dict[str, Any]) -> str:
    rel = segment["bands_rel_db"]
    if not rel:
        return "silent"
    return " ".join(f"{name} {'n/a' if rel[name] is None else format(rel[name], '+.0f')}" for name, _, _ in BANDS)


def _onset_lines(data: dict[str, Any], listed: int) -> list[str]:
    onsets, decay = data["onsets"], data["decay"]
    if onsets["count"] == 0:
        return ["onsets: none detected (no 8 dB rise); treat as a swell or a steady bed"]
    shown = []
    for item in onsets["items"][:listed]:
        place = f"{item['time']:.3f}s"
        if "bar_beat" in item:
            place += f" ({item['bar_beat']})"
        shown.append(f"{place} +{item['rise_db']:.0f}dB")
    lines = [f"onsets: {onsets['count']}" + (f" ({onsets['per_bar']:.1f}/bar)" if onsets["per_bar"] else "")
             + (f", regular ~{onsets['median_spacing_ms']:.0f} ms apart" if onsets["regular"] else "")
             + f"; first {min(listed, onsets['count'])}: " + ", ".join(shown)]
    lines.append(f"attack {_num(decay['attack_ms'], 1, ' ms')} to peak; decay from peak: -20 dB in "
                 f"{_num(decay['to_minus_20_db_seconds'], 3, ' s')}, -40 dB in "
                 f"{_num(decay['to_minus_40_db_seconds'], 3, ' s')}; tail after last onset "
                 f"{_num(decay['tail_seconds'], 2, ' s')}")
    return lines


def _render(data: dict[str, Any]) -> str:
    source, level, grid = data["source"], data["level"], data["grid"]
    listed = DETAILS[data["detail"]][2]
    head = (f"{Path(source['path']).name} - {source['duration_seconds']:.3f} s"
            + (f" ({data['loop']['bars']:.2f} bars at {grid['bpm']:g} bpm)" if grid else "")
            + f", {source['sample_rate']} Hz {'mono' if source['channels'] == 1 else str(source['channels']) + ' ch'}"
            + f", {source['sample_format']}")
    lufs = f"{level['integrated_lufs']:.1f} LUFS" if level["integrated_lufs"] is not None else \
        f"LUFS n/a ({level['lufs_note']})" if level["lufs_note"] else "LUFS n/a (silent gate)"
    lines = [head, f"level: peak {_num(level['peak_dbfs'])} dBFS, rms {_num(level['rms_dbfs'])} dBFS, crest "
             f"{_num(level['crest_db'])} dB, {lufs}" + (", OVER FULL SCALE" if level["over_full_scale"] else "")]
    env = data["envelope"]
    lines.append(f"envelope ({env['slice_count']} x {env['slice_seconds'] * 1000:.0f} ms, loudest slice "
                 f"{_num(env['loudest_slice_dbfs'])} dBFS): [{env['sketch']}]")
    lines.append("  dBFS: " + " ".join("--" if value is None else str(value) for value in env["slices_dbfs"]))
    lines.extend(_onset_lines(data, listed))
    silence = data["silence"]
    lines.append(f"silence: head {silence['head_seconds'] * 1000:.0f} ms, tail {silence['tail_seconds']:.3f} s "
                 f"(threshold {silence['threshold_dbfs']:g} dBFS)")
    lines.append(f"spectral (band dB re loudest band; {BAND_LEGEND}):")
    for segment in data["spectral"]["segments"]:
        span = "" if segment["name"] == "whole" else f" {segment['start']['time']:.3f}-{segment['end']['time']:.3f}s"
        lines.append(f"  {segment['name']:<7}{span}: centroid {_num(segment['centroid_hz'], 0, ' Hz')}, tilt "
                     f"{_num(segment['tilt_db_per_octave'], 1, ' dB/oct')}, flat {_num(segment['flatness'], 2)}: "
                     f"{_bands_line(segment)}")
    tone, pitch = data["tonality"], data["tonality"]["pitch"]
    root = (f"root ~ {pitch['note']} {pitch['cents']:+d}c ({pitch['f0_hz']:.1f} Hz, conf {pitch['confidence']:.2f}, "
            f"{pitch['agreement']:.0%} of frames agree)" if pitch else "no stable pitch")
    lines.append(f"tonality: {tone['kind']} (flatness {_num(tone['flatness'], 2)}); {root}")
    stereo = data["stereo"]
    if stereo["channels"] == 1:
        lines.append("stereo: mono file")
    else:
        lines.append(f"stereo: {stereo['width']} (L/R correlation {_num(stereo['correlation'], 2)}, side/mid "
                     f"{_num(stereo['side_mid_db'])} dB)")
    loop = data["loop"]
    lines.append(f"loop: edges {_num(loop['edge_start_dbfs'], 0)}/{_num(loop['edge_end_dbfs'], 0)} dBFS "
                 f"({'zero-crossing ok' if loop['zero_crossing_edges'] else 'not at zero'}), "
                 f"{'steady-state' if loop['steady_state'] else 'not steady'} (active range "
                 f"{_num(loop['active_level_range_db'], 0, ' dB')}), head/tail level "
                 f"{_num(loop['head_tail_level_match_db'], 1, ' dB')}"
                 + (f", {loop['bars']:.2f} bars{' (whole)' if loop['whole_bars'] else ''}" if grid else "")
                 + (", loopable" if loop["loopable"] else ""))
    lines.append("tags: " + (", ".join(data["tags"]) if data["tags"] else "none"))
    return "\n".join(lines)


# ---------------------------------------------------------------------------
# Public API


def describe_audio(source: object, *, bpm: float | None = None, ppq: int | None = None,
                   start_bar: int | None = None, beats_per_bar: int = 4, detail: str = "normal") -> AudioDescription:
    """Describe a WAV path, ``(samples, sample_rate)``, AudioSource or analysis object for an agent.

    ``detail`` is ``"brief"`` (one spectral segment, 16 envelope slices), ``"normal"`` or ``"full"``.
    With ``bpm`` (and optionally ``ppq`` and the file's ``start_bar``) onsets are also placed as
    ``bar:beat`` and ticks. The returned ``text`` is the rendering to hand to a model; ``data`` holds
    the same numbers as JSON-safe values. Nothing here reads FL Studio; give it a render or a sample.
    """
    if isinstance(source, AudioDescription):
        return source
    if detail not in DETAILS:
        raise ValueError("detail must be 'brief', 'normal' or 'full'.")
    grid = _bar_grid(bpm, ppq, start_bar, beats_per_bar)
    audio = as_source(source)
    data = _analyze(audio, grid, detail)
    if data.get("silent"):
        return _silent_description(audio, grid)
    return AudioDescription(data, _render(data))


def _delta(a: Any, b: Any, digits: int = 1) -> float | None:
    if a is None or b is None:
        return None
    return round(float(b) - float(a), digits)


def _verdicts(deltas: dict[str, Any], bands: dict[str, float | None]) -> list[str]:
    notes: list[str] = []
    rms = deltas["rms_dbfs"] or 0.0
    if deltas["rms_dbfs"] is not None and abs(rms) >= 1.5:
        notes.append("b is louder" if rms > 0 else "b is quieter")
    octaves = deltas["centroid_octaves"]
    if octaves is not None and abs(octaves) >= 0.25:
        notes.append("b is brighter" if octaves > 0 else "b is darker")
    pres = bands.get("pres")
    if pres is not None and pres - rms >= 2:
        notes.append("b is harsher in 2-4 kHz")
    elif pres is not None and pres - rms <= -2:
        notes.append("b is smoother in 2-4 kHz")
    sub = bands.get("sub")
    if sub is not None and abs(sub - rms) >= 3:
        notes.append("b has more sub" if sub > rms else "b has less sub")
    ratio = deltas["decay_40_ratio"]
    if ratio is not None and (ratio >= 1.5 or ratio <= 1 / 1.5):
        notes.append("b has a longer tail" if ratio > 1 else "b has a shorter tail")
    correlation = deltas["correlation"]
    if correlation is not None and abs(correlation) >= 0.2:
        notes.append("b is narrower" if correlation > 0 else "b is wider")
    return notes


def compare_audio(a: object, b: object, **options: Any) -> AudioComparison:
    """Differences of ``b`` relative to ``a`` (b minus a) in the describe vocabulary.

    Inputs are anything ``describe_audio`` accepts, including finished descriptions; ``options``
    (``bpm``, ``detail``...) are forwarded when a description must be computed.
    """
    first, second = describe_audio(a, **options), describe_audio(b, **options)
    if first.data.get("silent") or second.data.get("silent"):
        raise ValueError("compare_audio needs two non-silent sources.")
    la, lb = first.data["level"], second.data["level"]
    wa, wb = first.data["spectral"]["segments"][0], second.data["spectral"]["segments"][0]
    da, db_ = first.data["decay"], second.data["decay"]
    sa, sb = first.data["stereo"], second.data["stereo"]
    centroid_octaves = None if not wa["centroid_hz"] or not wb["centroid_hz"] else \
        round(math.log2(wb["centroid_hz"] / wa["centroid_hz"]), 2)
    to40a, to40b = da["to_minus_40_db_seconds"], db_["to_minus_40_db_seconds"]
    deltas: dict[str, Any] = {
        "peak_dbfs": _delta(la["peak_dbfs"], lb["peak_dbfs"]), "rms_dbfs": _delta(la["rms_dbfs"], lb["rms_dbfs"]),
        "crest_db": _delta(la["crest_db"], lb["crest_db"]),
        "integrated_lufs": _delta(la["integrated_lufs"], lb["integrated_lufs"]),
        "duration_seconds": _delta(first.data["source"]["duration_seconds"], second.data["source"]["duration_seconds"], 3),
        "attack_ms": _delta(da["attack_ms"], db_["attack_ms"]),
        "decay_40_seconds": _delta(to40a, to40b, 3),
        "decay_40_ratio": None if not to40a or to40b is None else round(to40b / to40a, 2),
        "tail_seconds": _delta(da["tail_seconds"], db_["tail_seconds"], 2),
        "centroid_hz": _delta(wa["centroid_hz"], wb["centroid_hz"], 0), "centroid_octaves": centroid_octaves,
        "tilt_db_per_octave": _delta(wa["tilt_db_per_octave"], wb["tilt_db_per_octave"]),
        "flatness": _delta(wa["flatness"], wb["flatness"], 3),
        "correlation": _delta(sa["correlation"], sb["correlation"], 2),
        "side_mid_db": _delta(sa["side_mid_db"], sb["side_mid_db"])}
    bands = {name: _delta(wa["bands_dbfs"].get(name), wb["bands_dbfs"].get(name)) for name, _, _ in BANDS}
    balance = {name: _delta(wa["bands_rel_db"].get(name), wb["bands_rel_db"].get(name)) for name, _, _ in BANDS}
    measurable = {name: value for name, value in bands.items() if value is not None}
    added = [tag for tag in second.tags if tag not in first.tags]
    removed = [tag for tag in first.tags if tag not in second.tags]
    verdicts = _verdicts(deltas, bands)
    data = {"a": first.data["source"], "b": second.data["source"], "delta_sign": "b minus a", "deltas": deltas,
            "bands_dbfs_delta": bands, "bands_balance_delta": balance,
            "largest_rise": max(measurable, key=lambda name: measurable[name]) if measurable else None,
            "largest_drop": min(measurable, key=lambda name: measurable[name]) if measurable else None,
            "tags_added": added, "tags_removed": removed, "verdicts": verdicts}
    text = _render_comparison(first, second, data)
    return AudioComparison(data, text)


def _signed(value: float | None, digits: int = 1, unit: str = "") -> str:
    return "n/a" if value is None else f"{value:+.{digits}f}{unit}"


def _render_comparison(first: AudioDescription, second: AudioDescription, data: dict[str, Any]) -> str:
    deltas, bands = data["deltas"], data["bands_dbfs_delta"]
    wa, wb = first.data["spectral"]["segments"][0], second.data["spectral"]["segments"][0]
    sa, sb = first.data["stereo"], second.data["stereo"]
    lines = [f"compare (b minus a): a = {Path(data['a']['path']).name}, b = {Path(data['b']['path']).name}",
             f"level: peak {_signed(deltas['peak_dbfs'])} dB, rms {_signed(deltas['rms_dbfs'])} dB, crest "
             f"{_signed(deltas['crest_db'])} dB, loudness {_signed(deltas['integrated_lufs'])} LU",
             f"shape: duration {_signed(deltas['duration_seconds'], 3, ' s')}, attack {_signed(deltas['attack_ms'], 1, ' ms')}, "
             f"decay to -40 dB {_signed(deltas['decay_40_seconds'], 3, ' s')}"
             + (f" (x{deltas['decay_40_ratio']:.2f})" if deltas["decay_40_ratio"] is not None else "")
             + f", tail {_signed(deltas['tail_seconds'], 2, ' s')}",
             f"spectral: centroid {_num(wa['centroid_hz'], 0)} -> {_num(wb['centroid_hz'], 0)} Hz "
             f"({_signed(deltas['centroid_octaves'], 2, ' oct')}), tilt {_num(wa['tilt_db_per_octave'])} -> "
             f"{_num(wb['tilt_db_per_octave'])} dB/oct, flatness {_num(wa['flatness'], 2)} -> {_num(wb['flatness'], 2)}",
             "bands (absolute dB): " + " ".join(f"{name} {_signed(bands[name], 1)}" for name, _, _ in BANDS),
             f"  largest rise {data['largest_rise'] or 'n/a'}, largest drop {data['largest_drop'] or 'n/a'}; "
             f"balance re loudest band: " + " ".join(f"{name} {_signed(data['bands_balance_delta'][name], 1)}"
                                                    for name, _, _ in BANDS),
             f"stereo: {sa['width']} -> {sb['width']} (correlation {_num(sa['correlation'], 2)} -> "
             f"{_num(sb['correlation'], 2)}, side/mid {_num(sa['side_mid_db'])} -> {_num(sb['side_mid_db'])} dB)",
             f"tags: +[{', '.join(data['tags_added'])}] -[{', '.join(data['tags_removed'])}]",
             "verdict: " + ("; ".join(data["verdicts"]) if data["verdicts"] else "no salient difference")]
    return "\n".join(lines)


# ---------------------------------------------------------------------------
# Batch browsing with a cache


def default_cache_dir() -> Path:
    """``%LocalAppData%\\FlMcp\\Projects\\results\\describe-cache`` on Windows, ``~/.cache/fruitylink`` elsewhere."""
    base = os.environ.get("LOCALAPPDATA")
    if base:
        return Path(base) / "FlMcp" / "Projects" / "results" / "describe-cache"
    return Path.home() / ".cache" / "fruitylink" / "describe-cache"


def _cache_key(path: Path, options: dict[str, Any]) -> str:
    stat = path.stat()
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1 << 20), b""):
            digest.update(chunk)
    digest.update(json.dumps({"version": DESCRIBE_VERSION, "size": stat.st_size, "mtime_ns": stat.st_mtime_ns,
                              **options}, sort_keys=True).encode())
    return digest.hexdigest()[:40]


def _cached(cache_dir: Path | None, path: Path, options: dict[str, Any]) -> tuple[AudioDescription | None, Path | None]:
    if cache_dir is None:
        return None, None
    entry = cache_dir / f"{_cache_key(path, options)}.json"
    if entry.is_file():
        try:
            record = json.loads(entry.read_text(encoding="utf-8"))
            return AudioDescription(record["data"], record["text"]), entry
        except (OSError, ValueError, KeyError, TypeError):
            return None, entry
    return None, entry


def _row(name: str, description: AudioDescription, cached: bool) -> dict[str, Any]:
    data = description.data
    if data.get("silent"):
        return {"name": name, "path": data["source"]["path"], "cached": cached, "error": None,
                "duration_seconds": data["source"]["duration_seconds"], "peak_dbfs": None, "rms_dbfs": None,
                "integrated_lufs": None, "root": None, "width": None, "tags": ["silent"]}
    pitch = data["tonality"]["pitch"]
    return {"name": name, "path": data["source"]["path"], "cached": cached, "error": None,
            "duration_seconds": data["source"]["duration_seconds"], "peak_dbfs": data["level"]["peak_dbfs"],
            "rms_dbfs": data["level"]["rms_dbfs"], "integrated_lufs": data["level"]["integrated_lufs"],
            "root": None if pitch is None else pitch["note"], "width": data["stereo"]["width"], "tags": data["tags"]}


def _table(rows: Sequence[dict[str, Any]]) -> str:
    width = min(48, max([len("file")] + [len(row["name"]) for row in rows]))
    lines = [f"{'file':<{width}}  {'dur':>7}  {'peak':>6}  {'rms':>6}  {'root':<5} {'width':<9} tags"]
    for row in rows:
        name = row["name"] if len(row["name"]) <= width else row["name"][:width - 3] + "..."
        if row["error"]:
            lines.append(f"{name:<{width}}  error: {row['error']}")
            continue
        lines.append(f"{name:<{width}}  {row['duration_seconds']:>6.2f}s  {_num(row['peak_dbfs']):>6}  "
                     f"{_num(row['rms_dbfs']):>6}  {(row['root'] or '-'):<5} {(row['width'] or '-'):<9} "
                     + ", ".join(row["tags"]))
    return "\n".join(lines)


def describe_samples(paths: Iterable[str | Path], *, cache_dir: str | Path | None = None, detail: str = "brief",
                     bpm: float | None = None, beats_per_bar: int = 4) -> SampleTable:
    """Describe many WAV files with a content-hash cache; one table line per file, errors inline.

    ``cache_dir`` defaults to ``default_cache_dir()``; pass ``False``-like ``""`` to disable caching.
    Entries are keyed by file hash, size, mtime and the describe options, so edited files re-analyze.
    """
    if detail not in DETAILS:
        raise ValueError("detail must be 'brief', 'normal' or 'full'.")
    directory: Path | None = default_cache_dir() if cache_dir is None else (Path(cache_dir) if cache_dir else None)
    if directory is not None:
        directory.mkdir(parents=True, exist_ok=True)
    options = {"detail": detail, "bpm": bpm, "beats_per_bar": beats_per_bar}
    rows: list[dict[str, Any]] = []
    descriptions: dict[str, AudioDescription] = {}
    for item in paths:
        path = Path(item).expanduser()
        name = path.name
        try:
            if path.suffix.lower() != ".wav":
                raise ValueError("unsupported format; describe reads RIFF WAV only")
            description, entry = _cached(directory, path, options)
            cached = description is not None
            if description is None:
                description = describe_audio(path, bpm=bpm, beats_per_bar=beats_per_bar, detail=detail)
                if entry is not None:
                    entry.write_text(json.dumps({"data": description.data, "text": description.text}), encoding="utf-8")
        except (OSError, ValueError, TypeError) as error:
            rows.append({"name": name, "path": str(path), "cached": False, "error": str(error), "tags": []})
            continue
        descriptions[str(path)] = description
        rows.append(_row(name, description, cached))
    return SampleTable(rows, descriptions, _table(rows))


__all__ = ["AudioComparison", "AudioDescription", "SampleTable", "as_source", "compare_audio", "default_cache_dir",
           "describe_audio", "describe_samples"]
