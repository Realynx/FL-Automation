"""Live per-insert audio capture through FL's own disk recording, plus the section-measurement policy.

Every mixer insert has a record-arm (the disc button). While the transport records, each armed insert
writes its post-FX output to one WAV in FL's recorded-audio folder. ``Audio.capture`` arms the requested
inserts, plays the requested bar range once, stops, disarms, finds the new files and measures them with
the offline analysis helpers. No audio is read from FL's memory, nothing is rendered offline, and the
session stays open. ``Audio.measure_section`` chooses between that live capture and an offline render
(which the MCP tool layer owns; this module never closes a session) from the section length.

Every FL-dependent step is marked ``needs live check`` in ``docs/live-audio-capture.md``; the pure
parts (planning, file discovery and matching, measurement, envelopes, the policy) are unit-tested.
"""

from __future__ import annotations

import math
import re
import shutil
import time
from collections.abc import Callable, Iterator, Mapping, Sequence
from dataclasses import asdict, dataclass, field
from pathlib import Path
from typing import TYPE_CHECKING

from .analysis import AudioAnalysis
from .analysis.audio import load_wav
from .analysis.bands import DEFAULT_BANDS, Band
from .errors import FruityLinkError
from .records import Timebase

if TYPE_CHECKING:
    from .studio import Studio

MASTER = 0
METHOD_LIVE = "fl_disk_recording"
METHOD_RENDER = "offline_render"
ENVELOPE_BANDS: tuple[Band, ...] = ((None, 90.0), (90.0, 250.0), (250.0, 2000.0), (2000.0, None))
MAX_SECTIONS_PER_CALL = 32
_STATE = re.compile(r"playing=(?P<playing>yes|no) pos=bar \d+ beat \d+ \(tick (?P<tick>\d+)\).*?"
                    r"playRange=\[(?P<range_start>-?\d+)\.\.(?P<range_end>-?\d+)\]")


class CaptureError(FruityLinkError):
    """A live capture could not be planned, driven or read back."""


class RenderRequired(CaptureError):
    """``measure_section`` decided on an offline render but no render callback was supplied.

    Python never closes the session to render; the MCP tool layer owns renders. ``decision`` and
    ``suggested_tool_call`` tell the orchestrator what to run instead.
    """

    def __init__(self, decision: CaptureDecision, start_bar: int, end_bar: int, tail_beats: float) -> None:
        self.decision = decision
        self.suggested_tool_call = {"tool": "fl_project_render", "startBar": start_bar, "endBar": end_bar,
                                    "tailBeats": tail_beats}
        super().__init__(f"Section bars {start_bar}-{end_bar} should be rendered offline ({decision.reason}); "
                         "the MCP tool layer owns renders, so call fl_project_render (or fl_section_measure) "
                         "and measure the WAV with measure_wav().")


# ----------------------------------------------------------------------------------------------------
# Policy: live capture for short sections, offline render for long ones.
# ----------------------------------------------------------------------------------------------------

@dataclass(frozen=True)
class CapturePolicy:
    """Cost model behind ``prefer="auto"``. Seconds are wall-clock estimates, not guarantees.

    Live capture costs the section itself (it plays in real time) plus a fixed overhead for arming,
    transport start/stop, file finalisation and analysis. An offline render costs a fixed overhead for
    the snapshot save, the renderer's FL launch (plugins such as Serum and Kontakt load again), the
    session reopen the MCP layer performs afterwards, plus the audio at the renderer's throughput. The
    render figures are working estimates from the Parking Lot Moon records (108-bar renders and reopens
    were the dominant cost of every mix pass); tune them once wall times are logged.
    """

    live_overhead_seconds: float = 8.0
    render_overhead_seconds: float = 45.0
    render_speed_factor: float = 8.0
    max_live_seconds: float = 120.0

    def __post_init__(self) -> None:
        for name in ("live_overhead_seconds", "render_overhead_seconds", "render_speed_factor", "max_live_seconds"):
            value = getattr(self, name)
            if isinstance(value, bool) or not isinstance(value, (int, float)) or not math.isfinite(value) or value <= 0:
                raise ValueError(f"{name} must be a finite positive number.")

    def live_cost(self, seconds: float) -> float:
        return seconds + self.live_overhead_seconds

    def render_cost(self, seconds: float) -> float:
        return self.render_overhead_seconds + seconds / self.render_speed_factor

    def choose(self, seconds: float, *, prefer: str = "auto", per_insert: bool = False) -> CaptureDecision:
        """Pick ``METHOD_LIVE`` or ``METHOD_RENDER``; per-insert requests can only be served live."""
        if prefer not in ("auto", "live", "render"):
            raise ValueError("prefer must be 'auto', 'live' or 'render'.")
        if isinstance(seconds, bool) or not isinstance(seconds, (int, float)) or not math.isfinite(seconds) or seconds <= 0:
            raise ValueError("seconds must be a finite positive number.")
        live, render = self.live_cost(seconds), self.render_cost(seconds)
        if per_insert:
            if prefer == "render":
                raise ValueError("Per-insert measurements need a live capture; a render only yields the master.")
            return CaptureDecision(METHOD_LIVE, live, render, "per-insert audio is only available from a live capture")
        if prefer == "live":
            return CaptureDecision(METHOD_LIVE, live, render, "caller preferred live capture")
        if prefer == "render":
            return CaptureDecision(METHOD_RENDER, live, render, "caller preferred an offline render")
        if seconds > self.max_live_seconds:
            return CaptureDecision(METHOD_RENDER, live, render,
                                   f"section of {seconds:.1f}s exceeds the {self.max_live_seconds:.0f}s live limit")
        if live <= render:
            return CaptureDecision(METHOD_LIVE, live, render, f"live {live:.1f}s <= render {render:.1f}s")
        return CaptureDecision(METHOD_RENDER, live, render, f"render {render:.1f}s < live {live:.1f}s")


