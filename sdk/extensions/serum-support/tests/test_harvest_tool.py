"""Offline parts of tools/harvest_live.py: the plan, the manifest/result round trip and apply_verified."""

from __future__ import annotations

import base64
import importlib.util
import json
import struct
import sys
from dataclasses import dataclass
from pathlib import Path
from typing import Any

import pytest

from fruitylink_serum import cbor, schema, xfer

TOOL = Path(__file__).resolve().parents[1] / "tools" / "harvest_live.py"
ZSTD = xfer.zstd_available()
needs_zstd = pytest.mark.skipif(not ZSTD, reason="zstd support requires Python 3.14 or the zstandard package")


def load_tool() -> Any:
    spec = importlib.util.spec_from_file_location("harvest_live", TOOL)
    assert spec is not None and spec.loader is not None
    module = importlib.util.module_from_spec(spec)
    sys.modules["harvest_live"] = module
    spec.loader.exec_module(module)
    return module


def test_plan_lists_unverified_anchors_with_probe_values() -> None:
    tool = load_tool()
    items = tool.plan()
    names = {item["fl"] for item in items}
    anchors = schema.parameter_map()["fl_anchors"]
    verified = {row["fl"] for row in anchors if row.get("confidence") == "verified"}
    assert "Main Vol" in verified and names.isdisjoint(verified)    # verified anchors are skipped by default
    assert names == {row["fl"] for row in anchors if row.get("confidence") != "verified"}
    everything = tool.plan(include_verified=True)
    assert everything and all(item["path"] and 0 <= item["value"] <= 1 for item in everything)
    assert {item["fl"] for item in everything} == {row["fl"] for row in anchors}
    assert tool._normalise("Oscillator1/plainParams/kParamVolume") == "Oscillator{n}/plainParams/kParamVolume"


@dataclass(frozen=True)
class Param:
    index: int
    name: str
    raw_value: int
    display_value: str


class Parameters:
    def __init__(self, ops: Any) -> None:
        self.ops = ops

    def set_named(self, name: str, value: float) -> int:
        if name == "Nope":
            raise LookupError("Expected one parameter named 'Nope'; found 0.")
        self.ops.written.append((name, value))
        self.ops.events.append(("write", name))
        return 21

    def read(self, index: int) -> Param:
        return Param(index, "B Level", 1058642330, "60%")


class Channel:
    def __init__(self, ops: Any) -> None:
        self.parameters = Parameters(ops)


class Ops:
    def __init__(self, state_after: dict[str, Any], baseline: dict[str, Any] | None = None) -> None:
        self.written: list[tuple[str, float]] = []
        self.events: list[tuple[str, str]] = []
        self.states = [baseline or {"Oscillator1": {"plainParams": {"kParamVolume": cbor.Float32(0.25)}}}, state_after]

    def load_channel_plugin_state(self, *, channel: int, path: str, use_channel_loader: bool = False) -> str:
        self.events.append(("load", Path(path).name))
        return f"channel {channel}: state record changed"

    def get_channel_plugin_state(self, *, channel: int) -> str:
        self.events.append(("state", "read"))
        state = self.states.pop(0)
        block = xfer.write_container(xfer.XferContainer(metadata={"component": "processor"}, state={**state, "component": "processor"}))
        return base64.b64encode(b"\x00" * 8 + struct.pack("<III", 3, len(block), 0) + block).decode("ascii")

    def get_mixer_effect_state(self, *, track: int, slot: int) -> str:
        raise AssertionError("unused")


class Studio:
    def __init__(self, state_after: dict[str, Any], baseline: dict[str, Any] | None = None) -> None:
        self.ops = Ops(state_after, baseline)
        self.channels = {4: Channel(self.ops)}


