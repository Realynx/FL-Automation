from typing import Any

import pytest
from conftest import RecordingTransport

from fruitylink import Studio
from fruitylink.project import Marker

LISTING = "3 markers:\nIntro - first light @ tick 0 (bar 1)\n(marker) @ tick 768 (bar 3)\nOutro - afterglow @ tick 39936 (bar 105)"


def test_markers_parse_host_listing_in_order(fl: Studio, transport: RecordingTransport) -> None:
    transport.responses["list_markers"] = LISTING
    assert fl.transport.markers() == (
        Marker(0, "Intro - first light", 0),
        Marker(1, "", 768),
        Marker(2, "Outro - afterglow", 39936),
    )
    assert transport.calls == [("invoke", {"operation": "list_markers", "arguments": {}})]


@pytest.mark.parametrize("text", ["(no markers)", "(no arrangement)", "", "garbage"])
def test_markers_are_empty_for_non_listings(fl: Studio, transport: RecordingTransport, text: str) -> None:
    transport.responses["list_markers"] = text
    assert fl.transport.markers() == ()


def test_delete_marker_by_index_does_not_list_first(fl: Studio, transport: RecordingTransport) -> None:
    assert fl.transport.delete_marker(2) == 2
    assert transport.calls == [("invoke", {"operation": "delete_marker", "arguments": {"index": 2}})]


def test_delete_marker_by_name_resolves_then_deletes(fl: Studio, transport: RecordingTransport) -> None:
    transport.responses["list_markers"] = LISTING
    assert fl.transport.delete_marker("Outro - afterglow") == 2
    assert transport.calls == [
        ("invoke", {"operation": "list_markers", "arguments": {}}),
        ("invoke", {"operation": "delete_marker", "arguments": {"index": 2}}),
    ]


@pytest.mark.parametrize("name", ["Chorus", "outro - afterglow"])
def test_delete_marker_missing_or_ambiguous_name_never_writes(fl: Studio, transport: RecordingTransport,
                                                            name: str) -> None:
    transport.responses["list_markers"] = LISTING + "\nChorus @ tick 100\nChorus @ tick 200"
    with pytest.raises(LookupError, match="Expected one marker"):
        fl.transport.delete_marker(name)
    assert [call[1]["operation"] for call in transport.calls] == ["list_markers"]


@pytest.mark.parametrize("marker", [-1, True, None, 1.5, [], {}, ""])
def test_delete_marker_rejects_invalid_identifiers_before_any_request(fl: Studio, transport: RecordingTransport,
                                                                    marker: Any) -> None:
    with pytest.raises((TypeError, IndexError, ValueError)):
        fl.transport.delete_marker(marker)
    assert transport.calls == []
