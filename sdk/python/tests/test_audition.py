from typing import Any

import pytest
from conftest import RecordingTransport

from fruitylink import RangeIsolation, Studio, isolate_bars, isolate_range
from fruitylink.values import JsonValue

BAR = 96 * 4  # ticks per 4/4 bar at the fixture PPQ

WRITES = {"resize_clips", "slice_clip", "delete_clips", "move_clips", "delete_marker", "add_marker",
          "set_loop_region"}


class FakePlaylist:
    """A minimal FL arrangement: clips keep collection order, a slice inserts sorted by start."""

    def __init__(self, clips: list[tuple[int, int, int]], markers: list[int] | None = None) -> None:
        self.clips: list[dict[str, Any]] = [{"start": start, "length": length, "track": track, "kind": "pattern"}
                                            for start, length, track in clips]
        self.markers = list(markers or [])
        self.marker_names = [f"M{i}" for i in range(len(self.markers))]
        self.loop: tuple[int, int] | None = (0, 99999)

    def __call__(self, method: str, params: dict[str, JsonValue]) -> JsonValue:
        operation = str(params.get("operation", method))
        arguments: Any = params.get("arguments", {})
        if operation == "get_ppq":
            return 96
        if operation == "query_clips":
            items: list[JsonValue] = [{"index": i, "track": c["track"], "startTick": c["start"], "lengthTick": c["length"],
                      "sourceKind": c["kind"], "sourceIndex": 1, "muted": False}
                     for i, c in enumerate(self.clips)]
            return {"items": items, "nextOffset": None, "total": len(items)}
        if operation == "resize_clips":
            for resize in arguments["resizes"]:
                self.clips[resize["index"]]["length"] = resize["lengthTick"]
            return None
        if operation == "slice_clip":
            clip = self.clips[arguments["clipIndex"]]
            tick = arguments["tick"]
            assert clip["start"] < tick < clip["start"] + clip["length"]
            second = dict(clip, start=tick, length=clip["start"] + clip["length"] - tick)
            clip["length"] = tick - clip["start"]
            position = next((i for i, c in enumerate(self.clips) if c["start"] > tick), len(self.clips))
            self.clips.insert(position, second)
            return None
        if operation == "delete_clips":
            for index in sorted(set(arguments["clipIndices"]), reverse=True):
                del self.clips[index]
            return None
        if operation == "move_clips":
            for move in arguments["moves"]:
                assert move["startTick"] >= 0
                self.clips[move["index"]].update(start=move["startTick"], track=move["track"])
            return None
        if operation == "list_markers":
            if not self.markers:
                return "(no markers)"
            return f"{len(self.markers)} markers:\n" + "\n".join(
                f"{n} @ tick {t}" for n, t in zip(self.marker_names, self.markers))
        if operation == "delete_marker":
            del self.marker_names[arguments["index"]]
            del self.markers[arguments["index"]]
            return None
        if operation == "add_marker":
            self.markers.append(arguments["tick"])
            self.marker_names.append(arguments["name"])
            return None
        if operation == "set_loop_region":
            self.loop = None if arguments["endTick"] <= arguments["startTick"] else (arguments["startTick"], arguments["endTick"])
            return None
        raise AssertionError(f"Unexpected operation {operation}")

    def spans(self) -> list[tuple[int, int, int]]:
        return [(int(c["start"]), int(c["length"]), int(c["track"])) for c in self.clips]


def operations(transport: RecordingTransport) -> list[str]:
    return [str(params["operation"]) for _, params in transport.calls]


def test_isolate_range_keeps_only_the_section_and_shifts_it_to_tick_zero(fl: Studio, transport: RecordingTransport) -> None:
    playlist = FakePlaylist([
        (0, 10 * BAR, 1),           # ends exactly at the range start: dropped
        (10 * BAR, 10 * BAR, 1),    # the section itself
        (15 * BAR, 10 * BAR, 2),    # crosses the range end: shortened, source start untouched
        (20 * BAR, 10 * BAR, 1),    # starts at the range end: dropped
    ], markers=[0, 30 * BAR])
    transport.handler = playlist

    result = isolate_range(fl, 10 * BAR, 10 * BAR)

    assert result == RangeIsolation(start_tick=10 * BAR, length_tick=10 * BAR, kept_clips=2, deleted_clips=2,
                                    cut_clips=0, deleted_markers=2)
    assert playlist.spans() == [(0, 10 * BAR, 1), (5 * BAR, 5 * BAR, 2)]
    assert playlist.markers == [] and playlist.loop is None
    assert result.to_dict()["kept_clips"] == 2
    assert operations(transport) == ["query_clips", "resize_clips", "query_clips", "delete_clips", "query_clips",
                                     "move_clips", "list_markers", "delete_marker", "delete_marker", "set_loop_region"]
    assert transport.calls[7][1]["arguments"] == {"index": 1}  # trailing marker first, so index 0 stays valid


def test_clips_crossing_the_start_are_refused_before_any_write(fl: Studio, transport: RecordingTransport) -> None:
    transport.handler = FakePlaylist([(0, 4 * BAR, 1), (2 * BAR, 4 * BAR, 2)])

    with pytest.raises(ValueError, match=r"Clips \[0, 1\] begin before tick 1152.*cut_clips=True"):
        isolate_range(fl, 3 * BAR, BAR)

    assert operations(transport) == ["query_clips"]


