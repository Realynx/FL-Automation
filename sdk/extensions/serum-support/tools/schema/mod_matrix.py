"""Generate ``src/fruitylink_serum/data/mod-matrix.json`` (derived statistics only).

Reads every ``.SerumPreset`` under the local library (``SERUM_PRESETS_PATH`` or
``~/Documents/Xfer/Serum 2 Presets``) with a zstd-capable interpreter and records how the
``ModSlot{n}`` sections are laid out: which ``source`` ids are used, which
``(destModuleTypeString, destModuleParamName)`` pairs map to which ``destModuleParamID``,
and how often. No amounts, curves or preset payloads are stored.

Source-id names are NOT in the files (Serum stores numeric ids). They are inferred from
usage patterns and from the order of the UI enum found as a string in ``Serum2.vst3``
(``kUIParamEnv1Drag..Env4Drag, kUIParamLFO1Drag..LFO10Drag, kUIParamVeloCurveDrag,
kUIParamNoteCurveDrag, kUIParamMacro1Drag..Macro8Drag, kUIParamWheelDrag``); every row
carries a ``confidence``. Parameter ids confirmed by an enum declaration string in the
binary (``kParamMasterVolume = 0, kParamMasterTuning, kParamVoiceAmp, ...``) are marked
``binary-enum``; the others are the library majority (``library-majority``).

    python tools/schema/mod_matrix.py    # -> src/fruitylink_serum/data/mod-matrix.json
"""

from __future__ import annotations

import collections
import json
import os
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path[:0] = [str(HERE.parents[1] / "src"), str(HERE.parents[3] / "python" / "src")]
from fruitylink_serum import xfer  # noqa: E402

ROOT = Path(os.environ.get("SERUM_PRESETS_PATH", os.path.expanduser(r"~\Documents\Xfer\Serum 2 Presets")))
OUT = HERE.parents[1] / "src" / "fruitylink_serum" / "data" / "mod-matrix.json"

# Parameter enums read as declaration strings from Serum2.vst3 (2.1.4). Index = destModuleParamID.
BINARY_ENUMS: dict[str, list[str]] = {
    "Global": ["kParamMasterVolume", "kParamMasterTuning", "kParamVoiceAmp", "kParamPortamentoTime",
               "kParamPortamentoCurve", "kParamBendRangeUp", "kParamBendRangeDn", "kParamPitchBendAuto",
               "kParamModWheel", "kParamProgram", "kParamMonoToggle", "kParamLegato", "kParamPortaAlways",
               "kParamPortaScaled", "kParamSwing", "kParamSwingDiv", "kParamTranspose", "kParamBypass",
               "kParamDirectVol", "kParamFXBus1Vol", "kParamFXBus2Vol"],
    "Oscillator": ["kParamEnable", "kParamVolume", "kParamPan", "kParamOctave", "kParamPitch", "kParamFine",
                   "kParamCoarsePit", "kParamPitchRatio", "kParamHzOffset", "kParamPitchTrack", "kParamStart",
                   "kParamEnd", "kParamReverse", "kParamScanRate", "kParamScanBPMRate", "kParamScanKeyTrack",
                   "kParamPosition", "kParamLoopStart", "kParamLoopEnd", "kParamLoopCrossfade", "kParamLoopMode",
                   "kParamLoopStartLink", "kParamSlicingSingleSlice", "kParamSlicingPlayToEnd", "kParamUnison",
                   "kParamUnisonStack", "kParamDetune", "kParamDetuneWid", "kParamUnisonStereo", "kParamUnisonSpan",
                   "kParamRandomStart", "kParamUnisonWarp", "kParamUnisonWarp2"],
    "RoutingSlot": ["kParamFilterBalance", "kParamFXBus1Level", "kParamFXBus2Level"],
    "MultiSampleOsc": ["kParamWarp", "kParamWarpVar", "kParamWarpMenu", "kParamWarp2", "kParamWarpVar2",
                       "kParamWarpMenu2", "kParamTimbreShift", "kParamRandomPhase", "kParamEnvOverride",
                       "kParamEnvDelay", "kParamEnvAttack", "kParamEnvHold", "kParamEnvDecay", "kParamEnvSustain",
                       "kParamEnvRelease", "kParamVelTrackOverride", "kParamVelTrack"],
    "FXConv": ["kParamEnable", "kParamWet", "kParamMinPhase", "kParamLevelOut", "kParamSize", "kParamAttack",
               "kParamDecay", "kParamIpTrim", "kParamTone", "kParamPredelay", "kParamPredelayBeatSync", "kParamDamping"],
    # The "kParamEnable, kParamWet, kParamDrive, kParamLPHP, kParamMode, kParamFreq, ..., kParamNumStages" enum in
    # the binary does NOT match the library's FXPhaser ids (kParamFreq is 6 there), so it is not used.
    "ModSlot": ["kParamAmount", "kParamOut"],
}

