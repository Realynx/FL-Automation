"""Channel handles: dB volume, raw REC_Chan controls, Sampler stretch/offset properties and retire()."""

from typing import Any

import pytest
from conftest import RecordingTransport

from fruitylink import ChannelControl, Studio
from fruitylink.channels import RETIRED_PREFIX
from fruitylink.levels import channel_volume_from_db


def operations(transport: RecordingTransport) -> list[tuple[str, dict[str, Any]]]:
    result: list[tuple[str, dict[str, Any]]] = []
    for _, params in transport.calls:
        arguments = params["arguments"]
        assert isinstance(arguments, dict)
        result.append((str(params["operation"]), dict(arguments)))
    return result


def test_volume_db_reads_and_writes_through_the_channel_model(fl: Studio, transport: RecordingTransport) -> None:
    transport.responses["get_channel_volume"] = 10240
    assert fl.channels[3].volume_db == pytest.approx(0.0)
    fl.channels[3].volume_db = -6.0
    assert fl.channels[3].set_volume(db=0.0) == 10240
    assert fl.channels[3].set_volume(9000) == 9000
    minus_six = channel_volume_from_db(-6.0)
    assert 7300 < minus_six < 7400   # calibrated curve (exponent 2.09), not the old 8062
    # The first guarded write probes the automation link index once per connection (query_clips here;
    # this transport cannot answer it, so the guard gives up quietly instead of failing the write).
    assert [name for name, _ in operations(transport)].count("query_clips") == 1
    assert [row for row in operations(transport) if row[0] != "query_clips"] == [
        ("get_channel_volume", {"channel": 3}),
        ("set_channel_volume", {"channel": 3, "value": minus_six}),
        ("set_channel_volume", {"channel": 3, "value": 10240}),
        ("set_channel_volume", {"channel": 3, "value": 9000}),
    ]


@pytest.mark.parametrize("kwargs", [{}, {"value": 100, "db": -1.0}])
def test_set_volume_requires_exactly_one_form(fl: Studio, transport: RecordingTransport, kwargs: dict[str, Any]) -> None:
    with pytest.raises(TypeError, match="exactly one"):
        fl.channels[0].set_volume(**kwargs)
    with pytest.raises(IndexError):
        fl.channels[0].set_volume(12801)
    with pytest.raises(ValueError, match="fader top"):
        fl.channels[0].set_volume(db=9.0)
    assert transport.calls == []


def test_sampler_controls_use_the_rec_chan_indices(fl: Studio, transport: RecordingTransport) -> None:
    transport.responses["get_channel_control"] = 0
    channel = fl.channels[8]
    assert channel.stretch_time == 0
    channel.stretch_time = 1200
    assert channel.sample_offset == 0
    channel.sample_offset = 4410
    assert channel.control(ChannelControl.FILTER_CUTOFF) == 0
    channel.set_control(2, 512)
    assert operations(transport) == [
        ("get_channel_control", {"channel": 8, "control": 14}),
        ("set_channel_control", {"channel": 8, "control": 14, "value": 1200}),
        ("get_channel_control", {"channel": 8, "control": 13}),
        ("set_channel_control", {"channel": 8, "control": 13, "value": 4410}),
        ("get_channel_control", {"channel": 8, "control": 2}),
        ("set_channel_control", {"channel": 8, "control": 2, "value": 512}),
    ]
    assert (ChannelControl.VOLUME, ChannelControl.MUTE, ChannelControl.MIXER_TRACK) == (0, 7, 8)


@pytest.mark.parametrize("control,value", [(0x2000, 1), (-1, 1), (14, 1.5), (14, True)])
def test_invalid_controls_never_reach_the_host(fl: Studio, transport: RecordingTransport, control: int, value: Any) -> None:
    with pytest.raises((IndexError, TypeError)):
        fl.channels[0].set_control(control, value)
    assert transport.calls == []


def test_retire_mutes_routes_to_master_and_renames_once(fl: Studio, transport: RecordingTransport) -> None:
    transport.responses["get_channel_name"] = "Sampler"
    assert fl.channels[0].retire() == RETIRED_PREFIX + "Sampler"
    assert operations(transport) == [
        ("get_channel_name", {"channel": 0}),
        ("set_channel_muted", {"channel": 0, "muted": True}),
        ("set_channel_fx_route", {"channel": 0, "mixerTrack": 0}),
        ("set_channel_name", {"channel": 0, "name": "(unused) Sampler"}),
    ]
    transport.calls.clear()
    transport.responses["get_channel_name"] = "(unused) Sampler"
    assert fl.channels.retire(0) == "(unused) Sampler"
    assert operations(transport)[-1] == ("set_channel_name", {"channel": 0, "name": "(unused) Sampler"})
    transport.calls.clear()
    assert fl.channels[2].retire(name="parked") == "parked"
    assert operations(transport)[-1] == ("set_channel_name", {"channel": 2, "name": "parked"})
    with pytest.raises(TypeError):
        fl.channels[2].retire(name=3)  # type: ignore[arg-type]
