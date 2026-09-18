"""Playlist tracks and clips. Index references must be refreshed after structural edits.

**A pattern clip does not loop.** FL plays the pattern once from the clip start and the rest of the
clip is silent; there is no repeat flag on a playlist clip. Live-verified 2026-09-18 on FL
26.1.3.5570: a 1-bar pattern placed as a 4-bar clip produced the clip (start 0, length 1536 =
four 384-tick bars at PPQ 96) whose master capture had audio in bar 1 only; bars 2-4 were silent.
Use ``tile_pattern`` (or ``repeat=True`` on ``add_pattern`` / ``add_pattern_ticks`` /
``add_patterns``) to place one clip per repetition;
placing one long clip instead emits ``PatternClipLongerThanPatternWarning``.
"""

import warnings
from collections.abc import Iterator, Sequence

from ._collection import checked_index, iter_pages
from ._gaps import GapList, IntervalMap, gaps_in_range, note_intervals, note_onsets
from ._properties import IndexedObject, NativeProperty
from .errors import FruityLinkError
from .models import ClipInfo, NoteInfo, PlaylistTrackInfo
from .operations import Operations
from .patterns import Notes, pattern_length_ticks
from .records import ClipMove, ClipResize, PatternClipSpec, Timebase

ResizeLike = ClipResize | tuple[int, int]
MoveLike = ClipMove | tuple[int, int, int]

MAX_TILED_CLIPS = 1000
"""Largest number of clips one ``tile_pattern`` / ``repeat=True`` call will place (refused before any write)."""


class PatternClipLongerThanPatternWarning(UserWarning):
    """A pattern clip was placed longer than the pattern it plays, so most of it is silent.

    FL does NOT loop a pattern clip: the clip plays its pattern once from the clip start and then
    holds silence for the rest of its length. Live-verified 2026-09-18 (FL 26.1.3.5570): a 1-bar
    pattern placed with ``add_patterns([PatternClipSpec(p, 1, 0, 4 * BAR)], enforce_lengths=True)``
    produced the clip ``(0, 1536)`` whose master capture had audio in bar 1 only; bars 2-4 were
    silent. Place one clip per repetition (``repeat=True``, or ``fl.playlist.tile_pattern(...)``)
    instead of one long clip.
    """


class PatternPlacement(int):
    """How many clips a placement wrote, carrying what was placed and the silent-span notice.

    The value is the CLIP COUNT (1 for a single clip, N for a tiled span), so it can be used as a
    plain int. ``notice`` is the ``PatternClipLongerThanPatternWarning`` text when the clip is longer
    than its pattern and was not tiled, else ``""``; ``pattern_length_tick`` is 0 when the host could
    not report the pattern's length.
    """

    pattern: int
    track: int
    start_tick: int
    length_tick: int
    pattern_length_tick: int
    repeated: bool
    notice: str

    def __new__(cls, clips: int, *, pattern: int, track: int, start_tick: int, length_tick: int,
                pattern_length_tick: int, repeated: bool, notice: str = "") -> "PatternPlacement":
        self = super().__new__(cls, clips)
        self.pattern = pattern
        self.track = track
        self.start_tick = start_tick
        self.length_tick = length_tick
        self.pattern_length_tick = pattern_length_tick
        self.repeated = repeated
        self.notice = notice
        return self

    def __repr__(self) -> str:
        return (f"PatternPlacement({int(self)}, pattern={self.pattern}, track={self.track}, "
                f"start_tick={self.start_tick}, length_tick={self.length_tick}, "
                f"pattern_length_tick={self.pattern_length_tick}, repeated={self.repeated}, "
                f"notice={self.notice!r})")


