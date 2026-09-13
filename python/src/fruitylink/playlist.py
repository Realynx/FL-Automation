"""Playlist tracks and clips. Index references must be refreshed after structural edits."""

from collections.abc import Iterator, Sequence

from ._collection import checked_index, iter_pages
from ._properties import IndexedObject, NativeProperty
from .models import ClipInfo, PlaylistTrackInfo
from .operations import Operations
from .records import ClipMove, ClipResize, PatternClipSpec, Timebase


class PlaylistTrack(IndexedObject):
    name = NativeProperty("get_track_name", "set_track_name", "track", "name", str)
    color = NativeProperty("get_track_color", "set_track_color", "track", "rgb", int)
    muted = NativeProperty("get_track_mute", "set_track_mute", "track", "muted", bool)
    collapsed = NativeProperty("get_track_collapsed", "set_track_collapsed", "track", "collapsed", bool)

    def select(self) -> None:
        self._ops.select_track(track=self.index)

    def toggle_solo(self) -> None:
        self._ops.set_track_solo(track=self.index)


class Clip(IndexedObject):
    muted = NativeProperty("get_clip_muted", "set_clip_muted", "clipIndex", "muted", bool)

    def move_ticks(self, *, start: int, track: int) -> None:
        self._ops.move_clip(clip_index=self.index, start_tick=start, track=checked_index(track, minimum=1))

    def move_beats(self, *, start: float, track: int) -> None:
        self.move_ticks(start=Timebase(self._ops.get_ppq()).ticks(start), track=track)

    def resize_ticks(self, length: int) -> None:
        self._ops.resize_clip(clip_index=self.index, length_tick=length)

    def resize_beats(self, length: float) -> None:
        self.resize_ticks(Timebase(self._ops.get_ppq()).ticks(length))

    def delete(self) -> None:
        self._ops.delete_clip(clip_index=self.index)

    def slice_ticks(self, tick: int) -> None:
        self._ops.slice_clip(clip_index=self.index, tick=tick)

    def slice_beats(self, beat: float) -> None:
        self.slice_ticks(Timebase(self._ops.get_ppq()).ticks(beat))

    def duplicate(self) -> None:
        self._ops.duplicate_clip(clip_index=self.index)


class Clips:
    def __init__(self, ops: Operations) -> None:
        self._ops = ops

    def __getitem__(self, index: int) -> Clip:
        return Clip(self._ops, checked_index(index))

    def list(self, track: int = -1) -> tuple[ClipInfo, ...]:
        return tuple(iter_pages(lambda offset: self._ops.query_clips(track=track, offset=offset)))

    def __iter__(self) -> Iterator[Clip]:
        return (self[item.index] for item in self.list())

    def list_text(self, *, offset: int = 0, track: int = -1) -> str:
        return self._ops.list_clips(offset=offset, track=track)

    def delete(self, indices: Sequence[int]) -> None:
        self._ops.delete_clips(clip_indices=indices)

    def move(self, moves: Sequence[ClipMove]) -> None:
        self._ops.move_clips(moves=moves)

    def resize(self, resizes: Sequence[ClipResize]) -> None:
        self._ops.resize_clips(resizes=resizes)

    def set_muted(self, indices: Sequence[int], muted: bool) -> None:
        self._ops.set_clips_muted(clip_indices=indices, muted=muted)


class Playlist:
    def __init__(self, ops: Operations) -> None:
        self._ops = ops
        self.clips = Clips(ops)

    def __getitem__(self, track: int) -> PlaylistTrack:
        return PlaylistTrack(self._ops, checked_index(track, minimum=1, maximum=500))

    def __iter__(self) -> Iterator[PlaylistTrack]:
        return (self[item.index] for item in self.list())

    def list(self) -> tuple[PlaylistTrackInfo, ...]:
        return self._ops.query_playlist_tracks()

    def list_text(self) -> str:
        return self._ops.list_playlist_tracks()

    def add_pattern_ticks(self, pattern: int, *, track: int, start: int, length: int = 0) -> None:
        self._ops.add_pattern_clip(pattern=checked_index(pattern, minimum=1), track=checked_index(track, minimum=1),
                                   start_tick=start, length_tick=length)

    def add_pattern(self, pattern: int, *, track: int, start_beats: float, length_beats: float = 0) -> None:
        timebase = Timebase(self._ops.get_ppq())
        self.add_pattern_ticks(pattern, track=track, start=timebase.ticks(start_beats), length=timebase.ticks(length_beats))

    def add_patterns(self, clips: Sequence[PatternClipSpec]) -> None:
        self._ops.add_pattern_clips(clips=clips)
