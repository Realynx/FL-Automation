"""One-based patterns and explicit beat/tick note authoring."""

from collections.abc import Iterator, Sequence

from ._collection import checked_index, iter_pages
from ._properties import IndexedObject, NativeProperty
from .models import NoteInfo, PatternInfo
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

    def edit(self, edits: Sequence[NoteEdit]) -> int:
        return self._ops.edit_notes(pattern=self.pattern, edits=edits)

    def delete(self, targets: Sequence[NoteRef]) -> int:
        return self._ops.delete_notes(pattern=self.pattern, targets=targets)


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
