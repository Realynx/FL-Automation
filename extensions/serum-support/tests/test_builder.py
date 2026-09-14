"""SerumPatch builder tests: packaged schema data plus synthetic schema files."""

from __future__ import annotations

import json
import math
from pathlib import Path
from typing import Any

import pytest

from fruitylink_serum import builder, cbor, loading, vstpreset, xfer
from fruitylink_serum.builder import PatchError, Schema, SerumPatch

ZSTD = xfer.zstd_available()
needs_zstd = pytest.mark.skipif(not ZSTD, reason="zstd support requires Python 3.14 or the zstandard package")


def test_unit_conversions_match_live_readbacks() -> None:
    assert round(builder.hz_to_normalized(226), 3) == 0.42          # chords filter: 0.42 read back as 226 Hz
    assert builder.normalized_to_hz(0.6) == pytest.approx(937, abs=1)  # lead filter: 0.6 read back as 937 Hz
    assert builder.hz_to_normalized(937) == pytest.approx(0.6, abs=0.001)
    assert builder.knob_to_gain(0.45) == pytest.approx(0.2025)       # Sub Level 45% [-13.9 dB]
    assert 20 * math.log10(builder.knob_to_gain(0.45)) == pytest.approx(-13.9, abs=0.05)
    assert 40 * math.log10(0.72) == pytest.approx(-5.7, abs=0.05)     # Env1 sustain 0.72 -> -5.7 dB
    assert builder.sustain_db_to_knob(-5.7) == pytest.approx(0.72, abs=0.005)
    assert builder.master_db_to_gain(-7.4) == pytest.approx(0.3025, abs=0.003)  # Main Vol 55% [-7.4 dB]
    with pytest.raises(PatchError):
        builder.hz_to_normalized(0)
    with pytest.raises(PatchError):
        builder.knob_to_gain(1.5)


def test_packaged_schema_data_is_loaded() -> None:
    schema = builder.default_schema()
    assert all(schema.sources[name] == "loaded" for name in builder._DATA_FILES)
    assert schema.parameters["Oscillator0.unison"].verified
    assert schema.parameters["Oscillator0.width"].key == "Oscillator0/plainParams/kParamUnisonStereo"
    assert schema.wavetable(builder.DEFAULT_TABLE) is not None
    assert schema.filter_type("lp24") == "MgL24" and schema.filter_type("MG Low 24") == "MgL24"
    assert schema.filter_type("hp12") == "H12" and schema.filter_type("lp12") == "MgL12"
    assert schema.effect("reverb").type == 6 and schema.effect("FXDelay").label == "Delay"


def test_init_base_is_processor_identity_plus_scalars_only() -> None:
    patch = SerumPatch(name="Init test")
    state = patch.to_state()
    assert not any(key.startswith(("Oscillator", "Env", "VoiceFilter")) for key in state)
    assert state["mpePitchBendRange"] == 48 and state["presetName"] == "Init test"
    assert patch.container().metadata["presetName"] == "Init test"


def test_square_lead_recipe_writes_verified_keys() -> None:
    patch = (SerumPatch(name="Square lead")
             .sub("square", octave=0, level=0.62)
             .osc_a(level=0.55, unison=5, detune=0.05, blend=75)
             .filter("lp12", cutoff_hz=937, resonance=17, drive=16)
             .amp_env(attack_ms=2.4, decay_ms=590, sustain_db=-5.7, release_ms=193)
             .mono(True, porta_ms=0.2, porta_always=True)
             .master_volume(knob=0.5))
    state = patch.to_state()
    osc = state["Oscillator0"]["plainParams"]
    assert osc["kParamUnison"] == 5.0 and isinstance(osc["kParamUnison"], cbor.Float32)
    assert osc["kParamVolume"] == pytest.approx(0.3025) and osc["kParamDetune"] == pytest.approx(0.05)
    assert osc["kParamDetuneWid"] == 75.0
    assert state["Oscillator4"]["SubOsc4"]["plainParams"]["kParamShape"] == "kSquare"
    assert state["Oscillator4"]["plainParams"]["kParamVolume"] == pytest.approx(0.3844, abs=1e-4)
    flt = state["VoiceFilter0"]["plainParams"]
    assert flt["kParamFreq"] == pytest.approx(0.6, abs=0.002) and flt["kParamReso"] == 17.0 and flt["kParamDrive"] == 16.0
    assert "kParamType" not in flt  # lp12 (MgL12) is Serum's default: left unset
    env = state["Env0"]["plainParams"]
    assert env["kParamAttack"] == pytest.approx(0.0024) and env["kParamDecay"] == pytest.approx(0.59)
    assert env["kParamSustain"] == pytest.approx(0.72, abs=0.005) and env["kParamRelease"] == pytest.approx(0.193)
    glob = state["Global0"]["plainParams"]
    assert glob["kParamMonoToggle"] == 1.0 and glob["kParamPortaAlways"] == 1.0
    assert glob["kParamPortamentoTime"] == pytest.approx(0.0002) and glob["kParamMasterVolume"] == 0.25
    assert "Oscillator0/plainParams/kParamUnison" in patch.changes()


