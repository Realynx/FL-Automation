"""Structured query snapshots. Names are JSON strings, never parsed display text."""

from dataclasses import MISSING, dataclass, fields
from types import UnionType
from typing import Generic, TypeVar, get_args, get_origin, get_type_hints

from .errors import ProtocolError
from .levels import send_level_to_db
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
class Gap:
    """A rest region on one channel, computed locally from note snapshots: [start_tick, end_tick)."""

    channel: int
    start_tick: int
    end_tick: int
    length_tick: int
    start_beat: float
    length_beats: float


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
class MixerSendInfo:
    """One active send from a track's native send table. ``level`` is FL's send scale (native / 16000):
    0.8 is unity (0 dB), 1.0 the knob top; ``level_db`` applies the SDK's fader-law model."""

    source: int
    destination: int
    destination_name: str
    level: float
    active: bool

    @property
    def level_db(self) -> float:
        return send_level_to_db(min(1.0, max(0.0, self.level)))


@dataclass(frozen=True)
class PluginParameterInfo:
    """One plugin parameter slot.

    ``name`` is the display name with FL's hint-formatting codes removed (stock effects report
    ``"^b^aWet level"``; ``raw_name`` keeps the string exactly as the plugin wrote it). ``raw_value``
    is the host's native integer: for VST/wrapper parameters it is the IEEE-754 bit pattern of the
    normalized value, decoded into ``normalized`` (a native FL switch reports a plain 0 or 1, which
    is already the normalized value; every other native integer scale does not decode to a 0..1
    float and leaves ``normalized`` None). Compare ``normalized`` with what you wrote; compare
    ``display_value`` only in a later request (see ``Parameters.set_verified``).
    """

    index: int
    name: str
    raw_value: int
    display_value: str
    raw_name: str = ""
    normalized: float | None = None

    def __post_init__(self) -> None:
        from .plugins import clean_parameter_name, normalized_from_raw  # local: plugins imports models

        raw_name = self.raw_name or self.name
        object.__setattr__(self, "raw_name", raw_name)
        object.__setattr__(self, "name", clean_parameter_name(self.name))
        if self.normalized is None:
            object.__setattr__(self, "normalized", normalized_from_raw(self.raw_value))


@dataclass(frozen=True)
class SampleInfo:
    """One installed audio sample as the loaders accept it.

    ``entry`` is the verbatim root-tagged string (``"[P]Drums\\Kicks\\909 Kick.wav"``) to pass straight
    to ``fl.channels.add_sample`` / ``Channel.replace_sample``; ``root_tag`` is ``"[P]"`` for FL's
    factory packs and ``"[U]"`` for the user's Image-Line content. ``name`` has no extension.
    """

    entry: str
    root_tag: str
    relative_path: str
    name: str
    extension: str


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
    defaulted = {f.name for f in fields(record)  # type: ignore[arg-type]
                 if f.default is not MISSING or f.default_factory is not MISSING}
    kwargs: dict[str, JsonValue] = {}
    for name, expected in hints.items():
        key = camel_case(name)
        if key not in value and name in defaulted:
            continue   # optional field added after the host build: keep the record's default
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
