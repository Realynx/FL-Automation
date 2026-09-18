"""Project lifecycle and transport use the same shared operations as any client."""

import re
import time
from collections.abc import Callable
from dataclasses import dataclass
from typing import TypeVar

from ._collection import checked_index
from .errors import FruityLinkError, ProtocolError
from .models import ProjectInfo
from .operations import Operations
from .records import Timebase

T = TypeVar("T")


# FL's global recording filter: the bitmask behind the record button's right-click "Recording filter"
# submenu. The values are FL's own menu-item tags, read out of the main form's DFM (Recfilter1Menu Tag 1
# "&Automation", Recfilter2Menu Tag 2 "&Notes", Recfilter3Menu Tag 4 "A&udio", Recfilter4Menu Tag 8
# "&Clips"; FL 2025 has no Clips item). Live audio capture needs AUDIO or FL writes no WAV at all.
RECORDING_FILTER_AUTOMATION = 1
RECORDING_FILTER_NOTES = 2
RECORDING_FILTER_AUDIO = 4
RECORDING_FILTER_CLIPS = 8
RECORDING_FILTER_BITS: dict[str, int] = {"automation": RECORDING_FILTER_AUTOMATION, "notes": RECORDING_FILTER_NOTES,
                                         "audio": RECORDING_FILTER_AUDIO, "clips": RECORDING_FILTER_CLIPS}
RECORDING_FILTER_ALL = RECORDING_FILTER_AUTOMATION | RECORDING_FILTER_NOTES | RECORDING_FILTER_AUDIO | RECORDING_FILTER_CLIPS


def recording_filter_names(flags: int) -> tuple[str, ...]:
    """The parts a recording-filter bitmask enables, in FL's menu order."""
    return tuple(name for name, bit in RECORDING_FILTER_BITS.items() if flags & bit)


def recording_filter_flags(current: int, **parts: bool | None) -> int:
    """``current`` with the named parts turned on/off and every other bit left alone.

    Parts are ``automation``, ``notes``, ``audio`` and ``clips``; ``None`` (the default for anything not
    named) leaves that part as it is, so ``recording_filter_flags(3, audio=True)`` is 7.
    """
    flags = current
    for name, wanted in parts.items():
        if name not in RECORDING_FILTER_BITS:
            raise ValueError(f"Unknown recording-filter part {name!r}; expected one of {sorted(RECORDING_FILTER_BITS)}.")
        if wanted is None:
            continue
        if not isinstance(wanted, bool):
            raise ValueError(f"Recording-filter part {name!r} must be True, False or None.")
        flags = flags | RECORDING_FILTER_BITS[name] if wanted else flags & ~RECORDING_FILTER_BITS[name]
    return flags


