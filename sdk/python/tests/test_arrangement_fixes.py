"""Parking Lot Moon fix batch (arrangement): clip lengths, clip call shapes, automation inventory,
hit-following ducks, tiling, settled seeks and the plugin_parameter argument orders.

Song facts mirror the project: PPQ 96, 4/4, so a bar is 384 ticks.
"""

from typing import Any

import pytest
from conftest import RecordingTransport

from fruitylink import (
    TENSION_EASE_IN,
    TENSION_EASE_OUT,
    AutomationChannelInfo,
    AutomationClipResult,
    AutomationPointSpec,
    AutomationTarget,
    ClipMove,
    ClipResize,
    NoteRef,
    PatternClipSpec,
    PumpResult,
    SeekResult,
    Studio,
)
from fruitylink.automation import duck_points, parse_automation_link, pump_points, tile_points
from fruitylink.models import ClipInfo, PluginParameterInfo
from fruitylink.values import JsonValue

PPQ = 96
BAR = 4 * PPQ
TICK = 1 / PPQ


def operations(transport: RecordingTransport) -> list[str]:
    return [str(call[1]["operation"]) for call in transport.calls]


def arguments(transport: RecordingTransport, position: int) -> dict[str, JsonValue]:
    value = transport.calls[position][1]["arguments"]
    assert isinstance(value, dict)
    return value


def page(items: list[dict[str, JsonValue]]) -> dict[str, JsonValue]:
    return {"items": list(items), "nextOffset": None, "total": len(items)}


def clip(index: int, track: int, start: int, length: int, *, kind: str = "pattern", source: int = 1,
         muted: bool = False) -> dict[str, JsonValue]:
    return {"index": index, "track": track, "startTick": start, "lengthTick": length, "sourceKind": kind,
            "sourceIndex": source, "muted": muted}


def note(channel: int, start: int, length: int, *, muted: bool = False) -> dict[str, JsonValue]:
    return {"index": 0, "channel": channel, "key": 36, "startTick": start, "lengthTick": length,
            "velocity": 100, "muted": muted}


def channel(index: int, name: str) -> dict[str, JsonValue]:
    return {"index": index, "name": name, "mixerTrack": 0, "muted": False, "volume": 10000, "pan": 6400}


# ---- clip call shapes -------------------------------------------------------------------------------

def test_resize_accepts_records_tuples_and_the_single_form(fl: Studio, transport: RecordingTransport) -> None:
    fl.clips.resize([ClipResize(1, 3072)])
    fl.clips.resize([(1, 3072), (2, 768)])
    fl.clips.resize(1, 3072)
    expected = {"resizes": [{"index": 1, "lengthTick": 3072}]}
    assert arguments(transport, 0) == expected
    assert arguments(transport, 1) == {"resizes": [{"index": 1, "lengthTick": 3072}, {"index": 2, "lengthTick": 768}]}
    assert arguments(transport, 2) == expected


def test_move_and_delete_accept_the_single_form(fl: Studio, transport: RecordingTransport) -> None:
    fl.clips.move(4, 768, 3)
    fl.clips.move([(4, 768, 3), ClipMove(5, 0, 2)])
    fl.clips.delete(7)
    fl.clips.set_muted(7, True)
    assert arguments(transport, 0) == {"moves": [{"index": 4, "startTick": 768, "track": 3}]}
    assert arguments(transport, 1) == {"moves": [{"index": 4, "startTick": 768, "track": 3},
                                                  {"index": 5, "startTick": 0, "track": 2}]}
    assert arguments(transport, 2) == {"clipIndices": [7]}
    assert arguments(transport, 3) == {"clipIndices": [7], "muted": True}


@pytest.mark.parametrize("call", [lambda fl: fl.clips.resize(1), lambda fl: fl.clips.resize([(1, 2)], 3),
                                  lambda fl: fl.clips.resize(True, 3), lambda fl: fl.clips.move(1, 2),
                                  lambda fl: fl.clips.move([(1, 2, 3)], track=2), lambda fl: fl.clips.delete(True)])
