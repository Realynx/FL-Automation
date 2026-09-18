"""One-based patterns and explicit beat/tick note authoring."""

from collections.abc import Iterator, Sequence

from ._collection import checked_index, iter_pages
from ._gaps import gaps_in_range, note_intervals
from ._properties import IndexedObject, NativeProperty
from .models import Gap, NoteInfo, PatternInfo
from .operations import Operations
from .records import ClipResize, NoteEdit, NoteRef, NoteSpec, Timebase


def pattern_length_ticks(ops: Operations, pattern: int) -> int:
    """The host-reported length of a one-based pattern in ticks (``query_patterns`` ``lengthTick``).

    This is the span a playlist clip of that pattern actually plays: FL does not loop a pattern clip,
    so it is also the tile width ``fl.playlist.tile_pattern`` uses. Raises ``LookupError`` when the
    pattern is not in the project or the host reports no usable length for it.
    """
    for item in ops.query_patterns():
        if item.index == pattern:
            if item.length_tick is None or item.length_tick <= 0:
                raise LookupError(f"Pattern {pattern} has no host-reported length; pass explicit tick lengths.")
            return item.length_tick
    raise LookupError(f"Pattern {pattern} is not in the project.")


class Notes:
    def __init__(self, ops: Operations, pattern: int) -> None:
        self._ops = ops
        self.pattern = checked_index(pattern, minimum=1)

    def list(self, channel: int = -1) -> tuple[NoteInfo, ...]:
        return tuple(iter_pages(lambda offset: self._ops.query_notes(pattern=self.pattern, channel=channel, offset=offset)))

    def list_text(self, channel: int = -1, offset: int = 0) -> str:
        return self._ops.get_notes(pattern=self.pattern, channel=channel, offset=offset)

    def add(self, notes: Sequence[NoteSpec]) -> None:
        self._ops.add_notes(pattern=self.pattern, notes=notes)

    def add_ticks(self, *, channel: int, key: int, start: int, length: int, velocity: int = 100) -> None:
        self.add([NoteSpec(channel, key, start, length, velocity)])

    def add_beats(self, *, channel: int, key: int, start: float, length: float, velocity: int = 100) -> None:
        timebase = Timebase(self._ops.get_ppq())
        self.add_ticks(channel=channel, key=key, start=timebase.ticks(start), length=timebase.ticks(length), velocity=velocity)

    def edit(self, edits: Sequence[NoteEdit], *, allow_multiple: bool = False, preserve_clips: bool = False) -> int:
        """Edit existing notes in place; returns the number changed. An edit whose (channel, key,
        start_tick[, length_tick]) matches several stacked notes is refused before any write unless
        ``allow_multiple`` is True, which applies it to all of them.

        The host keeps this pattern's playlist clips at their current lengths (FL alone would
        re-derive them from the edited notes). ``preserve_clips=True`` re-checks that from here:
        it lists the pattern's clips before and after the edit and resizes any whose length changed
        (two extra queries; a safety net for hosts without the fix).
        """
        before = self._clip_lengths() if preserve_clips else None
        changed = self._ops.edit_notes(pattern=self.pattern, edits=edits, allow_multiple=allow_multiple)
        if before:
            self._restore_clips(before)
        return changed

    def delete(self, targets: Sequence[NoteRef], *, allow_multiple: bool = False, preserve_clips: bool = False) -> int:
        """Delete the addressed notes; returns the number deleted. A target matching several stacked
        notes is refused before any write unless ``allow_multiple`` is True, which deletes all of them.

        Playlist clips of this pattern keep their lengths: FL alone shrinks every clip of the pattern
        to the remaining notes (a deleted last note pulled 3072-tick clips back to 1536), which the
        host now undoes as part of the delete. ``preserve_clips=True`` re-checks from here with two
        extra clip queries and resizes any clip whose length still changed. Change clip lengths
        deliberately with ``fl.clips.resize``.
        """
        before = self._clip_lengths() if preserve_clips else None
        deleted = self._ops.delete_notes(pattern=self.pattern, targets=targets, allow_multiple=allow_multiple)
        if before:
            self._restore_clips(before)
        return deleted

    def _clip_lengths(self) -> dict[int, tuple[int, int, int]]:
        """(track, start_tick, length_tick) of every playlist clip playing this pattern, by clip index."""
        clips = iter_pages(lambda offset: self._ops.query_clips(offset=offset))
        return {clip.index: (clip.track, clip.start_tick, clip.length_tick) for clip in clips
                if clip.source_kind == "pattern" and clip.source_index == self.pattern}

    def _restore_clips(self, before: dict[int, tuple[int, int, int]]) -> int:
        after = self._clip_lengths()
        fixes = [ClipResize(index, length) for index, (track, start, length) in before.items()
                 if index in after and after[index][:2] == (track, start) and after[index][2] != length]
        if fixes:
            self._ops.resize_clips(resizes=fixes)
        return len(fixes)


