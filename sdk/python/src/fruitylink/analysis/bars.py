"""Bar-grid mix checks: per-bar anomaly scan, named section descriptions, A/B transitions.

The bar grid is explicit and constant: bar ``start_bar`` begins at file time
``grid_offset_seconds`` and every bar lasts ``beats_per_bar * 60 / bpm``
seconds. Tempo automation is not inferred. Times are absolute file seconds.
"""

from __future__ import annotations

import math
from array import array
from collections.abc import Mapping, Sequence
from dataclasses import dataclass
from statistics import median

from ._kernels import dot, mean_channel_power, mono_mix, peak, sum_squares
from .audio import AudioSource, finite
from .bands import (
    BAND_METHOD,
    DEFAULT_BANDS,
    Band,
    BandSpec,
    band_definitions,
    band_mean_squares,
    band_specs,
    coerce_source,
    level_db,
    round_db,
)
from .loudness import Loudness, db
from .spectral import spectral

SCAN_BANDS: tuple[Band, ...] = ((None, 90.0), (4000.0, None))
MAX_SCAN_BANDS = 8
MAX_SCAN_BARS = 4096
MAX_SCAN_PAGE = 256
MAX_SECTIONS = 32
CENTROID_FFT = 2048
CENTROID_SEGMENTS = 16


@dataclass(frozen=True)
class BarGrid:
    bpm: float
    beats_per_bar: int
    start_bar: int
    offset_seconds: float

    @property
    def bar_seconds(self) -> float:
        return self.beats_per_bar * 60 / self.bpm

    def seconds(self, bar: int) -> float:
        return self.offset_seconds + (bar - self.start_bar) * self.bar_seconds

    def frame(self, source: AudioSource, bar: int) -> int:
        """Source-relative frame of the bar line, clamped to the selected audio."""
        absolute = round(self.seconds(bar) * source.sample_rate) - source.origin_frame
        return min(max(absolute, 0), source.frame_count)

    def record(self) -> dict[str, object]:
        return {"bpm": self.bpm, "beats_per_bar": self.beats_per_bar, "start_bar": self.start_bar,
                "grid_offset_seconds": self.offset_seconds, "bar_seconds": self.bar_seconds,
                "tempo": "constant; tempo automation is not inferred"}


def _integer(value: object, name: str, low: int, high: int) -> int:
    if type(value) is not int or not low <= value <= high:
        raise ValueError(f"{name} must be an integer in {low}..{high}.")
    return value


def bar_grid(bpm: float, beats_per_bar: int, start_bar: int, grid_offset_seconds: float) -> BarGrid:
    tempo = finite(bpm, "bpm", positive=True)
    if tempo > 1000:
        raise ValueError("bpm must not exceed 1000.")
    offset = finite(grid_offset_seconds, "grid_offset_seconds")
    if offset < 0:
        raise ValueError("grid_offset_seconds must be nonnegative.")
    return BarGrid(tempo, _integer(beats_per_bar, "beats_per_bar", 1, 64),
                   _integer(start_bar, "start_bar", -1_000_000, 1_000_000), offset)


def _page_bounds(total: int, offset: int, limit: int, max_limit: int) -> tuple[int, int]:
    _integer(offset, "offset", 0, 2**53 - 1)
    _integer(limit, "limit", 1, max_limit)
    return min(offset, total), min(offset + limit, total)


@dataclass(frozen=True)
class _Scan:
    grid: BarGrid
    specs: tuple[BandSpec, ...]
    step_db: float
    rows: list[dict[str, object]]
    anomalies: list[dict[str, object]]
    summary: dict[str, object]


