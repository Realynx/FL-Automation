"""Parameter names, duplicates, normalized readback and automatic paging (Parking Lot Moon fix batch)."""

import struct

import pytest
from conftest import RecordingTransport

from fruitylink import Studio
from fruitylink.models import PluginParameterInfo, decode_record
from fruitylink.plugins import PAGE_LIMIT, clean_parameter_name, unique_names
from fruitylink.values import JsonValue, to_json


def bits(value: float) -> int:
    return int(struct.unpack("<i", struct.pack("<f", value))[0])


def parameter(index: int, name: str, raw: int = 1234, display: str = "50 %") -> dict[str, JsonValue]:
    return {"index": index, "name": name, "rawValue": raw, "displayValue": display}


@pytest.mark.parametrize("raw,clean", [
    ("^b^aWet level", "Wet level"),
    ("^b^a^^(shift-click for I + II) ^Mode", "Mode"),
    ("Filter 1 Freq", "Filter 1 Freq"),
    ("Filter\nCutoff ♫", "Filter\nCutoff ♫"),
    ("^b^a", "^b^a"),                       # nothing but codes: keep the raw text rather than ""
])
def test_hint_codes_are_stripped_from_stock_effect_names(raw: str, clean: str) -> None:
    assert clean_parameter_name(raw) == clean


def test_record_keeps_raw_name_and_decodes_normalized() -> None:
    item = decode_record(PluginParameterInfo, parameter(48, "^b^aWet level", bits(0.626), "62.6 %"))
    assert item.name == "Wet level" and item.raw_name == "^b^aWet level"
    assert item.normalized == pytest.approx(0.626, abs=1e-6)
    assert item.raw_value == bits(0.626)   # live: 1059075707 = 0x3F203A7B for 0.626 written by an automation clip
    # native FL integer scales do not decode to a 0..1 float
    assert decode_record(PluginParameterInfo, parameter(0, "Vol", 12800)).normalized is None
    # optional fields sent by a newer host are accepted; constructor normalises too
    wire = dict(parameter(1, "^b^aDry", 0, "0 %"), rawName="^b^aDry", normalized=0.0)
    assert decode_record(PluginParameterInfo, wire) == PluginParameterInfo(1, "^b^aDry", 0, "0 %")
    encoded = to_json(PluginParameterInfo(2, "^b^aX", bits(0.5), "50 %"))
    assert encoded == {"index": 2, "name": "X", "rawValue": bits(0.5), "displayValue": "50 %", "rawName": "^b^aX",
                       "normalized": 0.5}


def test_find_matches_cleaned_or_raw_names(fl: Studio, transport: RecordingTransport) -> None:
    transport.responses["query_plugin_parameters"] = {
        "items": [parameter(3, "^b^aWet level"), parameter(4, "^b^aDry level")], "nextOffset": None, "total": 12}
    assert fl.mixer[4].effects[1].parameters.find("Wet level").index == 3
    assert fl.mixer[4].effects[1].parameters.find("^b^aWet level").index == 3
    assert fl.mixer[4].effects[1].parameters.set_named("Dry level", 0.2) == 4
    assert transport.calls[-1][1]["arguments"] == {"channelOrTrack": 4, "slot": 1, "paramIndex": 4, "value": 0.2}


def test_duplicate_names_are_refused_with_indices_and_addressable_by_index_suffix(
    fl: Studio, transport: RecordingTransport,
) -> None:
    transport.responses["query_plugin_parameters"] = {
        "items": [parameter(17, "^b^aFeedback"), parameter(18, "^b^aDistortion"), parameter(19, "^b^aDistortion"),
                  parameter(20, "^b^aDistortion")], "nextOffset": None, "total": 30}
    parameters = fl.mixer[4].effects[3].parameters
    with pytest.raises(LookupError) as error:
        parameters.find("Distortion")
    assert "found 3 at indices 18, 19, 20" in str(error.value) and "'Distortion [18]'" in str(error.value)
    with pytest.raises(LookupError):
        parameters.set_named("Distortion", 0.5)
    assert not any(call[1]["operation"] == "set_plugin_param" for call in transport.calls)
    assert parameters.find("Distortion [19]").index == 19
    assert parameters.set_named("Distortion [20]", 0.3) == 20
    with pytest.raises(LookupError):
        parameters.find("Distortion [21]")
    rows = unique_names(parameters.list())
    assert [row.name for row in rows] == ["Feedback", "Distortion [18]", "Distortion [19]", "Distortion [20]"]
    assert rows[1].raw_name == "^b^aDistortion"
    assert [row.name for row in parameters.all(unique=True)] == [row.name for row in rows]


def test_all_pages_automatically_and_page_limit_error_names_the_cap(fl: Studio, transport: RecordingTransport) -> None:
    def handler(method: str, params: dict[str, JsonValue]) -> JsonValue:
        query = params["arguments"]
        assert isinstance(query, dict) and query["limit"] == PAGE_LIMIT
        offset = query["offset"]
        assert isinstance(offset, int)
        if offset < 4096:
            return {"items": [parameter(offset, f"p{offset}")], "nextOffset": offset + 512, "total": 4240}
        return {"items": [parameter(offset, f"p{offset}")], "nextOffset": None, "total": 4240}
    transport.handler = handler
    rows = fl.channels[1].parameters.all()
    assert [row.index for row in rows] == [0, 512, 1024, 1536, 2048, 2560, 3072, 3584, 4096]
    assert len(transport.calls) == 9
    with pytest.raises(IndexError) as error:
        fl.channels[1].parameters.page(limit=4240)
    assert "512" in str(error.value) and "all()" in str(error.value)
