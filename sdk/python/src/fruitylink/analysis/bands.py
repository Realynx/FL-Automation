"""Time-domain band levels for mix checks: band energy, revision deltas and masking.

Each band is a ``(low_hz, high_hz)`` pair; ``None`` means no edge on that side.
Levels are RMS in dBFS (amplitude reference 1) of the channel-mean mono mix
after a fourth-order Butterworth high-pass at ``low_hz`` and/or low-pass at
``high_hz`` (two cascaded RBJ biquads per edge, 24 dB/octave). Filters run
forward only, so a band's level includes its skirts; deltas between two
renders measured with the same bands are what these helpers are for.
See docs/python-audio-analysis.md for the recipe and limitations.
"""

from __future__ import annotations

import math
from array import array
from collections.abc import Sequence
from dataclasses import dataclass
from pathlib import Path

from ._kernels import mean_channel_power, mono_mix, peak, sum_squares
from .audio import AudioSource, finite, load_wav
from .loudness import db

Band = tuple[float | None, float | None]
DEFAULT_BANDS: tuple[Band, ...] = ((None, 90.0), (90.0, 250.0), (250.0, 2000.0), (2000.0, 4000.0), (4000.0, None))
MAX_BANDS = 16
MAX_FILTER_WORK = 300_000_000
PREROLL_SECONDS = 0.5
_BUTTERWORTH_Q = (0.5411961001461970, 1.3065629648763766)
BAND_METHOD = ("channel-mean mono mix; 4th-order Butterworth (2 RBJ biquads per edge, 24 dB/oct), forward "
               "only, 0.5 s pre-roll; RMS dBFS re amplitude 1")


@dataclass(frozen=True)
class BandSpec:
    low_hz: float | None
    high_hz: float | None
    label: str


class Biquad:
    __slots__ = ("a1", "a2", "b0", "b1", "b2", "x1", "x2", "y1", "y2")

    def __init__(self, b0: float, b1: float, b2: float, a1: float, a2: float) -> None:
        self.b0, self.b1, self.b2, self.a1, self.a2 = b0, b1, b2, a1, a2
        self.reset()

    def reset(self) -> None:
        self.x1 = self.x2 = self.y1 = self.y2 = 0.0

    def process(self, samples: Sequence[float]) -> array[float]:
        b0, b1, b2, a1, a2 = self.b0, self.b1, self.b2, self.a1, self.a2
        x1, x2, y1, y2 = self.x1, self.x2, self.y1, self.y2
        result = array("d", bytes(8 * len(samples)))
        index = 0
        for x in samples:
            y = b0 * x + b1 * x1 + b2 * x2 - a1 * y1 - a2 * y2
            result[index] = y
            index += 1
            x2, x1, y2, y1 = x1, x, y1, y
        self.x1, self.x2, self.y1, self.y2 = x1, x2, y1, y2
        return result


def _rbj(cutoff_hz: float, sample_rate: int, q: float, highpass: bool) -> Biquad:
    w0 = 2 * math.pi * cutoff_hz / sample_rate
    alpha = math.sin(w0) / (2 * q)
    cos_w0 = math.cos(w0)
    a0 = 1 + alpha
    if highpass:
        b0, b1 = (1 + cos_w0) / 2, -(1 + cos_w0)
    else:
        b0, b1 = (1 - cos_w0) / 2, 1 - cos_w0
    return Biquad(b0 / a0, b1 / a0, b0 / a0, -2 * cos_w0 / a0, (1 - alpha) / a0)


class BandFilter:
    """Cascaded Butterworth sections for one band; state persists across process calls."""

    def __init__(self, spec: BandSpec, sample_rate: int) -> None:
        self.sections: list[Biquad] = []
        for cutoff, highpass in ((spec.low_hz, True), (spec.high_hz, False)):
            if cutoff is not None:
                self.sections.extend(_rbj(cutoff, sample_rate, q, highpass) for q in _BUTTERWORTH_Q)

    @property
    def passes(self) -> int:
        return len(self.sections)

    def reset(self) -> None:
        for section in self.sections:
            section.reset()

    def process(self, samples: Sequence[float]) -> Sequence[float]:
        for section in self.sections:
            samples = section.process(samples)
        return samples


