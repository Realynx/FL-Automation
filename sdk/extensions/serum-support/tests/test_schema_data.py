"""Invariants for the derived Serum state-schema data files."""

from __future__ import annotations

import json
from importlib import resources

import pytest

from fruitylink_serum import schema

FORBIDDEN_PAYLOAD_KEYS = {"storedPhasePos", "curveVals", "xVals", "yVals", "lfo", "flex", "PZs"}


@pytest.mark.parametrize("name", schema.DATA_FILES)
def test_data_files_are_json_objects_with_provenance(name: str) -> None:
    data = schema.load_data(name)
    assert isinstance(data, dict)
    assert "provenance" in data
    assert data["provenance"]["files_scanned"] > 0


def test_no_preset_payloads_are_stored() -> None:
    for name in schema.DATA_FILES:
        text = resources.files("fruitylink_serum").joinpath("data", name).read_text(encoding="utf-8")
        loaded = json.loads(text)

        def walk(node: object) -> None:
            if isinstance(node, dict):
                for key, value in node.items():
                    assert key not in FORBIDDEN_PAYLOAD_KEYS, f"{name} stores payload-like key {key}"
                    walk(value)
            elif isinstance(node, list):
                assert len(node) <= 4096
                for item in node:
                    walk(item)

        walk(loaded)


def test_parameter_rows_have_required_fields() -> None:
    rows = schema.parameter_map()["parameters"]
    assert len(rows) > 300
    for row in rows:
        for field in ("path", "section", "key", "type", "occurrences", "files", "scale", "confidence"):
            assert field in row, f"{row.get('path')} lacks {field}"
        assert "/plainParams/" in row["path"]
        if row["type"] == "enum":
            assert row["values"], row["path"]


def test_known_anchor_rows_present() -> None:
    volume = schema.parameter("Oscillator0/plainParams/kParamVolume")
    assert volume is not None and volume["scale"] == "linear_gain" and volume["max"] <= 1.0
    unison = schema.parameter("Oscillator0/plainParams/kParamUnison")
    assert unison is not None and unison["scale"] == "count" and unison["max"] == 16.0
    cutoff = schema.parameter("VoiceFilter0/plainParams/kParamFreq")
    assert cutoff is not None and cutoff["scale"] == "filter_cutoff_normalized"
    sustain = schema.parameter("Env0/plainParams/kParamSustain")
    assert sustain is not None and sustain["confidence"] == "verified"
    shape = schema.parameter("Oscillator4/SubOsc4/plainParams/kParamShape")
    assert shape is not None and {"kSaw", "kSquare", "kTriangle", "kPulse"} <= set(shape["values"])
    level = schema.fl_anchor("A Level")
    assert level is not None and level["path"] == "Oscillator0/plainParams/kParamVolume"
    assert schema.fl_anchor("filter 1 freq") is not None
    assert schema.parameters_in("Env")


def test_fx_schema_types_and_reverb() -> None:
    fx = schema.fx_schema()
    assert fx["type_map"]["6"]["section"] == "FXReverb"
    assert schema.fx_type_for("FXReverb") == 6
    reverb = schema.fx_effect("Reverb")
    assert reverb is not None
    keys = {row["key"] for row in reverb["parameters"]}
    assert {"kParamWet", "kParamSize", "kParamType", "kParamPreDelay"} <= keys
    reverb_type = next(row for row in reverb["parameters"] if row["key"] == "kParamType")
    assert "kHall" in reverb_type["values"]
    assert len(fx["effects"]) == 16
    with pytest.raises(KeyError):
        schema.fx_type_for("FXNope")


def test_filter_types_include_verified_default() -> None:
    rows = {row["state_value"]: row for row in schema.filter_types()}
    assert rows["MgL12"]["confidence"] == "verified-default"
    assert rows["MgL24"]["fl_display_guess"] == "MG Low 24"
    assert rows["H12"]["family"] == "highpass"


def test_wavetable_frames_and_position_rule() -> None:
    default = schema.wavetable("/S2 Tables/Default Shapes.wav")
    assert default is not None and default["num_frames"] == 9
    assert schema.find_frames("sine") == [2]
    assert schema.find_frames("saw") == [1]
    assert 4 in schema.find_frames("square")
    assert schema.find_frames("sine", "Analog/Basic Shapes.wav") == [1]
    assert schema.find_frames("sine", "Missing.wav") == []
    assert schema.frame_for_table_pos(88.29, 166) == 57
    assert schema.frame_for_table_pos(schema.table_pos_for_frame(2, 9), 9) == 2
    assert schema.frame_for_table_pos(256.0, 9) == 9
    with pytest.raises(ValueError):
        schema.table_pos_for_frame(0, 9)
    with pytest.raises(KeyError):
        schema.load_data("nope.json")