def test_shape_filter_effects_and_modulation_through_the_packaged_schema() -> None:
    patch = (SerumPatch(name="Square + reverb")
             .osc_a("square", unison=3, width=80, semi=12, fine=-5)
             .osc_b("saw", level=0.4, octave=-1)
             .sub("sine")
             .filter("lp24", cutoff_hz=2000)
             .filter("hp12", cutoff_hz=120, index=1)
             .macro(0, 35)
             .lfo(0, rate=2.0, beat_sync=False)
             .fx.reverb(mix=25, type="hall", size=60, predelay_ms=20, width=100)
             .fx.delay(mix=15, time_ms=250, feedback=35, mode=2)
             .fx.filter(cutoff_hz=800, resonance=20)
             .fx.compressor(attack_ms=10, release_ms=120, rack=1))
    state = patch.to_state()
    osc_a = state["Oscillator0"]
    assert osc_a["WTOsc0"]["relativePathToWT"] == "S2 Tables/Default Shapes.wav"
    assert osc_a["WTOsc0"]["plainParams"]["kParamTablePos"] == pytest.approx(256 * 4 / 9)  # frame 4 = square
    assert osc_a["plainParams"]["kParamUnisonStereo"] == 80.0
    assert osc_a["plainParams"]["kParamPitch"] == 12.0 and osc_a["plainParams"]["kParamFine"] == -5.0
    osc_b = state["Oscillator1"]
    assert osc_b["WTOsc1"]["plainParams"]["kParamTablePos"] == pytest.approx(256 * 1 / 9)  # frame 1 = saw
    assert osc_b["plainParams"]["kParamOctave"] == -1.0 and osc_b["plainParams"]["kParamVolume"] == pytest.approx(0.16)
    assert "SubOsc4" not in state["Oscillator4"]  # sine is the sub default: left unset
    assert state["VoiceFilter0"]["plainParams"]["kParamType"] == "MgL24"
    assert state["VoiceFilter1"]["plainParams"]["kParamType"] == "H12"
    assert state["Macro0"]["plainParams"]["kParamValue"] == 35.0
    assert state["LFO0"]["plainParams"]["kParamRate"] == 2.0 and state["LFO0"]["plainParams"]["kParamBeatSync"] == 0.0
    units = state["FXRack0"]["FX"]
    assert [u["type"] for u in units] == [6, 4, 8]
    reverb = units[0]["FXReverb"]["plainParams"]
    assert reverb["kParamWet"] == 25.0 and reverb["kParamType"] == "kHall" and reverb["kParamSize"] == 60.0
    assert reverb["kParamPreDelay"] == pytest.approx(0.02) and reverb["kParamWidth"] == 100.0
    delay = units[1]["FXDelay"]["plainParams"]
    assert delay["kParamTimeL"] == pytest.approx(0.25) and delay["kParamTimeR"] == pytest.approx(0.25)
    assert delay["kParamFeedback"] == 35.0 and delay["kParamMode"] == 2.0
    fxfilter = units[2]["FXFilter"]["plainParams"]
    assert fxfilter["kParamFreq"] == pytest.approx(builder.hz_to_normalized(800)) and fxfilter["kParamReso"] == 20.0
    comp = state["FXRack1"]["FX"][0]
    assert comp["type"] == 5 and comp["FXComp"]["plainParams"]["kParamAttack"] == 10.0  # compressor times are ms
    assert units[0]["kUIParamMixOrGain"] == 0.0
    assert patch.changes()["FXRack0/FX[0]"]["effect"] == "Reverb"