@dataclass(frozen=True)
class CaptureDecision:
    method: str
    live_cost_seconds: float
    render_cost_seconds: float
    reason: str

    def to_dict(self) -> dict[str, object]:
        return asdict(self)


# ----------------------------------------------------------------------------------------------------
# Planning: bars -> ticks -> seconds, using the project PPQ and one constant tempo.
# ----------------------------------------------------------------------------------------------------

def _bars(start_bar: object, end_bar: object) -> tuple[int, int]:
    if type(start_bar) is not int or type(end_bar) is not int or start_bar < 1 or end_bar < start_bar:
        raise ValueError("start_bar and end_bar must be one-based integers with end_bar >= start_bar.")
    return start_bar, end_bar


def _tail(value: object) -> float:
    if isinstance(value, bool) or not isinstance(value, (int, float)) or not math.isfinite(value) or value < 0:
        raise ValueError("tail_beats must be a finite number of beats >= 0.")
    return float(value)


def _bpm(value: object) -> float:
    if isinstance(value, bool) or not isinstance(value, (int, float)) or not math.isfinite(value) or not 10 <= value <= 522:
        raise ValueError("bpm must be a finite tempo in 10..522.")
    return float(value)


def resolve_inserts(inserts: Sequence[int] | str | None) -> tuple[int, ...]:
    """``"master"``/``None`` -> ``(0,)``; otherwise distinct nonnegative insert indices in call order."""
    if inserts is None or inserts == "master":
        return (MASTER,)
    if isinstance(inserts, str) or isinstance(inserts, bool) or not isinstance(inserts, Sequence):
        raise ValueError('inserts must be "master" or a sequence of mixer track indices (0 = Master).')
    result: list[int] = []
    for item in inserts:
        if isinstance(item, bool) or type(item) is not int or item < 0 or item > 500:
            raise ValueError("Mixer track indices must be integers in 0..500.")
        if item not in result:
            result.append(item)
    if not result:
        raise ValueError("At least one insert is required.")
    return tuple(result)


@dataclass(frozen=True)
class CapturePlan:
    """The tick and time span one capture plays; ``record_end_tick`` includes the tail."""

    inserts: tuple[int, ...]
    start_bar: int
    end_bar: int
    beats_per_bar: int
    ppq: int
    bpm: float
    start_tick: int
    end_tick: int
    tail_ticks: int

    @property
    def ticks_per_second(self) -> float:
        return self.bpm / 60 * self.ppq

    @property
    def record_end_tick(self) -> int:
        return self.end_tick + self.tail_ticks

    @property
    def seconds(self) -> float:
        return (self.end_tick - self.start_tick) / self.ticks_per_second

    @property
    def tail_seconds(self) -> float:
        return self.tail_ticks / self.ticks_per_second

    @property
    def bar_seconds(self) -> float:
        return self.beats_per_bar * 60 / self.bpm

    def to_dict(self) -> dict[str, object]:
        record: dict[str, object] = asdict(self)
        record.update(record_end_tick=self.record_end_tick, seconds=self.seconds, tail_seconds=self.tail_seconds)
        return record


def plan_capture(timebase: Timebase, bpm: float, inserts: Sequence[int] | str | None, start_bar: int, end_bar: int, *,
                 tail_beats: float = 0, beats_per_bar: int = 4) -> CapturePlan:
    """Bars are inclusive and one-based; the capture plays ``[start_tick, end_tick + tail)`` once."""
    start_bar, end_bar = _bars(start_bar, end_bar)
    tempo = _bpm(bpm)
    tail = _tail(tail_beats)
    start = timebase.bar_start(start_bar, beats_per_bar=beats_per_bar)
    end = timebase.bar_start(end_bar + 1, beats_per_bar=beats_per_bar)
    return CapturePlan(resolve_inserts(inserts), start_bar, end_bar, beats_per_bar, timebase.ppq, tempo,
                       int(start), int(end), int(timebase.ticks(tail)))


# ----------------------------------------------------------------------------------------------------
# Transport state readback and recorded-file discovery.
# ----------------------------------------------------------------------------------------------------

@dataclass(frozen=True)
class TransportState:
    playing: bool
    tick: int
    play_range: tuple[int, int] | None


def parse_state(text: str) -> TransportState:
    """Parse ``get_song_state`` text: play flag, playhead tick and the inclusive loop range (or None)."""
    match = _STATE.search(text)
    if match is None:
        raise CaptureError(f"Unrecognised transport state text: {text!r}")
    start, end = int(match.group("range_start")), int(match.group("range_end"))
    play_range = (start, end) if start >= 0 and end >= start else None
    return TransportState(match.group("playing") == "yes", int(match.group("tick")), play_range)


def snapshot_folder(folder: Path) -> dict[str, tuple[int, int]]:
    """Name -> (mtime_ns, size) for every WAV in ``folder`` so new recordings can be told apart."""
    result: dict[str, tuple[int, int]] = {}
    for path in folder.glob("*.wav"):
        try:
            stat = path.stat()
        except OSError:
            continue
        result[path.name] = (stat.st_mtime_ns, stat.st_size)
    return result


def new_recordings(folder: Path, before: Mapping[str, tuple[int, int]]) -> list[Path]:
    """WAVs created or rewritten since ``before``, oldest first."""
    after = snapshot_folder(folder)
    fresh = [folder / name for name, stamp in after.items() if before.get(name) != stamp]
    return sorted(fresh, key=lambda path: (after[path.name][0], path.name))