class Pattern(IndexedObject):
    name = NativeProperty("get_pattern_name", "set_pattern_name", "index", "name", str)

    @property
    def notes(self) -> Notes:
        return Notes(self._ops, self.index)

    @property
    def length_tick(self) -> int:
        """Host-reported pattern length in ticks; the span one playlist clip of this pattern plays.

        FL does not loop a pattern clip, so a playlist span longer than this needs one clip per
        repetition (``fl.playlist.tile_pattern``). Raises ``LookupError`` when the host reports none.
        """
        return pattern_length_ticks(self._ops, self.index)

    def select(self) -> None:
        self._ops.select_pattern(index=self.index)

    def clear(self) -> None:
        self._ops.clear_pattern(index=self.index)

    def clone(self) -> "Pattern | None":
        index = self._ops.clone_pattern(source_pattern=self.index)
        return Pattern(self._ops, index) if index > 0 else None

    def gaps(self, channel: int | None = None, min_ticks: int | None = None, min_beats: float = 1.0,
             *, end_tick: int | None = None) -> list[Gap]:
        """Rest regions per channel, sorted by time, computed from this pattern's note snapshot.

        A note occupies [start, start + length); muted notes are silent. The pattern spans
        tick 0 to ``end_tick`` (default: the later of the host-reported pattern length and the
        last note end, across all channels). ``channel=None`` reports every channel that has
        notes; an explicit channel with no notes yields one gap over the whole span. Gaps
        shorter than ``min_ticks`` (default ``min_beats`` at the project PPQ) are dropped.
        """
        if channel is not None:
            checked_index(channel)
        timebase = Timebase(self._ops.get_ppq())
        threshold = timebase.ticks(min_beats) if min_ticks is None else min_ticks
        notes = self.notes.list()
        if end_tick is None:
            reported = [item.length_tick or 0 for item in self._ops.query_patterns() if item.index == self.index]
            end_tick = max([note.start_tick + note.length_tick for note in notes] + reported, default=0)
        elif type(end_tick) is not int or end_tick < 0:
            raise ValueError("end_tick must be a nonnegative integer.")
        channels = {note.channel for note in notes} if channel is None else {channel}
        return gaps_in_range(note_intervals(notes, channel=channel), channels, start=0, end=end_tick,
                             min_ticks=threshold, ppq=timebase.ppq)


class Patterns:
    def __init__(self, ops: Operations) -> None:
        self._ops = ops

    def __getitem__(self, index: int) -> Pattern:
        return Pattern(self._ops, checked_index(index, minimum=1))

    def __iter__(self) -> Iterator[Pattern]:
        return (self[item.index] for item in self.list())

    @property
    def current(self) -> Pattern:
        return self[self._ops.get_current_pattern()]

    def create(self, name: str | None = None) -> Pattern:
        pattern = self[self._ops.create_pattern()]
        if name is not None:
            pattern.name = name
        return pattern

    def list(self) -> tuple[PatternInfo, ...]:
        return self._ops.query_patterns()

    def list_text(self) -> str:
        return self._ops.list_patterns()

    def find(self, name: str) -> Pattern:
        matches = [item.index for item in self.list() if item.name == name]
        if len(matches) != 1:
            raise LookupError(f"Expected one pattern named {name!r}; found {len(matches)}.")
        return self[matches[0]]
