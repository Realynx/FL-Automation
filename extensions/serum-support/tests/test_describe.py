"""describe_* summaries and signal-path notes on synthetic states (no proprietary presets)."""

from __future__ import annotations

import base64
import struct
from pathlib import Path
from typing import Any

import pytest

from fruitylink_serum import cbor, describe, loading, vstpreset, xfer
from fruitylink_serum.builder import SerumPatch

ZSTD = xfer.zstd_available()
needs_zstd = pytest.mark.skipif(not ZSTD, reason="zstd support requires Python 3.14 or the zstandard package")


def smart_future_like() -> dict[str, Any]:
    """Shape of the factory preset that silenced Filter 1 automation live: every direct level 0, sub audible."""
    return {
        "Oscillator0": {"plainParams": {"kParamUnison": 4.0, "kParamVolume": 0.0, "kParamDetune": cbor.Float32(0.1)},
                        "WTOsc0": {"relativePathToWT": "/S2 Tables/Default Shapes.wav", "numFrames": 9 * 2048,
                                   "plainParams": {"kParamTablePos": cbor.Float32(113.778)}}},
        "Oscillator1": {"plainParams": {"kParamEnable": 1.0, "kParamVolume": 0.0, "kParamUnison": 5.0},
                        "WTOsc1": {"relativePathToWT": "Custom/Mine.wav", "tableDisplayName": "Mine", "numFrames": 4 * 2048,
                                   "embeddedWTData": [0.0, 0.1], "plainParams": {}}},
        "Oscillator3": {"plainParams": {"kParamEnable": 1.0, "kParamVolume": 0.0}},
        "Oscillator4": {"plainParams": {"kParamEnable": 1.0, "kParamVolume": 0.75, "kParamOctave": -1.0},
                        "SubOsc4": {"plainParams": {"kParamShape": "kSaw"}}},
        "RoutingSlot1": {"plainParams": {"kParamRoutingDest": "kRoutingDestFilter"}},
        "VoiceFilter0": {"plainParams": {"kParamEnable": 1.0, "kParamFreq": cbor.Float32(0.6), "kParamReso": 10.0, "kParamWet": 0.0}},
        "Env0": {"plainParams": {"kParamAttack": 0.015, "kParamDecay": 2.46, "kParamSustain": 0.72}},
        "LFO0": {"plainParams": {"kParamRate": 2.0, "kParamBeatSync": 1.0}, "curveData": {"n": 1}},
        "FXRack0": {"FX": [
            {"type": 6, "FXReverb": {"plainParams": {"kParamWet": 30.0, "kParamType": "kHall"}}, "kUIParamMixOrGain": 0.0},
            {"type": 4, "FXDelay": {"plainParams": {"kParamEnable": 0.0}}, "kUIParamMixOrGain": 0.0},
        ]},
        "ModSlot0": {"source": [25, 0], "destModuleTypeString": "Oscillator", "destModuleID": 0,
                     "destModuleParamName": "kParamVolume", "destModuleParamID": 1, "plainParams": {"kParamAmount": 100.0}},
        "ModSlot1": {"plainParams": "default"},
        "Macro0": {"name": "FILTER", "plainParams": {"kParamValue": 40.0}},
        "Global0": {"plainParams": {"kParamBendRangeDn": -12.0, "kParamBendRangeUp": 12.0, "kParamMasterVolume": 0.25}},
        "presetName": "Smart-ish Future",
    }


