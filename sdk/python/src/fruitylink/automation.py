"""Automation curves use beats, unlike note and playlist native tick positions.

An automation clip OWNS its target until it is released: the link lives on the automation channel,
FL rewrites the target with the clip's initial value every time playback starts, and deleting the
playlist clip does not unlink anything (FL has no channel delete the bridge can call). See
``fruitylink.automation_links`` for the discovery and guard side of that, and ``release`` below for
the only neutralising move the native layer allows.
"""

import math
from collections.abc import Sequence

from ._collection import checked_index
from .automation_links import index as link_index
from .automation_links import (
    invalidate,
    links_to,
    normalized_value,
    parse_automation_link,
    scan,
    target_of,
    target_text,
)
from .automation_records import (
    AutomationChannelInfo,
    AutomationClipResult,
    AutomationPointSpec,
    AutomationTarget,
    PumpResult,
    ReleaseResult,
)
from .errors import ProtocolError
from .models import AutomationPointInfo
from .operations import Operations
from .playlist import Playlist
from .records import Timebase

MAX_POINTS = 4000


class AutomationCurve:
    def __init__(self, ops: Operations, channel: int) -> None:
        self._ops = ops
        self.channel = checked_index(channel)

    def list(self) -> tuple[AutomationPointInfo, ...]:
        return self._ops.query_automation_points(channel=self.channel)

    def list_text(self) -> str:
        return self._ops.list_automation_points(channel=self.channel)

    def add_beats(self, time: float, value: float, tension: float = 0) -> None:
        self._ops.add_automation_point(channel=self.channel, time_beats=time, value=value, tension=tension)

    def add_ticks(self, tick: int, value: float, tension: float = 0) -> None:
        self.add_beats(Timebase(self._ops.get_ppq()).beats(tick), value, tension)

    def delete(self, index: int) -> None:
        """Delete an interior point. The first and last points are protected endpoints; use set_point()."""
        self._ops.delete_automation_point(channel=self.channel, index=checked_index(index))

    def set_point(self, index: int, value: float, tension: float = 0) -> None:
        """Change one existing point's value 0..1 and tension -1..1 in place, keeping its time.

        Works for the protected first and last points, which delete() refuses. Curves
        that contain non-linear points are refused; replace those with set_points().
        Tension shapes the segment ending at this point: positive = fast start, slow
        finish; negative = slow start, accelerating finish (see AutomationPointSpec).
        """
        if isinstance(value, bool) or not isinstance(value, (int, float)) or not 0 <= value <= 1:
            raise ValueError("Automation point value must be a number within 0..1.")
        if isinstance(tension, bool) or not isinstance(tension, (int, float)) or not -1 <= tension <= 1:
            raise ValueError("Automation point tension must be a number within -1..1.")
        self._ops.set_automation_point(channel=self.channel, index=checked_index(index),
                                       value=float(value), tension=float(tension))

    def set_points(self, points: Sequence[AutomationPointSpec]) -> None:
        """Replace the complete linear envelope, starting at beat zero; times strictly increase.

        Each point's tension belongs to the segment that ends at that point (positive = fast
        start and slow finish, negative = slow start accelerating into the point).
        """
        if not 2 <= len(points) <= MAX_POINTS or any(not isinstance(p, AutomationPointSpec) for p in points):
            raise ValueError("Provide 2..4000 AutomationPointSpec values.")
        if points[0].time_beats != 0 or any(a.time_beats >= b.time_beats for a, b in zip(points, points[1:])):
            raise ValueError("First point must start at zero and times must strictly increase.")
        self._ops.set_automation_points(channel=self.channel, points=points)

    def release(self, value: float | None = None, *, place_clip: bool = True) -> ReleaseResult:
        """Flatten this curve to one held value, so the control it owns stops fighting plain writes.

        FL has no unlink and no channel delete the bridge can call (the native automation verbs are
        create / read / add / delete-point / set-points only), so a linked control cannot be freed:
        it will be rewritten with this clip's initial value every time playback starts. Releasing is
        the next best thing - the envelope becomes two points at the SAME value, so "the value FL
        keeps reapplying" becomes the value you asked for and the clip is audibly inert.

        ``value`` is an automation value 0..1. ``None`` reads the target's CURRENT value instead
        (``fl.mixer[5].volume`` of 6400 -> 0.4), which is what you want after a plain write read back
        but did not sound: it adopts the level you already asked for. That needs the target to be
        decodable and readable - FL's channel pitch and native plugin scales that are not float32
        bits are not (see ``automation_links.normalized_value``), so pass ``value`` for those.

        **A curve is only evaluated through a placed clip.** Live evidence 2026-09-17: with the
        channel's only clip deleted, releasing to 0.8 and then to 0.4 produced the SAME master level
        (-19.68 / -19.46 dBFS) - FL kept reapplying whatever the link last held and never looked at
        the flattened envelope. After one clip was placed for the same channel, 0.4 measured -32.18
        dBFS and 0.8 measured -19.60, the 12.58 dB the fader model predicts. So this places a clip at
        tick 0 on ``fl.playlist.first_free_track`` when the channel has none (as long as the previous
        envelope's span, at least one bar), and the returned ``ReleaseResult`` says whether it did.
        ``place_clip=False`` skips that check and edits nothing but the envelope.

        Returns a ``ReleaseResult``: the released value as a float, plus the placement. The two
        points keep the previous envelope's span, and the link, the channel and any existing clips
        stay exactly where they were.
        """
        if value is None:
            target = target_of(self._ops, self.channel)
            if target is None:
                raise LookupError(f"Channel {self.channel} is not a linked automation clip channel; "
                                  "pass value= explicitly (fl.automation.refresh() rescans the rack).")
            current = normalized_value(self._ops, target)
            if current is None:
                raise LookupError(f"The current value of {target_text(target)} cannot be read as an "
                                  "automation value; pass value= explicitly.")
            value = current
        if isinstance(value, bool) or not isinstance(value, (int, float)) or not 0 <= value <= 1:
            raise ValueError("Automation point value must be a number within 0..1.")
        span = max((point.time_beats for point in self.list()), default=0.0)
        held = float(value)
        self.set_points([AutomationPointSpec(0, held), AutomationPointSpec(span if span > 0 else 1.0, held)])
        return ReleaseResult(held) if not place_clip else self._ensure_placement(held, span)

    def _ensure_placement(self, held: float, span_beats: float) -> ReleaseResult:
        """Place one clip for this channel when the playlist has none, so FL evaluates the curve."""
        playlist = Playlist(self._ops)
        if any(clip.source_kind == "channel" and clip.source_index == self.channel
               for clip in playlist.clips.list()):
            return ReleaseResult(held)
        ppq = self._ops.get_ppq()
        length_tick = max(int(round(span_beats * ppq)), 4 * ppq)   # never shorter than one bar
        track = playlist.first_free_track(0, length_tick)
        clip_index = self.add_clip(track, 0, length_tick)
        invalidate(self._ops)   # the link index caches placements
        return ReleaseResult(held, placed_clip=True, track=track, start_tick=0, length_tick=length_tick,
                             clip_index=clip_index)

    def add_clip(self, track: int, start_tick: int, length_tick: int) -> int:
        """Place this automation channel on a one-based playlist track; times are ticks.

        Every placement plays the same envelope from its own start: automation clips have
        no loop or offset flag, so a pattern that differs per bar needs one envelope that
        spans the range (``Automation.duck`` / ``Automation.tile``), not several placements.
        """
        _placement(track, start_tick, length_tick)
        index = self._ops.add_automation_clip(channel=self.channel, track=track,
                                               start_tick=start_tick, length_tick=length_tick)
        if type(index) is not int or index < 0:
            raise ProtocolError("Invalid automation clip result; inspect playlist state before retrying placement.")
        return index


