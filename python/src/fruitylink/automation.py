"""Automation curves use beats, unlike note and playlist native tick positions."""

import math
from collections.abc import Sequence

from ._collection import checked_index
from .automation_records import AutomationClipResult, AutomationPointSpec, AutomationTarget, PumpResult
from .errors import ProtocolError
from .models import AutomationPointInfo
from .operations import Operations
from .records import Timebase


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
        """
        if isinstance(value, bool) or not isinstance(value, (int, float)) or not 0 <= value <= 1:
            raise ValueError("Automation point value must be a number within 0..1.")
        if isinstance(tension, bool) or not isinstance(tension, (int, float)) or not -1 <= tension <= 1:
            raise ValueError("Automation point tension must be a number within -1..1.")
        self._ops.set_automation_point(channel=self.channel, index=checked_index(index),
                                       value=float(value), tension=float(tension))

    def set_points(self, points: Sequence[AutomationPointSpec]) -> None:
        """Replace the complete linear envelope, starting at beat zero; times strictly increase."""
        if not 2 <= len(points) <= 4000 or any(not isinstance(p, AutomationPointSpec) for p in points):
            raise ValueError("Provide 2..4000 AutomationPointSpec values.")
        if points[0].time_beats != 0 or any(a.time_beats >= b.time_beats for a, b in zip(points, points[1:])):
            raise ValueError("First point must start at zero and times must strictly increase.")
        self._ops.set_automation_points(channel=self.channel, points=points)

    def add_clip(self, track: int, start_tick: int, length_tick: int) -> int:
        """Place this automation channel on a one-based playlist track; times are ticks."""
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

    def create(self, target: AutomationTarget, track: int, start_tick: int, length_tick: int,
               *, name: str | None = None) -> AutomationClipResult:
        """Create, link, and place automation. Inspect after failure before retrying creation."""
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

    def pump(self, target: AutomationTarget, start_tick: int, length_tick: int, *, track: int,
             depth: float = 0.5, recovery_beats: float = 0.75, beats_per_hit: float = 1, floor: float | None = None,
             ceiling: float = 1.0, tension: float = 0.5, name: str | None = None) -> PumpResult:
        """Create and place a sidechain-style curve: one dip per ``beats_per_hit`` over the clip.

        Each hit drops the value to ``floor`` (default ``ceiling - depth``), recovers to ``ceiling``
        over ``recovery_beats`` (``tension`` shapes that segment), holds, then drops one tick before
        the next hit. Point times are clip-relative beats; the last point sits at the clip end.
        Remember the target's scale: channel volume 1.0 is 12800, so pass ``ceiling`` to preserve a
        lower nominal level. Creation happens before the envelope write, so inspect the created
        channel if the second step fails. Long spans exceed the 4000-point limit; split them.
        """
        _placement(track, start_tick, length_tick)
        ppq = self._ops.get_ppq()
        points = pump_points(Timebase(ppq).beats(length_tick), ppq=ppq, depth=depth, recovery_beats=recovery_beats,
                             beats_per_hit=beats_per_hit, floor=floor, ceiling=ceiling, tension=tension)
        result = self.create(target, track, start_tick, length_tick, name=name)
        self[result.channel].set_points(points)
        return PumpResult(result, len(points))


def pump_points(length_beats: float, *, ppq: int, depth: float = 0.5, recovery_beats: float = 0.75,
                beats_per_hit: float = 1, floor: float | None = None, ceiling: float = 1.0,
                tension: float = 0.5) -> list[AutomationPointSpec]:
    """Pure envelope builder behind Automation.pump(); clip-relative beats, 2..4000 points."""
    numbers = [depth, recovery_beats, beats_per_hit, ceiling, tension, length_beats] + ([] if floor is None else [floor])
    if any(isinstance(v, bool) or not isinstance(v, (int, float)) or not math.isfinite(v) for v in numbers):
        raise ValueError("Pump settings must be finite numbers.")
    if not 0 <= depth <= 1 or not 0 < ceiling <= 1 or not -1 <= tension <= 1 or recovery_beats <= 0 \
            or beats_per_hit <= 0 or type(ppq) is not int or ppq < 1:
        raise ValueError("Require depth 0..1, ceiling 0<c<=1, tension -1..1, positive recovery, hit spacing, PPQ.")
    low = ceiling - depth if floor is None else floor
    if not 0 <= low < ceiling:
        raise ValueError("The dip value (floor or ceiling - depth) must be within 0 <= dip < ceiling.")
    tick = 1 / ppq
    if length_beats <= tick:
        raise ValueError("A pump clip must be longer than one tick.")
    points: list[AutomationPointSpec] = []
    hit = 0
    while hit * beats_per_hit < length_beats - tick:
        start, next_start = hit * beats_per_hit, min((hit + 1) * beats_per_hit, length_beats)
        final = next_start >= length_beats
        hold_until = length_beats if final else next_start - tick
        recovered = min(start + recovery_beats, hold_until)
        points.append(AutomationPointSpec(start, low))
        points.append(AutomationPointSpec(recovered, ceiling, tension))
        if recovered < hold_until:
            points.append(AutomationPointSpec(hold_until, ceiling))
        hit += 1
        if len(points) > 4000:
            raise ValueError("Pump curve exceeds 4000 points; place shorter clips or use a larger beats_per_hit.")
    return points


def _placement(track: int, start: int, length: int) -> None:
    if any(type(value) is not int for value in (track, start, length)):
        raise ValueError("Playlist track and tick values must be integers.")
    if not 1 <= track <= 500 or start < 0 or length <= 0 or start + length > 2**31 - 1:
        raise ValueError("Automation placement requires track 1..500 and a positive, Int32-safe tick span.")
