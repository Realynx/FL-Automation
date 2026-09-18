import struct
from itertools import islice
from typing import Any

import pytest
from conftest import RecordingTransport

from fruitylink import ProtocolError, Studio
from fruitylink.models import Page, PluginParameterInfo
from fruitylink.plugins import VerifiedWrite
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


def test_set_named_resolves_unique_name_across_pages_and_returns_written_index(
    fl: Studio, transport: RecordingTransport,
) -> None:
    def handler(method: str, params: dict[str, JsonValue]) -> JsonValue:
        operation = params.get("operation")
        query = arguments(params)
        if operation == "set_plugin_param":
            return None
        if query["offset"] == 0:
            return {"items": [parameter(4, "cutoff")], "nextOffset": 512, "total": 700}
        return {"items": [parameter(613)], "nextOffset": None, "total": 700}
    transport.handler = handler

    assert fl.channels[2].parameters.set_named("Cutoff", 0.625) == 613
    assert transport.calls[-1] == ("invoke", {"operation": "set_plugin_param", "arguments": {
        "channelOrTrack": 2, "slot": -1, "paramIndex": 613, "value": 0.625}})


@pytest.mark.parametrize("matches", [[], [parameter(3), parameter(9)]])
def test_set_named_does_not_write_when_name_is_missing_or_ambiguous(
    fl: Studio, transport: RecordingTransport, matches: list[JsonValue],
) -> None:
    transport.responses["query_plugin_parameters"] = {
        "items": matches, "nextOffset": None, "total": len(matches)}

    with pytest.raises(LookupError):
        fl.channels[0].parameters.set_named("Cutoff", 0.5)
    assert all(call[1].get("operation") != "set_plugin_param" for call in transport.calls)


def test_set_named_uses_the_existing_set_value_contract(fl: Studio, transport: RecordingTransport) -> None:
    transport.responses["query_plugin_parameters"] = {
        "items": [parameter(12)], "nextOffset": None, "total": 13}

    fl.channels[0].parameters.set_named("Cutoff", 1.25)
    assert arguments(transport.calls[-1][1]) == {
        "channelOrTrack": 0, "slot": -1, "paramIndex": 12, "value": 1.25}


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


def _slot(index: int, name: str, raw: int, display: str) -> dict[str, Any]:
    return {"items": [{"index": index, "name": name, "rawValue": raw, "displayValue": display}],
            "nextOffset": None, "total": 4240}


def test_read_returns_exactly_one_slot_by_index(fl: Studio, transport: RecordingTransport) -> None:
    transport.responses["query_plugin_parameters"] = _slot(199, "Sub Shape", 0, "Sine")
    assert fl.channels[1].parameters.read(199).display_value == "Sine"
    assert transport.calls == [("invoke", {"operation": "query_plugin_parameters", "arguments": {
        "channelOrTrack": 1, "slot": -1, "filter": None, "offset": 199, "limit": 1}})]


def test_set_verified_writes_once_and_stops_at_the_first_matching_readback(
        fl: Studio, transport: RecordingTransport) -> None:
    expected = struct.unpack("<i", struct.pack("<f", 0.6))[0]
    reads = iter([_slot(199, "Sub Shape", 0, "Sine"), _slot(199, "Sub Shape", 0, "Sine"),
                  _slot(199, "Sub Shape", expected, "Saw")])
    slept: list[float] = []

    def handler(method: str, params: dict[str, JsonValue]) -> JsonValue:
        operation = params["operation"]
        return next(reads) if operation == "query_plugin_parameters" else None

    transport.handler = handler
    result = fl.channels[1].parameters.set_verified(199, 0.6, attempts=4, delay=0.01, sleep=slept.append)

    assert result == VerifiedWrite(199, "Sub Shape", 0.6, 0, expected, "Sine", "Saw", True, 2,
                                   result.normalized_after, True)
    assert result.normalized_after == pytest.approx(0.6)
    assert slept == [0.01]
    operations = [str(call[1]["operation"]) for call in transport.calls]
    # The first guarded write probes the automation link index once per connection
    # (see fruitylink.automation_links); this transport cannot answer it, so the guard
    # gives up quietly instead of failing the write.
    assert operations.count("query_clips") == 1
    operations = [name for name in operations if name != "query_clips"]
    assert operations == ["query_plugin_parameters", "set_plugin_param", "query_plugin_parameters",
                          "query_plugin_parameters"]
    assert transport.calls[2] == ("invoke", {"operation": "set_plugin_param", "arguments": {
        "channelOrTrack": 1, "slot": -1, "paramIndex": 199, "value": 0.6}})