def test_unknown_names_raise_with_choices() -> None:
    with pytest.raises(PatchError, match="sine, square"):
        SerumPatch().sub("sawtooth")
    with pytest.raises(PatchError, match="lp12"):
        SerumPatch().filter("wobble")
    with pytest.raises(PatchError, match="outside"):
        SerumPatch().osc_a(unison=40)
    with pytest.raises(PatchError, match="not both"):
        SerumPatch().osc_a(level=0.5, level_db=-6)
    with pytest.raises(PatchError, match="labels"):
        SerumPatch().osc_a("organ")
    with pytest.raises(PatchError, match="not decoded"):
        SerumPatch().lfo(0, rate="1/4")  # type: ignore[arg-type]
    with pytest.raises(PatchError, match="Give knob or db"):
        SerumPatch().master_volume()
    with pytest.raises(PatchError, match="Unknown effect"):
        SerumPatch().fx.spaceship(mix=1)
    with pytest.raises(PatchError, match="kHall"):
        SerumPatch().fx.reverb(type="cathedral")
    with pytest.raises(PatchError, match="Unknown effect argument"):
        SerumPatch().fx.reverb(sparkle=3)
    with pytest.raises(PatchError, match="no parameter"):
        SerumPatch().fx.reverb(stages=3)


def test_shape_and_fx_need_schema_data_and_say_so() -> None:
    patch = SerumPatch(schema=Schema())
    with pytest.raises(NotImplementedError, match="wavetables.json"):
        patch.osc_a("square")
    with pytest.raises(NotImplementedError, match="fx-schema.json"):
        patch.fx.reverb(mix=20)
    with pytest.raises(PatchError, match="lp12"):
        patch.filter("lp24")
    # explicit table/frame always works without the frame map: frame is then the raw table position
    patch.osc_a(table="S2 Tables/Default Shapes.wav", frame=3).filter("lp12")
    osc0 = patch.to_state()["Oscillator0"]
    assert osc0["WTOsc0"]["relativePathToWT"] == "S2 Tables/Default Shapes.wav"
    assert osc0["WTOsc0"]["plainParams"]["kParamTablePos"] == 3.0


def _schema_dir(tmp_path: Path) -> Path:
    (tmp_path / "parameter-map.json").write_text(json.dumps({
        "parameters": [
            {"path": "Oscillator{n}/plainParams/kParamUnison", "section": "Oscillator{n}", "key": "kParamUnison",
             "type": "int", "min": 2.0, "max": 16.0, "scale": "count", "confidence": "verified"},
            {"path": "Oscillator{n}/plainParams/kParamUnisonStereo", "section": "Oscillator{n}",
             "key": "kParamUnisonStereo", "type": "float", "min": -100.0, "max": 100.0, "scale": "percent",
             "confidence": "inferred"},
            {"path": "LFO{n}/plainParams/kParamRate", "section": "LFO{n}", "key": "kParamRate", "type": "float",
             "min": 0.0, "max": 100.0, "scale": "lfo_rate", "confidence": "inferred"},
        ],
    }), encoding="utf-8")
    (tmp_path / "wavetables.json").write_text(json.dumps({
        "tables": [{"relative_path": "Test/Shapes.wav", "num_frames": 4,
                    "frames_by_label": {"sine": [1], "triangle": [2], "saw": [3], "square": [4]}}],
    }), encoding="utf-8")
    (tmp_path / "fx-schema.json").write_text(json.dumps({
        "effects": {"FXReverb": {"type": 6, "label": "Reverb", "parameters": [
            {"key": "kParamWet", "type": "float", "min": 0.0, "max": 100.0},
            {"key": "kParamPreDelay", "type": "float", "min": 0.0, "max": 2.5},
            {"key": "kParamType", "type": "enum", "values": ["kHall", "kSpace"]}]}},
    }), encoding="utf-8")
    (tmp_path / "filters.json").write_text(json.dumps({
        "types": [{"state_value": "MgL12", "fl_display_guess": "MG Low 12"},
                  {"state_value": "L24", "fl_display_guess": "Low 24"}],
    }), encoding="utf-8")
    return tmp_path


def test_schema_files_drive_the_builder(tmp_path: Path) -> None:
    schema = builder.load_schema(_schema_dir(tmp_path))
    assert schema.sources["fx-schema.json"] == "loaded" and "Oscillator0.unison" in schema.parameters
    assert schema.parameters["Oscillator1.width"].notes == "inferred"
    assert "not observed" in schema.parameters["Oscillator1.semi"].notes
    patch = (SerumPatch(schema=schema).osc_a("square", table="Test/Shapes.wav", width=60).lfo(0, rate=8.0)
             .filter("Low 24", cutoff_hz=1000).fx.reverb(mix=25, predelay_ms=20, type="space"))
    state = patch.to_state()
    assert state["Oscillator0"]["WTOsc0"]["plainParams"]["kParamTablePos"] == 256.0  # frame 4 of 4
    assert state["Oscillator0"]["plainParams"]["kParamUnisonStereo"] == 60.0
    assert state["LFO0"]["plainParams"]["kParamRate"] == 8.0
    assert state["VoiceFilter0"]["plainParams"]["kParamType"] == "L24"
    unit = state["FXRack0"]["FX"][0]
    assert unit["type"] == 6 and unit["FXReverb"]["plainParams"]["kParamWet"] == 25.0
    assert unit["FXReverb"]["plainParams"]["kParamPreDelay"] == pytest.approx(0.02)
    assert unit["FXReverb"]["plainParams"]["kParamType"] == "kSpace"
    with pytest.raises(PatchError, match="no parameter"):
        patch.fx.reverb(size=1)
    with pytest.raises(PatchError, match="Unknown effect"):
        patch.fx.phaser(mix=1)
    with pytest.raises(PatchError, match="lp24"):
        patch.filter("lp24")  # alias only offered when filters.json lists MgL24
    empty = builder.load_schema(tmp_path / "nowhere")
    assert empty.sources["parameter-map.json"].startswith("missing")