def _bar_spans(source: AudioSource, grid: BarGrid) -> list[tuple[int, int, int, bool]]:
    """(bar, begin, end, partial) for every grid bar overlapping the selected audio."""
    rate = source.sample_rate
    first_bar = grid.start_bar + max(0, math.floor((source.start_seconds - grid.offset_seconds) / grid.bar_seconds))
    spans: list[tuple[int, int, int, bool]] = []
    bar = first_bar
    while len(spans) <= MAX_SCAN_BARS:
        begin_frame = round(grid.seconds(bar) * rate) - source.origin_frame
        end_frame = round(grid.seconds(bar + 1) * rate) - source.origin_frame
        if begin_frame >= source.frame_count:
            break
        begin, end = max(0, begin_frame), min(source.frame_count, end_frame)
        if end > begin:
            spans.append((bar, begin, end, begin != begin_frame or end != end_frame))
        bar += 1
    if len(spans) > MAX_SCAN_BARS:
        raise ValueError(f"A scan covers at most {MAX_SCAN_BARS} bars; select a shorter time range.")
    if not spans:
        raise ValueError("No bar of the grid overlaps the selected audio; check grid_offset_seconds and start_bar.")
    return spans


def _step(previous: float | None, current: float | None, step_db: float) -> tuple[bool, float | None]:
    """Whether the move from the previous bar is an anomaly, and the delta when both are measurable."""
    if previous is None or current is None:
        return previous is not current, None
    delta = round(current - previous, 2)
    return abs(delta) > step_db, delta


def _scan(source: AudioSource, grid: BarGrid, specs: Sequence[BandSpec], step_db: float) -> _Scan:
    spans = _bar_spans(source, grid)
    squares = band_mean_squares(source, specs, [(begin, end) for _, begin, end, _ in spans])
    rows: list[dict[str, object]] = []
    anomalies: list[dict[str, object]] = []
    flagged: set[int] = set()
    fulls: list[tuple[float, int]] = []
    previous: dict[str, float | None] = {}
    rate = source.sample_rate
    for (bar, begin, end, partial), band_squares in zip(spans, squares):
        levels: dict[str, float | None] = {"full": level_db(mean_channel_power(source.channels, begin, end))}
        levels.update({spec.label: level_db(band_squares[spec.label]) for spec in specs})
        flags: list[str] = []
        for measure, level in levels.items() if previous else ():
            anomaly, delta = _step(previous[measure], level, step_db)
            if not anomaly:
                continue
            flags.append(f"{measure} {delta:+.2f} dB" if delta is not None else
                         f"{measure} {'became silent' if level is None else 'from silent'}")
            flagged.add(bar)
            anomalies.append({"bar": bar, "start_seconds": (source.origin_frame + begin) / rate,
                              "measure": measure, "previous_db": previous[measure], "db": level,
                              "delta_db": delta})
        full = levels["full"]
        if full is not None:
            fulls.append((full, bar))
        rows.append({"bar": bar, "start_seconds": (source.origin_frame + begin) / rate,
                     "end_seconds": (source.origin_frame + end) / rate, "partial": partial,
                     "full_db": full, "bands": {spec.label: levels[spec.label] for spec in specs},
                     "flags": flags})
        previous = levels
    summary: dict[str, object] = {
        "bar_count": len(rows), "first_bar": spans[0][0], "last_bar": spans[-1][0],
        "anomaly_count": len(anomalies), "flagged_bars": sorted(flagged),
        "loudest_bar": max(fulls, key=lambda pair: (pair[0], -pair[1]))[1] if fulls else None,
        "quietest_bar": min(fulls)[1] if fulls else None,
        "median_full_db": round(median(level for level, _ in fulls), 2) if fulls else None,
        "silent_bars": len(rows) - len(fulls)}
    return _Scan(grid, tuple(specs), step_db, rows, anomalies, summary)


def _scan_page(source: AudioSource, scan: _Scan, offset: int, limit: int) -> dict[str, object]:
    first, last = _page_bounds(len(scan.rows), offset, limit, MAX_SCAN_PAGE)
    return {"provenance": source.provenance(), "grid": scan.grid.record(), "step_db": scan.step_db,
            "band_definitions": band_definitions(scan.specs), "summary": scan.summary,
            "anomalies": scan.anomalies, "offset": offset, "total": len(scan.rows),
            "next_offset": last if last < len(scan.rows) else None, "bars": scan.rows[first:last],
            "flag_rule": "any measure moving more than step_db from the previous bar, or to/from silence",
            "full_db_method": "mean of channel powers, RMS dBFS re amplitude 1", "band_method": BAND_METHOD}


