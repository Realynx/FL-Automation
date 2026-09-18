"""Arrangement helpers: rest regions, sidechain pump curves, song end markers, free tracks.

Song facts mirror a real project: 140 BPM, PPQ 96, so a 4/4 bar is 384 ticks.
"""

from typing import Any

import pytest
from conftest import RecordingTransport

from fruitylink import (
    AutomationClipResult,
    AutomationTarget,
    Gap,
    ProtocolError,
    PumpResult,
    Studio,
    Timebase,
)
from fruitylink.automation import pump_points
from fruitylink.project import Marker
from fruitylink.values import JsonValue

PPQ = 96
BAR = 4 * PPQ


def note(channel: int, start: int, length: int, *, index: int = 0, muted: bool = False) -> dict[str, JsonValue]:
    return {"index": index, "channel": channel, "key": 60, "startTick": start, "lengthTick": length,
            "velocity": 100, "muted": muted}


def page(items: list[dict[str, JsonValue]]) -> dict[str, JsonValue]:
    return {"items": list(items), "nextOffset": None, "total": len(items)}


def clip(index: int, track: int, start: int, length: int, *, kind: str = "pattern", source: int = 1,
         muted: bool = False) -> dict[str, JsonValue]:
    return {"index": index, "track": track, "startTick": start, "lengthTick": length, "sourceKind": kind,
            "sourceIndex": source, "muted": muted}


def pattern_info(index: int, length: int | None) -> dict[str, JsonValue]:
    return {"index": index, "name": f"Pattern {index}", "lengthTick": length, "noteCount": None, "current": False}


