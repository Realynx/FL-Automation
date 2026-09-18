"""Automation links: who owns a control, what a plain write to it does, and releasing the curve.

Live defect this covers (FL 26.1.3.5570, 2026-09-17): a pump on insert 5's volume created automation
channel 12; after the playlist clip was deleted, ``fl.mixer[5].volume = 6400`` read back 6400 while
the engine kept the insert at the automation's value on every play. The link lives on the automation
CHANNEL and FL has no channel delete, so the SDK warns on such a write and releases the curve instead.
"""

import warnings
from typing import Any

import pytest
from conftest import RecordingTransport
from test_arrangement_fixes import PPQ, channel, clip, install_automation_project, operations, page

from fruitylink import AutomationLinkedWarning, AutomationTarget, PumpResult, Studio
from fruitylink.automation_links import linked_message, normalized_value
from fruitylink.values import JsonValue


def install_pinned_insert(transport: RecordingTransport, *, volume: int = 6400, points: int = 2,
                          placed: bool = False) -> None:
    """Channel 12 is "Insert 5 pump", linked to insert 5's volume, with its playlist clip deleted.

    ``placed=True`` keeps one clip for it instead, which is the case where ``release`` must not
    touch the playlist.
    """
    descriptions = {
        0: "Kick: generator 'FPC' (120 params)",
        12: "Insert 5 pump: automation clip -> Insert 5 volume, event 0x71401fc0",
    }

    def handler(method: str, params: dict[str, JsonValue]) -> JsonValue:
        operation = params.get("operation")
        args = params.get("arguments")
        assert isinstance(args, dict)
        if operation == "get_ppq":
            return PPQ
        if operation == "query_channels":
            return [channel(0, "Kick"), channel(12, "Insert 5 pump")]
        if operation == "get_channel_plugin":
            return descriptions[int(str(args["channel"]))]
        if operation == "query_clips":
            # The clip was deleted and the link survived it; with placed=True one clip remains.
            return page([clip(1, 19, 0, 8 * PPQ, kind="channel", source=12)] if placed else [])
        if operation == "query_automation_points":
            return [{"index": i, "timeBeats": float(i) * 4, "value": 0.8, "tension": 0.0, "curve": 0}
                    for i in range(points)]
        if operation == "get_mixer_volume":
            return volume
        if operation == "create_automation_clip":
            return {"channel": 40, "clipIndex": 4}
        if operation == "add_automation_clip":
            return 7
        if operation in ("set_mixer_volume", "set_automation_points", "set_channel_volume"):
            return None
        raise AssertionError(operation)

    transport.handler = handler


# ---- discovery ---------------------------------------------------------------------------------------

def test_links_to_finds_the_channel_that_owns_a_control(fl: Studio, transport: RecordingTransport) -> None:
    install_automation_project(transport)

    owners = fl.automation.links_to(AutomationTarget.mixer_volume(24))

    assert [item.channel for item in owners] == [27]
    assert owners[0].name == "PLM - Delay send level (insert 24)"
    assert owners[0].target == AutomationTarget.mixer_volume(24)
    assert owners[0].point_count == -1                 # the cached index never reads envelopes
    assert "query_automation_points" not in operations(transport)
    assert fl.automation.links_to(AutomationTarget.mixer_volume(23)) == ()


def test_targets_of_takes_loose_fields_and_matches_plugin_parameters(fl: Studio,
                                                                   transport: RecordingTransport) -> None:
    install_automation_project(transport)

    assert [item.channel for item in fl.automation.targets_of("mixer_volume", 6)] == [36]
    assert [item.channel for item in fl.automation.targets_of("plugin_parameter", 4, 0, 48)] == [21]
    assert fl.automation.targets_of("plugin_parameter", 4, 0, 49) == ()


def test_the_link_index_is_cached_per_connection_until_refresh(fl: Studio,
                                                             transport: RecordingTransport) -> None:
    install_automation_project(transport)

    fl.automation.links_to(AutomationTarget.mixer_volume(6))
    fl.automation.links_to(AutomationTarget.mixer_volume(24))
    assert operations(transport).count("query_channels") == 1

    fl.automation.refresh()
    assert operations(transport).count("query_channels") == 2


