import base64

from conftest import RecordingTransport

from fruitylink import Studio


def test_channel_get_state_decodes_the_wrapper_record(fl: Studio, transport: RecordingTransport) -> None:
    record = b"\x03\x00\x00\x00\x05\x00\x00\x00\x00\x00\x00\x00XferJson"
    transport.responses["get_channel_plugin_state"] = base64.b64encode(record).decode("ascii")
    assert fl.channels[4].get_state() == record
    assert transport.calls == [("invoke", {"operation": "get_channel_plugin_state", "arguments": {"channel": 4}})]


def test_effect_slot_get_state_targets_track_and_slot(fl: Studio, transport: RecordingTransport) -> None:
    transport.responses["get_mixer_effect_state"] = base64.b64encode(b"state").decode("ascii")
    assert fl.mixer[2].effects[1].get_state() == b"state"
    assert transport.calls == [("invoke", {"operation": "get_mixer_effect_state", "arguments": {"track": 2, "slot": 1}})]


def test_load_state_helpers_forward_paths(fl: Studio, transport: RecordingTransport) -> None:
    transport.responses["load_channel_plugin_state"] = "channel 4: loaded"
    transport.responses["load_mixer_effect_state"] = "mixer 2 slot 1: loaded"
    assert fl.channels[4].load_state("C:/p/lead.vstpreset") == "channel 4: loaded"
    assert fl.mixer[2].effects[1].load_state("C:/p/verb.fst") == "mixer 2 slot 1: loaded"
    assert transport.calls[0][1]["arguments"] == {"channel": 4, "path": "C:/p/lead.vstpreset", "useChannelLoader": False}
    assert transport.calls[1][1]["arguments"] == {"track": 2, "slot": 1, "path": "C:/p/verb.fst"}