@needs_zstd
def test_outputs_round_trip_and_load_through_the_host(tmp_path: Path) -> None:
    patch = SerumPatch(name="Out test").sub("saw", octave=-1, level=0.45).osc_a(unison=7, detune=0.1)
    saved = patch.save(tmp_path / "Out test.SerumPreset")
    reread = loading.read_preset(saved)
    assert reread.metadata["presetName"] == "Out test"
    assert reread.state["Oscillator4"]["SubOsc4"]["plainParams"]["kParamShape"] == "kSaw"
    vst = patch.to_vstpreset(tmp_path / "Out test.vstpreset")
    preset = vstpreset.read_vstpreset(vst.read_bytes())
    assert preset.class_id == vstpreset.SERUM2_CLASS_ID
    component = xfer.read_container(preset.component)
    assert component.state["component"] == "processor" and "presetName" not in component.state
    assert component.state["Oscillator0"]["plainParams"]["kParamUnison"] == 7.0

    class Ops:
        calls: list[dict[str, Any]] = []

        def load_channel_plugin_state(self, *, channel: int, path: str, use_channel_loader: bool = False) -> str:
            self.calls.append({"channel": channel, "path": path})
            return "ok"

        def load_mixer_effect_state(self, *, track: int, slot: int, path: str) -> str:
            return "ok"

    class Studio:
        ops = Ops()

    assert patch.load(Studio(), 4) == "ok"
    assert Path(Studio.ops.calls[-1]["path"]).suffix == ".vstpreset"

    # deriving from an existing container keeps its content and the base untouched
    base = patch.container()
    derived = SerumPatch(name="Derived", base=base).osc_a(unison=3)
    assert derived.to_state()["Oscillator0"]["plainParams"]["kParamUnison"] == 3.0
    assert base.state["Oscillator0"]["plainParams"]["kParamUnison"] == 7.0


def test_to_vstpreset_refuses_a_foreign_class_id(tmp_path: Path) -> None:
    container = SerumPatch().container()
    with pytest.raises(ValueError, match="ignored silently"):
        loading.to_vstpreset(container, tmp_path / "x.vstpreset", class_id="58455356736673506572756D20320000")
    if ZSTD:
        assert loading.to_vstpreset(container, tmp_path / "y.vstpreset", class_id="00" * 16, force_class_id=True).exists()


def test_describe_parameters_groups_by_section_with_musical_values() -> None:
    patch = (SerumPatch().filter(cutoff_hz=800, resonance=12).amp_env(attack_ms=10, sustain=0.5)
             .sub("triangle", level=0.5).set("Oscillator0/plainParams/kParamPan", 25.0))
    described = builder.describe_parameters(patch.container())
    flt = {entry["key"]: entry for entry in described["VoiceFilter0"]}
    assert flt["VoiceFilter0/plainParams/kParamFreq"]["musical"] == "800 Hz"
    assert flt["VoiceFilter0/plainParams/kParamReso"]["musical"] == "12 %"
    env = {entry["key"]: entry for entry in described["Env0"]}
    assert env["Env0/plainParams/kParamAttack"]["musical"] == "10.0 ms"
    assert env["Env0/plainParams/kParamSustain"]["musical"] == "-12.0 dB"
    sub = {entry["key"]: entry for entry in described["Oscillator4"]}
    assert sub["Oscillator4/SubOsc4/plainParams/kParamShape"]["value"] == "kTriangle"
    assert sub["Oscillator4/plainParams/kParamVolume"]["musical"] == "-12.0 dB"
    pan = {entry["key"]: entry for entry in described["Oscillator0"]}["Oscillator0/plainParams/kParamPan"]
    assert pan["name"] == "Oscillator0.pan" and pan["musical"] == "25 %"