def test_mixed_clip_forms_are_rejected_before_any_request(fl: Studio, transport: RecordingTransport, call: Any) -> None:
    with pytest.raises(TypeError):
        call(fl)
    assert transport.calls == []


# ---- add_patterns length enforcement ------------------------------------------------------------------

def test_add_patterns_is_one_native_pass_by_default(fl: Studio, transport: RecordingTransport) -> None:
    assert fl.playlist.add_patterns([PatternClipSpec(36, 9, 0, 3072)]) == 0
    assert operations(transport) == ["add_pattern_clips"]
    assert arguments(transport, 0) == {"clips": [{"pattern": 36, "track": 9, "startTick": 0, "lengthTick": 3072}]}


def test_add_patterns_enforce_lengths_resizes_only_clips_that_drifted(fl: Studio, transport: RecordingTransport) -> None:
    transport.responses["query_clips"] = page([
        clip(0, 9, 0, 3840, source=36), clip(1, 9, 3072, 3072, source=36), clip(2, 10, 0, 3456, source=37),
        clip(3, 11, 0, 3840, source=36)])
    specs = [PatternClipSpec(36, 9, 0, 3072), PatternClipSpec(36, 9, 3072, 3072), PatternClipSpec(37, 10, 0)]
    assert fl.playlist.add_patterns(specs, enforce_lengths=True) == 1
    assert operations(transport) == ["add_pattern_clips", "query_clips", "resize_clips"]
    assert arguments(transport, 2) == {"resizes": [{"index": 0, "lengthTick": 3072}]}, \
        "clip 1 already matches, clip 2 follows the pattern (length 0), clip 3 is not from these specs"


def test_pattern_clip_spec_length_defaults_to_the_pattern_length() -> None:
    assert PatternClipSpec(1, 2, 0).length_tick == 0


# ---- note deletion keeps clip lengths -------------------------------------------------------------------

def install_shrinking_host(transport: RecordingTransport, *, shrink: bool) -> None:
    state = {"deleted": False}

    def handler(method: str, params: dict[str, JsonValue]) -> JsonValue:
        operation = params.get("operation")
        if operation == "query_clips":
            length = 1536 if shrink and state["deleted"] else 3072
            return page([clip(48, 10, 20352, length, source=38), clip(51, 12, 20352, length, source=38),
                         clip(52, 3, 0, 768, source=2)])
        if operation == "delete_notes":
            state["deleted"] = True
            return 1
        if operation == "resize_clips":
            return None
        raise AssertionError(operation)

    transport.handler = handler


def test_delete_with_preserve_clips_restores_shrunk_clips(fl: Studio, transport: RecordingTransport) -> None:
    install_shrinking_host(transport, shrink=True)
    assert fl.patterns[38].notes.delete([NoteRef(18, 60, 1536)], preserve_clips=True) == 1
    assert operations(transport) == ["query_clips", "delete_notes", "query_clips", "resize_clips"]
    assert arguments(transport, 3) == {"resizes": [{"index": 48, "lengthTick": 3072}, {"index": 51, "lengthTick": 3072}]}


def test_delete_with_preserve_clips_writes_nothing_when_lengths_held(fl: Studio, transport: RecordingTransport) -> None:
    install_shrinking_host(transport, shrink=False)
    assert fl.patterns[38].notes.delete([NoteRef(18, 60, 1536)], preserve_clips=True) == 1
    assert operations(transport) == ["query_clips", "delete_notes", "query_clips"]


def test_delete_default_is_still_one_operation(fl: Studio, transport: RecordingTransport) -> None:
    transport.responses["delete_notes"] = 1
    assert fl.patterns[38].notes.delete([NoteRef(18, 60, 1536)]) == 1
    assert operations(transport) == ["delete_notes"]