def test_a_deleted_clip_still_reports_its_link(fl: Studio, transport: RecordingTransport) -> None:
    install_pinned_insert(transport)

    owners = fl.automation.links_to(AutomationTarget.mixer_volume(5))

    assert [(item.channel, item.name, item.clips) for item in owners] == [(12, "Insert 5 pump", ())]


# ---- guarded writes ----------------------------------------------------------------------------------

def test_writing_a_linked_mixer_volume_warns_and_still_writes(fl: Studio,
                                                            transport: RecordingTransport) -> None:
    install_pinned_insert(transport)

    with pytest.warns(AutomationLinkedWarning) as caught:
        fl.mixer[5].volume = 6400

    assert len(caught) == 1
    message = str(caught[0].message)
    assert "mixer_volume 5" in message and "channel 12 'Insert 5 pump'" in message
    assert "every time playback starts" in message and "fl.automation.release(12)" in message
    assert ("set_mixer_volume", {"track": 5, "value": 6400}) in [
        (name, dict(args)) for name, args in _calls(transport)]


def test_set_volume_can_raise_or_skip_the_check(fl: Studio, transport: RecordingTransport) -> None:
    install_pinned_insert(transport)

    with pytest.raises(AutomationLinkedWarning, match="release"):
        fl.mixer[5].set_volume(6400, linked="raise")
    assert "set_mixer_volume" not in operations(transport)   # refused before the write

    with warnings.catch_warnings():
        warnings.simplefilter("error")
        assert fl.mixer[5].set_volume(6400, linked="ignore") == 6400
    assert operations(transport).count("set_mixer_volume") == 1


def test_an_unlinked_control_is_written_without_a_warning(fl: Studio,
                                                        transport: RecordingTransport) -> None:
    install_pinned_insert(transport)

    with warnings.catch_warnings():
        warnings.simplefilter("error")
        fl.mixer[4].volume = 6400
        fl.channels[0].set_volume(9000)

    assert operations(transport).count("query_channels") == 1   # one scan served both writes


def test_a_host_that_cannot_describe_links_never_fails_a_write(fl: Studio,
                                                             transport: RecordingTransport) -> None:
    with warnings.catch_warnings():
        warnings.simplefilter("error")
        fl.mixer[5].volume = 6400
        fl.mixer[5].volume = 6500
        fl.channels[3].pitch = 100

    assert operations(transport).count("query_clips") == 1   # the failure is remembered, not retried
    assert operations(transport).count("set_mixer_volume") == 2


def test_a_linked_plugin_parameter_write_reports_the_link(fl: Studio,
                                                        transport: RecordingTransport) -> None:
    """Channel 21 automates insert 4's slot-0 parameter 48 (event 0x71008030, as FL prints it)."""
    descriptions = {0: "Kick: generator 'FPC' (120 params)",
                    21: "Pad HP: automation clip -> event 0x71008030"}
    slot: dict[str, JsonValue] = {
        "items": [{"index": 48, "name": "High Cut", "rawValue": 0, "displayValue": "20 kHz"}],
        "nextOffset": None, "total": 1}

    def handler(method: str, params: dict[str, JsonValue]) -> JsonValue:
        operation = params.get("operation")
        args = params.get("arguments")
        assert isinstance(args, dict)
        if operation == "query_channels":
            return [channel(0, "Kick"), channel(21, "Pad HP")]
        if operation == "get_channel_plugin":
            return descriptions[int(str(args["channel"]))]
        if operation == "query_clips":
            return page([])
        if operation == "query_plugin_parameters":
            return dict(slot)
        if operation == "set_plugin_param":
            return None
        raise AssertionError(operation)

    transport.handler = handler
    parameters = fl.mixer[4].effects[0].parameters

    with pytest.warns(AutomationLinkedWarning) as caught:
        result = parameters.set_verified(48, 0.0, attempts=1, delay=0, sleep=lambda _: None)

    assert len(caught) == 1                       # the inner set() must not warn a second time
    assert result.verified is True                # the write landed; it just will not survive a play
    assert len(result.automation_linked) == 1
    assert str(caught[0].message) == result.automation_linked[0]
    assert "insert 4 slot 0 parameter 48" in result.automation_linked[0]
    assert "channel 21 'Pad HP'" in result.automation_linked[0]

    with warnings.catch_warnings():
        warnings.simplefilter("error")
        parameters.set(48, 0.5, linked="ignore")
        assert parameters.set_verified(48, 0.5, attempts=1, delay=0, sleep=lambda _: None,
                                       linked="ignore").automation_linked == ()
    with pytest.raises(AutomationLinkedWarning):
        parameters.set(48, 0.5, linked="raise")


