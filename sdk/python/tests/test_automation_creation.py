from typing import Any

import pytest
from conftest import RecordingTransport

from fruitylink import AutomationClipResult, AutomationPointSpec, AutomationTarget, ProtocolError, Studio


@pytest.mark.parametrize("target", [AutomationTarget.channel_volume(3), AutomationTarget.channel_pan(3),
                                   AutomationTarget.channel_pitch(3), AutomationTarget.mixer_volume(2),
                                   AutomationTarget.mixer_pan(2), AutomationTarget.plugin_parameter(3, 42),
                                   AutomationTarget.plugin_parameter(2, 42, slot=0)])
def test_create_has_typed_target_wire_shape_and_typed_reply(fl: Studio, transport: RecordingTransport,
                                                          target: AutomationTarget) -> None:
    transport.responses["create_automation_clip"] = {"channel": 19, "clipIndex": 100}
    result = fl.automation.create(target, 500, 96, 384, name="Cutoff 🌙")
    assert result == AutomationClipResult(19, 100)
    assert transport.calls == [("invoke", {"operation": "create_automation_clip", "arguments": {
        "target": {"kind": target.kind, "index": target.index, "slot": target.slot, "parameter": target.parameter},
        "track": 500, "startTick": 96, "lengthTick": 384, "name": "Cutoff 🌙"}})]


def test_existing_automation_places_without_creating_or_relinking(fl: Studio,
                                                               transport: RecordingTransport) -> None:
    transport.responses["add_automation_clip"] = 42
    assert fl.automation[19].add_clip(1, 1536, 384) == 42
    assert transport.calls == [("invoke", {"operation": "add_automation_clip", "arguments": {
        "channel": 19, "track": 1, "startTick": 1536, "lengthTick": 384}})]


def test_full_envelope_converts_units_names_without_individual_edits(fl: Studio,
                                                                   transport: RecordingTransport) -> None:
    points = [AutomationPointSpec(0, 0.2), AutomationPointSpec(1.5, 0.8)]
    fl.automation[19].set_points(points)
    assert transport.calls == [("invoke", {"operation": "set_automation_points", "arguments": {
        "channel": 19, "points": [{"timeBeats": 0, "value": 0.2, "tension": 0, "curve": 0},
                                    {"timeBeats": 1.5, "value": 0.8, "tension": 0, "curve": 0}]}})]


@pytest.mark.parametrize("track,start,length", [(0, 0, 1), (501, 0, 1), (True, 0, 1), (1, -1, 2),
                                              (1, 0, 0), (1, 2**31 - 1, 1), (1, 1.5, 2)])
def test_invalid_placement_rejected_before_creation(fl: Studio, transport: RecordingTransport,
                                                  track: Any, start: Any, length: Any) -> None:
    with pytest.raises(ValueError):
        fl.automation.create(AutomationTarget.channel_volume(3), track, start, length)
    assert transport.calls == []


@pytest.mark.parametrize("kind,index,slot,param", [("other", 0, -1, -1), ("channel_volume", True, -1, -1),
                                                 ("channel_volume", 0, 0, -1),
                                                 ("plugin_parameter", 0, 10, 0),
                                                 ("plugin_parameter", 0, -1, -1)])
def test_invalid_target_fields_rejected(kind: str, index: Any, slot: int, param: int) -> None:
    with pytest.raises(ValueError):
        AutomationTarget(kind, index, slot, param)


@pytest.mark.parametrize("time,value,tension,curve", [(float("nan"), 0, 0, 0), (0, 1.1, 0, 0),
                                                    (0, 0.5, 2, 0), (0, 0.5, 0, 1), (0, True, 0, 0)])
def test_invalid_point_fields_rejected(time: float, value: Any, tension: float, curve: int) -> None:
    with pytest.raises(ValueError):
        AutomationPointSpec(time, value, tension, curve)


