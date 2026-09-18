import inspect
import re
from pathlib import Path

import pytest
from conftest import RecordingTransport

from fruitylink import Operations, ProtocolError, Studio
from fruitylink.models import NoteInfo, Page, decode_page
from fruitylink.values import JsonValue


def test_structured_query_contract_parameters_match() -> None:
    contract = Path(__file__).resolve().parents[2] / "src/FruityLink.Core/Abstractions/IFlStructuredQuery.cs"
    declarations = re.findall(r"Task(?:<[^\n]+?>)?\s+(\w+)Async\(([^;]+)\);", contract.read_text(encoding="utf-8"))
    assert {re.sub(r"(?<!^)(?=[A-Z])", "_", native).lower() for native, _ in declarations} == {
        name for name in dir(Operations) if name.startswith("query_")}
    for native, parameters in declarations:
        operation = re.sub(r"(?<!^)(?=[A-Z])", "_", native).lower()
        expected = [re.sub(r"(?<!^)(?=[A-Z])", "_", part.strip().split()[1]).lower()
                    for part in parameters.split(",") if "CancellationToken" not in part]
        assert list(inspect.signature(getattr(Operations, operation)).parameters) == ["self", *expected]


def test_filtered_empty_pages_still_follow_continuation(fl: Studio, transport: RecordingTransport) -> None:
    def handler(method: str, params: dict[str, JsonValue]) -> JsonValue:
        arguments = params["arguments"]
        assert isinstance(arguments, dict)
        if arguments["offset"] == 0:
            return {"items": [], "nextOffset": 512, "total": 600}
        return {"items": [{"index": 550, "channel": 2, "key": 60, "startTick": 96, "lengthTick": 48,
                           "velocity": 100, "muted": False}], "nextOffset": None, "total": 600}
    transport.handler = handler
    notes = fl.patterns[1].notes.list(channel=2)
    assert len(notes) == 1 and notes[0].index == 550
    assert len(transport.calls) == 2


def test_nonadvancing_page_rejected(fl: Studio, transport: RecordingTransport) -> None:
    transport.responses["query_notes"] = {"items": [], "nextOffset": 0, "total": 1}
    with pytest.raises(ProtocolError, match="advance"):
        fl.patterns[1].notes.list()


@pytest.mark.parametrize("value", [{"items": [], "nextOffset": -1, "total": 10},
    {"items": [], "nextOffset": None, "total": -1}, {"items": [{}], "nextOffset": None, "total": 1}])
def test_malformed_pages_rejected(value: JsonValue) -> None:
    with pytest.raises(ProtocolError):
        decode_page(NoteInfo, value)


def test_structured_records_keep_unicode_and_nullable_fields(fl: Studio, transport: RecordingTransport) -> None:
    transport.responses["query_patterns"] = [{"index": 1, "name": "♫ Verse\n:2", "lengthTick": None,
                                              "noteCount": None, "current": True}]
    pattern = fl.patterns.list()[0]
    assert pattern.name == "♫ Verse\n:2" and pattern.note_count is None
    assert Page((pattern,), None, 1).items[0] == pattern


def _sample(entry: str) -> dict[str, JsonValue]:
    tag, relative = entry[:3], entry[3:]
    stem, _, extension = relative.rpartition(".")
    return {"entry": entry, "rootTag": tag, "relativePath": relative,
            "name": stem.rpartition("\\")[2], "extension": f".{extension}"}


def test_sample_query_returns_loadable_entries_and_pages_on_matches(fl: Studio,
                                                                   transport: RecordingTransport) -> None:
    hits = [_sample(r"[P]Drums\Kicks\909 Kick.wav"), _sample(r"[P]Drums\Claps\Clap 1.wav"),
            _sample(r"[U]Hats\Closed Hat.wav")]

    def handler(method: str, params: dict[str, JsonValue]) -> JsonValue:
        arguments = params["arguments"]
        assert isinstance(arguments, dict)
        offset, limit = int(str(arguments["offset"])), int(str(arguments["limit"]))
        window = hits[offset:offset + limit]
        following = offset + len(window)
        return {"items": window, "nextOffset": following if following < len(hits) else None, "total": len(hits)}

    transport.handler = handler
    first = fl.samples.query("drum", limit=2)
    assert [item.entry for item in first.items] == [r"[P]Drums\Kicks\909 Kick.wav", r"[P]Drums\Claps\Clap 1.wav"]
    assert (first.next_offset, first.total) == (2, 3)
    assert (first.items[0].root_tag, first.items[0].name, first.items[0].extension) == ("[P]", "909 Kick", ".wav")
    assert first.items[0].relative_path == r"Drums\Kicks\909 Kick.wav"

    second = fl.samples.query("drum", offset=first.next_offset, limit=2)
    assert [item.root_tag for item in second.items] == ["[U]"]
    assert second.next_offset is None
    assert [call[1]["arguments"] for call in transport.calls] == [
        {"filter": "drum", "offset": 0, "limit": 2}, {"filter": "drum", "offset": 2, "limit": 2}]


def test_sample_query_defaults_and_page_bounds(fl: Studio, transport: RecordingTransport) -> None:
    transport.responses["query_samples"] = {"items": [], "nextOffset": None, "total": 0}
    assert fl.samples.query().items == ()
    assert transport.calls[0][1]["arguments"] == {"filter": None, "offset": 0, "limit": 50}
    for kwargs in ({"limit": 0}, {"limit": 513}, {"offset": -1}):
        with pytest.raises(IndexError):
            fl.samples.query(**kwargs)   # type: ignore[arg-type]
    assert len(transport.calls) == 1, "a refused page must not reach the host"