class PatternPlacementBatch(int):
    """``add_patterns`` result: the value stays the number of clips RESIZED by ``enforce_lengths``.

    ``placed`` is how many clips the call wrote (more than the number of specs when ``repeat=True``
    tiled them), ``repeated`` says whether tiling happened, and ``notices`` holds one
    ``PatternClipLongerThanPatternWarning`` text per spec that is longer than its pattern.
    """

    placed: int
    repeated: bool
    notices: tuple[str, ...]

    def __new__(cls, resized: int, *, placed: int, repeated: bool,
                notices: Sequence[str] = ()) -> "PatternPlacementBatch":
        self = super().__new__(cls, resized)
        self.placed = placed
        self.repeated = repeated
        self.notices = tuple(notices)
        return self

    def __repr__(self) -> str:
        return (f"PatternPlacementBatch({int(self)}, placed={self.placed}, repeated={self.repeated}, "
                f"notices={self.notices!r})")


def _pattern_spans(ops: Operations) -> dict[int, int]:
    """Host-reported length per pattern index from ONE ``query_patterns``, or ``{}`` when it cannot answer.

    The over-long-clip advice must never fail or slow down a placement, so a host that cannot describe
    its patterns (an older build, a refused query) simply yields no advice, and a whole ``add_patterns``
    batch shares a single lookup.
    """
    try:
        return {item.index: item.length_tick for item in ops.query_patterns()
                if item.length_tick is not None and item.length_tick > 0}
    except (FruityLinkError, LookupError, ValueError, TypeError):
        return {}


def long_clip_notice(pattern: int, pattern_length_tick: int, length_tick: int) -> str:
    """The text ``PatternClipLongerThanPatternWarning`` carries for one over-long clip."""
    silent = length_tick - pattern_length_tick
    repeats = length_tick / pattern_length_tick if pattern_length_tick else 0
    return (f"Pattern {pattern} is {pattern_length_tick} ticks long but the clip asks for {length_tick} ticks "
            f"({repeats:.2f} pattern lengths): FL does not loop a pattern clip, so it plays once and the last "
            f"{silent} ticks of the clip are SILENT. Place one clip per repetition instead - repeat=True on this "
            f"call, or fl.playlist.tile_pattern({pattern}, track=..., start_tick=..., length_tick={length_tick}).")


def _check_span(spans: dict[int, int], pattern: int, length_tick: int, *,
                stacklevel: int = 3) -> tuple[int, str]:
    """(host pattern length, notice) for a clip of ``length_tick``; warns when the clip outruns the pattern."""
    pattern_length = spans.get(pattern, 0)
    if length_tick <= 0 or pattern_length <= 0 or length_tick <= pattern_length:
        return (pattern_length, "")
    notice = long_clip_notice(pattern, pattern_length, length_tick)
    warnings.warn(PatternClipLongerThanPatternWarning(notice), stacklevel=max(1, stacklevel))
    return (pattern_length, notice)