# ---- plugin_parameter argument orders ----------------------------------------------------------------

def test_plugin_parameter_accepts_sdk_and_record_orders() -> None:
    expected = AutomationTarget("plugin_parameter", 1, 0, 556)
    assert AutomationTarget.plugin_parameter(1, 556, slot=0) == expected
    assert AutomationTarget.plugin_parameter(1, 0, 556) == expected
    assert AutomationTarget.plugin_parameter(track=1, slot=0, param=556) == expected
    assert AutomationTarget.plugin_parameter(channel_or_track=1, parameter=556, slot=0) == expected
    assert AutomationTarget.plugin_parameter(1, parameter=556, slot=0) == expected
    assert AutomationTarget.effect_parameter(1, 0, 556) == expected
    assert AutomationTarget.from_record("1/0/556") == expected
    assert AutomationTarget.from_record((1, 0, 556)) == expected
    assert AutomationTarget.from_record({"track": 1, "slot": 0, "param": 556}) == expected
    assert AutomationTarget.plugin_parameter(4, 205) == AutomationTarget.generator_parameter(4, 205)
    assert AutomationTarget.plugin_parameter(channel=4, parameter=205).slot == -1


@pytest.mark.parametrize("call", [
    lambda: AutomationTarget.plugin_parameter(1, 0, 556, slot=2),
    lambda: AutomationTarget.plugin_parameter(1, 556, parameter=3),
    lambda: AutomationTarget.plugin_parameter(1),
    lambda: AutomationTarget.plugin_parameter(1, 2, 3, 4),
    lambda: AutomationTarget.plugin_parameter(track=1, channel=1, parameter=5),
    lambda: AutomationTarget.plugin_parameter(1, track=2, parameter=5),
])
def test_plugin_parameter_rejects_ambiguous_forms(call: Any) -> None:
    with pytest.raises(TypeError):
        call()


@pytest.mark.parametrize("record", ["1/0", "a/b/c", (1, 0), {"slot": 0, "param": 5}, {"track": 1, "slot": "0", "param": 5}])
def test_from_record_rejects_malformed_records(record: Any) -> None:
    with pytest.raises(ValueError):
        AutomationTarget.from_record(record)


def test_tension_constants_state_the_sign_convention() -> None:
    assert TENSION_EASE_OUT > 0 > TENSION_EASE_IN
    doc = str(AutomationPointSpec.__doc__)
    assert "fast" in doc and "ENDS at this point" in doc


# ---- automation inventory ----------------------------------------------------------------------------

def install_automation_project(transport: RecordingTransport) -> None:
    descriptions = {
        0: "Kick: generator 'FPC' (120 params)",
        21: "PLM - Pad HP: automation clip -> event 0x71008030",
        27: "PLM - Delay send level (insert 24): automation clip -> Insert 24 volume, event 0x76001fc0",
        36: "PLM - Bass duck (insert 6): automation clip -> event 0x71801fc0",
    }
    points = {21: 4, 27: 30, 36: 488}

    def handler(method: str, params: dict[str, JsonValue]) -> JsonValue:
        operation = params.get("operation")
        args = params.get("arguments")
        assert isinstance(args, dict)
        if operation == "get_ppq":
            return PPQ
        if operation == "query_channels":
            return [channel(0, "Kick"), channel(21, "PLM - Pad HP"), channel(27, "PLM - Delay send level (insert 24)"),
                    channel(36, "PLM - Bass duck (insert 6)")]
        if operation == "get_channel_plugin":
            return descriptions[int(str(args["channel"]))]
        if operation == "query_automation_points":
            count = points[int(str(args["channel"]))]
            return [{"index": i, "timeBeats": float(i), "value": 0.5, "tension": 0.0, "curve": 0} for i in range(count)]
        if operation == "query_clips":
            return page([clip(0, 7, 0, BAR, source=29), clip(1, 19, 24 * BAR, 8 * BAR, kind="channel", source=21),
                         clip(2, 20, 0, 120 * BAR, kind="channel", source=36),
                         clip(3, 20, 120 * BAR, 8 * BAR, kind="channel", source=36, muted=True)])
        raise AssertionError(operation)

    transport.handler = handler


