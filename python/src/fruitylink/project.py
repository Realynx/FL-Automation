"""Project lifecycle and transport use the same shared operations as any client."""

import re
import time
from collections.abc import Callable
from dataclasses import dataclass
from typing import TypeVar

from ._collection import checked_index
from .errors import ProtocolError
from .models import ProjectInfo
from .operations import Operations
from .records import Timebase

T = TypeVar("T")


@dataclass(frozen=True)
class Marker:
    index: int
    name: str
    tick: int


@dataclass(frozen=True)
class SeekResult:
    """Outcome of a settled seek: the tick asked for, the playhead tick the host reported once two
    consecutive reads agreed (None when the state text carried no position), whether it settled
    within the budget, and how many position reads that took. Live evidence (Parking Lot Moon
    2026-09-14): a stopped seek reads 14-20 ticks late for about 100 ms while FL runs its
    automation pass, and plugin displays show the previous position's value until that pass
    finishes (about 300 ms), so read displays after ``settled`` with ``Transport.read_at``."""

    requested_tick: int
    position_tick: int | None
    settled: bool
    reads: int


_MARKER_LINE = re.compile(r"^(?P<name>.*) @ tick (?P<tick>\d+)(?: \(bar \d+\))?$")
_POSITION = re.compile(r"\(tick (?P<tick>\d+)\)")


def _parse_markers(text: str) -> tuple[Marker, ...]:
    lines = text.splitlines()
    if not lines or not re.match(r"^\d+ markers:$", lines[0]):
        return ()
    markers: list[Marker] = []
    for line in lines[1:]:
        match = _MARKER_LINE.match(line)
        if match is None:
            continue
        name = match.group("name")
        markers.append(Marker(len(markers), "" if name == "(marker)" else name, int(match.group("tick"))))
    return tuple(markers)


def _resolve_marker(markers: tuple[Marker, ...], name: str) -> int:
    matches = [marker.index for marker in markers if marker.name == name]
    if len(matches) != 1:
        raise LookupError(f"Expected one marker named {name!r}; found {len(matches)}.")
    return matches[0]


class Project:
    def __init__(self, ops: Operations) -> None:
        self._ops = ops

    @property
    def info(self) -> ProjectInfo:
        return self._ops.query_project()

    def info_text(self) -> str:
        return self._ops.get_project_info()

    def new(self) -> None:
        self._ops.new_project()

    def open(self, path: str) -> None:
        self._ops.open_project(path=path)

    def save(self, path: str) -> None:
        self._ops.save_project(path=path)

    def save_as(self, path: str) -> None:
        self._ops.save_project_as(path=path)

    def save_copy(self, path: str) -> None:
        self._ops.save_copy(path=path)

    def save_new_version(self) -> None:
        self._ops.save_new_version()

    def recent_text(self) -> str:
        return self._ops.list_recent_projects()

    def open_export_dialog(self, format_index: int = 0) -> None:
        """Open the interactive export dialog; this does not verify a completed render."""
        self._ops.open_export_dialog(format_index=format_index)