class Automation:
    def __init__(self, ops: Operations) -> None:
        self._ops = ops

    def __getitem__(self, channel: int) -> AutomationCurve:
        return AutomationCurve(self._ops, channel)

    def list(self, *, with_points: bool = True) -> tuple[AutomationChannelInfo, ...]:
        """Inventory of every linked automation clip channel: target, event ids, point count, placements.

        Reads the channel list, asks the host to describe each channel's generator (automation
        clips report ``automation clip -> <targets>``), decodes any ``event 0x...`` ids with
        ``AutomationTarget.from_event_id``, counts the envelope points (skipped with
        ``with_points=False`` to save one request per clip) and pairs the channel with the
        playlist clips whose source is that channel. Unlinked automation clips (no target yet)
        are not automation clips to the host and are not listed.

        Always a fresh read. ``links_to`` / ``targets_of`` and the write guard use a cached copy of
        the same scan instead (``refresh()`` rebuilds it).
        """
        return scan(self._ops, with_points=with_points)

    def links_to(self, target: AutomationTarget) -> tuple[AutomationChannelInfo, ...]:
        """Every automation clip channel whose decoded target is ``target`` - what OWNS that control.

        The answer a plain write needs: an automation channel keeps writing its clip's initial value
        to its target on every play even after the playlist clip is deleted, so a control that
        appears here cannot be set by hand until ``release`` flattens the curve. Served from the
        cached rack scan (``refresh()`` rebuilds it, ``create``/``pump``/``duck``/``tile``
        invalidate it), so ``point_count`` is -1 - call ``list()`` for envelope sizes.
        """
        return links_to(self._ops, target)

    def targets_of(self, kind: str, index: int, slot: int = -1,
                   parameter: int = -1) -> tuple[AutomationChannelInfo, ...]:
        """``links_to`` for loose fields: ``targets_of("mixer_volume", 5)``, ``("plugin_parameter", 1, 0, 556)``."""
        return self.links_to(AutomationTarget(kind, index, slot, parameter))

    def refresh(self) -> tuple[AutomationChannelInfo, ...]:
        """Rescan the rack for automation links and return the new index (``point_count`` is -1).

        The cache is per connection and is only invalidated by the SDK's own ``create`` /
        ``pump`` / ``duck`` / ``tile``; call this after automation was created, retargeted or
        renamed in FL's GUI, or by another client, so the write guard sees it.
        """
        invalidate(self._ops)
        return link_index(self._ops)

    def release(self, channel: int, value: float | None = None, *,
                place_clip: bool = True) -> ReleaseResult:
        """``fl.automation[channel].release(value)``: flatten a curve so its pinned target sits where
        you want it. FL offers no unlink, so this is how a linked control is neutralised.

        Places one clip for the channel when the playlist has none, because FL only evaluates a curve
        through a placed clip (``place_clip=False`` to skip that); the ``ReleaseResult`` reports it.
        """
        return self[channel].release(value, place_clip=place_clip)

    def describe(self, *, with_points: bool = True) -> str:
        """One line per automation clip channel, from ``list()``: target, points and placements."""
        ppq = self._ops.get_ppq()
        lines = []
        for item in self.list(with_points=with_points):
            decoded = ", ".join(target_text(target) for target in item.targets) or "-"
            places = ", ".join(f"track {c.track} @ {c.start_tick} ({_bar(c.start_tick, ppq)}) len {c.length_tick}"
                               + (" muted" if c.muted else "") for c in item.clips) or "not placed"
            points = "?" if item.point_count < 0 else str(item.point_count)
            lines.append(f"[{item.channel}] {item.name}: {item.targets_text} -> {decoded}; {points} points; {places}")
        return f"{len(lines)} automation clips" + ("\n" + "\n".join(lines) if lines else "")

    def create(self, target: AutomationTarget, track: int, start_tick: int, length_tick: int,
               *, name: str | None = None) -> AutomationClipResult:
        """Create, link, and place automation. Inspect after failure before retrying creation.

        The new channel OWNS ``target`` from now on: FL writes this clip's initial value into the
        control every time playback starts, and deleting the clip does not unlink it, so plain
        writes to that control stop being audible (they warn with ``AutomationLinkedWarning``).
        ``AutomationClipResult.link_notice`` says so, ``release`` is the way out.
        """
        if not isinstance(target, AutomationTarget):
            raise TypeError("target must be an AutomationTarget.")
        if name is not None and not isinstance(name, str):
            raise TypeError("name must be a string or None.")
        _placement(track, start_tick, length_tick)
        try:
            result = self._ops.create_automation_clip(target=target, track=track, start_tick=start_tick,
                                                       length_tick=length_tick, name=name)
            if result.channel < 0 or result.clip_index < 0:
                raise ProtocolError("Negative automation channel or clip index.")
            return result
        except ProtocolError as error:
            raise ProtocolError("Invalid automation creation result; inspect channels and playlist before retrying.") from error
        finally:
            invalidate(self._ops)   # a new link must be visible to the write guard

    def pump(self, target: AutomationTarget, start_tick: int, length_tick: int, *, track: int,
             depth: float = 0.5, recovery_beats: float = 0.75, beats_per_hit: float = 1, floor: float | None = None,
             ceiling: float = 1.0, tension: float = 0.5, name: str | None = None) -> PumpResult:
        """Create and place a sidechain-style curve: one dip per ``beats_per_hit`` over the clip.

        Each hit drops the value to ``floor`` (default ``ceiling - depth``), recovers to ``ceiling``
        over ``recovery_beats`` (``tension`` shapes that segment: the default +0.5 recovers fast and
        eases in; negative would start slowly and accelerate), holds, then drops one tick before the
        next hit. Point times are clip-relative beats; the last point sits at the clip end.
        Remember the target's scale: channel volume 1.0 is 12800, so pass ``ceiling`` to preserve a
        lower nominal level. Creation happens before the envelope write, so inspect the created
        channel if the second step fails. Long spans exceed the 4000-point limit; split them.
        For an irregular hit pattern (alternating kick bars, fills) use ``duck`` with the actual hits.
        The created channel OWNS the target from now on (``PumpResult.link_notice``): FL reapplies the
        clip's initial value on every play and deleting the clip does not unlink it, so plain writes to
        that control stay inaudible until ``fl.automation.release(result.channel)``.
        """
        _placement(track, start_tick, length_tick)
        ppq = self._ops.get_ppq()
        points = pump_points(Timebase(ppq).beats(length_tick), ppq=ppq, depth=depth, recovery_beats=recovery_beats,
                             beats_per_hit=beats_per_hit, floor=floor, ceiling=ceiling, tension=tension)
        result = self.create(target, track, start_tick, length_tick, name=name)
        self[result.channel].set_points(points)
        return PumpResult(result, len(points))

    def duck(self, target: AutomationTarget, hits_ticks: Sequence[int], start_tick: int, length_tick: int, *,
             track: int, depth: float = 0.5, recovery_beats: float = 0.25, floor: float | None = None,
             ceiling: float = 1.0, tension: float = 0.5, name: str | None = None) -> PumpResult:
        """Create and place ONE envelope that dips at every hit in ``hits_ticks`` (absolute ticks).

        This is ``pump`` for an irregular grid: pair it with ``fl.playlist.onsets(channel, start, end)``
        to follow a kick channel through the arrangement, including alternating bars and fills.
        Hits outside ``[start_tick, start_tick + length_tick)`` are ignored; duplicates collapse. Each
        hit holds ``ceiling`` until one tick before it, drops to ``floor`` (default ``ceiling - depth``)
        at the hit and recovers over ``recovery_beats`` (``tension`` +0.5 = fast start, slow finish).
        Automation clips cannot loop or offset, so one long clip is the only way to follow a pattern
        that changes per bar; spans needing more than 4000 points must be split into several clips.
        The created channel OWNS the target until it is released (``PumpResult.link_notice``): FL
        reapplies the clip's initial value on every play, even after the clip is deleted.
        """
        _placement(track, start_tick, length_tick)
        if any(type(hit) is not int for hit in hits_ticks):
            raise ValueError("Hit positions must be integer ticks.")
        ppq = self._ops.get_ppq()
        timebase = Timebase(ppq)
        hits = [timebase.beats(hit - start_tick) for hit in hits_ticks if start_tick <= hit < start_tick + length_tick]
        points = duck_points(hits, timebase.beats(length_tick), ppq=ppq, depth=depth, recovery_beats=recovery_beats,
                             floor=floor, ceiling=ceiling, tension=tension)
        result = self.create(target, track, start_tick, length_tick, name=name)
        self[result.channel].set_points(points)
        return PumpResult(result, len(points))

    def tile(self, target: AutomationTarget, shape: Sequence[AutomationPointSpec], start_tick: int, length_tick: int,
             *, track: int, period_beats: float, offsets_beats: Sequence[float] = (0,),
             name: str | None = None) -> PumpResult:
        """Create and place ONE envelope that repeats ``shape`` every ``period_beats`` across the clip.

        ``shape`` is a clip-relative envelope starting at beat 0 whose span fits in one period; repeat
        ``i`` starts at ``i * period_beats + offsets_beats[i % len(offsets_beats)]`` (``[0, 0.5]``
        alternates a half-beat shift on even and odd bars). The shape's last value holds until the
        next repeat. See ``tile_points`` for the pure builder and ``duck`` for hit-driven dips.
        The created channel OWNS the target until it is released (``PumpResult.link_notice``): FL
        reapplies the clip's initial value on every play, even after the clip is deleted.
        """
        _placement(track, start_tick, length_tick)
        ppq = self._ops.get_ppq()
        points = tile_points(shape, period_beats, Timebase(ppq).beats(length_tick), ppq=ppq, offsets_beats=offsets_beats)
        result = self.create(target, track, start_tick, length_tick, name=name)
        self[result.channel].set_points(points)
        return PumpResult(result, len(points))