def test_automation_list_decodes_targets_counts_points_and_finds_placements(fl: Studio,
                                                                          transport: RecordingTransport) -> None:
    install_automation_project(transport)
    items = fl.automation.list()
    assert [item.channel for item in items] == [21, 27, 36]
    pad = items[0]
    assert pad == AutomationChannelInfo(21, "PLM - Pad HP", "event 0x71008030", (0x71008030,),
                                        (AutomationTarget.plugin_parameter(4, 48, slot=0),), 4,
                                        (ClipInfo(1, 19, 24 * BAR, 8 * BAR, "channel", 21, False),))
    assert pad.target == AutomationTarget.effect_parameter(4, 0, 48)
    assert items[1].targets == (AutomationTarget.mixer_volume(24),)
    assert items[1].targets_text == "Insert 24 volume, event 0x76001fc0"
    assert items[2].target == AutomationTarget.mixer_volume(6) and items[2].point_count == 488
    assert [(c.index, c.muted) for c in items[2].clips] == [(2, False), (3, True)]
    assert operations(transport).count("query_clips") == 1 and operations(transport).count("query_automation_points") == 3


def test_automation_list_can_skip_point_counts(fl: Studio, transport: RecordingTransport) -> None:
    install_automation_project(transport)
    items = fl.automation.list(with_points=False)
    assert [item.point_count for item in items] == [-1, -1, -1]
    assert "query_automation_points" not in operations(transport)


def test_automation_describe_is_one_line_per_clip(fl: Studio, transport: RecordingTransport) -> None:
    install_automation_project(transport)
    text = fl.automation.describe()
    lines = text.splitlines()
    assert lines[0] == "3 automation clips"
    assert lines[1] == "[21] PLM - Pad HP: event 0x71008030 -> insert 4 slot 0 parameter 48; 4 points; " \
                       "track 19 @ 9216 (bar 25) len 3072"
    assert "mixer_volume 6; 488 points; track 20 @ 0 (bar 1) len 46080, track 20 @ 46080 (bar 121) len 3072 muted" \
        in lines[3]


@pytest.mark.parametrize("text,expected", [
    ("Pad: automation clip -> event 0x71008030", "event 0x71008030"),
    ("Odd: name: automation clip -> Insert 3 volume ", "Insert 3 volume"),
    ("Kick: generator 'FPC' (120 params)", None), ("Bus: no generator (bus/automation)", None), ("", None)])
def test_parse_automation_link(text: str, expected: str | None) -> None:
    assert parse_automation_link(text) == expected


# ---- hit-following ducks and tiling ------------------------------------------------------------------

def test_duck_points_follows_irregular_hits_and_holds_between_them() -> None:
    points = duck_points([1, 2.5, 4], 8, ppq=PPQ, depth=0.11, ceiling=0.8, recovery_beats=0.25, tension=0.5)
    assert [(round(p.time_beats, 6), round(p.value, 3), p.tension) for p in points] == [
        (0, 0.8, 0), (round(1 - TICK, 6), 0.8, 0), (1, 0.69, 0), (1.25, 0.8, 0.5), (round(2.5 - TICK, 6), 0.8, 0),
        (2.5, 0.69, 0), (2.75, 0.8, 0.5), (round(4 - TICK, 6), 0.8, 0), (4, 0.69, 0), (4.25, 0.8, 0.5), (8, 0.8, 0)]
    assert all(a.time_beats < b.time_beats for a, b in zip(points, points[1:]))