# Kick on every beat (channel 0), a bar-long pad chord in bars 1 and 3 (channel 1), one muted note.
PATTERN_NOTES = [note(0, beat * PPQ, PPQ // 2) for beat in range(8)] + [
    note(1, 0, BAR), note(1, 0, BAR // 2), note(1, 2 * BAR, BAR), note(1, BAR, BAR, muted=True)]


def install_pattern(transport: RecordingTransport, notes: list[dict[str, JsonValue]], length: int | None) -> None:
    transport.responses["query_notes"] = page(notes)
    transport.responses["query_patterns"] = [pattern_info(1, length)]


def test_pattern_gaps_reports_rests_per_channel_sorted_by_time(fl: Studio, transport: RecordingTransport) -> None:
    install_pattern(transport, PATTERN_NOTES, 4 * BAR)
    assert fl.patterns[1].gaps() == [
        Gap(1, BAR, 2 * BAR, BAR, 4.0, 4.0),
        Gap(0, 720, 4 * BAR, 816, 7.5, 8.5),
        Gap(1, 3 * BAR, 4 * BAR, BAR, 12.0, 4.0),
    ]
    assert [call[1]["operation"] for call in transport.calls] == ["get_ppq", "query_notes", "query_patterns"]


def test_pattern_gaps_min_beats_uses_project_ppq_and_min_ticks_overrides(fl: Studio,
                                                                       transport: RecordingTransport) -> None:
    install_pattern(transport, PATTERN_NOTES, 4 * BAR)
    half_beats = [gap for gap in fl.patterns[1].gaps(channel=0, min_beats=0.5) if gap.start_tick < 7 * PPQ]
    assert [(gap.start_tick, gap.length_tick) for gap in half_beats] == [(PPQ // 2 + beat * PPQ, PPQ // 2)
                                                                        for beat in range(7)]
    assert fl.patterns[1].gaps(channel=0, min_ticks=49, min_beats=0.1) == [Gap(0, 720, 4 * BAR, 816, 7.5, 8.5)]
    transport.responses["get_ppq"] = 2 * PPQ
    assert fl.patterns[1].gaps(channel=0, min_beats=0.5) == [Gap(0, 720, 4 * BAR, 816, 3.75, 4.25)]


def test_pattern_gaps_span_defaults_to_reported_length_or_last_note(fl: Studio,
                                                                  transport: RecordingTransport) -> None:
    install_pattern(transport, [note(0, 0, PPQ), note(0, 3 * PPQ, PPQ)], None)
    assert fl.patterns[1].gaps() == [Gap(0, PPQ, 3 * PPQ, 2 * PPQ, 1.0, 2.0)]
    assert fl.patterns[1].gaps(end_tick=2 * BAR)[-1] == Gap(0, 4 * PPQ, 2 * BAR, 4 * PPQ, 4.0, 4.0)


def test_pattern_gaps_for_silent_channel_and_empty_pattern(fl: Studio, transport: RecordingTransport) -> None:
    install_pattern(transport, PATTERN_NOTES, 4 * BAR)
    assert fl.patterns[1].gaps(channel=5) == [Gap(5, 0, 4 * BAR, 4 * BAR, 0.0, 16.0)]
    install_pattern(transport, [], None)
    assert fl.patterns[1].gaps() == []


@pytest.mark.parametrize("kwargs", [{"channel": -1}, {"min_ticks": 0}, {"end_tick": -1}, {"end_tick": 1.5}])
def test_pattern_gaps_rejects_invalid_arguments(fl: Studio, transport: RecordingTransport,
                                                kwargs: dict[str, Any]) -> None:
    install_pattern(transport, PATTERN_NOTES, 4 * BAR)
    with pytest.raises((ValueError, IndexError)):
        fl.patterns[1].gaps(**kwargs)


def install_arrangement(transport: RecordingTransport) -> None:
    patterns: dict[int, list[dict[str, JsonValue]]] = {
        1: [note(0, 0, PPQ), note(0, 2 * PPQ, PPQ)],  # beats 1 and 3 of a one-bar pattern
        2: [note(1, 0, 2 * BAR), note(1, BAR, PPQ)],  # a two-bar pad; second note starts past a one-bar clip
    }
    clips = [clip(0, 1, 0, BAR, source=1), clip(1, 1, BAR, BAR, source=1), clip(2, 2, 2 * BAR, BAR, source=2),
             clip(3, 3, 0, 4 * BAR, kind="channel", source=7), clip(4, 4, 3 * BAR, BAR, source=1, muted=True)]

    def handler(method: str, params: dict[str, JsonValue]) -> JsonValue:
        operation = params.get("operation")
        arguments = params.get("arguments")
        assert isinstance(arguments, dict)
        if operation == "get_ppq":
            return PPQ
        if operation == "query_clips":
            return page(clips)
        if operation == "query_notes":
            return page(patterns[int(str(arguments["pattern"]))])
        raise AssertionError(operation)

    transport.handler = handler


def test_playlist_gaps_maps_clip_notes_to_absolute_bars(fl: Studio, transport: RecordingTransport) -> None:
    install_arrangement(transport)
    assert fl.playlist.gaps(1, 4, min_beats=1) == [
        Gap(1, 0, 2 * BAR, 2 * BAR, 0.0, 8.0),
        Gap(0, PPQ, 2 * PPQ, PPQ, 1.0, 1.0),
        Gap(0, 3 * PPQ, 4 * PPQ, PPQ, 3.0, 1.0),
        Gap(0, 5 * PPQ, 6 * PPQ, PPQ, 5.0, 1.0),
        Gap(0, 7 * PPQ, 4 * BAR, 9 * PPQ, 7.0, 9.0),
    ]
    operations = [call[1]["operation"] for call in transport.calls]
    assert operations.count("query_notes") == 2, "notes are fetched once per distinct pattern"


def test_playlist_gaps_note_extends_past_clip_and_range_clips_edges(fl: Studio, transport: RecordingTransport) -> None:
    install_arrangement(transport)
    assert fl.playlist.gaps(3, 5, channel=1) == [Gap(1, 4 * BAR, 5 * BAR, BAR, 16.0, 4.0)]
    assert fl.playlist.gaps(2, 2, channel=0, min_beats=0.5) == [
        Gap(0, 5 * PPQ, 6 * PPQ, PPQ, 5.0, 1.0), Gap(0, 7 * PPQ, 2 * BAR, PPQ, 7.0, 1.0)]


@pytest.mark.parametrize("args,kwargs", [((0, 4), {}), ((4, 3), {}), ((1, 4), {"channel": -1}),
                                         ((1, 4), {"beats_per_bar": 0}), ((1.0, 4), {})])
def test_playlist_gaps_rejects_invalid_ranges(fl: Studio, transport: RecordingTransport, args: Any,
                                              kwargs: dict[str, Any]) -> None:
    install_arrangement(transport)
    with pytest.raises((ValueError, IndexError)):
        fl.playlist.gaps(*args, **kwargs)


def test_first_free_track_skips_pattern_and_automation_clips(fl: Studio, transport: RecordingTransport) -> None:
    install_arrangement(transport)
    assert fl.playlist.first_free_track(0, BAR) == 2
    assert fl.playlist.first_free_track(2 * BAR, 3 * BAR) == 1, "track 1's clips end before bar 3"
    assert fl.playlist.first_free_track(2 * BAR, 3 * BAR, above=2) == 4
    assert fl.playlist.first_free_track(4 * BAR, 5 * BAR) == 1
    assert fl.playlist.first_free_track(3 * BAR, 4 * BAR, above=3) == 5, "muted clips still occupy a track"


def test_first_free_track_reports_a_full_playlist(fl: Studio, transport: RecordingTransport) -> None:
    transport.responses["query_clips"] = page([clip(i, i + 1, 0, BAR) for i in range(500)])
    with pytest.raises(LookupError):
        fl.playlist.first_free_track(0, 1)


@pytest.mark.parametrize("start,end,above", [(-1, 1, 1), (4, 4, 1), (0, 1, 0), (0, 1, 501), (0.5, 1, 1)])
def test_first_free_track_rejects_invalid_arguments_before_any_request(fl: Studio, transport: RecordingTransport,
                                                                      start: Any, end: Any, above: Any) -> None:
    with pytest.raises(ValueError):
        fl.playlist.first_free_track(start, end, above=above)
    assert transport.calls == []


def test_pump_points_dip_recover_hold_per_beat() -> None:
    points = pump_points(8, ppq=PPQ)
    assert len(points) == 24
    assert [(p.time_beats, p.value, p.tension) for p in points[:4]] == [
        (0, 0.5, 0), (0.75, 1.0, 0.5), (1 - 1 / PPQ, 1.0, 0), (1, 0.5, 0)]
    assert points[-1].time_beats == 8 and points[-1].value == 1.0
    assert all(a.time_beats < b.time_beats for a, b in zip(points, points[1:]))


def test_pump_points_partial_final_hit_floor_and_spacing() -> None:
    points = pump_points(2.5, ppq=PPQ, floor=0.3, ceiling=0.8, beats_per_hit=1, recovery_beats=0.75)
    assert [(p.time_beats, p.value) for p in points] == [
        (0, 0.3), (0.75, 0.8), (1 - 1 / PPQ, 0.8), (1, 0.3), (1.75, 0.8), (2 - 1 / PPQ, 0.8), (2, 0.3), (2.5, 0.8)]
    saw = pump_points(4, ppq=PPQ, beats_per_hit=2, recovery_beats=2)
    assert [(p.time_beats, p.value) for p in saw] == [(0, 0.5), (2 - 1 / PPQ, 1.0), (2, 0.5), (4, 1.0)]


@pytest.mark.parametrize("kwargs", [{"depth": 1.5}, {"depth": -0.1}, {"floor": 1.0}, {"ceiling": 0}, {"tension": 2},
                                    {"recovery_beats": 0}, {"beats_per_hit": 0}, {"depth": True},
                                    {"depth": float("nan")}])
def test_pump_points_rejects_invalid_settings(kwargs: dict[str, Any]) -> None:
    with pytest.raises(ValueError):
        pump_points(8, ppq=PPQ, **kwargs)


def test_pump_points_limit() -> None:
    with pytest.raises(ValueError, match="4000"):
        pump_points(2000, ppq=PPQ)


def test_pump_creates_then_writes_envelope(fl: Studio, transport: RecordingTransport) -> None:
    transport.responses["create_automation_clip"] = {"channel": 19, "clipIndex": 100}
    target = AutomationTarget.channel_volume(3)
    result = fl.automation.pump(target, 8 * BAR, 2 * BAR, track=12, depth=0.6, ceiling=0.78, name="Pad pump")
    assert result == PumpResult(AutomationClipResult(19, 100), 24)
    operations = [call[1]["operation"] for call in transport.calls]
    assert operations == ["get_ppq", "create_automation_clip", "set_automation_points"]
    create_args = transport.calls[1][1]["arguments"]
    assert isinstance(create_args, dict)
    assert (create_args["track"], create_args["startTick"], create_args["lengthTick"], create_args["name"]) == (
        12, 8 * BAR, 2 * BAR, "Pad pump")
    points_args = transport.calls[2][1]["arguments"]
    assert isinstance(points_args, dict) and points_args["channel"] == 19
    points = points_args["points"]
    assert isinstance(points, list) and len(points) == 24
    first, last = points[0], points[-1]
    assert isinstance(first, dict) and isinstance(last, dict)
    assert first["timeBeats"] == 0 and isinstance(first["value"], float) and abs(first["value"] - 0.18) < 1e-9
    assert (last["timeBeats"], last["value"]) == (8.0, 0.78)


@pytest.mark.parametrize("track,start,length", [(0, 0, BAR), (1, -1, BAR), (1, 0, 0)])
def test_pump_rejects_bad_placement_before_any_request(fl: Studio, transport: RecordingTransport,
                                                     track: int, start: int, length: int) -> None:
    with pytest.raises(ValueError):
        fl.automation.pump(AutomationTarget.mixer_volume(2), start, length, track=track)
    assert transport.calls == []


def install_markers(transport: RecordingTransport, markers: list[tuple[str, int]]) -> list[tuple[str, int]]:
    state = list(markers)

    def handler(method: str, params: dict[str, JsonValue]) -> JsonValue:
        operation = params.get("operation")
        arguments = params.get("arguments")
        assert isinstance(arguments, dict)
        if operation == "get_ppq":
            return PPQ
        if operation == "list_markers":
            lines = [f"{name or '(marker)'} @ tick {tick} (bar {tick // BAR + 1})" for name, tick in state]
            return f"{len(state)} markers:\n" + "\n".join(lines)
        if operation == "delete_marker":
            del state[int(str(arguments["index"]))]
            return None
        if operation == "add_marker":
            state.append((str(arguments["name"]), int(str(arguments["tick"]))))
            state.sort(key=lambda item: item[1])
            return None
        raise AssertionError(operation)

    transport.handler = handler
    return state


def test_set_song_end_replaces_same_named_markers_and_returns_listing(fl: Studio,
                                                                     transport: RecordingTransport) -> None:
    state = install_markers(transport, [("Intro", 0), ("End", 100 * BAR), ("Drop", 40 * BAR), ("End", 90 * BAR)])
    assert fl.transport.set_song_end(121) == Marker(2, "End", 120 * BAR)
    assert state == [("Intro", 0), ("Drop", 40 * BAR), ("End", 120 * BAR)]
    operations = [call[1]["operation"] for call in transport.calls]
    assert operations == ["get_ppq", "list_markers", "delete_marker", "delete_marker", "add_marker", "list_markers"]
    assert [call[1]["arguments"] for call in transport.calls if call[1]["operation"] == "delete_marker"] == [
        {"index": 3}, {"index": 1}], "delete from the end so earlier indices stay valid"


def test_set_song_end_by_tick_with_custom_name(fl: Studio, transport: RecordingTransport) -> None:
    state = install_markers(transport, [("Intro", 0)])
    assert fl.transport.set_song_end(tick=4000, name="Fin") == Marker(1, "Fin", 4000)
    assert state == [("Intro", 0), ("Fin", 4000)]
    assert [call[1]["operation"] for call in transport.calls] == ["list_markers", "add_marker", "list_markers"]


def test_set_song_end_detects_a_host_that_did_not_list_the_marker(fl: Studio, transport: RecordingTransport) -> None:
    transport.responses["list_markers"] = "(no markers)"
    with pytest.raises(ProtocolError):
        fl.transport.set_song_end(9)


def test_set_song_end_after_bar_keeps_that_bar(fl: Studio, transport: RecordingTransport) -> None:
    """bar=N ends the song BEFORE bar N; after_bar=N keeps bar N. A DeepSeek run passed bar=96 for a
    96-bar song and lost its last bar."""
    state = install_markers(transport, [])

    assert fl.transport.set_song_end(after_bar=96) == Marker(0, "End", 96 * BAR)
    assert state == [("End", 96 * BAR)]
    assert fl.transport.set_song_end(96).tick == 95 * BAR, "bar=96 still means the START of bar 96"
    assert fl.transport.set_song_end(97).tick == fl.transport.set_song_end(after_bar=96).tick


def test_set_song_end_after_bar_respects_beats_per_bar(fl: Studio, transport: RecordingTransport) -> None:
    install_markers(transport, [])
    assert fl.transport.set_song_end(after_bar=8, beats_per_bar=3).tick == 24 * PPQ


@pytest.mark.parametrize("args,kwargs", [((), {}), ((2,), {"tick": 5}), ((0,), {}), ((), {"tick": -1}),
                                         ((), {"tick": 1.5}), ((2,), {"name": ""}), ((2,), {"beats_per_bar": 0}),
                                         ((2,), {"after_bar": 2}), ((), {"after_bar": 2, "tick": 5}),
                                         ((), {"after_bar": 0}), ((), {"after_bar": -1}),
                                         ((), {"after_bar": True}), ((), {"after_bar": 2.0})])
def test_set_song_end_rejects_invalid_arguments_before_any_write(fl: Studio, transport: RecordingTransport,
                                                                 args: Any, kwargs: dict[str, Any]) -> None:
    with pytest.raises(ValueError):
        fl.transport.set_song_end(*args, **kwargs)
    assert all(call[1]["operation"] == "get_ppq" for call in transport.calls)


def test_timebase_bar_start_is_one_based() -> None:
    timebase = Timebase(PPQ)
    assert (timebase.bar_start(1), timebase.bar_start(105), timebase.bar_start(2, beats_per_bar=3)) == (
        0, 104 * BAR, 3 * PPQ)
    with pytest.raises(ValueError):
        timebase.bar_start(0)