@pytest.mark.parametrize("mode", ["Warn", "", None, True])
def test_an_unknown_linked_mode_is_refused_before_the_write(fl: Studio, transport: RecordingTransport,
                                                          mode: Any) -> None:
    with pytest.raises(ValueError, match="linked"):
        fl.mixer[5].set_volume(6400, linked=mode)
    assert transport.calls == []


# ---- releasing ---------------------------------------------------------------------------------------

def test_release_flattens_the_curve_to_the_targets_current_value(fl: Studio,
                                                               transport: RecordingTransport) -> None:
    install_pinned_insert(transport, volume=6400, points=5)

    released = fl.automation.release(12)

    assert released == pytest.approx(6400 / 16000) and released.value == float(released)
    written = [args for name, args in _calls(transport) if name == "set_automation_points"]
    assert written == [{"channel": 12, "points": [
        {"timeBeats": 0, "value": 0.4, "tension": 0, "curve": 0},
        {"timeBeats": 16.0, "value": 0.4, "tension": 0, "curve": 0}]}]   # span of the old envelope


def test_release_accepts_an_explicit_value_and_needs_no_link_lookup(fl: Studio,
                                                                  transport: RecordingTransport) -> None:
    install_pinned_insert(transport, points=2)

    assert fl.automation[12].release(0.0, place_clip=False) == 0.0

    assert "query_channels" not in operations(transport) and "query_clips" not in operations(transport)
    assert "add_automation_clip" not in operations(transport)
    assert [args["points"] for name, args in _calls(transport) if name == "set_automation_points"] == [[
        {"timeBeats": 0, "value": 0.0, "tension": 0, "curve": 0},
        {"timeBeats": 4.0, "value": 0.0, "tension": 0, "curve": 0}]]


def test_release_places_a_clip_when_the_curve_has_none(fl: Studio,
                                                     transport: RecordingTransport) -> None:
    # Live 2026-09-17: with no clip in the playlist FL never evaluates the flattened curve - releasing
    # to 0.8 and to 0.4 both measured about -19.6 dBFS at the master. One placed clip made the same two
    # releases measure -19.60 and -32.18 dBFS, the 12.58 dB the fader model predicts.
    install_pinned_insert(transport, points=5)

    result = fl.automation.release(12)

    assert (result.placed_clip, result.track, result.start_tick, result.length_tick) == (True, 1, 0, 16 * PPQ)
    assert result.clip_index == 7
    assert [args for name, args in _calls(transport) if name == "add_automation_clip"] == [
        {"channel": 12, "track": 1, "startTick": 0, "lengthTick": 16 * PPQ}]


def test_release_leaves_an_existing_placement_alone(fl: Studio, transport: RecordingTransport) -> None:
    install_pinned_insert(transport, points=5, placed=True)

    result = fl.automation.release(12, 0.4)

    assert (result.placed_clip, result.track, result.clip_index) == (False, -1, -1)
    assert "add_automation_clip" not in operations(transport)
    assert operations(transport).count("query_clips") == 1   # one look, no playlist edit


def test_release_placement_is_never_shorter_than_a_bar(fl: Studio, transport: RecordingTransport) -> None:
    install_pinned_insert(transport, points=1)   # a one-point curve has no span at all

    result = fl.automation[12].release(0.5)

    assert (result.placed_clip, result.length_tick) == (True, 4 * PPQ)


