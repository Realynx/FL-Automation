"""Project lifecycle and transport use the same shared operations as any client."""

from .models import ProjectInfo
from .operations import Operations
from .records import Timebase


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

    def add_marker_ticks(self, tick: int, name: str) -> None:
        self._ops.add_marker(tick=tick, name=name)

    def add_marker_beats(self, beats: float, name: str) -> None:
        self.add_marker_ticks(Timebase(self._ops.get_ppq()).ticks(beats), name)