def test_set_verified_reports_unverified_after_exhausting_attempts(fl: Studio, transport: RecordingTransport) -> None:
    # A readable normalized value that neither matches the written one nor moves is a genuine failure
    # (an undecodable raw integer that does not move at all is the "already there" case below instead).
    transport.responses["query_plugin_parameters"] = _slot(199, "Sub Shape", _bits(0.3), "Pulse")
    result = fl.channels[1].parameters.set_verified(199, 0.0, attempts=3, delay=0, sleep=lambda _: None)
    assert result.verified is False
    assert result.attempts == 3
    assert result.raw_after == _bits(0.3)
    assert [call[1]["operation"] for call in transport.calls].count("set_plugin_param") == 1


def test_set_verified_resolves_names_through_find_and_treats_any_change_as_applied(
        fl: Studio, transport: RecordingTransport) -> None:
    page = {"items": [{"index": 46, "name": "A Uni Detune", "rawValue": 100, "displayValue": "0.01"}],
            "nextOffset": None, "total": 4240}
    reads = iter([page, page, _slot(46, "A Uni Detune", 200, "0.05")])
    transport.handler = lambda method, params: (
        next(reads) if params["operation"] == "query_plugin_parameters" else None)
    result = fl.channels[4].parameters.set_verified("A Uni Detune", 0.224, attempts=2, delay=0, sleep=lambda _: None)
    assert result.verified is True and result.index == 46 and result.display_after == "0.05"


@pytest.mark.parametrize("value,attempts,delay", [(1.5, 5, 0.02), (True, 5, 0.02), (0.5, 0, 0.02), (0.5, 5, 9)])
def test_set_verified_rejects_invalid_arguments_before_any_request(fl: Studio, transport: RecordingTransport,
                                                                 value: Any, attempts: Any, delay: Any) -> None:
    with pytest.raises((ValueError, IndexError)):
        fl.channels[1].parameters.set_verified(199, value, attempts=attempts, delay=delay)
    assert transport.calls == []


def _bits(value: float) -> int:
    return int(struct.unpack("<i", struct.pack("<f", value))[0])


def test_set_verified_keeps_reading_until_the_display_settles(fl: Studio, transport: RecordingTransport) -> None:
    # Live pattern: the raw value reflects the write at once, the display lags a few reads.
    reads = iter([_slot(199, "Sub Shape", _bits(0.6), "Saw"), _slot(199, "Sub Shape", _bits(0.8), "Saw"),
                  _slot(199, "Sub Shape", _bits(0.8), "Saw"), _slot(199, "Sub Shape", _bits(0.8), "Square")])
    slept: list[float] = []
    transport.handler = lambda method, params: (
        next(reads) if params["operation"] == "query_plugin_parameters" else None)
    result = fl.channels[1].parameters.set_verified(199, 0.8, attempts=5, delay=0.05, sleep=slept.append)
    assert result.verified is True and result.display_changed is True
    assert (result.display_before, result.display_after, result.attempts) == ("Saw", "Square", 3)
    assert slept == [0.05, 0.05]


def test_set_verified_without_settle_stops_at_the_first_verified_raw_value(
        fl: Studio, transport: RecordingTransport) -> None:
    reads = iter([_slot(199, "Sub Shape", _bits(0.6), "Saw"), _slot(199, "Sub Shape", _bits(0.8), "Saw")])
    transport.handler = lambda method, params: (
        next(reads) if params["operation"] == "query_plugin_parameters" else None)
    result = fl.channels[1].parameters.set_verified(199, 0.8, attempts=5, delay=0, sleep=lambda _: None,
                                                    settle_display=False)
    assert result.verified is True and result.display_changed is False and result.attempts == 1


def test_set_verified_reports_an_unsettled_display_honestly(fl: Studio, transport: RecordingTransport) -> None:
    reads = iter([_slot(199, "Sub Shape", _bits(0.6), "Saw")] + [_slot(199, "Sub Shape", _bits(0.8), "Saw")] * 3)
    transport.handler = lambda method, params: (
        next(reads) if params["operation"] == "query_plugin_parameters" else None)
    result = fl.channels[1].parameters.set_verified(199, 0.8, attempts=3, delay=0, sleep=lambda _: None)
    assert result.verified is True and result.display_changed is False and result.attempts == 3
    assert result.normalized_after == pytest.approx(0.8)


