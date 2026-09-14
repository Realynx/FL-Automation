"""Explicit units and immutable values for native batch operations.

Channels, mixer tracks, effect slots, and clip indices are zero-based.
Pattern and playlist-track indices are one-based. Clip indices can shift after deletion.
"""

import math
from dataclasses import dataclass
from typing import NewType

ChannelIndex = NewType("ChannelIndex", int)
MixerTrackIndex = NewType("MixerTrackIndex", int)
PatternIndex = NewType("PatternIndex", int)
PlaylistTrackIndex = NewType("PlaylistTrackIndex", int)
ClipIndex = NewType("ClipIndex", int)
Ticks = NewType("Ticks", int)
Beats = NewType("Beats", float)


@dataclass(frozen=True)
class NoteSpec:
    channel: int
    key: int
    start_tick: int
    length_tick: int
    velocity: int = 100


@dataclass(frozen=True)
class NoteRef:
    """Addresses existing notes by (channel, key, start_tick). FL allows stacked duplicates that share
    the triple; ``length_tick`` (the note's current length) narrows the match to one of them, and
    ``allow_multiple=True`` on the operation addresses all matches instead of refusing."""

    channel: int
    key: int
    start_tick: int
    length_tick: int | None = None


@dataclass(frozen=True)
class NoteEdit:
    """(channel, key, start_tick) locate the note (optionally narrowed by its current ``length_tick``);
    each ``new_*`` field, when set, is the note's new value (None = unchanged)."""

    channel: int
    key: int
    start_tick: int
    new_key: int | None = None
    new_start_tick: int | None = None
    new_length: int | None = None
    new_velocity: int | None = None
    muted: bool | None = None
    length_tick: int | None = None


@dataclass(frozen=True)
class ClipMove:
    index: int
    start_tick: int
    track: int


@dataclass(frozen=True)
class ClipResize:
    index: int
    length_tick: int


@dataclass(frozen=True)
class PatternClipSpec:
    pattern: int
    track: int
    start_tick: int
    length_tick: int


@dataclass(frozen=True)
class Timebase:
    """A snapshot of project PPQ. Refresh after opening/changing projects.

    Fractional ticks round to the nearest tick using Python's ties-to-even rule.
    """

    ppq: int

    def __post_init__(self) -> None:
        if self.ppq <= 0:
            raise ValueError("PPQ must be positive.")

    def ticks(self, beats: float) -> Ticks:
        if not math.isfinite(beats):
            raise ValueError("Beat position must be finite.")
        return Ticks(round(beats * self.ppq))

    def beats(self, ticks: int) -> Beats:
        return Beats(ticks / self.ppq)

    def bar_start(self, bar: int, *, beats_per_bar: int = 4) -> Ticks:
        """Tick at the start of a one-based bar (bar 1 is tick 0). FL's default meter is 4/4."""
        if type(bar) is not int or bar < 1 or type(beats_per_bar) is not int or beats_per_bar < 1:
            raise ValueError("bar must be a one-based integer and beats_per_bar a positive integer.")
        return Ticks((bar - 1) * beats_per_bar * self.ppq)
