import inspect
import re
from pathlib import Path

import pytest
from conftest import RecordingTransport

from fruitylink import NoteEdit, NoteRef, NoteSpec, Operations, PatternClipSpec, Studio
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
        assert list(signature.parameters) == ["self", *expected]
        assert signature.return_annotation is not inspect.Signature.empty
        assert method.__doc__


def test_record_arguments_keep_camel_case_wire_names(fl: Studio, transport: RecordingTransport) -> None:
    fl.ops.add_notes(pattern=2, notes=[NoteSpec(1, 60, 96, 48, 100)])
    assert transport.calls == [("invoke", {"operation": "add_notes", "arguments": {
        "pattern": 2, "notes": [{"channel": 1, "key": 60, "startTick": 96, "lengthTick": 48, "velocity": 100}]}})]
    assert to_json(NoteEdit(1, 60, 96, new_velocity=80)) == {
        "channel": 1, "key": 60, "startTick": 96, "newKey": None, "newStartTick": None,
        "newLength": None, "newVelocity": 80, "muted": None}
    assert to_json(NoteRef(1, 60, 96)) == {"channel": 1, "key": 60, "startTick": 96}
    assert to_json(PatternClipSpec(1, 2, 96, 384)) == {"pattern": 1, "track": 2, "startTick": 96, "lengthTick": 384}


def test_nullable_without_default_stays_required(fl: Studio) -> None:
    assert inspect.signature(fl.ops.list_samples).parameters["filter"].default is inspect.Parameter.empty
    assert inspect.signature(fl.ops.get_notes).parameters["offset"].default == 0


@pytest.mark.parametrize("value", [float("nan"), float("inf"), object(), {1: "bad"}, b"binary"])
def test_non_json_values_are_rejected(value: object) -> None:
    with pytest.raises((ValueError, TypeError)):
        to_json(value)
