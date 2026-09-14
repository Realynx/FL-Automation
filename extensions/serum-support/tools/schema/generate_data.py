"""Generate the fruitylink_serum data files from aggregate.json + wavetables-raw.json (derived stats only)."""
import json
import os
import re

HERE = os.path.dirname(os.path.abspath(__file__))
DATA = os.path.join(HERE, "..", "..", "src", "fruitylink_serum", "data")
os.makedirs(DATA, exist_ok=True)
agg = json.load(open(os.path.join(HERE, "aggregate.json"), encoding="utf-8"))
raw_wt = json.load(open(os.path.join(HERE, "wavetables-raw.json"), encoding="utf-8"))
st = agg["stats"]
TODAY = "2026-09-13"
PROVENANCE = {
    "generated": TODAY,
    "method": "Derived statistics from decoding the locally installed Serum 2 library (.SerumPreset/.SerumFX/.SerumFXRack) and this project's FL-saved processor states with fruitylink_serum.xfer; no preset payloads are stored.",
    "files_scanned": agg["files"],
    "engine_reference": "Serum 2 (productVersion up to 2.1.4), FL Studio 26.1.3 wrapper displays",
}

# ---- unit / scale annotations (key-level rules; section-specific overrides first) ----
# scale vocabulary: "linear_gain" (0..1 amplitude, FL % = sqrt*100, dB = 20log10),
# "normalized" (0..1 = FL normalized), "percent" (0..100), "seconds", "hz", "semitones", "cents",
# "count", "octaves", "degrees", "enum", "flag", "midi_note", "index"
RULES = {
    "Oscillator/kParamVolume": ("linear_gain", "verified", "A/B/C Level, Noise Level, Sub Level: FL % = sqrt(value)*100; 0.7586 read back as 87% [-2.4 dB]"),
    "Oscillator/kParamUnison": ("count", "verified", "A Unison; 1..16 voices; FL normalized = (n-1)/15"),
    "Oscillator/kParamDetune": ("normalized", "verified", "A Uni Detune; FL display = value^2 (0.316 -> 0.10)"),
    "Oscillator/kParamDetuneWid": ("percent", "verified", "A Uni Blend (81.99 read back as 82)"),
    "Oscillator/kParamUnisonStereo": ("percent", "inferred", "A Uni Width (-100..100)"),
    "Oscillator/kParamUnisonSpan": ("percent", "inferred", "A Uni Span"),
    "Oscillator/kParamUnisonWarp": ("percent", "inferred", "A Uni Warp"),
    "Oscillator/kParamUnisonStack": ("enum", "inferred", "A Uni Stack"),
    "Oscillator/kParamOctave": ("octaves", "verified", "A/B/C/Sub Octave (-4..4); Sub -1 read back as '-1 oct'"),
    "Oscillator/kParamPitch": ("semitones", "inferred", "A Semi (-12..12)"),
    "Oscillator/kParamFine": ("cents", "inferred", "A Fine (-100..100)"),
    "Oscillator/kParamCoarsePit": ("semitones", "inferred", "A Coarse Pitch (-64..64)"),
    "Oscillator/kParamPan": ("percent", "inferred", "A Pan (-50..50)"),
    "Oscillator/kParamEnable": ("flag", "verified", "A/B/C/Noise/Sub Enable"),
    "Oscillator/kParamPitchTrack": ("flag", "inferred", "A Pitch Track"),
    "Oscillator/kParamHzOffset": ("hz", "inferred", "A Hz Offset"),
    "Oscillator/kParamPitchRatio": ("ratio", "inferred", "A Ratio"),
    "Oscillator/kParamType": ("enum", "inferred", "oscillator engine: absent = wavetable; kOsc_Sample/kOsc_MultiSample/kOsc_Granular/kOsc_Spectral"),
    "Oscillator/kParamLoopMode": ("enum", "inferred", "A Loop Mode"),
    "Oscillator/kParamKeyZoneMin": ("midi_note", "inferred", None),
    "Oscillator/kParamKeyZoneMax": ("midi_note", "inferred", None),
    "WTOsc/kParamTablePos": ("table_position_256", "inferred", "A WT Pos; 0..256 spans the whole table: frame ~= value/256*numFrames (88.29 on a 166-frame table displayed as frame 57)"),
    "WTOsc/kParamInitialPhase": ("degrees", "inferred", "A Phase (0..360)"),
    "WTOsc/kParamRandomPhase": ("percent", "inferred", "A Rand Phase"),
    "WTOsc/kParamWarp": ("normalized", "inferred", "A Warp Var"),
    "WTOsc/kParamWarpMenu": ("enum", "inferred", "A Warp (mode)"),
    "WTOsc/kParamPhaseMemory": ("enum", "inferred", None),
    "WTOsc/kParamUnisonWTPos": ("percent", "inferred", "A Uni WT Pos (-100..100)"),
    "SubOsc/kParamShape": ("enum", "verified", "Sub Shape; FL normalized 0.0 Sine, 0.4 Triangle, 0.6 Saw, 0.8 Square, 1.0 Pulse (kRoundRect is the rounded-square entry)"),
    "SubOsc/kParamContiguousPhase": ("flag", "inferred", "Sub Cont. Phase"),
    "SubOsc/kParamInitialPhase": ("degrees", "inferred", "Sub Phase"),
    "VoiceFilter/kParamEnable": ("flag", "verified", "Filter 1/2 On"),
    "VoiceFilter/kParamFreq": ("filter_cutoff_normalized", "verified", "Filter 1/2 Freq; Hz ~= 8 * 2756^value (0.42 -> 226 Hz)"),
    "VoiceFilter/kParamReso": ("percent", "verified", "Filter 1/2 Res (10 read back as 10 %)"),
    "VoiceFilter/kParamDrive": ("percent", "inferred", "Filter 1/2 Drive"),
    "VoiceFilter/kParamVar": ("percent", "inferred", "Filter 1/2 Var"),
    "VoiceFilter/kParamWet": ("percent", "inferred", "Filter 1/2 Wet (absent = 100)"),
    "VoiceFilter/kParamStereo": ("percent", "inferred", "Filter 1/2 Stereo"),
    "VoiceFilter/kParamLevelOut": ("linear_gain", "inferred", "Filter 1/2 Level"),
    "VoiceFilter/kParamType": ("enum", "inferred", "Filter 1/2 Type; see filters.json"),
    "VoiceFilter/kParamKeyTrack": ("flag", "inferred", None),
    "Env/kParamAttack": ("seconds", "verified", "Env 1 Attack (0.015 read back as 15 ms)"),
    "Env/kParamHold": ("seconds", "inferred", "Env 1 Hold"),
    "Env/kParamDecay": ("seconds", "verified", "Env 1 Decay (2.46 read back as 2.46 s)"),
    "Env/kParamSustain": ("normalized", "verified", "Env 1 Sustain; FL dB = 20*log10(value^2) (0.72 -> -5.7 dB)"),
    "Env/kParamRelease": ("seconds", "verified", "Env 1 Release"),
    "Env/kParamCurve{n}": ("percent", "inferred", "Env Atk/Dec/Rel Curve (50 = linear)"),
    "Env/kParamBeatSync": ("flag", "inferred", None),
    "Global/kParamMasterVolume": ("linear_gain", "verified", "Main Vol; FL % = sqrt(value)*100, FL dB = 20*log10(value) + 3 (0.3025 -> 55% [-7.4 dB])"),
    "Global/kParamPortamentoTime": ("seconds", "inferred", "Porta Time"),
    "Global/kParamPortamentoCurve": ("percent", "inferred", "Porta Curve"),
    "Global/kParamMonoToggle": ("flag", "verified", "Mono Toggle"),
    "Global/kParamLegato": ("flag", "inferred", "Legato"),
    "Global/kParamPortaAlways": ("flag", "inferred", "Porta Always"),
    "Global/kParamPortaScaled": ("flag", "inferred", "Porta Scaled"),
    "Global/kParamBendRangeUp": ("semitones", "inferred", "Bend Up"),
    "Global/kParamBendRangeDn": ("semitones", "inferred", "Bend Down"),
    "Global/kParamTranspose": ("semitones", "inferred", "Transpose"),
    "Global/kParamPolyCount": ("count", "inferred", None),
    "Global/kParamModWheel": ("percent", "inferred", "Mod Wheel"),
    "Global/kParamSwing": ("percent", "inferred", "Swing"),
    "Global/kParamFXBus1Vol": ("linear_gain", "inferred", "Bus 1 Vol"),
    "Global/kParamFXBus2Vol": ("linear_gain", "inferred", "Bus 2 Vol"),
    "Global/kParamDirectVol": ("linear_gain", "inferred", "Direct Vol"),
    "Global/kParamOversampling": ("index", "inferred", None),
    "LFO/kParamRate": ("lfo_rate", "inferred", "LFO n Rate; Hz when Free and BeatSync=0, otherwise a division index"),
    "LFO/kParamMode": ("enum", "inferred", "Free / Envelope"),
    "LFO/kParamSmooth": ("percent", "inferred", "LFO n Smooth"),
    "LFO/kParamRise": ("seconds", "inferred", "LFO n Rise"),
    "LFO/kParamDelay": ("seconds", "inferred", "LFO n Delay"),
    "LFO/kParamPhase": ("degrees", "inferred", "LFO n Phase"),
    "LFO/kParamType": ("enum", "inferred", "Rossler/Lorenz/RandomSH/Path (absent = drawn shape)"),
    "Macro/kParamValue": ("percent", "inferred", "Macro 1..8"),
    "ModSlot/kParamAmount": ("percent", "inferred", "Mod n Amount (-100..100)"),
    "ModSlot/kParamBipolar": ("flag", "inferred", None),
}
FX_RULES = {
    "kParamWet": ("percent", "inferred", "unit mix 0..100"),
    "kParamEnable": ("flag", "inferred", "absent = enabled; 0 = bypassed"),
    "kParamLevelOut": ("linear_gain", "inferred", None),
    "kParamBeatSync": ("flag", "inferred", None),
}