def _key(text: str) -> str:
    return re.sub(r"[^a-z0-9]+", "", text.lower())


def match_recordings(files: Sequence[Path], names: Mapping[int, str]) -> dict[int, Path]:
    """Pair new recordings with the inserts they came from.

    FL auto-names a disk recording ``<project>_<n>_<track name>.wav`` (the engine's ``%s_%d_%s``
    template; the exact form needs a live check), so a file whose stem ends with a track's name is that
    track's. Names are compared case- and punctuation-insensitively. When exactly one file and one
    track remain unmatched they are paired; anything else is refused with the file list so nothing
    is measured under the wrong label.
    """
    remaining = list(files)
    result: dict[int, Path] = {}
    unmatched: list[int] = []
    for track, name in sorted(names.items(), key=lambda item: -len(item[1])):
        key = _key(name)
        hits = [path for path in remaining if key and _key(path.stem).endswith(key)]
        if len(hits) == 1:
            result[track] = hits[0]
            remaining.remove(hits[0])
        else:
            unmatched.append(track)
    if len(unmatched) == 1 and len(remaining) == 1:
        result[unmatched[0]] = remaining[0]
        unmatched, remaining = [], []
    if unmatched or remaining:
        raise CaptureError(
            f"Could not pair recordings with inserts: unmatched inserts {unmatched}, unmatched files "
            f"{[path.name for path in remaining]}; matched {[(t, p.name) for t, p in result.items()]}.")
    return result


# ----------------------------------------------------------------------------------------------------
# Measurement and the agent-facing waveform readout (plain WAV paths in, JSON-safe dicts out).
# ----------------------------------------------------------------------------------------------------

def _section_seconds(bpm: float, start_bar: int, end_bar: int, beats_per_bar: int) -> float:
    return (end_bar - start_bar + 1) * beats_per_bar * 60 / bpm


def _chunks(start_bar: int, end_bar: int, size: int) -> Iterator[tuple[int, int]]:
    bar = start_bar
    while bar <= end_bar:
        yield bar, min(end_bar, bar + size - 1)
        bar += size


def _rows(record: Mapping[str, object], key: str) -> list[dict[str, object]]:
    """The list of dict rows under ``key`` of an analysis result (typed as ``object`` by the helpers)."""
    items = record.get(key)
    return [item for item in items if isinstance(item, dict)] if isinstance(items, list) else []


def _bar_rows(audio: AudioAnalysis, tempo: float, start_bar: int, last_full: int, beats_per_bar: int,
              bands: Sequence[Band], loudness: bool, grid_offset_seconds: float) -> list[dict[str, object]]:
    """``describe_sections`` one bar at a time (chunked to its per-call limit) for bars ``start_bar..last_full``.
    The grid is anchored at ``grid_offset_seconds`` of the file, which is where ``start_bar`` begins."""
    rows: list[dict[str, object]] = []
    if last_full < start_bar:
        return rows
    for first, last in _chunks(start_bar, last_full, MAX_SECTIONS_PER_CALL):
        sections = {str(bar): (bar, bar) for bar in range(first, last + 1)}
        described = audio.describe_sections(tempo, sections, beats_per_bar=beats_per_bar, start_bar=start_bar,
                                            grid_offset_seconds=grid_offset_seconds, bands=bands, loudness=loudness)
        rows.extend(_rows(described, "sections"))
    return rows


def _peak_dbfs(summary: Mapping[str, object]) -> float | None:
    channels = summary.get("channels")
    values = [channel["sample_peak_dbfs"] for channel in channels if isinstance(channel, Mapping)
              and isinstance(channel.get("sample_peak_dbfs"), (int, float))] if isinstance(channels, list) else []
    return round(max(values), 2) if values else None


def _rms_dbfs(summary: Mapping[str, object]) -> float | None:
    channels = summary.get("channels")
    powers = [channel["mean_square"] for channel in channels if isinstance(channel, Mapping)
              and isinstance(channel.get("mean_square"), (int, float))] if isinstance(channels, list) else []
    if not powers:
        return None
    power = sum(powers) / len(powers)
    return round(10 * math.log10(power), 2) if power > 0 else None


