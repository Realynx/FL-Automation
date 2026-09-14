"""One-based patterns and explicit beat/tick note authoring."""

from collections.abc import Iterator, Sequence

from ._collection import checked_index, iter_pages
from ._gaps import gaps_in_range, note_intervals
from ._properties import IndexedObject, NativeProperty
from .models import Gap, NoteInfo, PatternInfo
from .operations import Operations
from .records import NoteEdit, NoteRef, NoteSpec, Timebase


class Notes:
    def __init__(self, ops: Operations, pattern: int) -> None:
        self._ops = ops
        self.pattern = checked_index(pattern, minimum=1)

    def list(self, channel: int = -1) -> tuple[NoteInfo, ...]:
        return tuple(iter_pages(lambda offset: self._ops.query_notes(pattern=self.pattern, channel=channel, offset=offset)))

    def list_text(self, channel: int = -1, offset: int = 0) -> str:
        return self._ops.get_notes(pattern=self.pattern, channel=channel, offset=offset)

    def add(self, notes: Sequence[NoteSpec]) -> None:
        self._ops.add_notes(pattern=self.pattern, notes=notes)

    def add_ticks(self, *, channel: int, key: int, start: int, length: int, velocity: int = 100) -> None:
        self.add([NoteSpec(channel, key, start, length, velocity)])

    def add_beats(self, *, channel: int, key: int, start: float, length: float, velocity: int = 100) -> None:
        timebase = Timebase(self._ops.get_ppq())
        self.add_ticks(channel=channel, key=key, start=timebase.ticks(start), length=timebase.ticks(length), velocity=velocity)

    def edit(self, edits: Sequence[NoteEdit], *, allow_multiple: bool = False) -> int:
        """Edit existing notes in place; returns the number changed. An edit whose (channel, key,
        start_tick[, length_tick]) matches several stacked notes is refused before any write unless
        ``allow_multiple`` is True, which applies it to all of them."""
        return self._ops.edit_notes(pattern=self.pattern, edits=edits, allow_multiple=allow_multiple)

    def delete(self, targets: Sequence[NoteRef], *, allow_multiple: bool = False) -> int:
        """Delete the addressed notes; returns the number deleted. A target matching several stacked
        notes is refused before any write unless ``allow_multiple`` is True, which deletes all of them."""
        return self._ops.delete_notes(pattern=self.pattern, targets=targets, allow_multiple=allow_multiple)


class Pattern(IndexedObject):
    name = NativeProperty("get_pattern_name", "set_pattern_name", "index", "name", str)

    @property
    def notes(self) -> Notes:
        return Notes(self._ops, self.index)

    def select(self) -> None:
        self._ops.select_pattern(index=self.index)

    def clear(self) -> None:
        self._ops.clear_pattern(index=self.index)

    def clone(self) -> "Pattern | None":
        index = self._ops.clone_pattern(source_pattern=self.index)
        return Pattern(self._ops, index) if index > 0 else None

    def gaps(self, channel: int | None = None, min_ticks: int | None = None, min_beats: float = 1.0,
             *, end_tick: int | None = None) -> list[Gap]:
        """Rest regions per channel, sorted by time, computed from this pattern's note snapshot.

        A note occupies [start, start + length); muted notes are silent. The pattern spans
        tick 0 to ``end_tick`` (default: the later of the host-reported pattern length and the
        last note end, across all channels). ``channel=None`` reports every channel that has
        notes; an explicit channel with no notes yields one gap over the whole span. Gaps
        shorter than ``min_ticks`` (default ``min_beats`` at the project PPQ) are dropped.
        """
        if channel is not None:
            checked_index(channel)
        timebase = Timebase(self._ops.get_ppq())
        threshold = timebase.ticks(min_beats) if min_ticks is None else min_ticks
        notes = self.notes.list()
        if end_tick is None:
            reported = [item.length_tick or 0 for item in self._ops.query_patterns() if item.index == self.index]
            end_tick = max([note.start_tick + note.length_tick for note in notes] + reported, default=0)
        elif type(end_tick) is not int or end_tick < 0:
            raise ValueError("end_tick must be a nonnegative integer.")
        channels = {note.channel for note in notes} if channel is None else {channel}
        return gaps_in_range(note_intervals(notes, channel=channel), channels, start=0, end=end_tick,
                             min_ticks=threshold, ppq=timebase.ppq)


class Patterns:
    def __init__(self, ops: Operations) -> None:
        self._ops = ops

    def __getitem__(self, index: int) -> Pattern:
        return Pattern(self._ops, checked_index(index, minimum=1))

    def __iter__(self) -> Iterator[Pattern]:
        return (self[item.index] for item in self.list())

    @property
    def current(self) -> Pattern:
        return self[self._ops.get_current_pattern()]

    def create(self, name: str | None = None) -> Pattern:
        pattern = self[self._ops.create_pattern()]
        if name is not None:
            pattern.name = name
        return pattern

    def list(self) -> tuple[PatternInfo, ...]:
        return self._ops.query_patterns()

    def list_text(self) -> str:
        return self._ops.list_patterns()

    def find(self, name: str) -> Pattern:
        matches = [item.index for item in self.list() if item.name == name]
        if len(matches) != 1:
            raise LookupError(f"Expected one pattern named {name!r}; found {len(matches)}.")
        return self[matches[0]]