def test_set_verified_compares_normalized_values_with_tolerance_and_skips_settling_when_already_there(
        fl: Studio, transport: RecordingTransport) -> None:
    # The slot already holds 0.6 + 2^-24 (a rounding-step away from the written 0.6): the write is a
    # no-op, the raw value legitimately never changes, and no display change can be expected.
    transport.responses["query_plugin_parameters"] = _slot(199, "Sub Shape", _bits(0.6 + 2 ** -24), "Saw")
    result = fl.channels[1].parameters.set_verified(199, 0.6, attempts=5, delay=0.05, sleep=lambda _: None)
    assert result.verified is True and result.attempts == 1 and result.display_changed is False
    assert [call[1]["operation"] for call in transport.calls].count("query_plugin_parameters") == 2


def test_set_verified_reports_an_exactly_equal_slot_as_verified_and_unchanged(
        fl: Studio, transport: RecordingTransport) -> None:
    # Delay 3 "Tempo sync" already On (1.0): the raw value cannot move, which used to read as verified=False.
    transport.responses["query_plugin_parameters"] = _slot(2, "Tempo sync", _bits(1.0), "On")
    result = fl.channels[1].parameters.set_verified(2, 1.0, attempts=6, delay=0.05, sleep=lambda _: None)
    assert result.verified is True and result.unchanged is True
    assert result.attempts == 1 and result.display_changed is False
    assert result.raw_before == result.raw_after and result.normalized_after == 1.0
    operations = [str(call[1]["operation"]) for call in transport.calls]
    # The first guarded write probes the automation link index once per connection
    # (see fruitylink.automation_links); this transport cannot answer it, so the guard
    # gives up quietly instead of failing the write.
    assert operations.count("query_clips") == 1
    operations = [name for name in operations if name != "query_clips"]
    assert operations == ["query_plugin_parameters", "set_plugin_param", "query_plugin_parameters"]


def test_set_verified_keeps_unchanged_false_for_a_real_change(fl: Studio, transport: RecordingTransport) -> None:
    reads = iter([_slot(199, "Sub Shape", _bits(0.2), "Saw"), _slot(199, "Sub Shape", _bits(0.8), "Square")])

    def handler(method: str, params: dict[str, JsonValue]) -> JsonValue:
        return next(reads) if params["operation"] == "query_plugin_parameters" else None

    transport.handler = handler
    result = fl.channels[1].parameters.set_verified(199, 0.8, attempts=2, delay=0, sleep=lambda _: None)
    assert result.verified is True and result.unchanged is False and result.display_changed is True


def test_normalized_from_raw_rejects_integer_scales_and_out_of_range_bits() -> None:
    from fruitylink.plugins import normalized_from_raw
    assert normalized_from_raw(_bits(0.25)) == pytest.approx(0.25)
    assert normalized_from_raw(0) == 0.0
    assert normalized_from_raw(1234) is None          # a native integer scale decodes to a denormal
    assert normalized_from_raw(_bits(1.5)) is None
    assert normalized_from_raw(_bits(float("nan"))) is None


def test_normalized_from_raw_decodes_a_native_switch_integer() -> None:
    from fruitylink.plugins import normalized_from_raw

    # Live evidence 2026-09-14 (Fruity Delay 3 "Tempo sync" On): stock FL effects report a plain
    # integer, and the float32 bit patterns 0 and 1 are +0.0 and a 1.4e-45 denormal, so 0/1 can only
    # be the switch's own normalized value. Wider native scales stay undecodable.
    assert normalized_from_raw(1) == 1.0
    assert normalized_from_raw(0) == 0.0
    assert normalized_from_raw(2) is None
    assert PluginParameterInfo(2, "Tempo sync", 1, "On").normalized == 1.0
    assert PluginParameterInfo(2, "Tempo sync", 0, "Off").normalized == 0.0
    assert PluginParameterInfo(18, "Distortion", 32768, "50 %").normalized is None