def measure_wav(path: str | Path, *, bpm: float, start_bar: int, end_bar: int, beats_per_bar: int = 4,
                offset_seconds: float = 0.0, bands: Sequence[Band] = DEFAULT_BANDS, loudness: bool = True,
                source_kind: str = "capture") -> dict[str, object]:
    """One measurement record for the bars ``start_bar..end_bar`` (one-based, **end inclusive**: 33..36 is
    four bars) of a WAV whose time ``offset_seconds`` is the start of ``start_bar``. The same record is
    produced for a live capture file and for a section render (offset 0), so both paths compare directly.

    ``peak_dbfs`` is the largest sample of any channel, ``rms_dbfs`` the mean channel power (anti-phase
    stereo does not cancel), ``integrated_lufs``/``short_term_lufs`` follow the BS.1770 helpers, ``bands``
    are the filtered mono-mix levels, and ``bars`` lists per-bar level, peak, crest, centroid, bands and
    the bar's own gated loudness (``lufs``; a single bar is shorter than a 3 s short-term window). A bar
    the file does not fully cover is marked ``partial``; ``covered_bars`` counts the complete ones.
    """
    start_bar, end_bar = _bars(start_bar, end_bar)
    tempo = _bpm(bpm)
    if isinstance(offset_seconds, bool) or not isinstance(offset_seconds, (int, float)) or not math.isfinite(offset_seconds) \
            or offset_seconds < 0:
        raise ValueError("offset_seconds must be a finite number >= 0.")
    seconds = _section_seconds(tempo, start_bar, end_bar, beats_per_bar)
    source = load_wav(path, start_seconds=offset_seconds, end_seconds=offset_seconds + seconds, source_kind=source_kind)
    audio = AudioAnalysis(source)
    stereo = len(source.channels) <= 2
    summary = audio.summary(loudness=loudness and stereo)
    measurements = summary["measurements"]
    assert isinstance(measurements, dict)
    energy = audio.band_energy(bands)
    bar_seconds = beats_per_bar * 60 / tempo
    covered = int(source.frame_count / source.sample_rate / bar_seconds + 1e-9)
    last_full = min(end_bar, start_bar + covered - 1)
    bars: list[dict[str, object]] = []
    for item in _bar_rows(audio, tempo, start_bar, last_full, beats_per_bar, bands, loudness and stereo,
                          float(offset_seconds)):
        width = item.get("width")
        bars.append({"bar": item["start_bar"], "partial": item["partial"], "rms_db": item["rms_db"],
                     "peak_db": item["peak_db"], "crest_db": item["crest_db"], "centroid_hz": item["centroid_hz"],
                     "correlation": width.get("correlation") if isinstance(width, dict) else None,
                     "bands": item["bands"], "lufs": item["integrated_lufs"]})
    return {
        "source": str(path), "source_kind": source_kind, "sample_rate": source.sample_rate,
        "channel_count": len(source.channels), "bpm": tempo, "beats_per_bar": beats_per_bar,
        "start_bar": start_bar, "end_bar": end_bar, "requested_seconds": seconds,
        "measured_seconds": source.frame_count / source.sample_rate, "covered_bars": covered,
        "partial": source.frame_count < round(seconds * source.sample_rate) - 1,
        "peak_dbfs": _peak_dbfs(measurements), "rms_dbfs": _rms_dbfs(measurements),
        "integrated_lufs": measurements.get("integrated_lufs"), "short_term_lufs": measurements.get("short_term_lufs"),
        "full_db": energy["full_db"], "bands": energy["bands"], "bars": bars,
        "methods": {"rms": "mean of channel powers, dBFS re amplitude 1", "peak": "largest stored sample of any channel",
                    "loudness": measurements.get("loudness_method"), "bands": energy["method"]},
    }


def envelope(path: str | Path, *, bpm: float, start_bar: int, end_bar: int, beats_per_bar: int = 4,
             offset_seconds: float = 0.0, slices_per_bar: int = 8, bands: Sequence[Band] = ENVELOPE_BANDS,
             source_kind: str = "capture") -> dict[str, object]:
    """A compact waveform readout for an agent: per-slice RMS and peak (dB, one decimal) over the bars,
    plus band levels per bar. ``slices_per_bar=8`` is an eighth note per point; a 16-bar section is 128
    numbers per series. Values are ``None`` for silence.
    """
    start_bar, end_bar = _bars(start_bar, end_bar)
    tempo = _bpm(bpm)
    if type(slices_per_bar) is not int or not 1 <= slices_per_bar <= 64:
        raise ValueError("slices_per_bar must be an integer in 1..64.")
    seconds = _section_seconds(tempo, start_bar, end_bar, beats_per_bar)
    source = load_wav(path, start_seconds=offset_seconds, end_seconds=offset_seconds + seconds, source_kind=source_kind)
    audio = AudioAnalysis(source)
    slice_seconds = beats_per_bar * 60 / tempo / slices_per_bar
    rms: list[float | None] = []
    peak: list[float | None] = []
    offset = 0
    while True:
        page = audio.windows(window_seconds=slice_seconds, hop_seconds=slice_seconds, offset=offset, limit=64)
        for item in _rows(page, "items"):
            level, top = _rms_dbfs(item), _peak_dbfs(item)
            rms.append(None if level is None else round(level, 1))
            peak.append(None if top is None else round(top, 1))
        next_offset = page.get("next_offset")
        if not isinstance(next_offset, int):
            break
        offset = next_offset
    bar_seconds = beats_per_bar * 60 / tempo
    covered = int(source.frame_count / source.sample_rate / bar_seconds + 1e-9)
    band_rows: list[dict[str, object]] = []
    for item in _bar_rows(audio, tempo, start_bar, min(end_bar, start_bar + covered - 1), beats_per_bar, bands, False,
                          float(offset_seconds)):
        levels = item.get("bands")
        band_rows.append({"bar": item["start_bar"], **(dict(levels) if isinstance(levels, dict) else {})})
    return {"source": str(path), "start_bar": start_bar, "end_bar": end_bar, "bpm": tempo,
            "slices_per_bar": slices_per_bar, "slice_seconds": slice_seconds, "slices": len(rms),
            "rms_db": rms, "peak_db": peak, "bands_per_bar": band_rows,
            "note": "rms is mean channel power in dBFS re amplitude 1; peak is the largest sample; one point per slice"}


# ----------------------------------------------------------------------------------------------------
# Results.
# ----------------------------------------------------------------------------------------------------

@dataclass(frozen=True)
class CaptureFile:
    track: int
    name: str
    path: Path
    sample_rate: int
    channel_count: int
    frames: int

    @property
    def seconds(self) -> float:
        return self.frames / self.sample_rate

    def to_dict(self) -> dict[str, object]:
        return {"track": self.track, "name": self.name, "path": str(self.path), "sample_rate": self.sample_rate,
                "channel_count": self.channel_count, "frames": self.frames, "seconds": self.seconds}