def scan_settings(source: AudioSource, bpm: float, beats_per_bar: int, bands: Sequence[Band], step_db: float,
                  start_bar: int, grid_offset_seconds: float) -> tuple[BarGrid, tuple[BandSpec, ...], float]:
    grid = bar_grid(bpm, beats_per_bar, start_bar, grid_offset_seconds)
    specs = band_specs(bands, source.sample_rate)
    if len(specs) > MAX_SCAN_BANDS:
        raise ValueError(f"scan_bars accepts at most {MAX_SCAN_BANDS} bands.")
    step = finite(step_db, "step_db", positive=True)
    return grid, tuple(specs), step


def scan_bars(source: AudioSource, bpm: float, *, beats_per_bar: int = 4, bands: Sequence[Band] = SCAN_BANDS,
              step_db: float = 6.0, start_bar: int = 1, grid_offset_seconds: float = 0.0, offset: int = 0,
              limit: int = 128) -> dict[str, object]:
    """Per-bar RMS of the full signal and each band, flagging steps larger than ``step_db``.

    ``anomalies`` and ``summary`` cover every scanned bar; ``bars`` is paged.
    A bar cut by the selected range is marked ``partial``. Run this on a mix
    before mastering: level jumps, missing sub, and harsh bars show up as flags.
    """
    grid, specs, step = scan_settings(source, bpm, beats_per_bar, bands, step_db, start_bar, grid_offset_seconds)
    return _scan_page(source, _scan(source, grid, specs, step), offset, limit)


def _mono_source(source: AudioSource, mono: array[float], begin: int, end: int) -> AudioSource:
    return AudioSource(source.sample_rate, (array("d", mono[begin:end]),), source.source, source.source_kind,
                       source.origin_frame + begin, source.requested_start_seconds,
                       source.requested_end_seconds, source.total_source_frames, source.sample_format)


def centroid_hz(source: AudioSource, mono: array[float], begin: int, end: int) -> float | None:
    """Power-weighted spectral centroid of the mono mix; long spans are sampled.

    Spans up to 16 FFT frames use the full averaged periodogram. Longer spans
    use 16 evenly spaced 2048-frame Hann segments, combined by their power, so
    the value is a sampled estimate rather than a whole-span average.
    """
    frames = end - begin
    if frames <= CENTROID_SEGMENTS * CENTROID_FFT:
        value = spectral(_mono_source(source, mono, begin, end), fft_size=CENTROID_FFT, dominant_bins=0)
        centroid = value["centroid_hz"]
        return round(centroid, 1) if isinstance(centroid, float) else None
    step = (frames - CENTROID_FFT) / (CENTROID_SEGMENTS - 1)
    weighted = total = 0.0
    for index in range(CENTROID_SEGMENTS):
        start = begin + round(index * step)
        value = spectral(_mono_source(source, mono, start, start + CENTROID_FFT), fft_size=CENTROID_FFT,
                         dominant_bins=0)
        power, centroid = value["windowed_mean_square"], value["centroid_hz"]
        if isinstance(power, float) and isinstance(centroid, float):
            weighted += power * centroid
            total += power
    return round(weighted / total, 1) if total > 0 else None


def stereo_width(source: AudioSource, begin: int, end: int) -> dict[str, object]:
    """L/R correlation and side/mid power ratio of the first two channels."""
    if len(source.channels) != 2:
        return {"correlation": None, "side_mid_db": None, "reason": "requires exactly two channels"}
    left, right = source.channels[0][begin:end], source.channels[1][begin:end]
    ll, rr, lr = sum_squares(left), sum_squares(right), dot(left, right)
    mid, side = (ll + rr + 2 * lr) / 4, (ll + rr - 2 * lr) / 4
    correlation = lr / math.sqrt(ll * rr) if ll > 0 and rr > 0 else None
    return {"correlation": None if correlation is None else round(max(-1.0, min(1.0, correlation)), 3),
            "side_mid_db": round_db(db(side / mid, 10)) if mid > 0 else None,
            "definition": "zero-lag L/R correlation; side/mid = 10*log10(sum((L-R)^2) / sum((L+R)^2))"}