def test_duck_points_handles_a_hit_at_zero_close_hits_and_hits_outside_the_clip() -> None:
    points = duck_points([-1, 0, 0.5, 0.5, 0.5 + TICK, 7.99, 8, 9], 8, ppq=PPQ, recovery_beats=1)
    times = [p.time_beats for p in points]
    assert times[0] == 0 and points[0].value == 0.5, "a hit on beat 0 starts with the dip"
    assert times[-1] == 8 and points[-1].value == 1.0
    assert all(a < b for a, b in zip(times, times[1:]))
    assert [p.value for p in points].count(0.5) == 3, "hits at 0, 0.5 and 0.5 + one tick; 7.99 is within the last tick"


def test_duck_points_without_hits_is_a_flat_line() -> None:
    assert duck_points([], 4, ppq=PPQ, ceiling=0.8) == [AutomationPointSpec(0, 0.8), AutomationPointSpec(4, 0.8)]


@pytest.mark.parametrize("kwargs", [{"depth": 1.5}, {"floor": 1.0}, {"recovery_beats": 0}, {"tension": 2},
                                    {"length_beats": TICK}, {"hits_beats": [float("nan")]}])
def test_duck_points_rejects_invalid_settings(kwargs: dict[str, Any]) -> None:
    settings: dict[str, Any] = {"hits_beats": [1], "length_beats": 4, "ppq": PPQ}
    settings.update(kwargs)
    with pytest.raises(ValueError):
        duck_points(**settings)


def test_pump_points_is_a_regular_duck() -> None:
    assert pump_points(8, ppq=PPQ, beats_per_hit=2) == duck_points([0, 2, 4, 6], 8, ppq=PPQ, recovery_beats=0.75)