def _safe_measure(path: Path, plan: CapturePlan, track: int) -> dict[str, object]:
    """``measure_wav`` for one capture file; a failure becomes ``{"error": ..., "source": ...}`` so every
    ``measurements`` value is a dict (a caller reading ``record.get("rms_dbfs")`` never sees a string)."""
    try:
        return measure_wav(path, bpm=plan.bpm, start_bar=plan.start_bar, end_bar=plan.end_bar,
                           beats_per_bar=plan.beats_per_bar, source_kind=f"fl_insert_{track}")
    except (OSError, ValueError, ArithmeticError) as error:
        return {"error": f"{type(error).__name__}: {error}", "source": str(path), "source_kind": f"fl_insert_{track}",
                "start_bar": plan.start_bar, "end_bar": plan.end_bar}


def as_measurement(value: object, track: int) -> dict[str, object]:
    """Coerce anything stored under a track into a dict record (defensive for serialisation)."""
    if isinstance(value, dict):
        return value
    return {"error": f"measurement for insert {track} is not a record: {value!r}"}


@dataclass(frozen=True)
class CaptureResult:
    """Per-insert WAV paths (plain files any descriptor can take), the actual captured range in ticks and
    a ``measurements`` record per insert (always a dict; a failed measurement carries an ``error`` key).
    ``files`` are FL's own recordings, or the named copies when ``name`` was given. ``deleted_clips``,
    ``retired_channels`` and ``removed_originals`` report the litter cleanup (see the doc's known limitations)."""

    plan: CapturePlan
    files: tuple[CaptureFile, ...]
    captured_end_tick: int
    stop_tick: int | None
    measurements: dict[int, dict[str, object]]
    recorded_folder: Path
    method: str = METHOD_LIVE
    warnings: tuple[str, ...] = field(default_factory=tuple)
    deleted_clips: int = 0
    retired_channels: tuple[int, ...] = field(default_factory=tuple)
    removed_originals: tuple[str, ...] = field(default_factory=tuple)

    def __post_init__(self) -> None:
        object.__setattr__(self, "measurements",
                           {track: as_measurement(record, track) for track, record in self.measurements.items()})

    @property
    def paths(self) -> dict[int, Path]:
        return {item.track: item.path for item in self.files}

    def path(self, track: int) -> Path:
        for item in self.files:
            if item.track == track:
                return item.path
        raise KeyError(f"No capture file for mixer track {track}.")

    @property
    def captured_start_tick(self) -> int:
        return self.plan.start_tick

    @property
    def complete(self) -> bool:
        return self.captured_end_tick >= self.plan.end_tick

    def envelope(self, track: int = MASTER, *, slices_per_bar: int = 8,
                 bands: Sequence[Band] = ENVELOPE_BANDS) -> dict[str, object]:
        return envelope(self.path(track), bpm=self.plan.bpm, start_bar=self.plan.start_bar, end_bar=self.plan.end_bar,
                        beats_per_bar=self.plan.beats_per_bar, slices_per_bar=slices_per_bar, bands=bands)

    def to_dict(self) -> dict[str, object]:
        return {"method": self.method, "plan": self.plan.to_dict(), "files": [item.to_dict() for item in self.files],
                "captured_start_tick": self.captured_start_tick, "captured_end_tick": self.captured_end_tick,
                "complete": self.complete, "stop_tick": self.stop_tick,
                "measurements": {str(track): as_measurement(record, track) for track, record in self.measurements.items()},
                "recorded_folder": str(self.recorded_folder), "warnings": list(self.warnings),
                "deleted_clips": self.deleted_clips, "retired_channels": list(self.retired_channels),
                "removed_originals": list(self.removed_originals)}


@dataclass(frozen=True)
class SectionMeasurement:
    """The same record whichever path produced it: ``measurements`` maps insert -> ``measure_wav`` record."""

    method: str
    decision: CaptureDecision
    start_bar: int
    end_bar: int
    files: dict[int, Path]
    measurements: dict[int, dict[str, object]]
    capture: CaptureResult | None = None

    def to_dict(self) -> dict[str, object]:
        return {"method": self.method, "decision": self.decision.to_dict(), "start_bar": self.start_bar,
                "end_bar": self.end_bar, "files": {str(t): str(p) for t, p in self.files.items()},
                "measurements": {str(t): r for t, r in self.measurements.items()},
                "capture": None if self.capture is None else self.capture.to_dict()}


# ----------------------------------------------------------------------------------------------------
# The live driver.
# ----------------------------------------------------------------------------------------------------

@dataclass(frozen=True)
class _LitterSnapshot:
    """Clips and channels before a pass, so the ones FL auto-creates per recording can be undone.

    Live finding (FL 26.1.3): every disk recording adds one sample channel (named like
    ``lc-001_<date>_<track>``) and one playlist clip even with "Auto-create audio clip" off. Clips
    are matched by (track, start, length, source) and deleted; channels have no delete call, so new
    ones are retired (muted, routed to Master, renamed "(unused) ...").
    """

    clips: frozenset[tuple[int, int, int, str, int]]
    channels: frozenset[int]

    @staticmethod
    def take(fl: Studio) -> _LitterSnapshot:
        return _LitterSnapshot(
            frozenset((c.track, c.start_tick, c.length_tick, c.source_kind, c.source_index) for c in fl.clips.list()),
            frozenset(c.index for c in fl.channels.list()))

    def clean(self, fl: Studio, warnings: list[str]) -> tuple[int, list[int]]:
        deleted = 0
        retired: list[int] = []
        try:
            fresh = [c.index for c in fl.clips.list()
                     if (c.track, c.start_tick, c.length_tick, c.source_kind, c.source_index) not in self.clips]
            if fresh:
                fl.clips.delete(fresh)
                deleted = len(fresh)
        except FruityLinkError as error:
            warnings.append(f"Auto-created clips were not deleted: {error}")
        for channel in sorted(c.index for c in fl.channels.list() if c.index not in self.channels):
            try:
                fl.channels.retire(channel)
                retired.append(channel)
            except FruityLinkError as error:
                warnings.append(f"Auto-created channel {channel} was not retired: {error}")
        if retired:
            warnings.append(f"FL added sample channel(s) {retired} for the recording(s); they were retired (muted, "
                            "routed to Master, renamed '(unused) ...') because FL has no channel-delete call.")
        return deleted, retired


