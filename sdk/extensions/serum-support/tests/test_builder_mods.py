"""bend_range / velocity / modulate, the mod-matrix schema, and the master-dB regression points."""

from __future__ import annotations

import json
import math
from pathlib import Path

import pytest

from fruitylink_serum import builder, cbor, schema
from fruitylink_serum.builder import PatchError, SerumPatch


def test_master_volume_display_points_from_ember_tides_v008() -> None:
    """Display dB = 20*log10(gain) + 3 and FL percent = sqrt(gain): the builder's formula is correct."""
    for gain, display_db in ((0.25, -9.0), (0.3548, -6.0), (0.5, -3.0)):
        assert 20 * math.log10(gain) + 3 == pytest.approx(display_db, abs=0.05)   # displays are rounded to 0.1 dB
        assert builder.master_db_to_gain(display_db) == pytest.approx(gain, abs=0.002)   # FL rounds the display to 0.1 dB
        assert SerumPatch(name="m").master_volume(db=display_db).get("Global0/plainParams/kParamMasterVolume") == pytest.approx(gain, abs=0.002)
    assert math.sqrt(0.25) == 0.5 and math.sqrt(0.5) == pytest.approx(0.7071, abs=1e-4)   # FL percent = sqrt(gain)


def test_bend_range_writes_both_globals_with_serums_sign_convention() -> None:
    state = SerumPatch(name="bend").bend_range(2, 12).to_state()
    assert state["Global0"]["plainParams"]["kParamBendRangeUp"] == 2.0
    assert state["Global0"]["plainParams"]["kParamBendRangeDn"] == -12.0     # KY - Smart Future stores -12.0
    assert SerumPatch(name="b").bend_range(down_semitones=-24).get("Global0/plainParams/kParamBendRangeDn") == -24.0
    only_up = SerumPatch(name="b").bend_range(up_semitones=7).to_state()["Global0"]["plainParams"]
    assert only_up == {"kParamBendRangeUp": 7.0}
    with pytest.raises(PatchError):
        SerumPatch(name="b").bend_range()
    with pytest.raises(PatchError):
        SerumPatch(name="b").bend_range(up_semitones=60)


def test_velocity_writes_a_factory_shaped_mod_slot() -> None:
    patch = SerumPatch(name="vel").osc_a("saw").velocity(0.75)
    slot = patch.to_state()["ModSlot0"]
    assert slot == {"source": [16, 0], "destModuleTypeString": "Global", "destModuleID": 0,
                    "destModuleParamName": "kParamVoiceAmp", "destModuleParamID": 2, "plainParams": {"kParamAmount": 75.0}}
    assert isinstance(slot["plainParams"]["kParamAmount"], cbor.Float32) and isinstance(slot["source"][0], int)
    change = patch.changes()["ModSlot0"]
    assert change["source"] == "Velocity" and change["confidence"] == "inferred" and change["target"] == "Global0/plainParams/kParamVoiceAmp"
    # further rows take the next free slot; explicit slots and other targets work
    patch.velocity(0.5, target="filter").velocity(1.0, target="osc_a", bipolar=True, slot=10)
    state = patch.to_state()
    assert state["ModSlot1"]["destModuleTypeString"] == "VoiceFilter" and state["ModSlot1"]["destModuleParamID"] == 3
    assert state["ModSlot10"]["destModuleID"] == 0 and state["ModSlot10"]["destModuleParamID"] == 1
    assert state["ModSlot10"]["plainParams"]["kParamBipolar"] == 1.0
    assert "ModSlot2" not in state
    with pytest.raises(PatchError):
        patch.velocity(1.5)
    with pytest.raises(PatchError):
        patch.velocity(1.0, slot=64)


def test_modulate_resolves_sources_targets_and_aux() -> None:
    patch = SerumPatch(name="mod").modulate("lfo 2", "Oscillator1.fine", -30, aux="mod wheel")
    row = patch.to_state()["ModSlot0"]
    assert row["source"] == [7, 18] and row["destModuleTypeString"] == "Oscillator" and row["destModuleID"] == 1
    assert row["destModuleParamName"] == "kParamFine" and row["destModuleParamID"] == 5
    patch.modulate("Macro 1", "Oscillator0/WTOsc0/plainParams/kParamTablePos", 100)
    wt = patch.to_state()["ModSlot1"]
    assert wt["destModuleTypeString"] == "WTOsc" and wt["destModuleID"] == 0 and wt["destModuleParamID"] == 6
    patch.modulate(17, "VoiceFilter1.cutoff", 50)                       # raw id, second filter
    assert patch.to_state()["ModSlot2"]["source"] == [17, 0] and patch.to_state()["ModSlot2"]["destModuleID"] == 1
    assert patch.modulate(99, "Env1.attack", 5).changes()["ModSlot3"]["source"] == "source 99"
    with pytest.raises(PatchError, match="Known:"):
        patch.modulate("theremin", "Oscillator0.volume", 10)
    with pytest.raises(PatchError):
        patch.modulate("velocity", "Oscillator0.volume", 150)
    with pytest.raises(PatchError):
        patch.modulate("velocity", "FXRack0/FX/plainParams/kParamWet", 10)
    with pytest.raises(PatchError, match="no destination id"):
        patch.modulate("velocity", "Global0/plainParams/kParamNotAThing", 10)
    with pytest.raises(PatchError):
        patch.modulate(True, "Oscillator0.volume", 10)


def test_mod_matrix_data_and_schema_access() -> None:
    data = schema.load_data("mod-matrix.json")
    assert data["provenance"]["files_scanned"] > 800 and "sources" in data and "destinations" in data
    by_id = {row["id"]: row for row in data["sources"]}
    assert by_id[16]["name"] == "Velocity" and by_id[16]["confidence"] == "inferred"
    assert by_id[25]["name"] == "Macro 1" and by_id[6]["name"] == "LFO 1"
    assert all("confidence" in row for row in data["sources"]) and all("confidence" in row for row in data["destinations"])
    dest = {(row["type"], row["param"]): row for row in data["destinations"]}
    assert dest[("Global", "kParamVoiceAmp")]["param_id"] == 2 and dest[("Global", "kParamVoiceAmp")]["confidence"] == "binary-enum"
    assert dest[("Oscillator", "kParamVolume")]["param_id"] == 1 and dest[("VoiceFilter", "kParamFreq")]["param_id"] == 3
    assert not any(row["confidence"] == "conflict" for row in data["destinations"])
    loaded = builder.default_schema()
    assert loaded.mod_source("vel").id == 16 and loaded.mod_source("Macro 8").id == 32 and loaded.mod_source("note").id == 17
    assert loaded.mod_destination("WTOsc", "kParamTablePos") == 6
    assert loaded.mod_source(2).confidence == "unknown"                  # unresolved envelope-group id


def test_without_mod_matrix_data_the_builder_says_so(tmp_path: Path) -> None:
    (tmp_path / "parameter-map.json").write_text(json.dumps({"parameters": []}), encoding="utf-8")
    empty = builder.load_schema(tmp_path)
    assert empty.sources["mod-matrix.json"].startswith("missing")
    patch = SerumPatch(name="x", schema=empty)
    with pytest.raises(NotImplementedError, match="mod-matrix.json"):
        patch.velocity()
    with pytest.raises(PatchError, match="mod-matrix.json"):
        patch.modulate(16, "Global0.voice_amp", 100)