def _sections(source: AudioSource, grid: BarGrid,
              sections: Mapping[str, Sequence[int]]) -> list[tuple[str, int, int, int, int, bool]]:
    if not isinstance(sections, Mapping) or not 1 <= len(sections) <= MAX_SECTIONS:
        raise ValueError(f"sections must map 1..{MAX_SECTIONS} names to (start_bar, end_bar) pairs.")
    result = []
    for name, bars in sections.items():
        if not isinstance(name, str) or not isinstance(bars, Sequence) or len(bars) != 2:
            raise ValueError("Each section is a name mapped to an inclusive (start_bar, end_bar) pair.")
        start, end = (_integer(bar, f"section {name!r} bar", -1_000_000, 1_000_000) for bar in bars)
        if end < start:
            raise ValueError(f"Section {name!r} ends before it starts.")
        begin, stop = grid.frame(source, start), grid.frame(source, end + 1)
        if stop <= begin:
            raise ValueError(f"Section {name!r} (bars {start}-{end}) lies outside the selected audio.")
        exact = (round(grid.seconds(start) * source.sample_rate), round(grid.seconds(end + 1) * source.sample_rate))
        partial = exact != (source.origin_frame + begin, source.origin_frame + stop)
        result.append((name, start, end, begin, stop, partial))
    return result


def describe_sections(source: AudioSource, bpm: float, sections: Mapping[str, Sequence[int]], *,
                      beats_per_bar: int = 4, start_bar: int = 1, grid_offset_seconds: float = 0.0,
                      bands: Sequence[Band] = DEFAULT_BANDS, loudness: bool = True) -> dict[str, object]:
    """Level, crest, centroid, stereo width, band levels and integrated LUFS per named section.

    Sections map names to inclusive ``(start_bar, end_bar)`` pairs. Integrated
    loudness reuses the BS.1770 gate with K-weighting run from the selected
    start; it is unavailable for more than two channels.
    """
    meter = None
    if loudness and len(source.channels) <= 2:
        meter = Loudness(source)
    return describe_sections_with(source, bpm, sections, beats_per_bar=beats_per_bar, start_bar=start_bar,
                                  grid_offset_seconds=grid_offset_seconds, bands=bands, meter=meter,
                                  loudness=loudness)


def describe_sections_with(source: AudioSource, bpm: float, sections: Mapping[str, Sequence[int]], *,
                           beats_per_bar: int, start_bar: int, grid_offset_seconds: float,
                           bands: Sequence[Band], meter: Loudness | None, loudness: bool) -> dict[str, object]:
    grid = bar_grid(bpm, beats_per_bar, start_bar, grid_offset_seconds)
    specs = band_specs(bands, source.sample_rate)
    ranges = _sections(source, grid, sections)
    spans = [(begin, stop) for _, _, _, begin, stop, _ in ranges]
    squares = band_mean_squares(source, specs, spans)
    base = min(begin for begin, _ in spans)
    mono = mono_mix(source.channels, base, max(stop for _, stop in spans))
    rate = source.sample_rate
    items: list[dict[str, object]] = []
    for (name, start, end, begin, stop, partial), band_squares in zip(ranges, squares):
        power = mean_channel_power(source.channels, begin, stop)
        peak_value = peak(source.channels, begin, stop)
        rms = math.sqrt(power)
        item: dict[str, object] = {
            "name": name, "start_bar": start, "end_bar": end, "partial": partial,
            "start_seconds": (source.origin_frame + begin) / rate, "end_seconds": (source.origin_frame + stop) / rate,
            "rms_db": level_db(power), "peak_db": round_db(db(peak_value)),
            "crest_db": round_db(db(peak_value / rms)) if rms > 0 else None,
            "centroid_hz": centroid_hz(source, mono, begin - base, stop - base),
            "width": stereo_width(source, begin, stop),
            "bands": {spec.label: level_db(band_squares[spec.label]) for spec in specs},
            "integrated_lufs": round_db(meter.integrated(begin, stop)) if meter is not None else None}
        items.append(item)
    return {"provenance": source.provenance(), "grid": grid.record(), "sections": items,
            "band_definitions": band_definitions(specs), "band_method": BAND_METHOD,
            "rms_method": "mean of channel powers, RMS dBFS re amplitude 1; peak is the largest sample",
            "centroid_method": "power-weighted mono centroid; spans over 16 FFT frames are sampled",
            "loudness_method": (None if not loudness else "BS.1770 integrated over the section; K-weighting from selected start"
                                if meter is not None else "unavailable for more than two channels")}


