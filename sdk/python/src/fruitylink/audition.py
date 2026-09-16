"""Trim the live playlist to one time range so a full-song render covers only that section.

FL's command-line exporter has no range switch (its documented options are ``/R``, ``/E``,
``/F``, ``/O`` and ``/D``) and always renders from tick 0 to the later of the last clip end
and the last time marker. ``isolate_range`` therefore rewrites the current arrangement in
place: clips outside the range are deleted, clips crossing the range end are shortened,
clips crossing the range start are cut (only when allowed), the survivors are shifted to
tick 0, every time marker is removed and any loop selection is cleared. Rendering the
result afterwards yields exactly that section (FL stops at the last clip end once the
markers are gone, so tails are cut) unless ``tail_beats`` places an End marker past it.

Apply it to a disposable copy: the edit is not reversible from Python. ``StudioSession``
exposes ``render_range`` which runs the transform and then its usual snapshot-and-render.
"""

from __future__ import annotations

import math
from dataclasses import asdict, dataclass
from typing import TYPE_CHECKING

from .models import ClipInfo
from .records import ClipMove, ClipResize

if TYPE_CHECKING:
    from .studio import Studio

_MAX_CUTS = 10_000


@dataclass(frozen=True)
class RangeIsolation:
    """What ``isolate_range`` changed: tick bounds plus clip and marker counts."""

    start_tick: int
    length_tick: int
    kept_clips: int
    deleted_clips: int
    cut_clips: int
    deleted_markers: int
    tail_ticks: int = 0

    def to_dict(self) -> dict[str, int]:
        return asdict(self)


def _tick(value: object, name: str, *, minimum: int) -> int:
    if type(value) is not int or value < minimum:
        raise ValueError(f"{name} must be an integer of at least {minimum}.")
    return value


def _tail(value: object) -> float:
    if isinstance(value, bool) or not isinstance(value, (int, float)) or not math.isfinite(value) or value < 0:
        raise ValueError("tail_beats must be a finite number of beats >= 0.")
    return float(value)


def _crosses_start(clip: ClipInfo, start: int) -> bool:
    return clip.start_tick < start < clip.start_tick + clip.length_tick


def _intersects(clip: ClipInfo, start: int, end: int) -> bool:
    return clip.start_tick < end and clip.start_tick + clip.length_tick > start


def isolate_range(fl: Studio, start_tick: int, length_tick: int, *, cut_clips: bool = False,
                  tail_beats: float = 0) -> RangeIsolation:
    """Keep only ``[start_tick, start_tick + length_tick)`` of the current arrangement, moved to tick 0.

    Clips that cross the range end are shortened; their source start is unchanged so they play
    exactly as before. Clips that cross the range start need a cut: with ``cut_clips=False``
    (the default) they raise ``ValueError`` before anything changes, because the SDK's slice
    restarts a *pattern* clip's second half from the pattern's first beat (audio and
    automation clips keep their source offset). Pass ``cut_clips=True`` to accept that, or
    choose a start on a clip boundary. Nothing is written when no clip lies in the range.

    Only the current arrangement is edited; markers and the loop selection are removed
    because FL extends renders to the last marker and may honour a saved selection. That
    also means a render stops at the last surviving clip end (live: bars 49-64 gave exactly
    16 bars, reverb and release tails cut). ``tail_beats`` > 0 places an ``"End"`` marker that
    many beats past the range so the render keeps ringing that long; the range itself is
    unchanged and ``RangeIsolation.tail_ticks`` reports the added ticks.
    """
    start = _tick(start_tick, "start_tick", minimum=0)
    length = _tick(length_tick, "length_tick", minimum=1)
    if type(cut_clips) is not bool:
        raise TypeError("cut_clips must be a bool.")
    tail = _tail(tail_beats)
    end = start + length

    clips = fl.clips.list()
    inside = [clip for clip in clips if _intersects(clip, start, end)]
    if not inside:
        raise ValueError(f"No clip lies within ticks {start}..{end}; nothing would render.")
    crossing = [clip.index for clip in inside if _crosses_start(clip, start)]
    if crossing and not cut_clips:
        raise ValueError(
            f"Clips {crossing} begin before tick {start} and would be cut; a cut pattern clip restarts "
            "from its first beat. Start the range on a clip boundary or pass cut_clips=True.")

    # 1. Shorten clips that run past the range end. Resizing never reorders the collection, so
    #    the indices of one listing stay valid for the whole bulk call.
    resizes = [ClipResize(clip.index, end - clip.start_tick)
               for clip in inside if clip.start_tick + clip.length_tick > end]
    if resizes:
        fl.clips.resize(resizes)

    # 2. Cut clips that begin before the range. A slice inserts a new clip, which may renumber the
    #    others, so re-list after each cut and locate the next one by position, never by a stale index.
    cuts = 0
    while crossing:
        if cuts >= _MAX_CUTS:
            raise RuntimeError("Clip cutting did not converge; the playlist listing keeps reporting crossing clips.")
        clip = next((clip for clip in fl.clips.list() if _crosses_start(clip, start)), None)
        if clip is None:
            break
        fl.clips[clip.index].slice_ticks(start)
        cuts += 1

    # 3. Delete everything outside the range (the bridge deletes highest index first).
    clips = fl.clips.list()
    outside = [clip.index for clip in clips if not _intersects(clip, start, end)]
    if outside:
        fl.clips.delete(outside)

    # 4. Shift the survivors to tick 0 in one bulk move (position pokes do not reorder either).
    kept = fl.clips.list()
    if start > 0:
        fl.clips.move([ClipMove(clip.index, clip.start_tick - start, clip.track) for clip in kept])

    # 5. Markers extend the render to the last one and are no longer at meaningful positions.
    markers = fl.transport.markers()
    for marker in reversed(markers):
        fl.transport.delete_marker(marker.index)
    fl.transport.clear_loop()

    # 6. A tail: FL renders up to the last marker, so an End marker past the range keeps the tails.
    tail_ticks = 0
    if tail > 0:
        tail_ticks = int(fl.timebase.ticks(tail))
        if tail_ticks > 0:
            fl.transport.set_song_end(tick=length + tail_ticks)

    return RangeIsolation(start_tick=start, length_tick=length, kept_clips=len(kept),
                          deleted_clips=len(outside), cut_clips=cuts, deleted_markers=len(markers),
                          tail_ticks=tail_ticks)


def isolate_bars(fl: Studio, start_bar: int, end_bar: int, *, cut_clips: bool = False,
                 beats_per_bar: int = 4, tail_beats: float = 0) -> RangeIsolation:
    """``isolate_range`` for an inclusive one-based bar span: bars 49..64 keep sixteen bars.

    Bars are converted with the project PPQ and ``beats_per_bar`` (FL's default meter is 4/4).
    ``tail_beats`` keeps the render ringing that many beats past ``end_bar`` (see ``isolate_range``).
    """
    if type(start_bar) is not int or type(end_bar) is not int or start_bar < 1 or end_bar < start_bar:
        raise ValueError("start_bar and end_bar must be one-based integers with end_bar >= start_bar.")
    _tail(tail_beats)
    timebase = fl.timebase
    start = timebase.bar_start(start_bar, beats_per_bar=beats_per_bar)
    end = timebase.bar_start(end_bar + 1, beats_per_bar=beats_per_bar)
    return isolate_range(fl, start, end - start, cut_clips=cut_clips, tail_beats=tail_beats)
