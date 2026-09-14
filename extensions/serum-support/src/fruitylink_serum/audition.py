"""Lay out several Serum candidates back to back so one render auditions them all.

The manual pattern from the Ember Tides revisions (v007, v008): add one Serum channel per
candidate, route it to the target mixer track, copy the source pattern's notes into a new
pattern for that channel, place the patterns one after another on a free playlist track
(optionally with accompaniment patterns alongside each slot), delete every time marker
(FL extends renders to the last marker), then render once and measure each slot.
:func:`audition_candidates` performs the scaffolding through existing host operations
only and returns the time layout; the caller renders (``fl_project_render``) and slices.

Nothing here removes what it created; the layout lists the channels, patterns and
playlist tracks so the caller can delete or mute them afterwards.
"""

from __future__ import annotations

import math
import os
import re
from collections.abc import Sequence
from pathlib import Path
from typing import Any

from fruitylink.records import NoteSpec

from . import loading
from .builder import SerumPatch

__all__ = ["audition_candidates", "free_playlist_tracks", "pattern_span_ticks"]

Candidate = "str | os.PathLike[str] | SerumPatch | int"
_MARKER_HEADER = re.compile(r"^(\d+) markers:")


def pattern_span_ticks(notes: Sequence[Any], *, bar_ticks: int, length_hint: int | None = None) -> int:
    """Whole-bar length that covers every note end (and ``length_hint`` when the host reports one)."""
    end = max((int(n.start_tick) + int(n.length_tick) for n in notes), default=0)
    if length_hint:
        end = max(end, int(length_hint))
    if end <= 0:
        raise ValueError("The source pattern has no notes.")
    return int(math.ceil(end / bar_ticks)) * bar_ticks


def _all_pages(fetch: Any) -> list[Any]:
    items: list[Any] = []
    offset = 0
    while True:
        page = fetch(offset)
        items.extend(page.items)
        if page.next_offset is None or page.next_offset <= offset:
            return items
        offset = page.next_offset


def free_playlist_tracks(fl: Any, count: int, *, start: int | None = None) -> list[int]:
    """The first ``count`` one-based playlist tracks (from ``start``) that hold no clip."""
    used = {int(clip.track) for clip in _all_pages(lambda offset: fl.ops.query_clips(track=-1, offset=offset))}
    tracks: list[int] = []
    track = start if start is not None else 1
    while len(tracks) < count:
        if track > 500:
            raise LookupError("No free playlist track left below 500.")
        if track not in used:
            tracks.append(track)
        track += 1
    return tracks


def _marker_count(fl: Any) -> int:
    text = str(fl.ops.list_markers())
    match = _MARKER_HEADER.match(text.strip())
    return int(match.group(1)) if match else 0


def _label(candidate: Any, channel_name: str | None) -> str:
    if isinstance(candidate, SerumPatch):
        return candidate.name
    if isinstance(candidate, (str, os.PathLike)):
        return Path(candidate).stem
    return channel_name or f"channel {candidate}"