def _flag(name: str, value: object) -> bool:
    """Transport toggles are booleans; 0/1 and truthy objects are refused so a typo cannot silently flip one."""
    if not isinstance(value, bool):
        raise ValueError(f"Transport toggle {name!r} must be True or False, not {type(value).__name__}.")
    return value


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

    @property
    def recording_filter(self) -> int:
        """FL's global recording filter bitmask (1 automation, 2 notes, 4 audio, 8 clips).

        This is the record button's right-click "Recording filter" submenu. It is a global FL setting,
        not a project one: FL reads it from the registry at startup and writes it back at exit, so the
        live value only exists inside the running engine. Live audio capture needs bit 4; without it FL
        arms the insert, records, and writes no file.
        """
        return self._ops.get_recording_filter()

    @recording_filter.setter
    def recording_filter(self, flags: int) -> None:
        if isinstance(flags, bool) or type(flags) is not int or not 0 <= flags <= RECORDING_FILTER_ALL:
            raise ValueError(f"The recording filter is a bitmask 0..{RECORDING_FILTER_ALL} "
                             "(1 automation, 2 notes, 4 audio, 8 clips).")
        self._ops.set_recording_filter(flags=flags)

    @property
    def recording_filter_parts(self) -> tuple[str, ...]:
        """The parts the live recording filter enables, e.g. ``("automation", "notes")``."""
        return recording_filter_names(self.recording_filter)

    def ensure_recording_filter(self, *, automation: bool | None = None, notes: bool | None = None,
                                audio: bool | None = True, clips: bool | None = None) -> int:
        """Turn the named parts of FL's recording filter on or off, leaving every other part alone.

        Returns the bitmask as it was **before** the call, so a caller that only needs Audio for one pass
        can hand it straight back to ``recording_filter`` afterwards. Nothing is written when the filter
        already matches, so the common case costs one read.
        """
        previous = self.recording_filter
        wanted = recording_filter_flags(previous, automation=automation, notes=notes, audio=audio, clips=clips)
        if wanted != previous:
            self.recording_filter = wanted
        return previous

    def play(self) -> None:
        self._ops.transport_play()

    def stop(self) -> None:
        self._ops.transport_stop()

    def toggle_record(self) -> None:
        self._ops.transport_toggle_record()

    # FL's global transport toggles. Each is an FL-wide setting (not a project one) that FL persists at exit,
    # so a pass that changes one for itself hands the previous value back. ``countdown`` and ``wait_for_input``
    # silently break an automated record pass, ``loop_record`` changes what it writes and ``metronome`` bleeds
    # into a live capture, which is why ``fl.audio.capture`` sets all four for itself.
    TOGGLES: tuple[str, ...] = ("song_mode", "metronome", "countdown", "wait_for_input", "loop_record",
                                "blend_recorded_notes")

    @property
    def metronome(self) -> bool:
        """FL's metronome click. It is mixed into what FL plays, so it also lands in a live capture."""
        return self._ops.get_metronome()

    @metronome.setter
    def metronome(self, value: bool) -> None:
        self._ops.set_metronome(on=_flag("metronome", value))

    @property
    def countdown(self) -> bool:
        """FL's countdown before recording (the toolbar "precount"). On, a record pass counts in first."""
        return self._ops.get_countdown()

    @countdown.setter
    def countdown(self, value: bool) -> None:
        self._ops.set_countdown(on=_flag("countdown", value))

    @property
    def wait_for_input(self) -> bool:
        """FL's "wait for input to start playing". On, ``play()`` waits for note or audio input."""
        return self._ops.get_wait_for_input()

    @wait_for_input.setter
    def wait_for_input(self, value: bool) -> None:
        self._ops.set_wait_for_input(on=_flag("wait_for_input", value))

    @property
    def loop_record(self) -> bool:
        """FL's loop recording. On, a pass over a looped range keeps every take."""
        return self._ops.get_loop_record()

    @loop_record.setter
    def loop_record(self, value: bool) -> None:
        self._ops.set_loop_record(on=_flag("loop_record", value))

    @property
    def blend_recorded_notes(self) -> bool:
        """FL's "blend recorded notes" (overdub): recorded notes merge into the pattern instead of replacing it."""
        return self._ops.get_blend_recorded_notes()

    @blend_recorded_notes.setter
    def blend_recorded_notes(self, value: bool) -> None:
        self._ops.set_blend_recorded_notes(on=_flag("blend_recorded_notes", value))

    def settings(self) -> dict[str, bool]:
        """Every transport toggle this build can report, as ``{name: bool}``.

        A toggle the running FL build does not expose is left out rather than guessed, so the result can be
        shorter than ``TOGGLES``; ``settings().keys()`` is the honest list of what is controllable here.
        """
        values: dict[str, bool] = {}
        for name in self.TOGGLES:
            try:
                values[name] = bool(getattr(self, name))
            except FruityLinkError:
                continue
        return values

    def ensure(self, **flags: bool) -> dict[str, bool]:
        """Set the named toggles and return the values they had before, for handing back afterwards.

        ``previous = fl.transport.ensure(countdown=False, metronome=False)`` then, later,
        ``fl.transport.ensure(**previous)``. Only toggles whose value differs are written, so the common case
        costs one read each. An unknown name is a ``ValueError``; a toggle the build cannot set raises.
        """
        previous: dict[str, bool] = {}
        for name, wanted in flags.items():
            if name not in self.TOGGLES:
                raise ValueError(f"Unknown transport toggle {name!r}; expected one of {list(self.TOGGLES)}.")
            current = bool(getattr(self, name))
            previous[name] = current
            if current != _flag(name, wanted):
                setattr(self, name, wanted)
        return previous

    @property
    def recording_active(self) -> bool:
        """Whether FL's engine currently has a recording pass running, independent of the toolbar button.

        The button's pressed byte says "the next play will record"; this says "the engine is recording now".
        Use it to verify a pass really started on a build/instance where the button state is unreliable.
        """
        return self._ops.get_recording_active()

    @property
    def record_pressed(self) -> bool:
        """Whether FL's transport record button is engaged, so the next ``play()`` records.

        ``toggle_record()`` only flips it and FL leaves it engaged after a recording pass, so code that
        needs recording ON must read this instead of toggling blindly -- otherwise a second pass turns
        recording off and records nothing.
        """
        return self._ops.get_record_pressed()

    def ensure_record_pressed(self, pressed: bool = True) -> bool:
        """Engage or release the record button, returning the state it was in before the call.

        FL can decline the toggle: the button is bound to ``ShortcutsModule.RecordAction``, whose click FL
        gates, so the pressed byte does not always follow. The refusal is reported with what FL can and
        cannot do about it rather than retried blindly.
        """
        previous = self.record_pressed
        if previous != pressed:
            self.toggle_record()
            if self.record_pressed != pressed:
                raise ProtocolError(
                    f"FL's record button did not change to {pressed}; it still reads {previous}. FL declined the "
                    "click on its own record button (the button is bound to ShortcutsModule.RecordAction, which FL "
                    "disables until a project can be recorded into). Load or create a project, or press R in FL.")
        return previous

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

    def set_song_end(self, bar: int | None = None, *, after_bar: int | None = None, tick: int | None = None,
                     name: str = "End", beats_per_bar: int = 4) -> Marker:
        """Place or move a single named end marker at a bar start or an absolute tick.

        ``bar=N`` puts the marker at the START of bar N, so the song ends *before* it and bar N is not
        played; use ``after_bar=N`` to keep bar N (the marker lands at the start of bar N+1). A 96-bar
        song therefore wants ``after_bar=96`` (or ``bar=97``) — ``bar=96`` cuts the last bar.

        FL extends the song length, play range and full-song renders to the last time marker, so a
        marker past the final note adds silence or room for tails. ``bar``, ``after_bar`` and ``tick``
        are mutually exclusive. Existing markers with the same name are deleted first, so the name stays
        unique. Returns the marker as the host lists it after the write.
        """
        given = [value is not None for value in (bar, after_bar, tick)]
        if sum(given) != 1:
            raise ValueError("Pass exactly one of bar, after_bar or tick.")
        if not isinstance(name, str) or not name:
            raise ValueError("The end marker needs a non-empty name.")
        if after_bar is not None:
            if isinstance(after_bar, bool) or type(after_bar) is not int or after_bar < 1:
                raise ValueError("after_bar must be a one-based bar number; the marker lands at the start of the next bar.")
            bar = after_bar + 1
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