def test_describe_reports_wavetable_identity_frame_and_levels() -> None:
    description = describe.describe_container(smart_future_like())
    a, b, c = description["oscillators"]
    assert a["enabled"] and a["enabled_from"] == "default"            # oscillator A: absent kParamEnable = on
    assert a["wavetable"]["path"].endswith("Default Shapes.wav") and a["wavetable"]["known_table"]
    assert a["wavetable"]["num_frames"] == 9 and a["wavetable"]["frame"] == 4 and a["wavetable"]["frame_label"] == "square"
    assert a["level"] == {"gain": 0.0, "knob_percent": 0.0, "db": "-inf"} and a["unison"] == 4
    assert b["wavetable"]["display_name"] == "Mine" and b["wavetable"]["embedded"] and not b["wavetable"]["known_table"]
    assert b["wavetable"]["num_frames"] == 4 and b["wavetable"]["frame"] == 1
    assert c["enabled"] is False and c["level"] == "default" and c["wavetable"] is None
    assert description["sub"]["shape"] == "saw" and description["sub"]["octave"] == -1.0
    assert description["sub"]["level"]["knob_percent"] == pytest.approx(86.6, abs=0.1)
    f1, f2 = description["filters"]
    assert f1["enabled"] and f1["type_display"] == "MG Low 12" and f1["cutoff_hz"] == 937 and f1["wet_percent"] == 0.0
    assert not f2["enabled"] and f2["wet_percent"] == "100 (default)"
    env = description["envelopes"][0]
    assert env["attack_ms"] == 15.0 and env["decay_ms"] == 2460.0 and env["sustain_db"] == -5.7
    assert description["lfos"][0]["drawn_shape"] and description["lfos"][0]["beat_sync"] is True
    chain = description["fx"][0]["units"]
    assert [u["label"] for u in chain] == ["Reverb", "Delay"] and chain[0]["mix_percent"] == 30.0 and chain[1]["enabled"] is False
    slot = description["mod_slots"][0]
    assert slot["source"]["name"] == "Macro 1" and slot["destination"] == "Oscillator0/kParamVolume" and slot["amount"] == 100.0
    assert description["macros"] == [{"index": 0, "name": "FILTER", "value": 40.0}]
    assert description["global"]["bend_down_semitones"] == -12.0 and description["global"]["master"]["fl_display_db"] == -9.0
    assert description["name"] == "Smart-ish Future"


def test_signal_path_notes_flag_the_live_incidents() -> None:
    notes = describe.signal_path_notes(smart_future_like())
    assert any("direct levels" in n and "Filter 1 Freq" in n for n in notes)
    assert any("VoiceFilter0 wet is 0 %" in n for n in notes)
    assert any("Velocity" in n for n in notes)
    assert not any("routes to" in n for n in notes)
    state = smart_future_like()
    state["RoutingSlot0"] = {"plainParams": {"kParamRoutingDest": "kRoutingDestDirect", "kParamFXBus1Level": 100.0}}
    state["Global0"]["plainParams"]["kParamFXBus1Vol"] = 0.5
    notes = describe.signal_path_notes(state)
    assert any("oscillator A routes to kRoutingDestDirect" in n for n in notes)
    assert any("sends 100 % to FX bus 1" in n for n in notes)
    assert any("bus routing is active" in n for n in notes)
    # a healthy builder patch with velocity: only the advisory-free path
    healthy = SerumPatch(name="ok").osc_a("saw", level=0.7).filter("lp12", cutoff_hz=1000).velocity(0.8).to_state()
    assert describe.signal_path_notes(healthy) == []
    init_notes = describe.signal_path_notes({})
    assert any("no voice filter is enabled" in n for n in init_notes)


def test_format_description_is_readable_text() -> None:
    text = describe.format_description(describe.describe_container(smart_future_like()))
    assert text.splitlines()[0] == "Serum 2 state: Smart-ish Future"
    assert "Osc A: on, kOsc_WT (default), /S2 Tables/Default Shapes.wav frame 4/9 (square), level 0.0% [-inf dB], unison 4" in text
    assert "Osc B: on, kOsc_WT (default), Mine frame 1/4" in text
    assert "Sub: on, saw, octave -1.0, level 86.6% [-2.5 dB]" in text
    assert "FXRack0 (main chain): Reverb mix 30.0 -> Delay (bypassed) mix 100 (default)" in text
    assert "Mod 0: Macro 1 -> Oscillator0/kParamVolume 100.0 %" in text
    assert "note: " in text