def test_set_verified_on_a_native_switch_that_already_holds_the_value(
        fl: Studio, transport: RecordingTransport) -> None:
    # Check 15 of the 2026-09-14 live run: rawValue 1, displayValue "On", normalized null gave
    # verified=False, unchanged=False after 6 attempts (300 ms of readbacks). One readback now.
    transport.responses["query_plugin_parameters"] = _slot(2, "Tempo sync", 1, "On")
    result = fl.mixer[4].effects[3].parameters.set_verified("Tempo sync", 1.0, attempts=6, delay=0.05,
                                                            sleep=lambda _: None)
    assert (result.verified, result.unchanged, result.attempts) == (True, True, 1)
    assert (result.raw_before, result.raw_after) == (1, 1)
    assert result.display_after == "On" and result.display_changed is False
    assert result.normalized_after == 1.0
    assert [call[1]["operation"] for call in transport.calls].count("set_plugin_param") == 1


def test_set_verified_flips_a_native_switch_off_and_sees_the_integer_move(
        fl: Studio, transport: RecordingTransport) -> None:
    reads = iter([_slot(2, "Tempo sync", 1, "On"), _slot(2, "Tempo sync", 0, "Off")])
    transport.handler = lambda method, params: (
        next(reads) if params["operation"] == "query_plugin_parameters" else None)
    result = fl.mixer[4].effects[3].parameters.set_verified(2, 0.0, attempts=6, delay=0, sleep=lambda _: None)
    assert (result.verified, result.unchanged, result.display_changed) == (True, False, True)
    assert (result.raw_before, result.raw_after, result.attempts) == (1, 0, 1)
    assert result.normalized_after == 0.0


def test_set_verified_on_an_undecodable_scale_that_already_holds_the_value(
        fl: Studio, transport: RecordingTransport) -> None:
    # Live 2026-09-17: Fruity Limiter "Gain" at rawValue 1000, display "0.0dB", normalized null.
    # set_verified(0, 0.5) returned verified=False, attempts=6 twice in a row because the `unchanged`
    # short-circuit only fired when the normalized readback was available. Nothing moved after an
    # accepted write, so the slot already held the value: one readback, verified and unchanged.
    transport.responses["query_plugin_parameters"] = _slot(0, "Gain", 1000, "0.0dB")
    result = fl.mixer[0].effects[0].parameters.set_verified(0, 0.5, attempts=6, delay=0.05, sleep=lambda _: None)
    assert (result.verified, result.unchanged, result.attempts) == (True, True, 1)
    assert (result.raw_before, result.raw_after) == (1000, 1000)
    assert result.normalized_after is None and result.display_changed is False
    assert result.display_after == "0.0dB"
    operations = [str(call[1]["operation"]) for call in transport.calls]
    # The first guarded write probes the automation link index once per connection
    # (see fruitylink.automation_links); this transport cannot answer it, so the guard
    # gives up quietly instead of failing the write.
    assert operations.count("query_clips") == 1
    operations = [name for name in operations if name != "query_clips"]
    assert operations == ["query_plugin_parameters", "set_plugin_param", "query_plugin_parameters"]


def test_set_verified_on_an_undecodable_scale_that_moves_is_unaffected(
        fl: Studio, transport: RecordingTransport) -> None:
    # The same slot, a moving write: raw 1000 -> 1400, "0.0dB" -> "7.5dB" (live 2026-09-17, verified in two).
    reads = iter([_slot(0, "Gain", 1000, "0.0dB"), _slot(0, "Gain", 1400, "7.5dB")])
    transport.handler = lambda method, params: (
        next(reads) if params["operation"] == "query_plugin_parameters" else None)
    result = fl.mixer[0].effects[0].parameters.set_verified(0, 0.7, attempts=6, delay=0, sleep=lambda _: None)
    assert (result.verified, result.unchanged, result.display_changed) == (True, False, True)
    assert (result.raw_before, result.raw_after, result.attempts) == (1000, 1400, 1)
    assert result.normalized_after is None


def test_set_verified_still_reports_unverified_when_only_the_display_moves(
        fl: Studio, transport: RecordingTransport) -> None:
    # An undecodable raw value that stands still while the display moves is not "nothing had to change":
    # something in the plugin did move, so the raw oracle cannot confirm the write and says so.
    reads = iter([_slot(0, "Gain", 1000, "0.0dB")] + [_slot(0, "Gain", 1000, "7.5dB")] * 3)
    transport.handler = lambda method, params: (
        next(reads) if params["operation"] == "query_plugin_parameters" else None)
    result = fl.mixer[0].effects[0].parameters.set_verified(0, 0.7, attempts=3, delay=0, sleep=lambda _: None)
    assert (result.verified, result.unchanged, result.attempts) == (False, False, 3)
    assert result.display_changed is True
