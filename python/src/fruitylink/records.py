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
    channel: int
    key: int
    start_tick: int


@dataclass(frozen=True)
class NoteEdit:
    channel: int
    key: int
    start_tick: int
    new_key: int | None = None
    new_start_tick: int | None = None
    new_length: int | None = None
    new_velocity: int | None = None
    muted: bool | None = None


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