def rule_for(section_base, key):
    base = re.sub(r"(\{n\}|\d+)$", "", section_base)
    generic_key = re.sub(r"\d+$", "{n}", key)
    for cand in (f"{base}/{key}", f"{base}/{generic_key}"):
        if cand in RULES:
            return RULES[cand]
    return None

def clean_values(vals, limit=24):
    return [k for k, _ in sorted(vals.items(), key=lambda x: -x[1])[:limit]]

rows = []
for path, r in st.items():
    if "/plainParams/" not in path:
        continue
    if path.startswith("FXRack"):
        continue
    parts = path.split("/plainParams/")
    section_path, key = parts[0], parts[1]
    section_base = section_path.split("/")[-1]
    types = r["types"]
    if "str" in types:
        vtype = "enum"
    elif "bool" in types:
        vtype = "bool"
    elif set(types) <= {"float", "int"}:
        vals = list(r["values"].keys())
        vtype = "int" if all(re.fullmatch(r"-?\d+(\.0)?", v) for v in vals) and r["max"] is not None and r["max"] <= 4096 else "float"
    else:
        vtype = "/".join(sorted(types))
    rule = rule_for(section_base, key)
    row = {"path": path, "section": section_path, "key": key, "type": vtype, "min": r["min"], "max": r["max"],
           "occurrences": r["count"], "files": r["files"]}
    if vtype in ("enum", "bool"):
        row["values"] = clean_values(r["values"])
    if rule:
        row["scale"], row["confidence"], note = rule
        if note:
            row["fl_parameter_note"] = note
    else:
        row["scale"] = "unknown"
        row["confidence"] = "observed-only"
    rows.append(row)
