import pytest
from conftest import RecordingTransport

from fruitylink import NoteSpec, ProtocolError, Studio, Timebase


def test_note_beats_use_live_ppq(fl: Studio, transport: RecordingTransport) -> None:
    transport.responses["get_ppq"] = 960
    fl.patterns[2].notes.add_beats(channel=0, key=60, start=1.5, length=0.25)
    assert transport.calls[-1] == ("invoke", {"operation": "add_notes", "arguments": {"pattern": 2,
        "notes": [{"channel": 0, "key": 60, "startTick": 1440, "lengthTick": 240, "velocity": 100}]}})


def test_scalar_properties_and_routing_use_correct_native_arguments(fl: Studio, transport: RecordingTransport) -> None:
    transport.responses["get_channel_name"] = "Bass\n0: arbitrary"
    assert fl.channels[3].name == "Bass\n0: arbitrary"
    fl.channels[3].mixer_track = 5
    fl.mixer[5].muted = True
    fl.mixer[5].send_to(7, 0.5)
    assert transport.calls[-3:] == [
        ("invoke", {"operation": "set_channel_fx_route", "arguments": {"channel": 3, "mixerTrack": 5}}),
        ("invoke", {"operation": "set_mixer_track_muted", "arguments": {"track": 5, "muted": True}}),
        ("invoke", {"operation": "set_mixer_send", "arguments": {"srcTrack": 5, "dstTrack": 7, "level": 0.5, "active": True}})]


def test_structured_names_do_not_parse_legacy_text(fl: Studio, transport: RecordingTransport) -> None:
    name = "Quotes '\" and\nnewlines: 4"
    transport.responses["query_channels"] = [{"index": 3, "name": name, "mixerTrack": 5, "muted": False, "volume": 10000, "pan": 6400}]
    assert fl.channels.find(name).index == 3
    transport.responses["list_channels"] = "legacy; arbitrary format"
    assert fl.channels.list_text() == "legacy; arbitrary format"


def test_duplicate_names_are_ambiguous(fl: Studio, transport: RecordingTransport) -> None:
    transport.responses["query_patterns"] = [{"index": i, "name": "Duplicate", "lengthTick": None,
        "noteCount": None, "current": False} for i in (1, 2)]
    with pytest.raises(LookupError, match="found 2"):
        fl.patterns.find("Duplicate")


def test_invalid_indices_are_rejected_before_requests(fl: Studio, transport: RecordingTransport) -> None:
    with pytest.raises(IndexError):
        _ = fl.patterns[0]
    with pytest.raises(IndexError):
        _ = fl.channels[-1]
    with pytest.raises(IndexError):
        _ = fl.mixer[0].effects[10]
    with pytest.raises(IndexError):
        _ = fl.playlist[0]
    assert transport.calls == []


def test_native_bulk_notes_remain_one_operation(fl: Studio, transport: RecordingTransport) -> None:
    fl.patterns[1].notes.add([NoteSpec(0, key, 0, 96) for key in (60, 64, 67)])
    assert len(transport.calls) == 1


def test_property_rejects_wrong_scalar_type(fl: Studio, transport: RecordingTransport) -> None:
    transport.responses["get_channel_volume"] = True
    with pytest.raises(ProtocolError):
        _ = fl.channels[0].volume


def test_timebase_roundtrip_and_validation() -> None:
    assert Timebase(96).ticks(1.5) == 144
    assert Timebase(96).beats(144) == 1.5
    with pytest.raises(ValueError):
        Timebase(0)
    with pytest.raises(ValueError):
        Timebase(96).ticks(float("inf"))


def test_channel_select_and_solo_use_the_channel_argument_name(fl: Studio, transport: RecordingTransport) -> None:
    fl.channels[3].select()
    fl.channels[4].toggle_solo()
    assert transport.calls == [
        ("invoke", {"operation": "select_channel", "arguments": {"channel": 3}}),
        ("invoke", {"operation": "set_channel_solo", "arguments": {"channel": 4}})]


def test_note_targets_carry_length_tick_and_allow_multiple(fl: Studio, transport: RecordingTransport) -> None:
    from fruitylink import NoteEdit, NoteRef
    transport.responses["delete_notes"] = 2
    transport.responses["edit_notes"] = 1
    assert fl.patterns[1].notes.delete([NoteRef(0, 60, 0), NoteRef(0, 60, 0, length_tick=96)], allow_multiple=True) == 2
    assert fl.patterns[1].notes.edit([NoteEdit(0, 60, 0, new_velocity=90, length_tick=192)]) == 1
    assert transport.calls[0][1]["arguments"] == {"pattern": 1, "allowMultiple": True, "targets": [
        {"channel": 0, "key": 60, "startTick": 0, "lengthTick": None},
        {"channel": 0, "key": 60, "startTick": 0, "lengthTick": 96}]}
    assert transport.calls[1][1]["arguments"] == {"pattern": 1, "allowMultiple": False, "edits": [
        {"channel": 0, "key": 60, "startTick": 0, "newKey": None, "newStartTick": None, "newLength": None,
         "newVelocity": 90, "muted": None, "lengthTick": 192}]}