def test_duck_creates_then_writes_one_envelope_from_absolute_hits(fl: Studio, transport: RecordingTransport) -> None:
    transport.responses["create_automation_clip"] = {"channel": 36, "clipIndex": 80}
    start = 8 * BAR
    hits = [start - 1, start, start + 2 * PPQ + PPQ // 2, start + BAR, start + BAR + 3 * PPQ, start + 2 * BAR]
    result = fl.automation.duck(AutomationTarget.mixer_volume(6), hits, start, 2 * BAR, track=20, depth=0.11,
                                ceiling=0.8, recovery_beats=0.25, name="Bass duck")
    assert result == PumpResult(AutomationClipResult(36, 80), 12), "four hits, three points each"
    assert operations(transport) == ["get_ppq", "create_automation_clip", "set_automation_points"]
    written = arguments(transport, 2)["points"]
    assert isinstance(written, list)
    dips = [p["timeBeats"] for p in written if isinstance(p, dict) and abs(float(str(p["value"])) - 0.69) < 1e-9]
    assert dips == [0, 2.5, 4, 7], "hits before the clip and at its end are ignored; times are clip-relative beats"


def test_tile_points_alternates_offsets_and_holds_between_repeats() -> None:
    shape = [AutomationPointSpec(0, 1.0), AutomationPointSpec(0.5, 0.2), AutomationPointSpec(1, 1.0, 0.5)]
    points = tile_points(shape, 4, 8, ppq=PPQ, offsets_beats=[0, 2])
    assert [(round(p.time_beats, 6), p.value, p.tension) for p in points] == [
        (0, 1.0, 0), (0.5, 0.2, 0), (1, 1.0, 0.5), (round(6 - TICK, 6), 1.0, 0), (6, 1.0, 0), (6.5, 0.2, 0),
        (7, 1.0, 0.5), (8, 1.0, 0)]


def test_tile_points_offset_first_repeat_and_partial_last_repeat() -> None:
    shape = [AutomationPointSpec(0, 0.3), AutomationPointSpec(1, 0.9)]
    points = tile_points(shape, 2, 5, ppq=PPQ, offsets_beats=[1])
    assert [(round(p.time_beats, 6), p.value) for p in points] == [
        (0, 0.3), (round(1 - TICK, 6), 0.3), (1, 0.3), (2, 0.9), (round(3 - TICK, 6), 0.9), (3, 0.3), (4, 0.9),
        (round(5 - TICK, 6), 0.9), (5, 0.3)]


@pytest.mark.parametrize("kwargs", [{"shape": [AutomationPointSpec(0, 1)]}, {"period_beats": 0},
                                    {"offsets_beats": []}, {"offsets_beats": [3.5]}, {"offsets_beats": [-1]},
                                    {"shape": [AutomationPointSpec(1, 1), AutomationPointSpec(2, 0)]}])
def test_tile_points_rejects_invalid_settings(kwargs: dict[str, Any]) -> None:
    settings: dict[str, Any] = {"shape": [AutomationPointSpec(0, 1), AutomationPointSpec(1, 0)], "period_beats": 4,
                                "length_beats": 8, "ppq": PPQ}
    settings.update(kwargs)
    with pytest.raises(ValueError):
        tile_points(**settings)


def test_tile_creates_then_writes_the_tiled_envelope(fl: Studio, transport: RecordingTransport) -> None:
    transport.responses["create_automation_clip"] = {"channel": 40, "clipIndex": 90}
    shape = [AutomationPointSpec(0, 1.0), AutomationPointSpec(1, 0.5)]
    result = fl.automation.tile(AutomationTarget.channel_volume(3), shape, 0, 4 * BAR, track=21, period_beats=4)
    assert result.clip == AutomationClipResult(40, 90) and result.point_count == 12
    assert operations(transport) == ["get_ppq", "create_automation_clip", "set_automation_points"]


# ---- playlist onsets ---------------------------------------------------------------------------------

def test_playlist_onsets_reads_kick_hits_through_clips(fl: Studio, transport: RecordingTransport) -> None:
    patterns = {29: [note(7, 0, 24), note(7, 2 * PPQ + PPQ // 2, 24), note(8, PPQ, 24), note(7, 5 * PPQ, 24)],
                30: [note(7, 0, 24), note(7, 3 * PPQ, 24), note(7, 2 * PPQ, 24, muted=True)]}
    clips = [clip(0, 7, 0, BAR, source=29), clip(1, 7, BAR, BAR, source=30), clip(2, 7, 2 * BAR, BAR, source=29),
             clip(3, 7, 3 * BAR, BAR, source=30, muted=True), clip(4, 9, 0, 4 * BAR, kind="channel", source=21)]

    def handler(method: str, params: dict[str, JsonValue]) -> JsonValue:
        operation = params.get("operation")
        args = params.get("arguments")
        assert isinstance(args, dict)
        if operation == "query_clips":
            return page(clips)
        if operation == "query_notes":
            return page(patterns[int(str(args["pattern"]))])
        raise AssertionError(operation)

    transport.handler = handler
    hits = fl.playlist.onsets(7, 0, 4 * BAR)
    assert hits == (0, 240, BAR, BAR + 288, 2 * BAR, 2 * BAR + 240)
    assert operations(transport).count("query_notes") == 2, "each pattern's notes are fetched once"
    assert fl.playlist.onsets(7, BAR, 2 * BAR) == (BAR, BAR + 288)


@pytest.mark.parametrize("args", [(-1, 0, 4), (7, -1, 4), (7, 4, 4), (7, 0.5, 4)])
def test_playlist_onsets_rejects_invalid_arguments(fl: Studio, transport: RecordingTransport, args: Any) -> None:
    with pytest.raises((ValueError, IndexError)):
        fl.playlist.onsets(*args)
    assert transport.calls == []


# ---- settled seeks and parameter reads ----------------------------------------------------------------

def state(tick: int) -> str:
    return f"mode=song playing=no pos=bar {tick // BAR + 1} beat 1 (tick {tick}) ppq=96 songLength=120 bars"


def install_drifting_transport(transport: RecordingTransport, positions: list[int], reads: list[str]) -> list[float]:
    slept: list[float] = []
    queue = list(positions)
    displays = list(reads)

    def handler(method: str, params: dict[str, JsonValue]) -> JsonValue:
        operation = params.get("operation")
        if operation == "seek":
            return None
        if operation == "get_song_state":
            return state(queue.pop(0) if len(queue) > 1 else queue[0])
        if operation == "query_plugin_parameters":
            display = displays.pop(0) if len(displays) > 1 else displays[0]
            return {"items": [{"index": 556, "name": "Output Level", "rawValue": 7, "displayValue": display}],
                    "nextOffset": None, "total": 600}
        raise AssertionError(operation)

    transport.handler = handler
    return slept


def test_position_tick_parses_the_state_text(fl: Studio, transport: RecordingTransport) -> None:
    transport.responses["get_song_state"] = state(3086)
    assert fl.transport.position_tick == 3086
    transport.responses["get_song_state"] = "mode=song playing=no"
    assert fl.transport.position_tick is None


def test_seek_settled_waits_for_two_agreeing_reads(fl: Studio, transport: RecordingTransport) -> None:
    slept = install_drifting_transport(transport, [3072, 3080, 3086, 3086], [])
    result = fl.transport.seek_settled(3072, delay=0.05, sleep=slept.append)
    assert result == SeekResult(3072, 3086, True, 4)
    assert operations(transport) == ["seek"] + ["get_song_state"] * 4
    assert slept == [0.05] * 4


def test_seek_settled_reports_an_unsettled_position(fl: Studio, transport: RecordingTransport) -> None:
    slept = install_drifting_transport(transport, list(range(3072, 3072 + 40)), [])
    result = fl.transport.seek_settled(3072, attempts=5, sleep=slept.append)
    assert result == SeekResult(3072, 3076, False, 5)


def test_seek_ticks_settle_flag_and_plain_seek(fl: Studio, transport: RecordingTransport) -> None:
    install_drifting_transport(transport, [3086, 3086], [])
    assert fl.transport.seek_ticks(3072) is None
    assert operations(transport) == ["seek"]
    assert fl.transport.seek_ticks(3072, settle=True, sleep=lambda _: None) == SeekResult(3072, 3086, True, 2)


def test_read_at_settles_then_reads_until_stable(fl: Studio, transport: RecordingTransport) -> None:
    slept = install_drifting_transport(transport, [3090, 3090], ["-4.00 dB", "-12.00 dB", "-12.00 dB"])
    value = fl.mixer[1].effects[0].parameters.read_at(556, 3072, settle=0.3, delay=0.1, sleep=slept.append)
    assert value == PluginParameterInfo(556, "Output Level", 7, "-12.00 dB")
    assert slept == [0.05, 0.05, 0.3, 0.1, 0.1]
    assert operations(transport) == ["seek", "get_song_state", "get_song_state"] + ["query_plugin_parameters"] * 3
    assert fl.mixer[1].effects[0].parameters.display_at(556, 3072, sleep=lambda _: None) == "-12.00 dB"


def test_transport_read_at_accepts_any_reader(fl: Studio, transport: RecordingTransport) -> None:
    values = iter([12784, 11040, 11040])
    install_drifting_transport(transport, [3086, 3086], [])
    result = fl.transport.read_at(3072, lambda: next(values), settle=0, delay=0, sleep=lambda _: None)
    assert result == 11040


@pytest.mark.parametrize("kwargs", [{"attempts": 0}, {"delay": 6}, {"delay": True}])
def test_seek_settled_rejects_invalid_polling(fl: Studio, transport: RecordingTransport, kwargs: dict[str, Any]) -> None:
    with pytest.raises((ValueError, IndexError)):
        fl.transport.seek_settled(0, **kwargs)
    assert transport.calls == []
