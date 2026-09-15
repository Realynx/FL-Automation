"""Playlist tracks and clips. Index references must be refreshed after structural edits."""

from collections.abc import Iterator, Sequence

from ._collection import checked_index, iter_pages
from ._gaps import GapList, IntervalMap, gaps_in_range, note_intervals, note_onsets
from ._properties import IndexedObject, NativeProperty
from .models import ClipInfo, NoteInfo, PlaylistTrackInfo
from .operations import Operations
from .patterns import Notes
from .records import ClipMove, ClipResize, PatternClipSpec, Timebase

ResizeLike = ClipResize | tuple[int, int]
MoveLike = ClipMove | tuple[int, int, int]


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

    def delete(self, indices: Sequence[int] | int) -> None:
        """Delete clips by zero-based index: one index or a sequence (one native pass)."""
        self._ops.delete_clips(clip_indices=_indices(indices))

    def move(self, moves: Sequence[MoveLike] | int, start_tick: int | None = None, track: int | None = None) -> None:
        """Move clips in one native pass.

        ``move([ClipMove(index, start_tick, track), ...])``, plain ``(index, start_tick, track)``
        tuples, or the single form ``move(index, start_tick, track)``. Tracks are one-based.
        """
        if isinstance(moves, int):
            if isinstance(moves, bool) or start_tick is None or track is None:
                raise TypeError("move(index, start_tick, track) needs a clip index, the start tick and the track.")
            moves = [ClipMove(moves, start_tick, track)]
        elif start_tick is not None or track is not None:
            raise TypeError("Pass start_tick and track only with a single clip index.")
        records = [item if isinstance(item, ClipMove) else ClipMove(*item) for item in moves]
        self._ops.move_clips(moves=records)

    def resize(self, resizes: Sequence[ResizeLike] | int, length_tick: int | None = None) -> None:
        """Resize clips in one native pass.

        ``resize([ClipResize(index, length_tick), ...])``, plain ``(index, length_tick)`` tuples, or
        the single form ``resize(index, length_tick)``. Lengths are ticks; ``Clip.resize_beats``
        converts from beats.
        """
        if isinstance(resizes, int):
            if isinstance(resizes, bool) or length_tick is None:
                raise TypeError("resize(index, length_tick) needs a clip index and the new length in ticks.")
            resizes = [ClipResize(resizes, length_tick)]
        elif length_tick is not None:
            raise TypeError("Pass length_tick only with a single clip index.")
        records = [item if isinstance(item, ClipResize) else ClipResize(*item) for item in resizes]
        self._ops.resize_clips(resizes=records)

    def set_muted(self, indices: Sequence[int] | int, muted: bool) -> None:
        self._ops.set_clips_muted(clip_indices=_indices(indices), muted=muted)


def _indices(indices: Sequence[int] | int) -> Sequence[int]:
    if isinstance(indices, int):
        if isinstance(indices, bool):
            raise TypeError("Clip indices must be integers.")
        return [indices]
    return indices


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

    def add_patterns(self, clips: Sequence[PatternClipSpec], *, enforce_lengths: bool = False) -> int:
        """Place many pattern clips in one native pass (one refresh); returns the number of clips resized.

        A spec's positive ``length_tick`` is pinned by the host, so the clip keeps it even when the
        pattern's notes overhang (an 8-bar spec stays 8 bars). ``enforce_lengths=True`` additionally
        re-lists the clips afterwards and resizes any placed clip whose length still differs from its
        spec (a safety net for hosts that re-derive the pattern length); ``length_tick`` 0 follows the
        pattern and is never enforced.
        """
        self._ops.add_pattern_clips(clips=clips)
        if not enforce_lengths:
            return 0
        wanted = {(spec.pattern, spec.track, spec.start_tick): spec.length_tick for spec in clips if spec.length_tick > 0}
        fixes = [ClipResize(clip.index, wanted[key]) for clip in self.clips.list()
                 if clip.source_kind == "pattern"
                 and (key := (clip.source_index, clip.track, clip.start_tick)) in wanted
                 and clip.length_tick != wanted[key]]
        if fixes:
            self._ops.resize_clips(resizes=fixes)
        return len(fixes)

    def onsets(self, channel: int, start_tick: int, end_tick: int) -> tuple[int, ...]:
        """Absolute ticks at which ``channel`` has a note-on inside ``[start_tick, end_tick)``, through
        every unmuted pattern clip (clips are assumed to start at their pattern's beginning; muted notes
        and notes starting past their clip end are silent). Sorted, duplicates removed. Feed the result
        to ``fl.automation.duck`` to follow a kick channel through an arrangement.
        """
        checked_index(channel)
        if any(type(value) is not int for value in (start_tick, end_tick)) or start_tick < 0 or end_tick <= start_tick:
            raise ValueError("Require integer ticks with 0 <= start_tick < end_tick.")
        notes_by_pattern: dict[int, tuple[NoteInfo, ...]] = {}
        hits: set[int] = set()
        for clip in self.clips.list():
            if clip.source_kind != "pattern" or clip.muted or clip.start_tick >= end_tick \
                    or clip.start_tick + clip.length_tick <= start_tick:
                continue
            if clip.source_index not in notes_by_pattern:
                notes_by_pattern[clip.source_index] = Notes(self._ops, clip.source_index).list()
            for tick in note_onsets(notes_by_pattern[clip.source_index], channel=channel, offset=clip.start_tick,
                                    within=clip.length_tick):
                if start_tick <= tick < end_tick:
                    hits.add(tick)
        return tuple(sorted(hits))

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