rows.sort(key=lambda x: x["path"])

# FL anchors (verified live or by exact readback correlation)
FL_ANCHORS = [
    {"fl": "Main Vol", "path": "Global{n}/plainParams/kParamMasterVolume", "fl_normalized_to_state": "state = fl^2", "confidence": "verified"},
    {"fl": "A Level", "path": "Oscillator0/plainParams/kParamVolume", "fl_normalized_to_state": "state = fl^2", "confidence": "verified"},
    {"fl": "B Level", "path": "Oscillator1/plainParams/kParamVolume", "fl_normalized_to_state": "state = fl^2", "confidence": "inferred"},
    {"fl": "C Level", "path": "Oscillator2/plainParams/kParamVolume", "fl_normalized_to_state": "state = fl^2", "confidence": "inferred"},
    {"fl": "Noise Level", "path": "Oscillator3/plainParams/kParamVolume", "fl_normalized_to_state": "state = fl^2", "confidence": "inferred"},
    {"fl": "Sub Level", "path": "Oscillator4/plainParams/kParamVolume", "fl_normalized_to_state": "state = fl^2", "confidence": "verified"},
    {"fl": "A Enable", "path": "Oscillator0/plainParams/kParamEnable", "fl_normalized_to_state": "state = fl (0/1)", "confidence": "verified"},
    {"fl": "Sub Enable", "path": "Oscillator4/plainParams/kParamEnable", "fl_normalized_to_state": "state = fl (0/1)", "confidence": "verified"},
    {"fl": "A Unison", "path": "Oscillator0/plainParams/kParamUnison", "fl_normalized_to_state": "state = 1 + fl*15", "confidence": "verified"},
    {"fl": "A Uni Detune", "path": "Oscillator0/plainParams/kParamDetune", "fl_normalized_to_state": "state = fl; display = fl^2", "confidence": "verified"},
    {"fl": "A Uni Blend", "path": "Oscillator0/plainParams/kParamDetuneWid", "fl_normalized_to_state": "state = fl*100", "confidence": "verified"},
    {"fl": "A Uni Width", "path": "Oscillator0/plainParams/kParamUnisonStereo", "fl_normalized_to_state": "state = fl*100 (observed -100..100 in files)", "confidence": "inferred"},
    {"fl": "A Octave", "path": "Oscillator0/plainParams/kParamOctave", "fl_normalized_to_state": "state = round(-4 + fl*8)", "confidence": "inferred"},
    {"fl": "Sub Octave", "path": "Oscillator4/plainParams/kParamOctave", "fl_normalized_to_state": "1/3 -> -1 oct read back; consistent with -4..4 over 0..1 only if the range is -3..3: unresolved", "confidence": "verified-value-only"},
    {"fl": "Sub Shape", "path": "Oscillator4/SubOsc4/plainParams/kParamShape", "fl_normalized_to_state": "0.0 kSine(default,absent) / 0.4 kTriangle / 0.6 kSaw / 0.8 kSquare / 1.0 kPulse; kRoundRect ~0.2", "confidence": "verified"},
    {"fl": "A WT Pos", "path": "Oscillator0/WTOsc0/plainParams/kParamTablePos", "fl_normalized_to_state": "state = fl*256; display frame = value/256*numFrames", "confidence": "inferred"},
    {"fl": "Filter 1 On", "path": "VoiceFilter0/plainParams/kParamEnable", "fl_normalized_to_state": "state = fl (0/1)", "confidence": "verified"},
    {"fl": "Filter 1 Freq", "path": "VoiceFilter0/plainParams/kParamFreq", "fl_normalized_to_state": "state = fl; Hz = 8*2756^fl", "confidence": "verified"},
    {"fl": "Filter 1 Res", "path": "VoiceFilter0/plainParams/kParamReso", "fl_normalized_to_state": "state = fl*100", "confidence": "verified"},
    {"fl": "Filter 1 Drive", "path": "VoiceFilter0/plainParams/kParamDrive", "fl_normalized_to_state": "state = fl*100", "confidence": "inferred"},
    {"fl": "Filter 1 Type", "path": "VoiceFilter0/plainParams/kParamType", "fl_normalized_to_state": "enum; default (absent) displays 'MG Low 12' = MgL12", "confidence": "verified-default-only"},
    {"fl": "Env 1 Attack", "path": "Env0/plainParams/kParamAttack", "fl_normalized_to_state": "seconds (FL display ms); fl->seconds curve unknown", "confidence": "verified-value-only"},
    {"fl": "Env 1 Decay", "path": "Env0/plainParams/kParamDecay", "fl_normalized_to_state": "seconds", "confidence": "verified-value-only"},
    {"fl": "Env 1 Sustain", "path": "Env0/plainParams/kParamSustain", "fl_normalized_to_state": "state = fl; dB = 20*log10(fl^2)", "confidence": "verified"},
    {"fl": "Env 1 Release", "path": "Env0/plainParams/kParamRelease", "fl_normalized_to_state": "seconds", "confidence": "verified-value-only"},
    {"fl": "Mono Toggle", "path": "Global{n}/plainParams/kParamMonoToggle", "fl_normalized_to_state": "state = fl (0/1)", "confidence": "verified"},
    {"fl": "Porta Time", "path": "Global{n}/plainParams/kParamPortamentoTime", "fl_normalized_to_state": "seconds", "confidence": "inferred"},
    {"fl": "Porta Always", "path": "Global{n}/plainParams/kParamPortaAlways", "fl_normalized_to_state": "state = fl (0/1)", "confidence": "inferred"},
    {"fl": "Legato", "path": "Global{n}/plainParams/kParamLegato", "fl_normalized_to_state": "state = fl (0/1)", "confidence": "inferred"},
    {"fl": "Bend Up", "path": "Global{n}/plainParams/kParamBendRangeUp", "fl_normalized_to_state": "semitones", "confidence": "inferred"},
    {"fl": "Bend Down", "path": "Global{n}/plainParams/kParamBendRangeDn", "fl_normalized_to_state": "semitones (negative)", "confidence": "inferred"},
    {"fl": "Transpose", "path": "Global{n}/plainParams/kParamTranspose", "fl_normalized_to_state": "semitones", "confidence": "inferred"},
    {"fl": "Macro 1", "path": "Macro0/plainParams/kParamValue", "fl_normalized_to_state": "state = fl*100", "confidence": "inferred"},
    {"fl": "LFO 1 Rate", "path": "LFO0/plainParams/kParamRate", "fl_normalized_to_state": "unresolved (Hz vs division index)", "confidence": "inferred"},
    {"fl": "Mod 1 Amount", "path": "ModSlot0/plainParams/kParamAmount", "fl_normalized_to_state": "state = -100 + fl*200", "confidence": "inferred"},
]
SECTIONS = {
    "Oscillator0": "Oscillator A (wavetable by default; WTOsc0 sub-section holds the table path and position)",
    "Oscillator1": "Oscillator B", "Oscillator2": "Oscillator C",
    "Oscillator3": "Noise oscillator (SampleOsc3 for the noise sample)",
    "Oscillator4": "Sub oscillator (SubOsc4 sub-section holds kParamShape)",
    "VoiceFilter0": "Filter 1", "VoiceFilter1": "Filter 2",
    "Env0": "Env 1 (amplitude)", "Env1": "Env 2", "Env2": "Env 3", "Env3": "Env 4",
    "LFO0..9": "LFO 1..10 (curveData holds the drawn shape)",
    "ModSlot0..63": "Mod matrix rows: source [id, sub], destModuleTypeString/destModuleID/destModuleParamName, plainParams.kParamAmount",
    "Macro0..7": "Macros (name + kParamValue)",
    "Global0": "Global/voice settings (master volume, mono/legato/portamento, bends, bus levels, oversampling)",
    "FXRack0..2": "FX racks: 0 = main insert chain, 1..2 = FX buses (see fx-schema.json)",
    "RoutingSlot0..6": "oscillator -> filter/bus routing", "VoicePanel0": "voice panel", "Arp0 / ArpClip / MidiClip / ClipPlayer": "arpeggiator and clip player",
    "scalars": "note/velocity curves", "PitchQuantizer0 / RetriggerState0 / LFOPointModBus": "misc",
    "component / product / productVersion / version": "processor identity fields required by the VST3 state (see loading.normalize_processor_state)",
}
param_map = {
    "provenance": PROVENANCE,
    "scale_vocabulary": {
        "linear_gain": "0..1 amplitude; FL % = sqrt(value)*100; dB = 20*log10(value) (+3 dB for Main Vol)",
        "normalized": "0..1, equals FL's normalized value",
        "percent": "0..100 (or -100..100 where the min is negative)",
        "seconds": "seconds", "hz": "hertz", "semitones": "semitones", "cents": "cents", "count": "integer count",
        "octaves": "integer octave offset", "degrees": "0..360", "enum": "string enum (see values)", "flag": "0/1; absent = default",
        "filter_cutoff_normalized": "0..1; Hz ~= 8 * 2756^value", "table_position_256": "0..256 spanning the whole wavetable",
        "lfo_rate": "Hz in free mode; division index when synced", "midi_note": "0..127", "index": "integer selector", "ratio": "frequency ratio",
        "unknown": "observed only; no unit inferred",
    },
    "sections": SECTIONS,
    "fl_anchors": FL_ANCHORS,
    "parameters": rows,
}
json.dump(param_map, open(os.path.join(DATA, "parameter-map.json"), "w", encoding="utf-8"), indent=1, sort_keys=False)