def band_label(low_hz: float | None, high_hz: float | None) -> str:
    if low_hz is None and high_hz is None:
        return "full"
    if low_hz is None:
        return f"<{high_hz:g}"
    if high_hz is None:
        return f">{low_hz:g}"
    return f"{low_hz:g}-{high_hz:g}"


def band_specs(bands: Sequence[Band], sample_rate: int) -> list[BandSpec]:
    """Validate ``(low_hz, high_hz)`` pairs; edges must be positive and below Nyquist."""
    if not isinstance(bands, Sequence) or isinstance(bands, str) or not 1 <= len(bands) <= MAX_BANDS:
        raise ValueError(f"bands must be a sequence of 1..{MAX_BANDS} (low_hz, high_hz) pairs.")
    nyquist = sample_rate / 2
    specs: list[BandSpec] = []
    for band in bands:
        if not isinstance(band, Sequence) or isinstance(band, str) or len(band) != 2:
            raise ValueError("Each band must be a (low_hz, high_hz) pair; use None for an open edge.")
        low = None if band[0] is None else finite(band[0], "Band low_hz", positive=True)
        high = None if band[1] is None else finite(band[1], "Band high_hz", positive=True)
        if (low is not None and low >= nyquist) or (high is not None and high >= nyquist):
            raise ValueError(f"Band edges must be below the Nyquist frequency ({nyquist:g} Hz).")
        if low is not None and high is not None and low >= high:
            raise ValueError("Band low_hz must be below high_hz.")
        specs.append(BandSpec(low, high, band_label(low, high)))
    if len({spec.label for spec in specs}) != len(specs):
        raise ValueError("Bands must be distinct.")
    return specs


def span_frames(source: AudioSource, start_seconds: float | None, end_seconds: float | None) -> tuple[int, int]:
    """Absolute seconds inside the selected source, converted to source-relative frames."""
    rate = source.sample_rate
    start = source.start_seconds if start_seconds is None else finite(start_seconds, "start_seconds")
    end = source.end_seconds if end_seconds is None else finite(end_seconds, "end_seconds")
    if start < source.start_seconds - 1e-9 or end > source.end_seconds + 1e-9 or end <= start:
        raise ValueError(f"The time window must be nonempty and inside the selected audio "
                         f"({source.start_seconds:g}..{source.end_seconds:g} s).")
    begin = max(0, round(start * rate) - source.origin_frame)
    stop = min(source.frame_count, round(end * rate) - source.origin_frame)
    if stop <= begin:
        raise ValueError("The time window must span at least one frame.")
    return begin, stop


def coerce_source(value: object, start_seconds: float | None, end_seconds: float | None) -> AudioSource:
    """Accept a path, an AudioSource or an analysis object; paths load only the requested window."""
    if isinstance(value, (str, Path)):
        return load_wav(value, start_seconds=start_seconds or 0, end_seconds=end_seconds)
    if isinstance(value, AudioSource):
        return value
    inner = getattr(value, "source", None)
    if isinstance(inner, AudioSource):
        return inner
    raise TypeError("Expected a WAV path, an AudioSource, or an AudioAnalysis object.")


def check_filter_work(frames: int, specs: Sequence[BandSpec]) -> None:
    passes = sum((spec.low_hz is not None) * 2 + (spec.high_hz is not None) * 2 for spec in specs)
    if frames * passes > MAX_FILTER_WORK:
        raise ValueError(f"Band filtering work exceeds {MAX_FILTER_WORK:,} filter samples; select a shorter "
                         "time range or fewer bands. No audio was skipped.")