def pump_points(length_beats: float, *, ppq: int, depth: float = 0.5, recovery_beats: float = 0.75,
                beats_per_hit: float = 1, floor: float | None = None, ceiling: float = 1.0,
                tension: float = 0.5) -> list[AutomationPointSpec]:
    """Pure envelope builder behind Automation.pump(); clip-relative beats, 2..4000 points."""
    _finite([beats_per_hit, length_beats])
    if isinstance(beats_per_hit, bool) or beats_per_hit <= 0:
        raise ValueError("Require a positive hit spacing.")
    _duck_settings(depth, recovery_beats, floor, ceiling, tension, ppq)
    tick = 1 / ppq
    hits: list[float] = []
    while len(hits) * beats_per_hit < length_beats - tick:
        hits.append(len(hits) * beats_per_hit)
        if len(hits) > MAX_POINTS:
            raise ValueError("Pump curve exceeds 4000 points; place shorter clips or use a larger beats_per_hit.")
    return duck_points(hits, length_beats, ppq=ppq, depth=depth, recovery_beats=recovery_beats, floor=floor,
                       ceiling=ceiling, tension=tension)


def duck_points(hits_beats: Sequence[float], length_beats: float, *, ppq: int, depth: float = 0.5,
                recovery_beats: float = 0.25, floor: float | None = None, ceiling: float = 1.0,
                tension: float = 0.5) -> list[AutomationPointSpec]:
    """Pure envelope builder behind Automation.duck(): a dip at each clip-relative hit, 2..4000 points.

    The curve sits at ``ceiling``, holds until one tick before each hit, drops to the dip value at
    the hit, recovers to ``ceiling`` over ``recovery_beats`` (cut short when the next hit is closer)
    and ends at ``length_beats``. Hits outside ``[0, length_beats - 1 tick]`` are ignored.
    """
    _finite(list(hits_beats) + [length_beats])
    low = _duck_settings(depth, recovery_beats, floor, ceiling, tension, ppq)
    tick = 1 / ppq
    if length_beats <= tick:
        raise ValueError("A duck clip must be longer than one tick.")
    hits = sorted({float(hit) for hit in hits_beats if 0 <= hit < length_beats - tick})
    points: list[AutomationPointSpec] = []

    def push(time: float, value: float, shape: float = 0) -> None:
        if not points or time > points[-1].time_beats:
            points.append(AutomationPointSpec(time, value, shape))

    if not hits or hits[0] > 0:
        push(0, ceiling)
    for position, hit in enumerate(hits):
        hold_until = length_beats if position + 1 == len(hits) else hits[position + 1] - tick
        if hit >= tick:
            push(hit - tick, ceiling)
        push(hit, low)
        recovered = min(hit + recovery_beats, hold_until)
        push(recovered, ceiling, tension)
        push(hold_until, ceiling)
        if len(points) > MAX_POINTS:
            raise ValueError("Envelope exceeds 4000 points; split the span into several clips.")
    push(length_beats, ceiling)
    return points