# ---- FX schema ----
fx_types = {t: list(names.keys())[0] for t, names in agg["fx_types"].items()}
FX_LABELS = {"FXDistortion": "Distortion", "FXFlanger": "Flanger", "FXPhaser": "Phaser", "FXChorus": "Chorus", "FXDelay": "Delay",
             "FXComp": "Compressor", "FXReverb": "Reverb", "FXEQ": "Equalizer", "FXFilter": "Filter", "FXHyperD": "Hyper / Dimension",
             "FXBode": "Bode (frequency shifter)", "FXConv": "Convolve (convolution reverb)", "FXUtils": "Utility", "FXSplit": "Splitter (L/H)",
             "FXSplit3": "Splitter (3 band)", "FXSplitMS": "Splitter (M/S)"}
effects = {}
for t, sec in fx_types.items():
    params = []
    prefix = f"FXRack{{n}}/FX[{sec}]/plainParams/"
    for path, r in st.items():
        if path.startswith(prefix):
            key = path[len(prefix):]
            types = r["types"]
            vtype = "enum" if "str" in types else ("bool" if "bool" in types else "float")
            row = {"key": key, "type": vtype, "min": r["min"], "max": r["max"], "occurrences": r["count"]}
            if vtype == "enum":
                row["values"] = clean_values(r["values"])
            rule = FX_RULES.get(key)
            if rule:
                row["scale"], row["confidence"], note = rule
                if note:
                    row["note"] = note
            else:
                row["confidence"] = "observed-only"
            params.append(row)
    extra = {}
    for path, r in st.items():
        p2 = f"FXRack{{n}}/FX[{sec}]/"
        if path.startswith(p2) and "/plainParams/" not in path:
            extra[path[len(p2):]] = {"types": r["types"], "occurrences": r["count"], "values": clean_values(r["values"], 6)}
    effects[sec] = {"type": int(t), "label": FX_LABELS.get(sec, sec), "occurrences": sum(agg["fx_types"][t].values()),
                    "parameters": sorted(params, key=lambda x: x["key"]), "other_fields": extra}
