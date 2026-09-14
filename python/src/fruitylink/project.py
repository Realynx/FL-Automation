"""Project lifecycle and transport use the same shared operations as any client."""

import re
from dataclasses import dataclass

from ._collection import checked_index
from .errors import ProtocolError
from .models import ProjectInfo
from .operations import Operations
from .records import Timebase


@dataclass(frozen=True)
class Marker:
    index: int
    name: str
    tick: int


_MARKER_LINE = re.compile(r"^(?P<name>.*) @ tick (?P<tick>\d+)(?: \(bar \d+\))?$")


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

    def seek_ticks(self, tick: int) -> None:
        self._ops.seek(tick=tick)

    def seek_beats(self, beats: float) -> None:
        self.seek_ticks(Timebase(self._ops.get_ppq()).ticks(beats))

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
