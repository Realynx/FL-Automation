"""Symbolic note density and explicit constant-tempo conversion, independent of audio."""

from collections.abc import Sequence

from ..models import NoteInfo
from .audio import finite


def tick_range_seconds(start_tick: int, end_tick: int, *, ppq: int, bpm: float) -> tuple[float, float]:
    """Convert an absolute tick interval using an explicitly constant tempo. No tempo-map inference."""
    _tick_range(start_tick, end_tick, ppq)
    tempo = finite(bpm, "bpm", positive=True)
    scale = 60 / (tempo * ppq)
    return start_tick * scale, end_tick * scale


def _tick_range(start: int, end: int, ppq: int) -> None:
    if any(type(value) is not int for value in (start, end, ppq)) or ppq <= 0 or start < 0 or end <= start:
        raise ValueError("A nonempty nonnegative integer tick range and positive integer PPQ are required.")


def note_density(notes: Sequence[NoteInfo], *, start_tick: int, end_tick: int, ppq: int,
                 include_muted: bool = False) -> dict[str, object]:
    """Pattern-local note onsets, occupied duration and average polyphony; not audio density.

    Notes crossing a boundary contribute only their intersection. Playlist repetitions,
    cropping, muted clips/tracks, and channel audibility must be resolved by the caller.
    """
    _tick_range(start_tick, end_tick, ppq)
    spans = []
    onsets = 0
    for note in notes:
        if note.muted and not include_muted:
            continue
        if note.length_tick <= 0 or note.start_tick < 0:
            continue
        begin, end = max(start_tick, note.start_tick), min(end_tick, note.start_tick + note.length_tick)
        if begin >= end:
            continue
        spans.append((begin, end))
        onsets += start_tick <= note.start_tick < end_tick
    occupied = 0
    previous_end = start_tick
    for begin, end in sorted(spans):
        occupied += max(0, end - max(begin, previous_end))
        previous_end = max(previous_end, end)
    duration = end_tick - start_tick
    return {"source_kind": "pattern_local_notes", "start_tick": start_tick, "end_tick": end_tick,
            "ppq": ppq, "include_muted": include_muted, "overlapping_notes": len(spans),
            "onsets": onsets, "onsets_per_beat": onsets * ppq / duration,
            "occupied_fraction": occupied / duration,
            "average_polyphony": sum(end - begin for begin, end in spans) / duration}
