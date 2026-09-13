from typing import Any

import pytest
from conftest import RecordingTransport

from fruitylink import ProtocolError, Studio
from fruitylink.mixer import MixerTrack
from fruitylink.values import JsonValue


def test_add_appends_without_renaming_or_routing(fl: Studio, transport: RecordingTransport) -> None:
    transport.responses["add_mixer_track"] = 126

    track = fl.mixer.add()

    assert isinstance(track, MixerTrack)
    assert track.index == 126
    assert transport.calls == [("invoke", {"operation": "add_mixer_track", "arguments": {"afterTrack": -1}})]


@pytest.mark.parametrize("after,new_index", [(0, 1), (3, 4), (125, 126)])
def test_add_names_only_returned_insert(fl: Studio, transport: RecordingTransport,
                                      after: int, new_index: int) -> None:
    transport.responses["add_mixer_track"] = new_index
    name = "Bus 🎛 — alternate"

    track = fl.mixer.add(name, after=after)

    assert track.index == new_index
    assert transport.calls == [
        ("invoke", {"operation": "add_mixer_track", "arguments": {"afterTrack": after}}),
        ("invoke", {"operation": "set_mixer_track_name", "arguments": {"track": new_index, "name": name}}),
    ]


@pytest.mark.parametrize("after", [-1, -2, True, False, 1.5, "3"])
def test_add_rejects_invalid_explicit_after_before_mutation(fl: Studio, transport: RecordingTransport,
                                                          after: Any) -> None:
    with pytest.raises(IndexError):
        fl.mixer.add(after=after)
    assert transport.calls == []


@pytest.mark.parametrize("name", [True, 42, [], {}])
def test_add_rejects_invalid_name_before_mutation(fl: Studio, transport: RecordingTransport,
                                                name: Any) -> None:
    with pytest.raises(TypeError, match="name"):
        fl.mixer.add(name)
    assert transport.calls == []


@pytest.mark.parametrize("index", [0, -1, True, None, "4"])
def test_invalid_creation_reply_never_renames_existing_track(fl: Studio, transport: RecordingTransport,
                                                           index: JsonValue) -> None:
    transport.responses["add_mixer_track"] = index
    with pytest.raises(ProtocolError, match="inspect mixer state"):
        fl.mixer.add("New insert")
    assert len(transport.calls) == 1


def test_rename_failure_does_not_retry_structural_edit(fl: Studio, transport: RecordingTransport) -> None:
    def handler(method: str, arguments: dict[str, JsonValue]) -> JsonValue:
        if arguments["operation"] == "add_mixer_track":
            return 4
        raise RuntimeError("Rename failed after insert was created")

    transport.handler = handler
    with pytest.raises(RuntimeError, match="Rename failed"):
        fl.mixer.add("New insert", after=3)
    assert [arguments["operation"] for _, arguments in transport.calls] == [
        "add_mixer_track", "set_mixer_track_name"]


def test_raw_operation_preserves_native_default_and_return_value(fl: Studio,
                                                               transport: RecordingTransport) -> None:
    transport.responses["add_mixer_track"] = 126
    assert fl.ops.add_mixer_track() == 126
    assert transport.calls == [("invoke", {"operation": "add_mixer_track", "arguments": {"afterTrack": -1}})]


def test_mixer_iteration_uses_typed_indices_without_inventing_current(fl: Studio,
                                                                  transport: RecordingTransport) -> None:
    transport.responses["get_mixer_track_count"] = 18
    transport.responses["query_mixer_tracks"] = [
        {"index": 0, "name": "Master", "kind": "master"},
        {"index": 3, "name": "Piano\nBus", "kind": "insert"},
        {"index": 16, "name": "Last ordinary", "kind": "insert"},
    ]

    assert [(item.index, item.kind) for item in fl.mixer.list()] == [(0, "master"), (3, "insert"), (16, "insert")]
    assert len(fl.mixer) == 3
    assert [track.index for track in fl.mixer] == [0, 3, 16]
    assert fl.mixer.find("Piano\nBus").index == 3
    assert all(arguments["operation"] == "query_mixer_tracks" for _, arguments in transport.calls)


def test_mixer_find_refuses_duplicate_names_in_typed_snapshot(fl: Studio,
                                                           transport: RecordingTransport) -> None:
    transport.responses["query_mixer_tracks"] = [
        {"index": 3, "name": "Bus", "kind": "insert"},
        {"index": 4, "name": "Bus", "kind": "insert"},
    ]
    with pytest.raises(LookupError, match="found 2"):
        fl.mixer.find("Bus")
    assert len(transport.calls) == 1