@pytest.mark.parametrize("points", [[], [AutomationPointSpec(0, 0)],
                                    [AutomationPointSpec(1, 0), AutomationPointSpec(2, 1)],
                                    [AutomationPointSpec(0, 0), AutomationPointSpec(0, 1)],
                                    [AutomationPointSpec(0, 0)] * 4001])
def test_invalid_envelope_never_mutates(fl: Studio, transport: RecordingTransport,
                                      points: list[AutomationPointSpec]) -> None:
    with pytest.raises(ValueError):
        fl.automation[19].set_points(points)
    assert transport.calls == []


def test_malformed_creation_reply_is_not_retried(fl: Studio, transport: RecordingTransport) -> None:
    transport.responses["create_automation_clip"] = {"channel": True, "clipIndex": 10}
    with pytest.raises(ProtocolError):
        fl.automation.create(AutomationTarget.mixer_volume(2), 1, 0, 384)
    assert len(transport.calls) == 1


def test_set_point_edits_one_point_in_place_including_endpoints(fl: Studio, transport: RecordingTransport) -> None:
    fl.automation[19].set_point(0, 0.9)
    fl.automation[19].set_point(18, 1, tension=-0.5)
    assert transport.calls == [
        ("invoke", {"operation": "set_automation_point", "arguments": {
            "channel": 19, "index": 0, "value": 0.9, "tension": 0.0}}),
        ("invoke", {"operation": "set_automation_point", "arguments": {
            "channel": 19, "index": 18, "value": 1.0, "tension": -0.5}}),
    ]


@pytest.mark.parametrize("index,value,tension", [(-1, 0.5, 0), (True, 0.5, 0), (0, 1.5, 0), (0, -0.1, 0),
                                                 (0, True, 0), (0, "0.5", 0), (0, 0.5, 2), (0, 0.5, None)])
def test_set_point_rejects_invalid_values_before_any_request(fl: Studio, transport: RecordingTransport,
                                                          index: Any, value: Any, tension: Any) -> None:
    with pytest.raises((ValueError, IndexError)):
        fl.automation[19].set_point(index, value, tension)
    assert transport.calls == []


@pytest.mark.parametrize("event_id, expected", [
    (0x180cd, AutomationTarget.plugin_parameter(1, 205)),          # live: channel 1 "Filter 1 Freq"
    (0x480cd, AutomationTarget.plugin_parameter(4, 205)),
    (0x1080cd, AutomationTarget.plugin_parameter(16, 205)),
    (0x1880cd, AutomationTarget.plugin_parameter(24, 205)),
    (0x48007, AutomationTarget.plugin_parameter(4, 7)),            # live: channel 4 "Pitch Bend"
    (0x18000, AutomationTarget.plugin_parameter(1, 0)),            # live: channel 1 fade = parameter 0
    (0x30000, AutomationTarget.channel_volume(3)),                 # live: the scratch pump
    (0x160000, AutomationTarget.channel_volume(22)),
    (0x30001, AutomationTarget.channel_pan(3)),
    (0x30004, AutomationTarget.channel_pitch(3)),
    (0x70401fc0, AutomationTarget.mixer_volume(1)),                # live: "Chords - pump" on insert 1
    (0x70001fc1, AutomationTarget.mixer_pan(0)),
    (0x70428003, AutomationTarget.plugin_parameter(1, 3, slot=2)),  # insert 1, effect slot 2, parameter 3
    (0x30007, None),                                               # channel mute: not a supported target
    (0x70001f00, None),                                            # mixer control the SDK does not name
])
def test_event_ids_decode_to_typed_targets(event_id: int, expected: AutomationTarget | None) -> None:
    assert AutomationTarget.from_event_id(event_id) == expected


@pytest.mark.parametrize("event_id", [-1, 2**32, True, 1.5, "0x180cd"])
def test_event_id_decoding_rejects_non_uint32(event_id: Any) -> None:
    with pytest.raises(ValueError):
        AutomationTarget.from_event_id(event_id)