def test_release_can_skip_the_placement(fl: Studio, transport: RecordingTransport) -> None:
    install_pinned_insert(transport, points=5)

    result = fl.automation[12].release(0.4, place_clip=False)

    assert result.placed_clip is False and float(result) == 0.4
    assert "query_clips" not in operations(transport)


def test_release_refuses_a_channel_with_no_readable_link(fl: Studio, transport: RecordingTransport) -> None:
    install_pinned_insert(transport)

    with pytest.raises(LookupError, match="not a linked automation clip channel"):
        fl.automation.release(0)
    assert "set_automation_points" not in operations(transport)


@pytest.mark.parametrize("value", [-0.1, 1.5, True, "0.5"])
def test_release_rejects_values_outside_an_automation_point(fl: Studio, transport: RecordingTransport,
                                                          value: Any) -> None:
    install_pinned_insert(transport)
    with pytest.raises(ValueError):
        fl.automation[12].release(value)
    assert transport.calls == []


@pytest.mark.parametrize("target,response,expected", [
    (AutomationTarget.mixer_volume(5), ("get_mixer_volume", 12800), 0.8),
    (AutomationTarget.mixer_pan(5), ("get_mixer_pan", -6400), 0.0),
    (AutomationTarget.mixer_pan(5), ("get_mixer_pan", 0), 0.5),
    (AutomationTarget.channel_volume(3), ("get_channel_volume", 10240), 0.8),
    (AutomationTarget.channel_pan(3), ("get_channel_pan", 6400), 0.5),
    (AutomationTarget.channel_pitch(3), ("get_channel_pitch", 100), None),
])
def test_current_values_convert_to_automation_values(fl: Studio, transport: RecordingTransport,
                                                   target: AutomationTarget, response: tuple[str, int],
                                                   expected: float | None) -> None:
    transport.responses[response[0]] = response[1]
    assert normalized_value(fl.ops, target) == expected


# ---- what the results and messages promise ----------------------------------------------------------

def test_pump_result_states_that_the_target_stays_linked(fl: Studio, transport: RecordingTransport) -> None:
    transport.responses["create_automation_clip"] = {"channel": 12, "clipIndex": 3}
    transport.responses["get_ppq"] = PPQ

    result = fl.automation.pump(AutomationTarget.mixer_volume(5), 0, 4 * PPQ, track=8)

    assert isinstance(result, PumpResult) and result.channel == 12
    assert "Automation channel 12" in result.link_notice
    assert "every time playback starts" in result.link_notice
    assert "deleting the clip does not unlink it" in result.link_notice
    assert "fl.automation.release(12)" in result.link_notice
    assert result.link_notice == result.clip.link_notice


def test_creation_invalidates_the_cached_link_index(fl: Studio, transport: RecordingTransport) -> None:
    install_pinned_insert(transport)
    fl.automation.links_to(AutomationTarget.mixer_volume(5))
    assert operations(transport).count("query_channels") == 1

    fl.automation.create(AutomationTarget.mixer_volume(9), 1, 0, 384)
    fl.automation.links_to(AutomationTarget.mixer_volume(5))

    assert operations(transport).count("query_channels") == 2   # a new link must not stay invisible


def test_the_warning_text_names_every_owner(fl: Studio, transport: RecordingTransport) -> None:
    install_automation_project(transport)
    owners = fl.automation.links_to(AutomationTarget.mixer_volume(6))

    message = linked_message(AutomationTarget.mixer_volume(6), owners)

    assert message.startswith("mixer_volume 6 is linked to automation clip channel 36")
    assert "'PLM - Bass duck (insert 6)'" in message


def _calls(transport: RecordingTransport) -> list[tuple[str, dict[str, Any]]]:
    result: list[tuple[str, dict[str, Any]]] = []
    for _, params in transport.calls:
        arguments = params["arguments"]
        assert isinstance(arguments, dict)
        result.append((str(params["operation"]), dict(arguments)))
    return result
