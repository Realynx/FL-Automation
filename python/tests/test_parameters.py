from itertools import islice

import pytest
from conftest import RecordingTransport

from fruitylink import ProtocolError, Studio
from fruitylink.models import Page, PluginParameterInfo
from fruitylink.values import JsonValue
from fruitylink.worker import execute


def parameter(index: int, name: str = "Cutoff") -> dict[str, JsonValue]:
    return {"index": index, "name": name, "rawValue": 1234, "displayValue": "4.2 kHz"}


def arguments(params: dict[str, JsonValue]) -> dict[str, JsonValue]:
    value = params["arguments"]
    assert isinstance(value, dict)
    return value


def test_page_is_one_typed_bounded_request_for_effect_slot(fl: Studio, transport: RecordingTransport) -> None:
    transport.responses["query_plugin_parameters"] = {
        "items": [parameter(70, "Filter\nCutoff ♫")], "nextOffset": 128, "total": 4240}
    page = fl.mixer[3].effects[2].parameters.page("cutoff", offset=64)
    assert page == Page((PluginParameterInfo(70, "Filter\nCutoff ♫", 1234, "4.2 kHz"),), 128, 4240)
    assert transport.calls == [("invoke", {"operation": "query_plugin_parameters", "arguments": {
        "channelOrTrack": 3, "slot": 2, "filter": "cutoff", "offset": 64, "limit": 64}})]


@pytest.mark.parametrize("offset,limit", [(-1, 64), (True, 64), (1.5, 64), (0, 0), (0, 513), (0, True)])
def test_invalid_page_bounds_do_not_read_remote_state(
    fl: Studio, transport: RecordingTransport, offset: int, limit: int,
) -> None:
    with pytest.raises(IndexError):
        fl.channels[0].parameters.page(offset=offset, limit=limit)
    assert not transport.calls


def test_iteration_is_lazy_and_stops_before_fetching_the_next_page(
    fl: Studio, transport: RecordingTransport,
) -> None:
    transport.responses["query_plugin_parameters"] = {
        "items": [parameter(0), parameter(1, "Resonance")], "nextOffset": 64, "total": 4240}
    iterator = iter(fl.channels[0].parameters)
    assert not transport.calls
    assert [item.index for item in islice(iterator, 2)] == [0, 1]
    assert len(transport.calls) == 1
    assert arguments(transport.calls[0][1])["limit"] == 64


def test_filtered_iteration_follows_empty_pages_and_raw_offsets(
    fl: Studio, transport: RecordingTransport,
) -> None:
    def handler(method: str, params: dict[str, JsonValue]) -> JsonValue:
        query = arguments(params)
        assert query["filter"] == "OSC" and query["limit"] == 32
        if query["offset"] == 0:
            return {"items": [], "nextOffset": 32, "total": 70}
        if query["offset"] == 32:
            return {"items": [parameter(61, "Osc A Level")], "nextOffset": 64, "total": 70}
        return {"items": [parameter(68, "OSC B Level")], "nextOffset": None, "total": 70}
    transport.handler = handler
    assert [item.index for item in fl.channels[0].parameters.iter("OSC", page_size=32)] == [61, 68]
    assert [arguments(call[1])["offset"] for call in transport.calls] == [0, 32, 64]


def test_list_retains_complete_tuple_contract(fl: Studio, transport: RecordingTransport) -> None:
    def handler(method: str, params: dict[str, JsonValue]) -> JsonValue:
        query = arguments(params)
        assert query["filter"] == "Cut" and query["limit"] == 512
        if query["offset"] == 0:
            return {"items": [parameter(0)], "nextOffset": 512, "total": 4240}
        return {"items": [parameter(4000)], "nextOffset": None, "total": 4240}
    transport.handler = handler
    result = fl.channels[0].parameters.list("Cut")
    assert isinstance(result, tuple) and [item.index for item in result] == [0, 4000]


def test_find_filters_remotely_but_keeps_exact_case_sensitive_matching(
    fl: Studio, transport: RecordingTransport,
) -> None:
    def handler(method: str, params: dict[str, JsonValue]) -> JsonValue:
        query = arguments(params)
        assert query["filter"] == "Cutoff"
        if query["offset"] == 0:
            return {"items": [parameter(5, "cutoff"), parameter(6, "Cutoff Fine")],
                    "nextOffset": 512, "total": 4240}
        if query["offset"] == 512:
            return {"items": [parameter(550)], "nextOffset": 1024, "total": 4240}
        return {"items": [], "nextOffset": None, "total": 4240}
    transport.handler = handler
    assert fl.channels[0].parameters.find("Cutoff").index == 550
    assert len(transport.calls) == 3  # The final page proves that the exact name is unique.


def test_find_rejects_duplicate_names_without_scanning_remaining_pages(
    fl: Studio, transport: RecordingTransport,
) -> None:
    def handler(method: str, params: dict[str, JsonValue]) -> JsonValue:
        query = arguments(params)
        offset = query["offset"]
        assert isinstance(offset, int)
        return {"items": [parameter(offset)], "nextOffset": offset + 512, "total": 4240}
    transport.handler = handler
    with pytest.raises(LookupError, match="at least 2"):
        fl.channels[0].parameters.find("Cutoff")
    assert len(transport.calls) == 2


def test_find_missing_and_invalid_continuation_are_explicit(fl: Studio, transport: RecordingTransport) -> None:
    transport.responses["query_plugin_parameters"] = {"items": [], "nextOffset": None, "total": 10}
    with pytest.raises(LookupError, match="found 0"):
        fl.channels[0].parameters.find("Cutoff")
    transport.responses["query_plugin_parameters"] = {"items": [], "nextOffset": 0, "total": 10}
    with pytest.raises(ProtocolError, match="advance"):
        tuple(fl.channels[0].parameters)


def test_bounded_parameter_result_fits_without_weakening_existing_guard(
    fl: Studio, transport: RecordingTransport,
) -> None:
    def handler(method: str, params: dict[str, JsonValue]) -> JsonValue:
        query = arguments(params)
        offset, limit = query["offset"], query["limit"]
        assert isinstance(offset, int) and isinstance(limit, int)
        end = min(4240, offset + limit)
        return {"items": [parameter(index, f"Wrapper Parameter {index:04} - " + "x" * 70)
                          for index in range(offset, end)],
                "nextOffset": end if end < 4240 else None, "total": 4240}
    transport.handler = handler
    bounded = execute("result = fl.channels[0].parameters.page(limit=32)", fl)
    assert bounded["ok"] is True and len(transport.calls) == 1
    result = bounded["result"]
    assert isinstance(result, dict) and result["nextOffset"] == 32 and result["total"] == 4240
    complete = execute("result = fl.channels[0].parameters.list()", fl)
    assert complete["ok"] is False and "512 KiB" in str(complete["error"])