def band_mean_squares(source: AudioSource, specs: Sequence[BandSpec],
                      spans: Sequence[tuple[int, int]]) -> list[dict[str, float]]:
    """Per span, the mean square of the filtered mono mix for every band.

    Spans are source-relative frame ranges processed in order. Filter state
    continues across adjacent spans; a gap resets the filter and runs a pre-roll
    of up to 0.5 s of preceding audio so that short windows are not dominated
    by filter start-up transients.
    """
    if any(end <= begin or begin < 0 or end > source.frame_count for begin, end in spans):
        raise ValueError("Spans must be nonempty and inside the selected audio.")
    check_filter_work(sum(end - begin for begin, end in spans), specs)
    first = min((begin for begin, _ in spans), default=0)
    last = max((end for _, end in spans), default=0)
    preroll = round(PREROLL_SECONDS * source.sample_rate)
    base = max(0, first - preroll)
    mono = mono_mix(source.channels, base, last)
    results: list[dict[str, float]] = [{} for _ in spans]
    for spec in specs:
        band_filter = BandFilter(spec, source.sample_rate)
        previous_end = -1
        for index, (begin, end) in enumerate(spans):
            if band_filter.passes and begin != previous_end:
                band_filter.reset()
                band_filter.process(mono[max(base, begin - preroll) - base:begin - base])
            filtered = band_filter.process(mono[begin - base:end - base])
            results[index][spec.label] = sum_squares(filtered) / (end - begin)
            previous_end = end
    return results


def round_db(value: float | None) -> float | None:
    return None if value is None else round(value, 2)


def level_db(mean_square: float) -> float | None:
    return round_db(db(mean_square, 10))


def band_definitions(specs: Sequence[BandSpec]) -> list[dict[str, object]]:
    return [{"label": spec.label, "low_hz": spec.low_hz, "high_hz": spec.high_hz} for spec in specs]


def _window_levels(source: AudioSource, specs: Sequence[BandSpec], begin: int,
                   end: int) -> tuple[dict[str, float | None], float | None, float | None]:
    squares = band_mean_squares(source, specs, [(begin, end)])[0]
    bands = {spec.label: level_db(squares[spec.label]) for spec in specs}
    full = level_db(mean_channel_power(source.channels, begin, end))
    return bands, full, round_db(db(peak(source.channels, begin, end)))


def _window_record(source: AudioSource, begin: int, end: int) -> dict[str, object]:
    rate = source.sample_rate
    return {"source": source.source, "source_kind": source.source_kind,
            "start_seconds": (source.origin_frame + begin) / rate,
            "end_seconds": (source.origin_frame + end) / rate}


def band_energy(source: AudioSource, bands: Sequence[Band] = DEFAULT_BANDS, *,
                start_seconds: float | None = None, end_seconds: float | None = None) -> dict[str, object]:
    """RMS level in dBFS per band over an absolute time window of the selected audio.

    ``full_db`` averages channel powers (anti-phase stereo does not cancel);
    band levels use the mono mix. Silence yields ``None`` rather than -inf.
    """
    specs = band_specs(bands, source.sample_rate)
    begin, end = span_frames(source, start_seconds, end_seconds)
    levels, full, peak_db = _window_levels(source, specs, begin, end)
    return {**_window_record(source, begin, end), "provenance": source.provenance(),
            "full_db": full, "peak_db": peak_db, "bands": levels,
            "band_definitions": band_definitions(specs), "method": BAND_METHOD}


def _delta(a: float | None, b: float | None) -> float | None:
    return None if a is None or b is None else round(b - a, 2)


