"""Structured query snapshots. Names are JSON strings, never parsed display text."""

from dataclasses import dataclass
from types import UnionType
from typing import Generic, TypeVar, get_args, get_origin, get_type_hints

from .errors import ProtocolError
from .values import JsonValue, camel_case

T = TypeVar("T")


@dataclass(frozen=True)
class ChannelInfo:
    index: int
    name: str
    mixer_track: int
    muted: bool
    volume: int
    pan: int


@dataclass(frozen=True)
class PatternInfo:
    index: int
    name: str
    length_tick: int | None
    note_count: int | None
    current: bool


@dataclass(frozen=True)
class NoteInfo:
    index: int
    channel: int
    key: int
    start_tick: int
    length_tick: int
    velocity: int
    muted: bool


@dataclass(frozen=True)
class PlaylistTrackInfo:
    index: int
    name: str
    color: int
    muted: bool
    collapsed: bool
    selected: bool
    mode: int


@dataclass(frozen=True)
class ClipInfo:
    index: int
    track: int
    start_tick: int
    length_tick: int
    source_kind: str
    source_index: int
    muted: bool


@dataclass(frozen=True)
class ArrangementInfo:
    index: int
    name: str
    current: bool


@dataclass(frozen=True)
class MixerTrackInfo:
    index: int
    name: str
    kind: str


@dataclass(frozen=True)
class PluginParameterInfo:
    index: int
    name: str
    raw_value: int
    display_value: str


@dataclass(frozen=True)
class ProjectInfo:
    title: str
    path: str
    untitled: bool


@dataclass(frozen=True)
class AutomationPointInfo:
    index: int
    time_beats: float
    value: float
    tension: float
    curve: int


@dataclass(frozen=True)
class Page(Generic[T]):
    items: tuple[T, ...]
    next_offset: int | None
    total: int


def _matches(value: JsonValue, expected: object) -> bool:
    if get_origin(expected) is UnionType:
        return any(_matches(value, item) for item in get_args(expected))
    if expected is float:
        return type(value) in (int, float)
    if expected is type(None):
        return value is None
    return type(value) is expected


def decode_record(record: type[T], value: JsonValue) -> T:
    if not isinstance(value, dict):
        raise ProtocolError(f"Expected a {record.__name__} object.")
    hints = get_type_hints(record)
    kwargs: dict[str, JsonValue] = {}
    for name, expected in hints.items():
        key = camel_case(name)
        if key not in value or not _matches(value[key], expected):
            raise ProtocolError(f"Invalid {record.__name__}.{key} field.")
        kwargs[name] = value[key]
    return record(**kwargs)


def decode_records(record: type[T], value: JsonValue) -> tuple[T, ...]:
    if not isinstance(value, list):
        raise ProtocolError(f"Expected a {record.__name__} array.")
    return tuple(decode_record(record, item) for item in value)


def decode_page(record: type[T], value: JsonValue) -> Page[T]:
    if not isinstance(value, dict) or "nextOffset" not in value:
        raise ProtocolError("Expected a paginated query response.")
    offset, total = value.get("nextOffset"), value.get("total")
    if offset is not None and (type(offset) is not int or offset < 0):
        raise ProtocolError("Invalid page continuation offset.")
    if type(total) is not int or total < 0:
        raise ProtocolError("Invalid page total.")
    return Page(decode_records(record, value.get("items")), offset, total)
