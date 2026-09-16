"""Arrangement references and creation/selection operations."""

from collections.abc import Iterator

from ._collection import checked_index
from ._properties import IndexedObject, NativeProperty
from .models import ArrangementInfo
from .operations import Operations


class Arrangement(IndexedObject):
    name = NativeProperty("get_arrangement_name", "rename_arrangement", "idx", "name", str)

    def select(self) -> None:
        self._ops.select_arrangement(idx=self.index)

    def delete(self) -> None:
        self._ops.delete_arrangement(idx=self.index)

    def clone(self, name: str | None = None) -> "Arrangement":
        return Arrangement(self._ops, self._ops.clone_arrangement(src_idx=self.index, name=name))


class Arrangements:
    def __init__(self, ops: Operations) -> None:
        self._ops = ops

    def __getitem__(self, index: int) -> Arrangement:
        return Arrangement(self._ops, checked_index(index))

    def __iter__(self) -> Iterator[Arrangement]:
        return (self[item.index] for item in self.list())

    @property
    def current(self) -> Arrangement:
        matches = [item for item in self.list() if item.current]
        if len(matches) != 1:
            raise LookupError("The current arrangement is unavailable.")
        return self[matches[0].index]

    def list(self) -> tuple[ArrangementInfo, ...]:
        return self._ops.query_arrangements()

    def list_text(self) -> str:
        return self._ops.list_arrangements()

    def add(self, name: str | None = None) -> Arrangement:
        return self[self._ops.add_arrangement(name=name)]
