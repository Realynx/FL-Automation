"""Local rest-region arithmetic shared by pattern and playlist gap queries. No native calls."""

from collections.abc import Iterable

from .models import Gap, NoteInfo

Interval = tuple[int, int]
IntervalMap = dict[int, list[Interval]]
GapList = list[Gap]  # aliases exist because ``list`` is shadowed by ``Playlist.list`` inside that class body


def note_intervals(notes: Iterable[NoteInfo], *, offset: int = 0, within: int | None = None,
                   channel: int | None = None) -> IntervalMap:
    """Group sounding notes into [start, end) tick intervals per channel.

    Muted notes are silent and ignored. ``offset`` shifts pattern-local ticks to absolute time.
    ``within`` keeps only notes that *start* before that pattern-local tick (a clip's length);
    a note that starts inside the clip keeps its full length, matching FL's playback.
    """
    intervals: IntervalMap = {}
    for note in notes:
        if note.muted or (channel is not None and note.channel != channel):
            continue
        if within is not None and note.start_tick >= within:
            continue
        start = offset + note.start_tick
        intervals.setdefault(note.channel, []).append((start, start + note.length_tick))
    return intervals


def note_onsets(notes: Iterable[NoteInfo], *, channel: int, offset: int = 0, within: int | None = None) -> list[int]:
    """Absolute start ticks of one channel's sounding notes, using the same clip rules as note_intervals."""
    return [start for start, _ in note_intervals(notes, offset=offset, within=within, channel=channel).get(channel, [])]


def merged(intervals: Iterable[Interval]) -> list[Interval]:
    result: list[Interval] = []
    for start, end in sorted(intervals):
        if result and start <= result[-1][1]:
            result[-1] = (result[-1][0], max(result[-1][1], end))
        else:
            result.append((start, end))
    return result


def gaps_in_range(intervals: IntervalMap, channels: Iterable[int], *, start: int, end: int,
                  min_ticks: int, ppq: int) -> list[Gap]:
    """Complement of each channel's merged intervals inside [start, end), at least ``min_ticks`` long."""
    if type(min_ticks) is not int or min_ticks < 1:
        raise ValueError("min_ticks must be a positive integer.")
    gaps: list[Gap] = []
    for channel in sorted(set(channels)):
        cursor = start
        for note_start, note_end in merged(intervals.get(channel, ())) + [(end, end)]:
            if note_start - cursor >= min_ticks:
                gaps.append(_gap(channel, cursor, min(note_start, end), ppq))
            cursor = max(cursor, note_end)
            if cursor >= end:
                break
    gaps.sort(key=lambda gap: (gap.start_tick, gap.channel))
    return gaps


def _gap(channel: int, start: int, end: int, ppq: int) -> Gap:
    return Gap(channel, start, end, end - start, start / ppq, (end - start) / ppq)