fx_schema = {
    "provenance": PROVENANCE,
    "layout": {
        "racks": "state['FXRack0'..'FXRack2'] = {'FX': [unit, ...], 'displayName': str, 'plainParams': ..., 'proxyParams': {...}}; FXRack0 is the main chain, FXRack1/2 the buses fed by Global kParamFXBus1Vol/kParamFXBus2Vol",
        "unit": "each unit is a map {'type': <int>, '<FXName>': {'plainParams': {...}, ...}, 'kUIParamMixOrGain': 0.0, optional 'flex': [..], optional 'selectedPreset': 'Factory/<name>.SerumFX', 'presetEdited': bool}",
        "type_key": "unit['type'] selects the effect; the single 'FX*' key must match type_map",
        "order": "list order is the processing order",
        "enable": "plainParams.kParamEnable absent = enabled; 0.0 = bypassed",
        "mix": "plainParams.kParamWet 0..100 percent (most units); kParamLevelOut linear gain",
        "chain_lengths_observed": st["FXRack{n}/FX"]["values"],
    },
    "type_map": {t: {"section": sec, "label": FX_LABELS.get(sec, sec)} for t, sec in fx_types.items()},
    "effects": effects,
    "confidence_vocabulary": {
        "verified": "a live FL readback matched the rule",
        "inferred": "unit reasoned from the key name and observed range",
        "observed-only": "range recorded; unit and default unknown",
    },
    "notes": [
        "Value ranges are observed extremes across the library, not documented limits.",
        "FXReverb.kParamType enum: kHall, kVintage, kAbyss, kSpace; kParamSize/kParamWidth/kParamWet percent; kParamPreDelay seconds; kParamDelay ms-like (0..250).",
        "FXDistortion.kParamMode enum lists the drive algorithms (kOverdrive, kSoftClip, kTapeSat, kDiode1, kDownsample, ...).",
        "FXFilter.kParamType uses the same enum vocabulary as VoiceFilter (filters.json).",
        "FXDelay.kParamTimeL/R are seconds when kParamBeatSync = 0; kParamMode 1 = normal, 2 = ping-pong (inferred from the two observed values).",
        "9 factory .SerumFX files use a container variant whose metadata is not UTF-8 JSON and were skipped.",
    ],
}
json.dump(fx_schema, open(os.path.join(DATA, "fx-schema.json"), "w", encoding="utf-8"), indent=1)