class Transport:
    def __init__(self, ops: Operations) -> None:
        self._ops = ops

    @property
    def tempo(self) -> float:
        return self._ops.get_tempo()

    @tempo.setter
    def tempo(self, bpm: float) -> None:
        self._ops.set_tempo(bpm=bpm)

    @property
    def song_mode(self) -> bool:
        return self._ops.get_song_mode()

    @song_mode.setter
    def song_mode(self, value: bool) -> None:
        self._ops.set_song_mode(song=value)

    def play(self) -> None:
        self._ops.transport_play()

    def stop(self) -> None:
        self._ops.transport_stop()

    def toggle_record(self) -> None:
        self._ops.transport_toggle_record()

    @property
    def position_tick(self) -> int | None:
        """The playhead tick from the host's state text, or None when it reports no position."""
        match = _POSITION.search(self.state_text())
        return int(match.group("tick")) if match else None

    def seek_ticks(self, tick: int, *, settle: bool = False, attempts: int = 20, delay: float = 0.05,
                   sleep: Callable[[float], None] = time.sleep) -> SeekResult | None:
        """Move the playhead to an absolute tick. ``settle=True`` waits until the reported position
        stops moving (see ``seek_settled``) and returns the ``SeekResult``; otherwise returns None."""
        if type(tick) is not int or tick < 0:
            raise ValueError("tick must be a nonnegative integer.")
        if settle:
            return self.seek_settled(tick, attempts=attempts, delay=delay, sleep=sleep)
        self._ops.seek(tick=tick)
        return None

    def seek_beats(self, beats: float, *, settle: bool = False) -> SeekResult | None:
        return self.seek_ticks(Timebase(self._ops.get_ppq()).ticks(beats), settle=settle)

    def seek_settled(self, tick: int, *, attempts: int = 20, delay: float = 0.05,
                     sleep: Callable[[float], None] = time.sleep) -> SeekResult:
        """Seek, then poll the playhead until two consecutive reads agree.

        Why: after ``seek`` the stopped host still advances the reported position for roughly
        100 ms (14-20 ticks at 100 BPM) while it runs an automation pass, so a position read in
        the same request lands late and a short envelope (a 24-tick duck) is never sampled at
        the requested tick. Polls up to ``attempts`` times, ``delay`` seconds apart; ``settled``
        is False when the position was still moving at the end of the budget. Expect the settled
        position slightly past the request; verify short envelopes from the point list instead.
        """
        if type(tick) is not int or tick < 0:
            raise ValueError("tick must be a nonnegative integer.")
        checked_index(attempts, minimum=1, maximum=200)
        if isinstance(delay, bool) or not isinstance(delay, (int, float)) or not 0 <= delay <= 5:
            raise ValueError("Poll delay must be 0..5 seconds.")
        self._ops.seek(tick=tick)
        previous: int | None = None
        position: int | None = None
        for read in range(1, attempts + 1):
            if delay > 0:
                sleep(delay)
            position = self.position_tick
            if position is not None and position == previous:
                return SeekResult(tick, position, True, read)
            previous = position
        return SeekResult(tick, position, False, attempts)

    def read_at(self, tick: int, read: Callable[[], T], *, settle: float = 0.3, attempts: int = 6,
                delay: float = 0.1, sleep: Callable[[float], None] = time.sleep) -> T:
        """Seek to ``tick``, wait for the host to apply automation there, then return a stable reading.

        ``read`` is any zero-argument reader (``lambda: fl.mixer[6].volume``,
        ``fl.mixer[1].effects[0].parameters.read`` bound to an index, ...). The seek settles first
        (``seek_settled``), then ``settle`` seconds pass (live evidence: a display read right after
        a seek shows the PREVIOUS position's automated value; 300 ms was always enough), then
        ``read`` is called until two consecutive values compare equal, at most ``attempts`` times
        ``delay`` apart. The last value is returned even when no two agreed.
        """
        if isinstance(settle, bool) or not isinstance(settle, (int, float)) or not 0 <= settle <= 10:
            raise ValueError("settle must be 0..10 seconds.")
        checked_index(attempts, minimum=1, maximum=100)
        if isinstance(delay, bool) or not isinstance(delay, (int, float)) or not 0 <= delay <= 5:
            raise ValueError("Readback delay must be 0..5 seconds.")
        self.seek_settled(tick, sleep=sleep)
        if settle > 0:
            sleep(settle)
        value = read()
        for _ in range(attempts - 1):
            if delay > 0:
                sleep(delay)
            again = read()
            if again == value:
                return again
            value = again
        return value

    def loop_ticks(self, start: int, end: int) -> None:
        self._ops.set_loop_region(start_tick=start, end_tick=end)

    def loop_beats(self, start: float, end: float) -> None:
        timebase = Timebase(self._ops.get_ppq())
        self.loop_ticks(timebase.ticks(start), timebase.ticks(end))

    def clear_loop(self) -> None:
        self.loop_ticks(0, -1)

    def state_text(self) -> str:
        return self._ops.get_song_state()

    def markers_text(self) -> str:
        return self._ops.list_markers()

    def markers(self) -> tuple[Marker, ...]:
        """Parse the native marker listing into (index, name, tick) records in host order."""
        return _parse_markers(self.markers_text())

    def delete_marker(self, marker: int | str) -> int:
        """Delete one song time marker by index or by exact, unique name; returns the deleted index.

        FL extends renders and the play range to the last marker, so delete trailing
        markers to shorten an audition. Resolution and deletion are separate steps:
        a missing or ambiguous name causes no write.
        """
        if isinstance(marker, bool) or not isinstance(marker, (int, str)):
            raise TypeError("Identify a marker by its zero-based index or its exact name.")
        if marker == "":
            raise ValueError("Unnamed markers must be deleted by index.")
        index = checked_index(marker) if isinstance(marker, int) else _resolve_marker(self.markers(), marker)
        self._ops.delete_marker(index=index)
        return index

    def add_marker_ticks(self, tick: int, name: str) -> None:
        self._ops.add_marker(tick=tick, name=name)

    def add_marker_beats(self, beats: float, name: str) -> None:
        self.add_marker_ticks(Timebase(self._ops.get_ppq()).ticks(beats), name)

    def set_song_end(self, bar: int | None = None, *, tick: int | None = None, name: str = "End",
                     beats_per_bar: int = 4) -> Marker:
        """Place or move a single named end marker at a one-based bar start or an absolute tick.

        FL extends the song length, play range and full-song renders to the last time marker,
        so a marker past the final note adds silence or room for tails. Existing markers with
        the same name are deleted first, so the name stays unique. Returns the marker as the
        host lists it after the write.
        """
        if (bar is None) == (tick is None):
            raise ValueError("Pass exactly one of bar or tick.")
        if not isinstance(name, str) or not name:
            raise ValueError("The end marker needs a non-empty name.")
        if tick is None:
            tick = Timebase(self._ops.get_ppq()).bar_start(bar if bar is not None else 1, beats_per_bar=beats_per_bar)
        elif type(tick) is not int or tick < 0:
            raise ValueError("tick must be a nonnegative integer.")
        for marker in reversed([marker for marker in self.markers() if marker.name == name]):
            self._ops.delete_marker(index=marker.index)
        self._ops.add_marker(tick=tick, name=name)
        placed = [marker for marker in self.markers() if marker.name == name and marker.tick == tick]
        if len(placed) != 1:
            raise ProtocolError("The end marker was not listed once after writing; inspect the marker list.")
        return placed[0]
