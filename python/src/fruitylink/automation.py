"""Automation curves use beats, unlike note and playlist native tick positions."""

from collections.abc import Sequence

from ._collection import checked_index
from .automation_records import AutomationClipResult, AutomationPointSpec, AutomationTarget
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
        self._ops.delete_automation_point(channel=self.channel, index=checked_index(index))

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


def _placement(track: int, start: int, length: int) -> None:
    if any(type(value) is not int for value in (track, start, length)):
        raise ValueError("Playlist track and tick values must be integers.")
    if not 1 <= track <= 500 or start < 0 or length <= 0 or start + length > 2**31 - 1:
        raise ValueError("Automation placement requires track 1..500 and a positive, Int32-safe tick span.")