@needs_zstd
def test_describe_preset_and_state_decode_files_and_the_host(tmp_path: Path) -> None:
    container = xfer.XferContainer(metadata={"fileType": "SerumPreset", "presetName": "Synth"}, state=smart_future_like())
    preset = tmp_path / "Synth.SerumPreset"
    preset.write_bytes(xfer.write_container(container))
    described = describe.describe_preset(preset)
    assert described["path"] == str(preset.resolve()) and described["oscillators"][0]["wavetable"]["frame_label"] == "square"
    vst = loading.to_vstpreset(preset, tmp_path / "Synth.vstpreset")
    assert vstpreset.read_vstpreset(vst.read_bytes()).class_id == vstpreset.SERUM2_CLASS_ID
    assert describe.describe_preset(vst)["sub"]["shape"] == "saw"

    processor = xfer.XferContainer(metadata={"component": "processor"}, state={**smart_future_like(), "component": "processor"})
    block = xfer.write_container(processor)
    blob = b"\x00" * 16 + struct.pack("<III", 3, len(block), 0) + block

    class Ops:
        def get_channel_plugin_state(self, *, channel: int) -> str:
            return base64.b64encode(blob).decode("ascii")

        def get_mixer_effect_state(self, *, track: int, slot: int) -> str:
            return base64.b64encode(blob).decode("ascii")

    class Studio:
        ops = Ops()

    live = describe.describe_state(Studio(), 4)
    assert live["channel"] == 4 and live["product"]["component"] == "processor"
    assert describe.describe_state(Studio(), 2, slot=1)["slot"] == 1


def factory_default_sub() -> dict[str, Any]:
    """Exact shape of "KY - Smart Future" / "PD - Airy Chant" (live 2026-09-14): SubOsc4/plainParams is the string
    "default", every direct level is 0 and the sub carries the sound."""
    return {
        "Oscillator0": {"plainParams": {"kParamVolume": 0.0, "kParamUnison": 4.0}},
        "Oscillator1": {"plainParams": {"kParamEnable": 1.0, "kParamVolume": 0.0}},
        "Oscillator4": {"SubOsc4": {"plainParams": "default"}, "plainParams": {"kParamVolume": 0.75, "kParamEnable": 1.0}},
        "VoiceFilter0": {"plainParams": {"kParamEnable": 1.0, "kParamFreq": cbor.Float32(0.5)}},
        "FXRack0": {"FX": [{"type": 6, "FXReverb": "default", "kUIParamMixOrGain": 0.0}, "default"]},
        "LFO0": "default",
        "Macro0": {"name": "FILTER", "plainParams": "default"},
        "presetName": "Smart Future-like",
    }


def test_signal_path_notes_survive_string_valued_sections() -> None:
    notes = describe.signal_path_notes(factory_default_sub())      # raised AttributeError at describe.py:130 before
    assert any(n.startswith("all enabled oscillator direct levels") and "the sub oscillator" in n
               and "Filter 1 Freq" in n for n in notes)
    description = describe.describe_container(factory_default_sub())
    assert description["sub"]["shape"] == "sine (default)" and description["sub"]["enabled"]
    assert description["sub"]["level"]["knob_percent"] == pytest.approx(86.6, abs=0.1)
    units = description["fx"][0]["units"]
    assert [u["label"] for u in units] == ["Reverb"] and units[0]["enabled"] and units[0]["params"] == {}
    assert description["lfos"] == [] and description["macros"] == [{"index": 0, "name": "FILTER", "value": None}]
    assert "note: all enabled oscillator direct levels" in describe.format_description(description)


@needs_zstd
def test_load_preset_reports_the_direct_level_note_for_a_factory_default_sub(tmp_path: Path) -> None:
    from test_loading_safety import Studio as SafetyStudio

    root = tmp_path / "root"
    folder = root / "Presets" / "Factory"
    folder.mkdir(parents=True)
    preset = folder / "Smart.SerumPreset"
    preset.write_bytes(xfer.write_container(xfer.XferContainer(
        metadata={"fileType": "SerumPreset", "presetName": "Smart"}, state=factory_default_sub())))
    result = loading.load_preset(SafetyStudio(), 1, "Smart", root=root)
    assert not any(w.startswith("signal-path check skipped") for w in result.warnings)
    assert any(w.startswith("preset: all enabled oscillator direct levels") for w in result.warnings)
    assert describe.describe_preset(preset)["sub"]["shape"] == "sine (default)"