def default_recorded_folders() -> tuple[Path, ...]:
    """Where FL writes disk recordings by default (user data folder, ``Audio\\Recorded``)."""
    documents = Path.home() / "Documents" / "Image-Line"
    return (documents / "FL Studio" / "Audio" / "Recorded", documents / "Data" / "FL Studio" / "Audio" / "Recorded")


def _probe(path: Path) -> tuple[int, int, int]:
    """(sample_rate, channels, total frames) from the header; reads a tiny PCM slice only."""
    source = load_wav(path, end_seconds=0.001)
    return source.sample_rate, len(source.channels), source.total_source_frames


class Audio:
    """``fl.audio``: live capture and section measurement. Nothing here renders or closes a session."""

    def __init__(self, studio: Studio, *, policy: CapturePolicy | None = None, recorded_folder: str | Path | None = None,
                 sleep: Callable[[float], None] = time.sleep, clock: Callable[[], float] = time.monotonic) -> None:
        self._fl = studio
        self.policy = policy or CapturePolicy()
        self.recorded_folder = Path(recorded_folder) if recorded_folder is not None else None
        self._sleep = sleep
        self._clock = clock

    # -- folders -------------------------------------------------------------------------------------

    def resolve_recorded_folder(self) -> Path:
        """The folder FL records into: an explicit override, else the first default that exists."""
        if self.recorded_folder is not None:
            if not self.recorded_folder.is_dir():
                raise CaptureError(f"Recorded-audio folder does not exist: {self.recorded_folder}")
            return self.recorded_folder
        for candidate in default_recorded_folders():
            if candidate.is_dir():
                return candidate
        raise CaptureError("FL's recorded-audio folder was not found; pass recorded_folder= (FL: Options > File "
                           "settings > user data folder, then Audio\\Recorded).")

    # -- capture -------------------------------------------------------------------------------------

    def plan(self, inserts: Sequence[int] | str | None, start_bar: int, end_bar: int, *, tail_beats: float = 0,
             beats_per_bar: int = 4) -> CapturePlan:
        return plan_capture(self._fl.timebase, self._fl.transport.tempo, inserts, start_bar, end_bar,
                            tail_beats=tail_beats, beats_per_bar=beats_per_bar)

    def capture(self, inserts: Sequence[int] | str = "master", start_bar: int = 1, end_bar: int = 1, *,
                tail_beats: float = 0, name: str | None = None, beats_per_bar: int = 4, timeout: float | None = None,
                poll_seconds: float = 0.25, file_timeout: float = 15.0, keep_originals: bool = False,
                cleanup: bool = True, arm_refresh: bool = True) -> CaptureResult:
        """Arm ``inserts``, play bars ``start_bar..end_bar`` (one-based, **end inclusive**: 33..36 plays four
        bars) plus ``tail_beats`` once while FL records, then measure the WAV FL wrote for each insert.

        The transport must be stopped. Song mode is selected, any loop selection is cleared for the
        pass and restored afterwards, the playhead is seeked to the section start, record is toggled
        on, play starts, and the playhead is polled until it passes the end (or FL stops by itself).
        Inserts armed by this call are disarmed afterwards; inserts that were already armed also
        record and their files are ignored.

        FL quirks handled here (live, FL 26.1.3): FL only registers the recording set after a *second*
        arm-state change, so after arming the requested inserts one non-requested insert (Master unless
        Master is requested) is armed and disarmed again (``arm_refresh``; noted in ``warnings``).
        Every recording also adds a sample channel and a playlist clip even with "Auto-create audio
        clip" off; with ``cleanup`` the new clips are deleted and the new channels retired
        (``Channel.retire``: muted, routed to Master, renamed "(unused) ..."; FL has no channel-delete
        call), reported as ``deleted_clips`` / ``retired_channels``. With ``name`` the recordings are
        copied next to the originals as ``<name>-<track>.wav`` and, unless ``keep_originals``, FL's
        auto-named originals are deleted once the copies verify (``removed_originals``); with
        ``name=None`` FL's files are kept as they are. See docs/live-audio-capture.md.
        """
        if poll_seconds <= 0 or not math.isfinite(poll_seconds):
            raise ValueError("poll_seconds must be positive.")
        fl = self._fl
        plan = self.plan(inserts, start_bar, end_bar, tail_beats=tail_beats, beats_per_bar=beats_per_bar)
        names = {item.index: item.name for item in fl.mixer.list()}
        missing = [track for track in plan.inserts if track not in names]
        if missing:
            raise CaptureError(f"Mixer tracks {missing} are not addressable inserts; query fl.mixer.list().")
        folder = self.resolve_recorded_folder()
        state = parse_state(fl.transport.state_text())
        if state.playing:
            raise CaptureError("The transport is playing; stop it before capturing.")
        deadline_seconds = timeout if timeout is not None else plan.seconds + plan.tail_seconds + 10.0
        if deadline_seconds <= 0 or not math.isfinite(deadline_seconds):
            raise ValueError("timeout must be positive.")
        before = snapshot_folder(folder)
        litter = _LitterSnapshot.take(fl) if cleanup else None
        stop_tick, warnings = self._record_pass(plan, state, deadline_seconds, poll_seconds, names, arm_refresh)
        files = self._collect(folder, before, plan, names, file_timeout, poll_seconds, arm_refresh)
        removed: list[str] = []
        if name is not None:
            copies = tuple(self._rename(item, folder, name) for item in files)
            if not keep_originals:
                removed = self._remove_originals(files, copies, warnings)
            files = copies
        deleted_clips = 0
        retired: list[int] = []
        if litter is not None:
            deleted_clips, retired = litter.clean(fl, warnings)
        shortest = min(item.seconds for item in files)
        captured_end = plan.start_tick + int(shortest * plan.ticks_per_second)
        measurements = {item.track: _safe_measure(item.path, plan, item.track) for item in files}
        return CaptureResult(plan, files, captured_end, stop_tick, measurements, folder, warnings=tuple(warnings),
                             deleted_clips=deleted_clips, retired_channels=tuple(retired), removed_originals=tuple(removed))

    @staticmethod
    def _refresh_candidate(plan: CapturePlan, names: Mapping[int, str], armed: Callable[[int], bool]) -> int | None:
        """The insert used for the arm-refresh workaround: Master unless requested, else the first
        addressable, non-requested, currently unarmed insert; None when there is no such insert."""
        if MASTER not in plan.inserts and MASTER in names:
            return MASTER
        for track in sorted(names):
            if track not in plan.inserts and not armed(track):
                return track
        return None

    def _record_pass(self, plan: CapturePlan, state: TransportState, deadline_seconds: float,
                     poll_seconds: float, names: Mapping[int, str], arm_refresh: bool) -> tuple[int | None, list[str]]:
        """Song mode, no loop, arm (+ refresh), seek, record+play, wait, stop; then restore what was changed."""
        fl = self._fl
        warnings: list[str] = []
        previous_mode = fl.transport.song_mode
        if not previous_mode:
            fl.transport.song_mode = True
        if state.play_range is not None:
            fl.transport.clear_loop()
        armed_here: list[int] = []
        stop_tick: int | None = None
        try:
            for track in plan.inserts:
                if not fl.mixer[track].armed:
                    fl.mixer[track].armed = True
                    armed_here.append(track)
            if arm_refresh:
                self._arm_refresh(plan, names, warnings)
            fl.transport.seek_ticks(plan.start_tick)
            fl.transport.toggle_record()
            fl.transport.play()
            stop_tick = self._wait_for_end(plan, deadline_seconds, poll_seconds, warnings)
            fl.transport.stop()
        finally:
            for track in reversed(armed_here):
                try:
                    fl.mixer[track].armed = False
                except FruityLinkError as error:
                    warnings.append(f"Could not disarm insert {track}: {error}")
            if state.play_range is not None:
                fl.transport.loop_ticks(state.play_range[0], state.play_range[1] + 1)
            if not previous_mode:
                fl.transport.song_mode = False
        return stop_tick, warnings

    def _arm_refresh(self, plan: CapturePlan, names: Mapping[int, str], warnings: list[str]) -> None:
        """Live finding (FL 26.1.3): a pass that arms only inserts that are not the mixer's selected track
        writes no file unless one more arm-state change follows. Arm and disarm a non-requested insert
        and verify both readbacks; the applied workaround is noted in the warnings."""
        fl = self._fl
        track = self._refresh_candidate(plan, names, lambda index: bool(fl.mixer[index].armed))
        if track is None:
            warnings.append("Arm-refresh workaround skipped: no unarmed non-requested insert exists; if FL writes no "
                            "file, add an insert or arm/disarm one by hand before the pass.")
            return
        fl.mixer[track].armed = True
        if not fl.mixer[track].armed:
            raise CaptureError(f"Arm-refresh insert {track} did not read back armed; FL refused the arm.")
        fl.mixer[track].armed = False
        if fl.mixer[track].armed:
            raise CaptureError(f"Arm-refresh insert {track} stayed armed after disarming; inspect the mixer.")
        warnings.append(f"Arm-refresh workaround applied: insert {track} ({names.get(track, '?')}) was armed and "
                        "disarmed after the requested inserts so FL registers the recording set (FL 26.1.3 finding).")

    def _wait_for_end(self, plan: CapturePlan, deadline_seconds: float, poll_seconds: float, warnings: list[str]) -> int:
        """Poll the playhead until it passes the record end, FL stops by itself, or the deadline lapses."""
        deadline = self._clock() + deadline_seconds
        while True:
            self._sleep(poll_seconds)
            current = parse_state(self._fl.transport.state_text())
            if current.tick >= plan.record_end_tick or not current.playing:
                if not current.playing and current.tick < plan.record_end_tick:
                    warnings.append(f"FL stopped by itself at tick {current.tick} (song end?) before {plan.record_end_tick}.")
                return current.tick
            if self._clock() >= deadline:
                warnings.append(f"Playhead readback did not pass tick {plan.record_end_tick} within {deadline_seconds:.1f}s;"
                                " stopped on the deadline.")
                return current.tick

    def _collect(self, folder: Path, before: Mapping[str, tuple[int, int]], plan: CapturePlan, names: Mapping[int, str],
                 file_timeout: float, poll_seconds: float, arm_refresh: bool = True) -> tuple[CaptureFile, ...]:
        """Wait for one stable new WAV per requested insert, then pair them by track name."""
        expected = len(plan.inserts)
        deadline = self._clock() + file_timeout
        previous: dict[str, tuple[int, int]] = {}
        while True:
            fresh = new_recordings(folder, before)
            current = {path.name: snapshot_folder(folder)[path.name] for path in fresh if path.name in snapshot_folder(folder)}
            if len(fresh) >= expected and current == previous:
                probed = {}
                for path in fresh:
                    try:
                        probed[path] = _probe(path)
                    except (OSError, ValueError):
                        break
                else:
                    matched = match_recordings(fresh, {track: names[track] for track in plan.inserts}) \
                        if len(fresh) == expected else self._match_subset(fresh, plan, names)
                    return tuple(CaptureFile(track, names[track], path, *probed[path]) for track, path in matched.items())
            previous = current
            if self._clock() >= deadline:
                if not fresh:
                    raise CaptureError(
                        f"FL wrote no WAV in {folder} within {file_timeout:.0f}s for inserts {plan.inserts}. Check FL's "
                        "recording settings: (1) the recording filter must include Audio (right-click the record "
                        "button > Recording filter > Audio; registry HKCU\\Software\\Image-Line\\FL Studio 26\\General\\"
                        "FruityLoopsMainForm RecordingFilter2, value 3 means Audio off); (2) mixer menu > Disk "
                        "recording > 'Auto-create audio clip' should be off; (3) FL registers the recording set only "
                        "after a second arm-state change"
                        + (" (the arm-refresh workaround was applied)." if arm_refresh else
                           " (arm_refresh=False was passed; leave it on or arm/disarm another insert by hand)."))
                raise CaptureError(f"FL wrote {len(fresh)} new WAV(s) in {folder} within {file_timeout:.0f}s; expected "
                                   f"{expected} for inserts {plan.inserts}: {[p.name for p in fresh]}.")
            self._sleep(poll_seconds)

    @staticmethod
    def _match_subset(fresh: Sequence[Path], plan: CapturePlan, names: Mapping[int, str]) -> dict[int, Path]:
        """More files than requested inserts (other inserts were already armed): match by name only."""
        wanted = {track: names[track] for track in plan.inserts}
        result: dict[int, Path] = {}
        for track, name in wanted.items():
            hits = [path for path in fresh if _key(path.stem).endswith(_key(name))]
            if len(hits) != 1:
                raise CaptureError(f"Insert {track} ({name!r}) matches {len(hits)} of the new files {[p.name for p in fresh]}.")
            result[track] = hits[0]
        return result

    @staticmethod
    def _rename(item: CaptureFile, folder: Path, name: str) -> CaptureFile:
        if not re.fullmatch(r"[A-Za-z0-9 ._-]{1,80}", name):
            raise ValueError("name must be 1..80 characters of letters, digits, space, dot, underscore or dash.")
        target = folder / f"{name}-{'master' if item.track == MASTER else item.track}.wav"
        if target.exists():
            raise CaptureError(f"Capture target already exists: {target}")
        shutil.copyfile(item.path, target)
        return CaptureFile(item.track, item.name, target, item.sample_rate, item.channel_count, item.frames)

    @staticmethod
    def _remove_originals(originals: Sequence[CaptureFile], copies: Sequence[CaptureFile],
                          warnings: list[str]) -> list[str]:
        """Delete FL's auto-named recordings once each named copy re-reads with the same header."""
        removed: list[str] = []
        for original, copy in zip(originals, copies):
            try:
                if _probe(copy.path) != (original.sample_rate, original.channel_count, original.frames):
                    warnings.append(f"Kept {original.path.name}: the copy {copy.path.name} did not verify.")
                    continue
                original.path.unlink()
                removed.append(original.path.name)
            except (OSError, ValueError) as error:
                warnings.append(f"Kept {original.path.name}: {error}")
        return removed

    # -- policy --------------------------------------------------------------------------------------

    def decide(self, start_bar: int, end_bar: int, inserts: Sequence[int] | str | None = None, *, prefer: str = "auto",
               beats_per_bar: int = 4) -> CaptureDecision:
        start_bar, end_bar = _bars(start_bar, end_bar)
        seconds = _section_seconds(_bpm(self._fl.transport.tempo), start_bar, end_bar, beats_per_bar)
        return self.policy.choose(seconds, prefer=prefer, per_insert=inserts is not None and inserts != "master")

    def measure_section(self, start_bar: int, end_bar: int, inserts: Sequence[int] | str | None = None, *,
                        prefer: str = "auto", tail_beats: float = 0, beats_per_bar: int = 4,
                        render: Callable[[int, int, float], str | Path] | None = None) -> SectionMeasurement:
        """Measure bars ``start_bar..end_bar`` (one-based, **end inclusive**): live capture when the section
        is short, offline render when it is long (``CapturePolicy``), returning the same record either way.

        Python never renders or closes the session. When the policy picks a render, ``render`` (a
        callback ``(start_bar, end_bar, tail_beats) -> wav path`` owned by the MCP tool layer) is
        called; without one ``RenderRequired`` is raised with the suggested ``fl_project_render`` call.
        ``inserts`` other than the master force a live capture.
        """
        decision = self.decide(start_bar, end_bar, inserts, prefer=prefer, beats_per_bar=beats_per_bar)
        if decision.method == METHOD_LIVE:
            capture = self.capture(inserts if inserts is not None else "master", start_bar, end_bar,
                                   tail_beats=tail_beats, beats_per_bar=beats_per_bar)
            return SectionMeasurement(METHOD_LIVE, decision, start_bar, end_bar, capture.paths, capture.measurements, capture)
        if render is None:
            raise RenderRequired(decision, start_bar, end_bar, _tail(tail_beats))
        path = Path(render(start_bar, end_bar, _tail(tail_beats)))
        record = measure_wav(path, bpm=self._fl.transport.tempo, start_bar=start_bar, end_bar=end_bar,
                             beats_per_bar=beats_per_bar, source_kind="master_render")
        return SectionMeasurement(METHOD_RENDER, decision, start_bar, end_bar, {MASTER: path}, {MASTER: record})