def test_cut_clips_slices_at_the_start_and_relists_after_each_cut(fl: Studio, transport: RecordingTransport) -> None:
    # The second crossing clip's index changes once the first slice inserts its new half before it.
    playlist = FakePlaylist([(0, 4 * BAR, 1), (BAR, 4 * BAR, 2), (3 * BAR, BAR, 3)])
    transport.handler = playlist

    result = isolate_range(fl, 2 * BAR, 2 * BAR, cut_clips=True)

    assert result.cut_clips == 2 and result.kept_clips == 3 and result.deleted_clips == 2
    assert sorted(playlist.spans()) == [(0, 2 * BAR, 1), (0, 2 * BAR, 2), (BAR, BAR, 3)]
    assert operations(transport).count("slice_clip") == 2
    assert operations(transport).index("resize_clips") < operations(transport).index("slice_clip")


def test_no_clip_in_range_raises_without_writing(fl: Studio, transport: RecordingTransport) -> None:
    transport.handler = FakePlaylist([(0, BAR, 1)], markers=[8 * BAR])

    with pytest.raises(ValueError, match="No clip lies within ticks 768..1536"):
        isolate_range(fl, 2 * BAR, 2 * BAR)

    assert not WRITES & set(operations(transport))


def test_range_from_tick_zero_deletes_tail_without_moving(fl: Studio, transport: RecordingTransport) -> None:
    playlist = FakePlaylist([(0, 4 * BAR, 1), (4 * BAR, 4 * BAR, 1)])
    transport.handler = playlist

    result = isolate_range(fl, 0, 4 * BAR)

    assert result.kept_clips == 1 and "move_clips" not in operations(transport)
    assert playlist.spans() == [(0, 4 * BAR, 1)]


def test_isolate_bars_uses_project_ppq_for_an_inclusive_bar_span(fl: Studio, transport: RecordingTransport) -> None:
    playlist = FakePlaylist([(0, 200 * BAR, 1)])
    transport.handler = playlist

    result = isolate_bars(fl, 49, 64, cut_clips=True)

    assert (result.start_tick, result.length_tick) == (48 * BAR, 16 * BAR)
    assert playlist.spans() == [(0, 16 * BAR, 1)]


@pytest.mark.parametrize("start, length", [(-1, BAR), (0, 0), (True, BAR), (BAR, 1.5), ("0", BAR)])
def test_invalid_ticks_are_rejected_before_any_request(fl: Studio, transport: RecordingTransport,
                                                        start: Any, length: Any) -> None:
    with pytest.raises(ValueError):
        isolate_range(fl, start, length)
    assert transport.calls == []


@pytest.mark.parametrize("start, end", [(0, 4), (5, 4), (1.0, 4), (True, 2)])
def test_invalid_bars_are_rejected_before_any_request(fl: Studio, transport: RecordingTransport,
                                                       start: Any, end: Any) -> None:
    with pytest.raises(ValueError):
        isolate_bars(fl, start, end)
    assert transport.calls == []


def test_tail_beats_places_an_end_marker_past_the_range(fl: Studio, transport: RecordingTransport) -> None:
    # Live 2026-09-14: with every marker deleted FL stops the render at the last clip end, so tails were cut.
    playlist = FakePlaylist([(0, 200 * BAR, 1)], markers=[0, 30 * BAR])
    transport.handler = playlist

    result = isolate_bars(fl, 49, 64, cut_clips=True, tail_beats=8)

    assert result.tail_ticks == 8 * 96 and result.deleted_markers == 2 and result.to_dict()["tail_ticks"] == 768
    assert playlist.spans() == [(0, 16 * BAR, 1)]
    assert list(zip(playlist.marker_names, playlist.markers)) == [("End", 16 * BAR + 8 * 96)]
    ops = operations(transport)
    assert ops.index("add_marker") > ops.index("set_loop_region") and ops.count("add_marker") == 1


def test_tail_beats_defaults_to_zero_and_adds_no_marker(fl: Studio, transport: RecordingTransport) -> None:
    playlist = FakePlaylist([(0, 4 * BAR, 1)], markers=[8 * BAR])
    transport.handler = playlist

    result = isolate_range(fl, 0, 4 * BAR, tail_beats=0)

    assert result.tail_ticks == 0 and playlist.markers == [] and "add_marker" not in operations(transport)
    fractional = isolate_range(fl, 0, 4 * BAR, tail_beats=2.5)
    assert fractional.tail_ticks == 240 and playlist.markers == [4 * BAR + 240]


@pytest.mark.parametrize("tail", [-1, float("nan"), float("inf"), True, "8"])
def test_invalid_tail_is_rejected_before_any_request(fl: Studio, transport: RecordingTransport, tail: Any) -> None:
    with pytest.raises(ValueError):
        isolate_range(fl, 0, BAR, tail_beats=tail)
    with pytest.raises(ValueError):
        isolate_bars(fl, 1, 2, tail_beats=tail)
    assert transport.calls == []