# Source ids: inferred names. Evidence is in the generated file; keep this table in sync with docs/state-schema.md.
SOURCE_NAMES: dict[int, tuple[str, str, str]] = {
    0: ("none", "inferred", "slots without a source key are empty; id 0 never appears with a destination"),
    1: ("Env 1", "inferred", "most used envelope-group id; the usual 'via' source for LFO fades"),
    2: ("envelope group (unresolved)", "unknown", "rare id between Env 1 and the next envelope; may be Env 2 or an envelope variant"),
    3: ("Env 2 (probable)", "inferred", "second most used envelope id; 'Env via Velocity' pairs use it most"),
    4: ("Env 3 (probable)", "inferred", "usage decays 3 > 4 > 5"),
    5: ("Env 4 (probable)", "inferred", "usage decays 3 > 4 > 5"),
    16: ("Velocity", "inferred", "61 of 92 modulations of Global kParamVoiceAmp use it; the common aux source for envelopes"),
    17: ("Note", "inferred", "dominant use is VoiceFilter kParamFreq (key tracking)"),
    18: ("Mod Wheel", "inferred", "the classic aux source for LFO depth; UI enum order Velo, Note, Macro..., Wheel"),
}
for _i in range(10):
    SOURCE_NAMES[6 + _i] = (f"LFO {_i + 1}", "inferred", "ids 6..15 show a monotone usage decay matching LFO 1..10")
for _i in range(8):
    SOURCE_NAMES[25 + _i] = (f"Macro {_i + 1}", "inferred", "eight consecutive ids with ~1000+ uses each, matching factory macro assignment")