def tile_specs(pattern: int, *, track: int, start_tick: int, length_tick: int,
               pattern_length_tick: int) -> list[PatternClipSpec]:
    """One ``PatternClipSpec`` per repetition across ``[start_tick, start_tick + length_tick)``.

    The last clip is shortened to whatever is left of the span, so the tiled run ends exactly where
    the caller asked. Pure: no connection is needed.
    """
    if pattern_length_tick <= 0:
        raise ValueError("A tiled pattern needs a positive pattern length.")
    specs: list[PatternClipSpec] = []
    at = start_tick
    end = start_tick + length_tick
    while at < end:
        specs.append(PatternClipSpec(pattern, track, at, min(pattern_length_tick, end - at)))
        at += pattern_length_tick
    return specs


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

    def pattern_length(self, pattern: int) -> int:
        """The host-reported length of a one-based pattern in ticks (``fl.patterns.list()`` ``lengthTick``).

        This is the span one pattern clip actually plays, and therefore the tile width ``tile_pattern``
        uses. Raises ``LookupError`` when the pattern is missing or the host reports no length.
        """
        return pattern_length_ticks(self._ops, checked_index(pattern, minimum=1))

    def tile_pattern(self, pattern: int, *, track: int, start_tick: int, length_tick: int) -> PatternPlacement:
        """Fill ``[start_tick, start_tick + length_tick)`` with ONE CLIP PER PATTERN REPETITION; returns the clip count.

        FL does not loop a pattern clip (live-verified 2026-09-18, see the module docstring), so a
        4-bar span of a 1-bar pattern is four clips, not one 4-bar clip. Clip ``i`` starts at
        ``start_tick + i * pattern_length`` and the LAST clip is shortened to whatever is left of the
        span, so the run ends exactly where you asked. Placed in one native pass (one refresh).

        The tile width is the pattern's own host-reported length (``pattern_length``); a pattern the
        host cannot measure is refused rather than guessed. More than ``MAX_TILED_CLIPS`` (1000) clips
        is refused before any write - that is a sign the wrong pattern or the wrong unit was passed.
        The returned ``PatternPlacement`` is the clip count and carries ``pattern_length_tick``.
        """
        index = checked_index(pattern, minimum=1)
        one_track = checked_index(track, minimum=1)
        if any(type(value) is not int for value in (start_tick, length_tick)) or start_tick < 0 or length_tick <= 0:
            raise ValueError("Require integer ticks with start_tick >= 0 and length_tick > 0.")
        span = self.pattern_length(index)
        count = -(-length_tick // span)
        if count > MAX_TILED_CLIPS:
            raise ValueError(f"Tiling pattern {index} ({span} ticks) across {length_tick} ticks needs {count} clips, "
                             f"over the {MAX_TILED_CLIPS} limit; check the pattern and the units.")
        specs = tile_specs(index, track=one_track, start_tick=start_tick, length_tick=length_tick,
                           pattern_length_tick=span)
        self._ops.add_pattern_clips(clips=specs)
        return PatternPlacement(len(specs), pattern=index, track=one_track, start_tick=start_tick,
                                length_tick=length_tick, pattern_length_tick=span, repeated=True)

    def tile_pattern_beats(self, pattern: int, *, track: int, start_beats: float,
                           length_beats: float) -> PatternPlacement:
        """``tile_pattern`` in beats at the project PPQ; returns the same ``PatternPlacement``."""
        timebase = Timebase(self._ops.get_ppq())
        return self.tile_pattern(pattern, track=track, start_tick=timebase.ticks(start_beats),
                                 length_tick=timebase.ticks(length_beats))

    def add_pattern_ticks(self, pattern: int, *, track: int, start: int, length: int = 0,
                          repeat: bool = False) -> PatternPlacement:
        """Place one pattern clip at ``start`` for ``length`` ticks (0 = the pattern's own length).

        ``repeat=True`` tiles instead: one clip per pattern repetition across the span (see
        ``tile_pattern``), which is what a span longer than the pattern almost always wants. With
        ``repeat=False`` (the default) a ``length`` longer than the pattern places ONE long clip, which
        FL plays once and then leaves silent, so the call emits ``PatternClipLongerThanPatternWarning``
        naming the pattern, its length and the clip length; the returned ``PatternPlacement.notice``
        holds the same text (``""`` when there is nothing to say).
        """
        index = checked_index(pattern, minimum=1)
        one_track = checked_index(track, minimum=1)
        if repeat and length > 0:
            return self.tile_pattern(index, track=one_track, start_tick=start, length_tick=length)
        span, notice = _check_span(_pattern_spans(self._ops) if length > 0 else {}, index, length, stacklevel=3)
        self._ops.add_pattern_clip(pattern=index, track=one_track, start_tick=start, length_tick=length)
        return PatternPlacement(1, pattern=index, track=one_track, start_tick=start, length_tick=length,
                                pattern_length_tick=span, repeated=False, notice=notice)

    def add_pattern(self, pattern: int, *, track: int, start_beats: float, length_beats: float = 0,
                    repeat: bool = False) -> PatternPlacement:
        """``add_pattern_ticks`` in beats at the project PPQ; ``repeat=True`` tiles the span."""
        timebase = Timebase(self._ops.get_ppq())
        return self.add_pattern_ticks(pattern, track=track, start=timebase.ticks(start_beats),
                                      length=timebase.ticks(length_beats), repeat=repeat)

    def add_patterns(self, clips: Sequence[PatternClipSpec], *, enforce_lengths: bool = False,
                     repeat: bool = False) -> PatternPlacementBatch:
        """Place many pattern clips in one native pass (one refresh); the result is the number of clips resized.

        A spec's positive ``length_tick`` is pinned by the host, so the clip keeps it even when the
        pattern's notes overhang (an 8-bar spec stays 8 bars). ``enforce_lengths=True`` additionally
        re-lists the clips afterwards and resizes any placed clip whose length still differs from its
        spec (a safety net for hosts that re-derive the pattern length); ``length_tick`` 0 follows the
        pattern and is never enforced.

        **A pinned clip is not a loop.** FL plays the pattern once from the clip start and the rest of
        the clip is silent (live-verified 2026-09-18), so every spec longer than its pattern emits
        ``PatternClipLongerThanPatternWarning`` and its text is collected in the result's ``notices``.
        ``repeat=True`` expands those specs into one clip per repetition before the native pass (the
        last clip of each span shortened to fit), which is what an 8-bar spec of a 2-bar pattern means;
        the result's ``placed`` counts the clips actually written.
        """
        specs = list(clips)
        notices: list[str] = []
        spans = _pattern_spans(self._ops) if any(spec.length_tick > 0 for spec in specs) else {}
        if repeat:
            expanded: list[PatternClipSpec] = []
            for spec in specs:
                span = spans.get(spec.pattern, 0) if spec.length_tick > 0 else 0
                if span <= 0 or spec.length_tick <= span:
                    expanded.append(spec)
                    continue
                if -(-spec.length_tick // span) > MAX_TILED_CLIPS:
                    raise ValueError(f"Tiling pattern {spec.pattern} ({span} ticks) across {spec.length_tick} ticks "
                                     f"needs more than the {MAX_TILED_CLIPS} clips one call places.")
                expanded.extend(tile_specs(spec.pattern, track=spec.track, start_tick=spec.start_tick,
                                           length_tick=spec.length_tick, pattern_length_tick=span))
            specs = expanded
        else:
            for spec in specs:
                _, notice = _check_span(spans, spec.pattern, spec.length_tick, stacklevel=3)
                if notice:
                    notices.append(notice)
        self._ops.add_pattern_clips(clips=specs)
        if not enforce_lengths:
            return PatternPlacementBatch(0, placed=len(specs), repeated=repeat, notices=notices)
        wanted = {(spec.pattern, spec.track, spec.start_tick): spec.length_tick for spec in specs if spec.length_tick > 0}
        fixes = [ClipResize(clip.index, wanted[key]) for clip in self.clips.list()
                 if clip.source_kind == "pattern"
                 and (key := (clip.source_index, clip.track, clip.start_tick)) in wanted
                 and clip.length_tick != wanted[key]]
        if fixes:
            self._ops.resize_clips(resizes=fixes)
        return PatternPlacementBatch(len(fixes), placed=len(specs), repeated=repeat, notices=notices)

    def onsets(self, channel: int, start_tick: int, end_tick: int) -> tuple[int, ...]:
        """Absolute ticks at which ``channel`` has a note-on inside ``[start_tick, end_tick)``, through
        every unmuted pattern clip (clips are assumed to start at their pattern's beginning; muted notes
        and notes starting past their clip end are silent). Sorted, duplicates removed. Feed the result
        to ``fl.automation.duck`` to follow a kick channel through an arrangement.

        **A clip contributes its pattern's notes ONCE, and that is FL's real behaviour** (live-verified
        2026-09-18, FL 26.1.3.5570): a pattern clip does not loop, so a clip longer than its pattern is
        silent past the pattern length and this reading matches what the engine plays. A span that
        should repeat is one clip per repetition (``tile_pattern`` / ``repeat=True``), and then every
        repetition appears here because each clip contributes its own onsets.
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

        **A clip's pattern is counted ONCE, never looped, and that is FL's real behaviour**
        (live-verified 2026-09-18, FL 26.1.3.5570): a 1-bar pattern in a 4-bar clip sounded in bar 1
        only and bars 2-4 measured silent at the master, so the rest of such a clip is correctly
        reported here as a REST. Tile the span (``tile_pattern`` / ``repeat=True``) to make it play,
        and this reading follows.
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