# ---- filters ----
def fl_name(state_name):
    m = re.fullmatch(r"MgL(\d+)", state_name)
    if m:
        return f"MG Low {m.group(1)}", "pattern"
    m = re.fullmatch(r"L(\d+)", state_name)
    if m:
        return f"Low {m.group(1)}", "pattern"
    m = re.fullmatch(r"H(\d+)", state_name)
    if m:
        return f"High {m.group(1)}", "pattern"
    m = re.fullmatch(r"B(\d+)", state_name)
    if m:
        return f"Band {m.group(1)}", "pattern"
    return None, "unknown"
vf = st.get("VoiceFilter{n}/plainParams/kParamType", {"values": {}})
fx_f = st.get("FXRack{n}/FX[FXFilter]/plainParams/kParamType", {"values": {}})
names = {}
for src, vals in (("voice", vf["values"]), ("fx", fx_f["values"])):
    for k, c in vals.items():
        names.setdefault(k, {"state_value": k, "voice_filter_occurrences": 0, "fx_filter_occurrences": 0})
        names[k]["voice_filter_occurrences" if src == "voice" else "fx_filter_occurrences"] += c
for k, row in names.items():
    disp, conf = fl_name(k)
    row["fl_display_guess"] = disp
    row["confidence"] = conf
    fam = "lowpass" if k.startswith(("MgL", "L", "Ladder", "DirtyMg", "ZDF")) and not k.startswith(("LH", "LB", "LN", "LNH", "LBH", "LPH")) else \
          "highpass" if re.match(r"^H\d|^HP", k) else "bandpass" if re.match(r"^B\d|^BP|^BN|^BPN", k) else \
          "notch/reject" if "BandReject" in k or k.startswith("N") else "comb/flange/phase" if any(s in k for s in ("Comb", "Flange", "Phase", "Allpass")) else \
          "formant/vowel" if "Formant" in k else "multi-mode" if k in ("LH12", "LB12", "LN12", "LNH24", "LBH24", "LPH24", "LNH12", "PP12", "HEQ12") else "special"
    row["family"] = fam