@needs_zstd
def test_two_phase_harvest_and_apply(tmp_path: Path) -> None:
    tool = load_tool()
    fl = Studio({"Oscillator1": {"plainParams": {"kParamVolume": cbor.Float32(0.36)}}})
    items = [{"fl": "B Level", "path": "Oscillator1/plainParams/kParamVolume", "rule": "state = fl^2", "confidence": "inferred", "value": 0.6},
             {"fl": "Nope", "path": "Nope0/plainParams/kParamX", "rule": None, "confidence": "inferred", "value": 0.6}]
    first = tool.write_phase(fl, 4, out_dir=tmp_path, items=items)
    assert first["written"] == 1 and first["errors"][0]["fl"] == "Nope"
    assert fl.ops.written == [("B Level", 0.6), ("A Level", 0.5)]          # the ordering sentinel is written last
    manifest = json.loads(Path(first["manifest"]).read_text(encoding="utf-8"))
    assert manifest["baseline"]["Oscillator1/plainParams/kParamVolume"] == pytest.approx(0.25)
    second = tool.read_phase(fl, 4, manifest=first["manifest"])
    rows = {row["fl"]: row for row in second["rows"]}
    assert rows["B Level"]["status"] == "verified" and rows["B Level"]["display"] == "60%"
    assert rows["B Level"]["state_after"] == pytest.approx(0.36) and rows["Nope"]["status"] == "not_written"
    assert second["verified"] == ["B Level"] and Path(second["result"]).is_file()
    assert second["ordering"]["status"] == "unresolved" and second["warnings"] == []   # no A Level in the stub state

    data_dir = tmp_path / "data"
    data_dir.mkdir()
    (data_dir / "parameter-map.json").write_text(json.dumps({
        "parameters": [{"path": "Oscillator{n}/plainParams/kParamVolume", "confidence": "inferred"},
                       {"path": "Env{n}/plainParams/kParamAttack", "confidence": "inferred"}],
        "fl_anchors": [{"fl": "B Level", "path": "Oscillator1/plainParams/kParamVolume", "confidence": "inferred"}],
    }), encoding="utf-8")
    applied = tool.apply_verified(second["result"], data_dir=data_dir)
    assert applied["rows_updated"] == 2 and applied["verified"] == ["B Level"]
    updated = json.loads((data_dir / "parameter-map.json").read_text(encoding="utf-8"))
    assert updated["parameters"][0]["confidence"] == "verified" and updated["parameters"][1]["confidence"] == "inferred"
    assert updated["fl_anchors"][0]["confidence"] == "verified"


PROBE_STATE = {"Oscillator0": {"plainParams": {"kParamVolume": cbor.Float32(0.49)}},
               "Env0": {"plainParams": {"kParamAttack": cbor.Float32(2.488)}}}
ITEMS = [{"fl": "Env 1 Attack", "path": "Env0/plainParams/kParamAttack", "rule": None, "confidence": "inferred", "value": 0.6}]


@needs_zstd
def test_probe_patch_loads_before_the_baseline_and_every_write(tmp_path: Path) -> None:
    # Live 2026-09-14: the probe load came after the writes and wiped them (6 verified / 15 contradictory rows).
    after = {"Oscillator0": {"plainParams": {"kParamVolume": cbor.Float32(0.25)}},
             "Env0": {"plainParams": {"kParamAttack": cbor.Float32(0.001)}}}
    fl = Studio(after, baseline=PROBE_STATE)
    tool = load_tool()

    first = tool.write_phase(fl, 4, out_dir=tmp_path, items=ITEMS, probe_mod_matrix=True)

    kinds = [kind for kind, _ in fl.ops.events]
    assert kinds.index("load") < kinds.index("state") < kinds.index("write")
    assert fl.ops.written == [("Env 1 Attack", 0.6), ("A Level", 0.5)]
    assert first["mod_probe"]["order"].startswith("loaded before")
    assert first["sentinel"] == {"fl": "A Level", "path": "Oscillator0/plainParams/kParamVolume", "value": 0.5,
                                 "expected_state": 0.25, "state_before": pytest.approx(0.49),
                                 "rule": "state = fl^2 (verified)", "index": 21, "error": None}
    manifest = json.loads(Path(first["manifest"]).read_text(encoding="utf-8"))
    assert manifest["order"] == ["mod_probe", "baseline", "writes", "sentinel"]
    assert manifest["baseline"]["Env0/plainParams/kParamAttack"] == pytest.approx(2.488)   # the probe's state, read after the load

    second = tool.read_phase(fl, 4, manifest=first["manifest"])
    assert second["ordering"]["status"] == "ok" and second["verified"] == ["Env 1 Attack"] and second["warnings"] == []
    assert second["mod_probe_result"] == {}


@needs_zstd
def test_read_phase_reports_a_load_that_followed_the_writes(tmp_path: Path) -> None:
    fl = Studio(PROBE_STATE, baseline=PROBE_STATE)      # the state read back still holds the probe patch's values
    tool = load_tool()
    first = tool.write_phase(fl, 4, out_dir=tmp_path, items=ITEMS, probe_mod_matrix=True)

    second = tool.read_phase(fl, 4, manifest=first["manifest"])

    assert second["ordering"]["status"] == "load_after_write" and second["ordering"]["state_after"] == pytest.approx(0.49)
    assert second["verified"] == [] and second["unresolved"] == ["Env 1 Attack"]
    assert second["warnings"][0].startswith("load after write: A Level still holds its pre-write state")
    assert {row["fl"]: row["status"] for row in second["rows"]} == {"Env 1 Attack": "unchanged"}
    saved = json.loads(Path(second["result"]).read_text(encoding="utf-8"))
    assert saved["ordering"]["status"] == "load_after_write"