def compare_bands(a: object, b: object, bands: Sequence[Band] = DEFAULT_BANDS, *,
                  start_seconds: float | None = None, end_seconds: float | None = None) -> dict[str, object]:
    """Band level deltas (b minus a, dB) between two renders over the same time window.

    ``a`` and ``b`` are WAV paths, AudioSource objects or analysis objects. Paths
    load only the requested window. Use this to confirm that a new voice did not
    add presence or air, for example with ``bands=((2000, 4000), (5000, None))``.
    """
    source_a = coerce_source(a, start_seconds, end_seconds)
    source_b = coerce_source(b, start_seconds, end_seconds)
    if source_a.sample_rate != source_b.sample_rate:
        raise ValueError("Both sources must share one sample rate.")
    specs = band_specs(bands, source_a.sample_rate)
    levels_a, full_a, _ = _window_levels(source_a, specs, *span_frames(source_a, start_seconds, end_seconds))
    levels_b, full_b, _ = _window_levels(source_b, specs, *span_frames(source_b, start_seconds, end_seconds))
    rows = {spec.label: {"a_db": levels_a[spec.label], "b_db": levels_b[spec.label],
                         "delta_db": _delta(levels_a[spec.label], levels_b[spec.label])} for spec in specs}
    deltas = {label: row["delta_db"] for label, row in rows.items() if row["delta_db"] is not None}
    extremes: dict[str, object] = {"largest_increase": None, "largest_decrease": None}
    if deltas:
        up = max(deltas, key=lambda label: deltas[label])
        down = min(deltas, key=lambda label: deltas[label])
        extremes = {"largest_increase": {"band": up, "delta_db": deltas[up]},
                    "largest_decrease": {"band": down, "delta_db": deltas[down]}}
    return {"a": _window_record(source_a, *span_frames(source_a, start_seconds, end_seconds)),
            "b": _window_record(source_b, *span_frames(source_b, start_seconds, end_seconds)),
            "full": {"a_db": full_a, "b_db": full_b, "delta_db": _delta(full_a, full_b)},
            "bands": rows, **extremes, "band_definitions": band_definitions(specs),
            "delta_sign": "b minus a", "method": BAND_METHOD}


def masking_report(a: object, b: object, bands: Sequence[Band] = DEFAULT_BANDS, *,
                   start_seconds: float | None = None, end_seconds: float | None = None,
                   clash_threshold_db: float = 6.0, floor_db: float = -60.0) -> dict[str, object]:
    """Per band, each source's level and how close the quieter one sits under the louder.

    ``overlap_db`` is the quieter level minus the louder (0 means equal energy,
    the strongest competition; more negative means more separation). A band is
    listed in ``clashes`` when both sources exceed ``floor_db`` and their
    separation is within ``clash_threshold_db``. Both inputs should be isolated
    renders of the two instruments over the same time window.
    """
    threshold = finite(clash_threshold_db, "clash_threshold_db")
    floor = finite(floor_db, "floor_db")
    if threshold < 0:
        raise ValueError("clash_threshold_db must be nonnegative.")
    source_a = coerce_source(a, start_seconds, end_seconds)
    source_b = coerce_source(b, start_seconds, end_seconds)
    if source_a.sample_rate != source_b.sample_rate:
        raise ValueError("Both sources must share one sample rate.")
    specs = band_specs(bands, source_a.sample_rate)
    levels_a, _, _ = _window_levels(source_a, specs, *span_frames(source_a, start_seconds, end_seconds))
    levels_b, _, _ = _window_levels(source_b, specs, *span_frames(source_b, start_seconds, end_seconds))
    rows: dict[str, object] = {}
    clashes: list[str] = []
    for spec in specs:
        level_a, level_b = levels_a[spec.label], levels_b[spec.label]
        louder = overlap = None
        if level_a is not None and level_b is not None:
            louder = "a" if level_a >= level_b else "b"
            overlap = round(min(level_a, level_b) - max(level_a, level_b), 2)
            if min(level_a, level_b) > floor and -overlap <= threshold:
                clashes.append(spec.label)
        rows[spec.label] = {"a_db": level_a, "b_db": level_b, "louder": louder, "overlap_db": overlap}
    return {"a": _window_record(source_a, *span_frames(source_a, start_seconds, end_seconds)),
            "b": _window_record(source_b, *span_frames(source_b, start_seconds, end_seconds)),
            "bands": rows, "clashes": clashes, "clash_threshold_db": threshold, "floor_db": floor,
            "band_definitions": band_definitions(specs),
            "overlap_definition": "quieter level minus louder level in dB; 0 is full overlap",
            "method": BAND_METHOD}
