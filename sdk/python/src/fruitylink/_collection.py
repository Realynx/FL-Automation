"""Shared snapshot paging and index validation."""

from collections.abc import Callable, Iterator
from typing import TypeVar

from .errors import ProtocolError
from .models import Page

T = TypeVar("T")


def checked_index(value: int, *, minimum: int = 0, maximum: int | None = None) -> int:
    if type(value) is not int or value < minimum or (maximum is not None and value > maximum):
        raise IndexError(f"Index must be an integer >= {minimum}" + (f" and <= {maximum}." if maximum is not None else "."))
    return value


def iter_pages(fetch: Callable[[int], Page[T]]) -> Iterator[T]:
    offset = 0
    while True:
        page = fetch(offset)
        yield from page.items
        if page.next_offset is None:
            return
        if page.next_offset <= offset:
            raise ProtocolError("Query continuation did not advance; refusing an infinite page loop.")
        offset = page.next_offset