def tile_points(shape: Sequence[AutomationPointSpec], period_beats: float, length_beats: float, *, ppq: int,
                offsets_beats: Sequence[float] = (0,)) -> list[AutomationPointSpec]:
    """Pure envelope builder behind Automation.tile(): ``shape`` repeated every ``period_beats``.

    ``shape`` starts at beat 0 with strictly increasing times that fit inside one period. Repeat
    ``i`` is shifted by ``offsets_beats[i % len(offsets_beats)]`` (each offset must keep the shape
    inside its period). Between repeats the last value holds; before an offset first repeat the
    shape's first value holds from beat 0; the curve ends at ``length_beats``. Points beyond the
    clip end are dropped (a partial last repeat). At most 4000 points.
    """
    if type(ppq) is not int or ppq < 1:
        raise ValueError("PPQ must be a positive integer.")
    if len(shape) < 2 or any(not isinstance(point, AutomationPointSpec) for point in shape):
        raise ValueError("A tile shape needs at least two AutomationPointSpec values.")
    if shape[0].time_beats != 0 or any(a.time_beats >= b.time_beats for a, b in zip(shape, shape[1:])):
        raise ValueError("The shape must start at beat zero with strictly increasing times.")
    _finite([period_beats, length_beats] + list(offsets_beats))
    if not offsets_beats or period_beats <= 0 or length_beats <= 1 / ppq:
        raise ValueError("Require at least one offset, a positive period and a clip longer than one tick.")
    span = shape[-1].time_beats
    if any(offset < 0 or offset + span > period_beats for offset in offsets_beats):
        raise ValueError("Every offset must keep the whole shape inside one period (0 <= offset <= period - span).")
    tick = 1 / ppq
    points: list[AutomationPointSpec] = []

    def push(time: float, value: float, tension: float = 0) -> None:
        if time <= length_beats and (not points or time > points[-1].time_beats):
            points.append(AutomationPointSpec(time, value, tension))

    repeat = 0
    while repeat * period_beats < length_beats:
        base = repeat * period_beats + offsets_beats[repeat % len(offsets_beats)]
        if repeat == 0 and base > 0:
            push(0, shape[0].value)
        if points:
            push(base - tick, points[-1].value)
        for point in shape:
            push(base + point.time_beats, point.value, point.tension)
        repeat += 1
        if len(points) > MAX_POINTS:
            raise ValueError("Tiled curve exceeds 4000 points; split the span or lengthen the period.")
    push(length_beats, points[-1].value)
    return points