def main() -> None:
    presets = ROOT / "Presets"
    if not presets.is_dir():
        raise SystemExit(f"Serum preset directory not found: {presets}")
    dest_ids: dict[tuple[str, str], collections.Counter[int]] = collections.defaultdict(collections.Counter)
    source_use: collections.Counter[int] = collections.Counter()
    aux_use: collections.Counter[int] = collections.Counter()
    source_dest: dict[int, collections.Counter[str]] = collections.defaultdict(collections.Counter)
    slot_keys: collections.Counter[str] = collections.Counter()
    plain_keys: collections.Counter[str] = collections.Counter()
    files = 0
    for path in presets.rglob("*.SerumPreset"):
        try:
            state = xfer.read_container(path.read_bytes()).state
        except Exception:  # noqa: BLE001 - skip undecodable variants, as aggregate.py does
            continue
        files += 1
        for key, slot in state.items():
            if not key.startswith("ModSlot") or not isinstance(slot, dict):
                continue
            for k in slot:
                slot_keys[k] += 1
            plain = slot.get("plainParams")
            if isinstance(plain, dict):
                for k in plain:
                    plain_keys[k] += 1
            source = slot.get("source")
            kind, name, pid = slot.get("destModuleTypeString"), slot.get("destModuleParamName"), slot.get("destModuleParamID")
            if isinstance(kind, str) and isinstance(name, str) and isinstance(pid, int):
                dest_ids[(kind, name)][pid] += 1
            if isinstance(source, list) and len(source) == 2 and all(isinstance(v, int) for v in source):
                source_use[source[0]] += 1
                if source[1]:
                    aux_use[source[1]] += 1
                if isinstance(kind, str) and isinstance(name, str):
                    source_dest[source[0]][f"{kind}:{name}"] += 1

    destinations = []
    for (kind, name), counter in sorted(dest_ids.items(), key=lambda kv: (-sum(kv[1].values()), kv[0])):
        majority, count = counter.most_common(1)[0]
        enum = BINARY_ENUMS.get(kind)
        row: dict[str, object] = {"type": kind, "param": name, "param_id": majority, "occurrences": sum(counter.values())}
        if enum is not None and name in enum:
            row["confidence"] = "binary-enum" if enum.index(name) == majority else "conflict"
            row["binary_enum_id"] = enum.index(name)
        else:
            row["confidence"] = "library-majority"
        if len(counter) > 1:
            row["other_ids"] = {str(k): v for k, v in counter.items() if k != majority}
        destinations.append(row)
    sources = []
    for sid in sorted(set(source_use) | set(aux_use) | set(SOURCE_NAMES)):
        name, confidence, evidence = SOURCE_NAMES.get(sid, ("unknown", "unknown", "no name inferred"))
        sources.append({"id": sid, "name": name, "confidence": confidence, "evidence": evidence,
                        "uses_as_main": source_use.get(sid, 0), "uses_as_aux": aux_use.get(sid, 0),
                        "top_destinations": [k for k, _ in source_dest[sid].most_common(4)]})
    data = {
        "provenance": {
            "generated": "2026-09-14",
            "method": "Decoded every .SerumPreset in the local library with fruitylink_serum.xfer and counted ModSlot "
                      "source ids and destination (type, param) -> destModuleParamID pairs; parameter enums cross-checked "
                      "against declaration strings in Serum2.vst3 (2.1.4). No amounts, curves or payloads are stored.",
            "files_scanned": files,
            "engine_reference": "Serum 2 (productVersion up to 2.1.4)",
        },
        "layout": {
            "section": "ModSlot0..ModSlot63 (64 rows); an empty row is {'plainParams': 'default'} or absent",
            "source": "[main_source_id, aux_source_id]; aux 0 = no 'via' source",
            "destModuleTypeString": "section family of the target (Oscillator, WTOsc, VoiceFilter, Env, LFO, Global, Macro, RoutingSlot, FX*)",
            "destModuleID": "instance index within that family (Oscillator 0..4, VoiceFilter 0..1, Env 0..3, LFO 0..9, Global 0, FX unit index)",
            "destModuleParamName": "the kParam* name of the target parameter",
            "destModuleParamID": "index of that parameter in the module's parameter enum (see destinations)",
            "plainParams": "kParamAmount -100..100 percent; optional kParamBipolar 1, kParamBypass 1, kParamCurveIn/Out, kParamOut",
            "slot_keys_observed": [f"{k} ({v})" for k, v in slot_keys.most_common()],
            "plain_keys_observed": [f"{k} ({v})" for k, v in plain_keys.most_common()],
        },
        "sources": sources,
        "destinations": destinations,
        "binary_enums": BINARY_ENUMS,
        "notes": [
            "Source ids are numeric in files; the names here are inferred (see each row's confidence) and must be "
            "confirmed live: load a patch with one slot and check Serum's matrix, or render two velocities.",
            "Ids 26/27/28 for Oscillator kParamDetune/kParamDetuneWid/kParamUnisonStereo match the 2.1.4 enum; the rare "
            "10/11 ids come from older presets.",
        ],
    }
    OUT.write_text(json.dumps(data, indent=1) + "\n", encoding="utf-8")
    print(f"wrote {OUT} ({files} files, {len(destinations)} destinations, {len(sources)} sources)")


if __name__ == "__main__":
    main()
