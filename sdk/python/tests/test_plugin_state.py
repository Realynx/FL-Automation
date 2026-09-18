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


def test_native_generator_preset_leaves_the_route_to_the_host(fl: Studio, transport: RecordingTransport) -> None:
    """A native FL generator .fst needs FL's channel loader, but the HOST decides that from the hosted plugin's
    identity: load_preset never asks for the channel-loader route itself, and it returns the host's line verbatim
    so the route and the restored channel state stay visible to the caller."""
    line = ("channel 3: loaded 'Sync Lead.fst' into 'Sytrus' via FL's channel loader (automatic for an FL-native "
            "generator .fst: the wrapper dispatcher is a no-op for these; restored name 'Sync Lead' -> 'Sytrus', "
            "mute -> unmuted) (same instance; params 0->0; state record changed (1271 -> 1283 bytes)).")
    transport.responses["load_channel_plugin_state"] = line
    assert fl.channels[3].load_preset(r"C:\FL\Data\Patches\Plugin presets\Generators\Sytrus\Lead\Sync Lead.fst") == line
    assert transport.calls[0][1]["arguments"]["useChannelLoader"] is False


def test_load_state_can_force_the_channel_loader_route(fl: Studio, transport: RecordingTransport) -> None:
    transport.responses["load_channel_plugin_state"] = "channel 4: loaded"
    assert fl.channels[4].load_state("C:/p/lead.fst", use_channel_loader=True) == "channel 4: loaded"
    assert transport.calls[0][1]["arguments"] == {"channel": 4, "path": "C:/p/lead.fst", "useChannelLoader": True}