def _finite(values: Sequence[float]) -> None:
    if any(isinstance(v, bool) or not isinstance(v, (int, float)) or not math.isfinite(v) for v in values):
        raise ValueError("Envelope settings must be finite numbers.")


def _duck_settings(depth: float, recovery_beats: float, floor: float | None, ceiling: float, tension: float,
                   ppq: int) -> float:
    _finite([depth, recovery_beats, ceiling, tension] + ([] if floor is None else [floor]))
    if not 0 <= depth <= 1 or not 0 < ceiling <= 1 or not -1 <= tension <= 1 or recovery_beats <= 0 \
            or type(ppq) is not int or ppq < 1:
        raise ValueError("Require depth 0..1, ceiling 0<c<=1, tension -1..1, positive recovery, hit spacing, PPQ.")
    low = ceiling - depth if floor is None else floor
    if not 0 <= low < ceiling:
        raise ValueError("The dip value (floor or ceiling - depth) must be within 0 <= dip < ceiling.")
    return low


def _bar(tick: int, ppq: int) -> str:
    return f"bar {tick // (4 * ppq) + 1}" + ("" if tick % (4 * ppq) == 0 else f" +{tick % (4 * ppq)}")


def _placement(track: int, start: int, length: int) -> None:
    if any(type(value) is not int for value in (track, start, length)):
        raise ValueError("Playlist track and tick values must be integers.")
    if not 1 <= track <= 500 or start < 0 or length <= 0 or start + length > 2**31 - 1:
        raise ValueError("Automation placement requires track 1..500 and a positive, Int32-safe tick span.")


__all__ = ["Automation", "AutomationCurve", "duck_points", "parse_automation_link", "pump_points",
           "target_text", "tile_points"]