def audition_candidates(
    fl: Any,
    *,
    source_pattern: int,
    mixer_track: int,
    candidates: Sequence[Any],
    accompaniment_patterns: Sequence[int] = (),
    gap_bars: int = 0,
    start_track: int | None = None,
    plugin: str = "Serum 2",
    name_prefix: str = "Audition",
    root: Path | str | None = None,
    delete_markers: bool = True,
) -> dict[str, Any]:
    """Build one back-to-back audition of ``candidates`` and return its time layout.

    ``candidates``: preset names/paths (a new ``plugin`` channel is added and the preset loaded through
    :func:`loading.load_preset`), :class:`SerumPatch` objects (added and loaded through ``patch.load``)
    or existing channel indices (used as they are). Every candidate channel is routed to
    ``mixer_track``; the notes of one-based ``source_pattern`` (all channels, unmuted) are copied into a
    new pattern targeting the candidate channel; the patterns are placed one slot after another on the
    first free playlist track at/after ``start_track`` (each slot is the source span rounded up to whole
    4/4 bars plus ``gap_bars``); ``accompaniment_patterns`` are placed alongside each slot on the next
    free tracks; all time markers are deleted when ``delete_markers``.

    Returns ``{"candidates": [{"index", "label", "channel", "pattern", "start_tick", "end_tick",
    "start_seconds", "end_seconds", "load", "warnings"}], "total_ticks", "total_seconds", "slot_ticks",
    "track", "accompaniment_tracks", "notes": [...]}``. Render once afterwards (the caller's step) and
    measure each ``[start_seconds, end_seconds)`` window.
    """
    if not candidates:
        raise ValueError("Give at least one candidate.")
    if gap_bars < 0:
        raise ValueError("gap_bars must be >= 0.")
    ppq = int(fl.ops.get_ppq())
    bpm = float(fl.ops.get_tempo())
    bar_ticks = ppq * 4
    notes = [n for n in _all_pages(lambda offset: fl.ops.query_notes(pattern=source_pattern, channel=-1, offset=offset))
             if not getattr(n, "muted", False)]
    hint = next((p.length_tick for p in fl.ops.query_patterns() if p.index == source_pattern), None)
    span = pattern_span_ticks(notes, bar_ticks=bar_ticks, length_hint=hint)
    gap = gap_bars * bar_ticks
    slot = span + gap
    tracks = free_playlist_tracks(fl, 1 + len(accompaniment_patterns), start=start_track)
    track, accompaniment_tracks = tracks[0], tracks[1:]
    seconds = 60.0 / bpm / ppq
    layout: list[dict[str, Any]] = []
    for index, candidate in enumerate(candidates):
        start = index * slot
        load: str | None = None
        warnings: list[str] = []
        if isinstance(candidate, bool) or not isinstance(candidate, (int, str, os.PathLike, SerumPatch)):
            raise TypeError(f"Candidate {index} must be a preset path/name, a SerumPatch or a channel index.")
        if isinstance(candidate, int):
            channel = candidate
            label = _label(candidate, str(fl.ops.get_channel_name(channel=channel)))
        else:
            channel = int(fl.ops.add_channel(plugin_name=plugin))
            label = _label(candidate, None)
            fl.ops.set_channel_name(channel=channel, name=f"{name_prefix} {index + 1}: {label}"[:60])
            if isinstance(candidate, SerumPatch):
                load = candidate.load(fl, channel)
            else:
                result = loading.load_preset(fl, channel, candidate, root=root)
                load = str(result)
                warnings = list(getattr(result, "warnings", ()))
        fl.ops.set_channel_fx_route(channel=channel, mixer_track=mixer_track)
        pattern = int(fl.ops.create_pattern())
        fl.ops.set_pattern_name(index=pattern, name=f"{name_prefix} {index + 1}: {label}"[:60])
        fl.ops.add_notes(pattern=pattern, notes=[
            NoteSpec(channel, int(n.key), int(n.start_tick), int(n.length_tick), int(n.velocity)) for n in notes])
        fl.ops.add_pattern_clip(pattern=pattern, track=track, start_tick=start, length_tick=span)
        for acc_track, acc_pattern in zip(accompaniment_tracks, accompaniment_patterns):
            fl.ops.add_pattern_clip(pattern=int(acc_pattern), track=acc_track, start_tick=start, length_tick=span)
        layout.append({
            "index": index, "label": label, "channel": channel, "pattern": pattern,
            "start_tick": start, "end_tick": start + span,
            "start_seconds": round(start * seconds, 4), "end_seconds": round((start + span) * seconds, 4),
            "load": load, "warnings": warnings,
        })
    deleted = 0
    if delete_markers:
        for _ in range(_marker_count(fl)):
            fl.ops.delete_marker(index=0)
            deleted += 1
    total = len(candidates) * slot - gap if candidates else 0
    return {
        "candidates": layout,
        "total_ticks": total,
        "total_seconds": round(total * seconds, 4),
        "slot_ticks": slot,
        "gap_ticks": gap,
        "track": track,
        "accompaniment_tracks": accompaniment_tracks,
        "mixer_track": mixer_track,
        "source_pattern": source_pattern,
        "notes_copied": len(notes),
        "markers_deleted": deleted,
        "tempo_bpm": bpm,
        "ppq": ppq,
        "notes": [
            "render the whole song once (e.g. fl_project_render) and measure each candidate's [start_seconds, end_seconds) window",
            "slots assume 4/4 bars (ppq * 4 ticks) and a constant tempo; tempo automation shifts the seconds",
            "other clips and automation in the project still play and set the render length; mute or remove them for a clean audition",
            "candidate channels, patterns and playlist clips remain in the project; delete them when done",
        ],
    }
