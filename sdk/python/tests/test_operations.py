import inspect
import json
import re
from pathlib import Path

import pytest
from conftest import RecordingTransport

from fruitylink import NoteEdit, NoteRef, NoteSpec, Operations, PatternClipSpec, Studio
from fruitylink.operations import ARGUMENT_ALIASES
from fruitylink.values import to_json


def test_all_native_operations_have_concrete_typed_signatures() -> None:
    contract = Path(__file__).resolve().parents[2] / "src/FruityLink.Core/Abstractions/INativeFlControl.cs"
    declarations = re.findall(r"Task(?:<[^\n]+?>)?\s+(\w+)Async\(([^;]+)\);", contract.read_text(encoding="utf-8"))
    assert len(declarations) >= 123
    for native, parameters in declarations:
        operation = re.sub(r"(?<!^)(?=[A-Z])", "_", native).lower()
        method = getattr(Operations, operation)
        signature = inspect.signature(method)
        expected = [re.sub(r"(?<!^)(?=[A-Z])", "_", part.strip().split()[1]).lower()
                    for part in parameters.split(",") if "CancellationToken" not in part]
        aliases = list(ARGUMENT_ALIASES.get(operation, {}))
        assert list(signature.parameters) == ["self", *expected, *aliases]
        assert signature.return_annotation is not inspect.Signature.empty
        assert method.__doc__
        for alias in aliases:
            assert f"``{alias}=``" in method.__doc__


def test_volume_and_pan_setters_accept_the_property_name_as_an_alias(fl: Studio, transport: RecordingTransport) -> None:
    fl.ops.set_channel_volume(channel=15, volume=8000)
    fl.ops.set_mixer_volume(track=3, volume=12800)
    fl.ops.set_master_volume(volume=9000)
    fl.ops.set_channel_pan(channel=2, pan=6400)
    fl.ops.set_mixer_pan(track=3, pan=-3200)
    fl.ops.set_channel_volume(channel=15, value=7000)
    assert [arguments["arguments"] for _, arguments in transport.calls] == [
        {"channel": 15, "value": 8000}, {"track": 3, "value": 12800}, {"value": 9000},
        {"channel": 2, "value": 6400}, {"track": 3, "value": -3200}, {"channel": 15, "value": 7000}]
    wire = [arguments["arguments"] for _, arguments in transport.calls]
    assert all(isinstance(item, dict) and item.keys() <= {"channel", "track", "value"} for item in wire)


def test_alias_misuse_is_a_local_type_error_naming_the_canonical_keyword(fl: Studio, transport: RecordingTransport) -> None:
    with pytest.raises(TypeError, match=r"canonical: value="):
        fl.ops.set_channel_volume(channel=1)
    with pytest.raises(TypeError, match=r"exactly one of value=, volume="):
        fl.ops.set_channel_volume(channel=1, value=1, volume=2)
    with pytest.raises(TypeError):
        fl.ops.set_mixer_pan(track=1, value=0, pan=0)
    assert transport.calls == []


def test_record_arguments_keep_camel_case_wire_names(fl: Studio, transport: RecordingTransport) -> None:
    fl.ops.add_notes(pattern=2, notes=[NoteSpec(1, 60, 96, 48, 100)])
    assert transport.calls == [("invoke", {"operation": "add_notes", "arguments": {
        "pattern": 2, "notes": [{"channel": 1, "key": 60, "startTick": 96, "lengthTick": 48, "velocity": 100}]}})]
    assert to_json(NoteEdit(1, 60, 96, new_velocity=80)) == {
        "channel": 1, "key": 60, "startTick": 96, "newKey": None, "newStartTick": None,
        "newLength": None, "newVelocity": 80, "muted": None, "lengthTick": None}
    assert to_json(NoteRef(1, 60, 96)) == {"channel": 1, "key": 60, "startTick": 96, "lengthTick": None}
    assert to_json(NoteRef(1, 60, 96, 48)) == {"channel": 1, "key": 60, "startTick": 96, "lengthTick": 48}
    assert to_json(PatternClipSpec(1, 2, 96, 384)) == {"pattern": 1, "track": 2, "startTick": 96, "lengthTick": 384}


def test_nullable_without_default_stays_required(fl: Studio) -> None:
    assert inspect.signature(fl.ops.list_samples).parameters["filter"].default is inspect.Parameter.empty
    assert inspect.signature(fl.ops.get_notes).parameters["offset"].default == 0


@pytest.mark.parametrize("value", [float("nan"), float("inf"), object(), {(1, 2): "bad"},
                                   {None: "bad"}, {float("nan"): "bad"}, b"binary"])
def test_non_json_values_are_rejected(value: object) -> None:
    with pytest.raises((ValueError, TypeError)):
        to_json(value)


def test_numeric_and_boolean_mapping_keys_become_json_names() -> None:
    """Index maps and enum tables (Serum's explain_parameters keys meanings by normalized value)
    are legitimate results; refusing them made them unreturnable from the embedded worker."""
    assert to_json({12: "Sine", 0.5: "Saw", True: "on", "x": 1}) == {
        "12": "Sine", "0.5": "Saw", "True": "on", "x": 1}
    assert to_json({0.0: "sine", 0.2: "roundrect", 1.0: "pulse"}) == {
        "0.0": "sine", "0.2": "roundrect", "1.0": "pulse"}
    assert json.dumps(to_json({1: {2: [3.5]}}), allow_nan=False) == '{"1": {"2": [3.5]}}'
    with pytest.raises(ValueError, match="Duplicate JSON object key"):
        to_json({1: "int", "1": "string"})
