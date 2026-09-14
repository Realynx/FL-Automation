"""Playlist tracks and clips. Index references must be refreshed after structural edits."""

from collections.abc import Iterator, Sequence

from ._collection import checked_index, iter_pages
from ._gaps import GapList, IntervalMap, gaps_in_range, note_intervals
from ._properties import IndexedObject, NativeProperty
from .models import ClipInfo, NoteInfo, PlaylistTrackInfo
from .operations import Operations
from .patterns import Notes
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

    def first_free_track(self, start_tick: int, end_tick: int, *, above: int = 1) -> int:
        """Lowest one-based track >= ``above`` with no clip of any kind overlapping [start_tick, end_tick).

        Pattern, audio and automation clips all count; muted clips still occupy their track.
        Raises LookupError when tracks up to 500 are all occupied.
        """
        if any(type(value) is not int for value in (start_tick, end_tick, above)):
            raise ValueError("Tick positions and the starting track must be integers.")
        if start_tick < 0 or end_tick <= start_tick or not 1 <= above <= 500:
            raise ValueError("Require 0 <= start_tick < end_tick and a starting track 1..500.")
        occupied = {clip.track for clip in self.clips.list()
                    if clip.start_tick < end_tick and clip.start_tick + clip.length_tick > start_tick}
        track = above
        while track in occupied:
            track += 1
        if track > 500:
            raise LookupError("No free playlist track in 1..500 for that range.")
        return track

    def gaps(self, start_bar: int, end_bar: int, channel: int | None = None, min_beats: float = 1.0,
             *, beats_per_bar: int = 4) -> GapList:
        """Rest regions per channel across bars ``start_bar``..``end_bar`` inclusive (one-based), absolute ticks.

        Reads every unmuted pattern clip overlapping the range and that pattern's notes: a note
        starting inside its clip sounds for its full length (even past the clip end); notes
        starting after the clip end do not sound; muted notes are silent. Clips are assumed to
        start at their pattern's beginning (sliced clips with an offset are not distinguishable
        here). ``channel=None`` reports every channel with notes in those clips.
        """
        if channel is not None:
            checked_index(channel)
        timebase = Timebase(self._ops.get_ppq())
        start = timebase.bar_start(start_bar, beats_per_bar=beats_per_bar)
        end = timebase.bar_start(end_bar + 1, beats_per_bar=beats_per_bar)
        if end <= start:
            raise ValueError("end_bar must not precede start_bar.")
        notes_by_pattern: dict[int, tuple[NoteInfo, ...]] = {}
        intervals: IntervalMap = {}
        channels: set[int] = set()
        for clip in self.clips.list():
            if clip.source_kind != "pattern" or clip.muted or clip.start_tick >= end \
                    or clip.start_tick + clip.length_tick <= start:
                continue
            if clip.source_index not in notes_by_pattern:
                notes_by_pattern[clip.source_index] = Notes(self._ops, clip.source_index).list()
            notes = notes_by_pattern[clip.source_index]
            channels.update(note.channel for note in notes if not note.muted)
            for key, spans in note_intervals(notes, offset=clip.start_tick, within=clip.length_tick,
                                             channel=channel).items():
                intervals.setdefault(key, []).extend(spans)
        if channel is not None:
            channels = {channel}
        return gaps_in_range(intervals, channels, start=start, end=end, min_ticks=timebase.ticks(min_beats),
                             ppq=timebase.ppq)