names["MgL12"] = names.get("MgL12", {"state_value": "MgL12", "voice_filter_occurrences": 0, "fx_filter_occurrences": 0, "family": "lowpass"})
names["MgL12"].update({"fl_display_guess": "MG Low 12", "confidence": "verified-default", "note": "absent kParamType displayed 'MG Low 12' in FL"})
filters = {"provenance": PROVENANCE, "key": "VoiceFilter{n}/plainParams/kParamType and FXRack{n}/FX[FXFilter]/plainParams/kParamType share this enum",
           "default": "MgL12 (absent)", "types": sorted(names.values(), key=lambda r: -(r["voice_filter_occurrences"] + r["fx_filter_occurrences"])),
           "confidence_vocabulary": {"verified-default": "absent kParamType displayed 'MG Low 12' live",
                                     "pattern": "FL display name guessed from the state value pattern",
                                     "unknown": "no display-name guess"}}
json.dump(filters, open(os.path.join(DATA, "filters.json"), "w", encoding="utf-8"), indent=1)

# ---- wavetables ----
tables = []
for t in raw_wt["tables"]:
    frames = [{"frame": f["frame"], "label": f["label"], "even_over_odd": f["even_over_odd"], "odd_decay_slope": f["odd_decay_slope"], "nulls": f["nulls"]} for f in t["frame_labels"]]
    by_label = {}
    for f in frames:
        by_label.setdefault(f["label"], []).append(f["frame"])
    n = t["frames"]
    tables.append({"relative_path": t["relative_path"], "num_frames": n, "frame_length": raw_wt["frame_length"],
                   "frames_by_label": by_label,
                   "table_pos_for_frame": {str(f): round(256.0 * f / n, 3) for f in range(1, min(n, 64) + 1)} if n <= 64 else "use 256*frame/num_frames",
                   "frames": frames if n <= 64 else frames[:64],
                   "confidence": "derived-labels"})