def _window_summary(source: AudioSource, begin: int, end: int) -> dict[str, float | None]:
    mono = mono_mix(source.channels, begin, end)
    return {"rms_db": level_db(mean_channel_power(source.channels, begin, end)),
            "peak_db": round_db(db(peak(source.channels, begin, end))),
            "centroid_hz": centroid_hz(source, mono, 0, end - begin)}


def _deltas(first: Mapping[str, float | None], second: Mapping[str, float | None]) -> dict[str, float | None]:
    result: dict[str, float | None] = {}
    for key, before in first.items():
        after = second[key]
        result[key] = None if before is None or after is None else round(after - before, 2)
    return result


def transition(a: object, b: object, bpm: float, bar: int, *, window_seconds: float = 0.43,
               beats_per_bar: int = 4, start_bar: int = 1, grid_offset_seconds: float = 0.0) -> dict[str, object]:
    """Compare the window before and after a bar line between two renders.

    Reports RMS, peak and centroid of each window in ``a`` and ``b``, the
    ``b - a`` deltas, and each render's own jump across the bar line. Paths
    load only the two windows. The default 0.43 s is roughly one beat at 140 bpm.
    """
    grid = bar_grid(bpm, beats_per_bar, start_bar, grid_offset_seconds)
    line = grid.seconds(_integer(bar, "bar", -1_000_000, 1_000_000))
    window = finite(window_seconds, "window_seconds", positive=True)
    if window > 60:
        raise ValueError("window_seconds must not exceed 60.")
    if line - window < 0:
        raise ValueError("The window before the bar line starts before the file; choose a later bar.")
    sources = {name: coerce_source(value, line - window, line + window) for name, value in (("a", a), ("b", b))}
    sides: dict[str, dict[str, dict[str, float | None]]] = {"before": {}, "after": {}}
    for name, source in sources.items():
        rate = source.sample_rate
        centre = round(line * rate) - source.origin_frame
        width = round(window * rate)
        if centre - width < 0 or centre + width > source.frame_count:
            raise ValueError(f"Source {name!r} does not contain both windows around bar {bar} ({line:g} s).")
        sides["before"][name] = _window_summary(source, centre - width, centre)
        sides["after"][name] = _window_summary(source, centre, centre + width)
    result: dict[str, object] = {"bar": bar, "bar_line_seconds": line, "window_seconds": window,
                                 "grid": grid.record(), "delta_sign": "b minus a"}
    for side, values in sides.items():
        result[side] = {**values, "delta": _deltas(values["a"], values["b"])}
    result["jump"] = {name: _deltas(sides["before"][name], sides["after"][name]) for name in sources}
    result["sources"] = {name: {"source": source.source, "source_kind": source.source_kind}
                         for name, source in sources.items()}
    result["method"] = ("RMS is the mean of channel powers in dBFS; centroid is the mono power-weighted "
                        "spectral centroid (2048-point Hann periodograms)")
    return result