wavetables = {
    "provenance": {**PROVENANCE, "method": "Read factory wavetable WAVs (float32 mono, 2048-sample frames per the 'clm ' chunk), computed the first 32 harmonics of each frame with a direct DFT and labelled by even/odd energy, odd-harmonic decay slope and spectral nulls. Labels are derived; no audio is stored."},
    "tables_root_hint": "Documents/Xfer/Serum 2 Presets/Tables; state relativePathToWT is relative to this folder (e.g. 'S2 Tables/Default Shapes.wav', 'Analog/Basic Shapes.wav'; a leading '/' also occurs)",
    "position_rule": {"key": "Oscillator{n}/WTOsc{n}/plainParams/kParamTablePos", "range": "0..256 spanning the table",
                      "frame_from_value": "frame = round(value / 256 * num_frames), minimum 1", "value_for_frame": "256 * frame / num_frames",
                      "evidence": "88.29 on the 166-frame Analog/Basic Mg.wav displayed as WT Pos 57 in FL (88.29/256*166 = 57.25 -> 57); library max observed 256.0", "confidence": "inferred"},
    "sub_oscillator_shapes": {"key": "Oscillator4/SubOsc4/plainParams/kParamShape", "values": ["kSine (default, absent)", "kRoundRect", "kTriangle", "kSaw", "kSquare", "kPulse"],
                              "fl_normalized": {"kSine": 0.0, "kRoundRect": 0.2, "kTriangle": 0.4, "kSaw": 0.6, "kSquare": 0.8, "kPulse": 1.0}, "confidence": "verified except kRoundRect"},
    "label_rules": {"sine": "h1 dominant, other harmonics < 5 % of h1", "triangle": "odd harmonics only, ~1/n^2 decay", "square": "odd harmonics only, ~1/n decay",
                    "saw": "all harmonics, ~1/n decay, no nulls", "pulse": "all harmonics with sinc nulls", "other": "anything else (analog-modelled or complex frames)"},
    "tables": tables,
    "confidence_vocabulary": {
        "derived-labels": "frame labels come from harmonic analysis of the factory .wav, not from Serum's own names; "
                          "the frame <-> kParamTablePos rule is 'inferred' (one live point)",
    },
}
json.dump(wavetables, open(os.path.join(DATA, "wavetables.json"), "w", encoding="utf-8"), indent=1)
print("rows", len(rows), "effects", len(effects), "filters", len(names), "tables", len(tables))
