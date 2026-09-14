"""Describe a Serum 2 patch in musical terms and turn it into a loadable preset.

``SerumPatch`` is a fluent builder over the processor state that
:func:`fruitylink_serum.loading.to_vstpreset` wraps and FL's wrapper accepts
(live-verified on FL 26.1.3). Every method converts musical units (Hz, ms, dB,
knob fractions, counts, names) into Serum's own state units through the schema
data shipped in ``fruitylink_serum/data`` (``parameter-map.json``,
``wavetables.json``, ``fx-schema.json``, ``filters.json``; see
``docs/state-schema.md``). A small built-in map of keys read back from real
project states keeps the builder importable when the data files are absent.

Anything the loaded schema does not describe raises ``PatchError`` or
``NotImplementedError`` that names the missing data instead of guessing a key.
The escape hatch :meth:`SerumPatch.set` writes any ``Section/plainParams/kParam``
path verbatim.

Unit conventions (FL readbacks of Ember Tides channel states, 2026-09-13):

* ``kParamVolume`` (oscillators, sub, noise) and ``Global0 kParamMasterVolume``
  are linear gain; FL's knob percent is ``sqrt(state)`` so ``level`` (knob 0..1)
  stores ``knob**2``; dB = ``20*log10(state)`` (FL adds +3 dB to the master display);
* envelope times are seconds (methods take milliseconds); ``kParamSustain`` is a
  0..1 knob whose FL display is ``40*log10(knob)`` dB;
* filter ``kParamFreq`` is normalised: ``hz = 8.1846 * 2698.7 ** v`` (fitted to the
  readbacks 0.42 -> 226 Hz and 0.60 -> 937 Hz); ``kParamReso``/``kParamDrive`` are percent;
* ``kParamUnison`` is a voice count, ``kParamDetune`` the displayed 0..1 value,
  ``kParamDetuneWid`` the unison blend percent, ``kParamUnisonStereo`` the width percent;
* the sub oscillator is ``Oscillator4`` with ``SubOsc4/plainParams/kParamShape`` as a
  string enum (``kSine`` is the default and is left unset);
* wavetable shapes are a table path plus ``kParamTablePos`` on a 0..256 span
  (``256 * frame / num_frames``; frame labels come from ``wavetables.json``);
* effects are units appended to ``FXRack0/FX`` as ``{"type": n, "FX<Name>": {"plainParams":
  {...}}, "kUIParamMixOrGain": 0.0}``; ``kParamWet`` is the unit mix in percent;
* modulation rows are ``ModSlot{n}`` maps (``source: [main, aux]``, ``destModuleTypeString``,
  ``destModuleID``, ``destModuleParamName``, ``destModuleParamID``, ``plainParams/kParamAmount``
  percent) described by ``mod-matrix.json``; source ids are inferred (see ``docs/state-schema.md``).

Defaults implied by absence (Serum omits parameters at their default): oscillator A enabled,
B/C/noise/sub disabled; voice filters disabled and, when enabled, ``kParamWet`` 100 %;
sub shape sine; filter type ``MgL12``; effect units enabled; bend range ±2 semitones is
Serum's own default (unverified) and ``bend_range`` writes both keys explicitly.
"""

from __future__ import annotations

import copy
import json
import math
import os
import re
import tempfile
from collections.abc import Mapping
from dataclasses import dataclass, field, replace
from importlib import resources
from pathlib import Path
from typing import Any

from . import cbor, loading, xfer

__all__ = [
    "BUILTIN_PARAMETERS",
    "DEFAULT_TABLE",
    "FILTER_ALIASES",
    "FX_ALIASES",
    "MOD_SLOT_COUNT",
    "ModSourceInfo",
    "PatchError",
    "Schema",
    "SerumPatch",
    "describe_parameters",
    "flatten_state",
    "init_container",
    "load_schema",
]


class PatchError(ValueError):
    """A musical request cannot be expressed with the available schema."""


# ---------------------------------------------------------------------------- units

# Fitted to two live readbacks of ``VoiceFilter0 kParamFreq`` (0.42 -> 226 Hz, 0.60 -> 937 Hz):
# hz = _HZ_BASE * _HZ_RANGE ** v. Serum nominally spans about 8 Hz .. 22 kHz; expect ~1 % error elsewhere.
_HZ_BASE = 8.1846
_HZ_RANGE = 2698.7


def hz_to_normalized(hz: float) -> float:
    if hz <= 0:
        raise PatchError("Frequency must be positive.")
    return float(max(0.0, min(1.0, math.log(hz / _HZ_BASE) / math.log(_HZ_RANGE))))


def normalized_to_hz(value: float) -> float:
    return float(_HZ_BASE * _HZ_RANGE**value)


def knob_to_gain(knob: float) -> float:
    """FL's level knob (0..1, shown as a percent) stores ``knob**2`` as linear gain."""
    if not 0.0 <= knob <= 1.0:
        raise PatchError("Level knob fractions are 0..1.")
    return knob * knob


def db_to_gain(db: float) -> float:
    return float(10.0 ** (db / 20.0))


def sustain_db_to_knob(db: float) -> float:
    return float(10.0 ** (db / 40.0))


def master_db_to_gain(db: float) -> float:
    """FL displays the master as ``20*log10(gain) + 3`` dB; the state stores the linear gain."""
    return db_to_gain(db - 3.0)


_UNITS = {
    "seconds": "seconds (builder methods take milliseconds)",
    "gain": "linear gain; FL knob percent = sqrt(value) (methods take level 0..1 or level_db)",
    "knob": "knob fraction 0..1 (sustain_db = 40*log10(knob))",
    "normalized_hz": "0..1, hz = 8.1846 * 2698.7**v (two-point fit)",
    "percent": "0..100 (or -100..100)",
    "count": "integer",
    "toggle": "0.0 or 1.0",
    "enum": "string enum",
    "string": "string",
    "hz": "hertz",
    "table_pos": "0..256 span of the wavetable",
    "float": "raw float",
}

# parameter-map.json scale vocabulary -> builder unit names.
_UNIT_BY_SCALE = {
    "linear_gain": "gain", "normalized": "float", "percent": "percent", "seconds": "seconds", "hz": "hz",
    "semitones": "count", "cents": "float", "count": "count", "octaves": "count", "degrees": "float",
    "enum": "enum", "flag": "toggle", "filter_cutoff_normalized": "normalized_hz",
    "table_position_256": "table_pos", "lfo_rate": "float", "midi_note": "count", "ratio": "float",
}


# ---------------------------------------------------------------------------- schema

@dataclass(frozen=True)
class ParameterSpec:
    key: str
    unit: str
    verified: bool = False
    range: tuple[float, float] | None = None
    choices: tuple[str, ...] = ()
    notes: str = ""


# Friendly name -> (state path, unit, range, verified-by-readback). Ranges are Serum's control
# limits where known; the parameter map's observed extremes are statistics, not limits.
_OSC_ALIASES: dict[str, tuple[str, str, tuple[float, float] | None]] = {
    "enable": ("plainParams/kParamEnable", "toggle", None),
    "volume": ("plainParams/kParamVolume", "gain", None),
    "unison": ("plainParams/kParamUnison", "count", (1, 16)),
    "detune": ("plainParams/kParamDetune", "float", (0, 1)),
    "blend": ("plainParams/kParamDetuneWid", "percent", (0, 100)),
    "width": ("plainParams/kParamUnisonStereo", "percent", (-100, 100)),
    "octave": ("plainParams/kParamOctave", "count", (-4, 4)),
    "semi": ("plainParams/kParamPitch", "count", (-12, 12)),
    "fine": ("plainParams/kParamFine", "float", (-100, 100)),
    "pan": ("plainParams/kParamPan", "percent", (-50, 50)),
}
_VERIFIED_OSC0 = {"enable", "volume", "unison", "detune", "blend", "octave"}


def _builtin() -> dict[str, ParameterSpec]:
    specs: dict[str, ParameterSpec] = {}
    for n in (0, 1, 2):
        for name, (suffix, unit, rng) in _OSC_ALIASES.items():
            verified = n == 0 and name in _VERIFIED_OSC0
            specs[f"Oscillator{n}.{name}"] = ParameterSpec(f"Oscillator{n}/{suffix}", unit, verified, rng)
        specs[f"Oscillator{n}.table"] = ParameterSpec(f"Oscillator{n}/WTOsc{n}/relativePathToWT", "string", n == 0)
        specs[f"Oscillator{n}.frame"] = ParameterSpec(f"Oscillator{n}/WTOsc{n}/plainParams/kParamTablePos",
                                                     "table_pos", n == 0, (0, 256))
    specs["Oscillator3.enable"] = ParameterSpec("Oscillator3/plainParams/kParamEnable", "toggle", False)
    specs["Oscillator3.volume"] = ParameterSpec("Oscillator3/plainParams/kParamVolume", "gain", False)
    specs["Oscillator4.enable"] = ParameterSpec("Oscillator4/plainParams/kParamEnable", "toggle", True)
    specs["Oscillator4.volume"] = ParameterSpec("Oscillator4/plainParams/kParamVolume", "gain", True)
    specs["Oscillator4.octave"] = ParameterSpec("Oscillator4/plainParams/kParamOctave", "count", True, (-4, 4))
    specs["Oscillator4.shape"] = ParameterSpec("Oscillator4/SubOsc4/plainParams/kParamShape", "enum", True,
                                               choices=tuple(SUB_SHAPES))
    for n in (0, 1):
        p = f"VoiceFilter{n}/plainParams"
        v = n == 0
        specs[f"VoiceFilter{n}.enable"] = ParameterSpec(f"{p}/kParamEnable", "toggle", v)
        specs[f"VoiceFilter{n}.cutoff"] = ParameterSpec(f"{p}/kParamFreq", "normalized_hz", v, (0, 1))
        specs[f"VoiceFilter{n}.resonance"] = ParameterSpec(f"{p}/kParamReso", "percent", v, (0, 100))
        specs[f"VoiceFilter{n}.drive"] = ParameterSpec(f"{p}/kParamDrive", "percent", v, (0, 100))
        specs[f"VoiceFilter{n}.type"] = ParameterSpec(f"{p}/kParamType", "enum", v, choices=("lp12",))
    for n in range(4):
        p = f"Env{n}/plainParams"
        specs[f"Env{n}.attack"] = ParameterSpec(f"{p}/kParamAttack", "seconds", True)
        specs[f"Env{n}.hold"] = ParameterSpec(f"{p}/kParamHold", "seconds", False)
        specs[f"Env{n}.decay"] = ParameterSpec(f"{p}/kParamDecay", "seconds", True)
        specs[f"Env{n}.sustain"] = ParameterSpec(f"{p}/kParamSustain", "knob", True, (0, 1))
        specs[f"Env{n}.release"] = ParameterSpec(f"{p}/kParamRelease", "seconds", True)
    for n in range(10):
        specs[f"LFO{n}.rate"] = ParameterSpec(f"LFO{n}/plainParams/kParamRate", "float", False,
                                              notes="Hz in free mode; division index when beat-synced (unresolved)")
        specs[f"LFO{n}.beat_sync"] = ParameterSpec(f"LFO{n}/plainParams/kParamBeatSync", "toggle", False)
    for n in range(8):
        specs[f"Macro{n}.value"] = ParameterSpec(f"Macro{n}/plainParams/kParamValue", "percent", False, (0, 100))
    g = "Global0/plainParams"
    specs["Global0.master"] = ParameterSpec(f"{g}/kParamMasterVolume", "gain", True)
    specs["Global0.mono"] = ParameterSpec(f"{g}/kParamMonoToggle", "toggle", True)
    specs["Global0.legato"] = ParameterSpec(f"{g}/kParamLegato", "toggle", False)
    specs["Global0.porta_always"] = ParameterSpec(f"{g}/kParamPortaAlways", "toggle", True)
    specs["Global0.porta_time"] = ParameterSpec(f"{g}/kParamPortamentoTime", "seconds", True)
    specs["Global0.transpose"] = ParameterSpec(f"{g}/kParamTranspose", "count", False, (-48, 48))
    specs["Global0.bend_up"] = ParameterSpec(f"{g}/kParamBendRangeUp", "count", False, (0, 48))
    specs["Global0.bend_down"] = ParameterSpec(f"{g}/kParamBendRangeDn", "count", False, (-48, 0))
    specs["Global0.voice_amp"] = ParameterSpec(f"{g}/kParamVoiceAmp", "float", False,
                                               notes="per-voice amplitude; the usual velocity destination")
    return specs


SUB_SHAPES: dict[str, str] = {
    "sine": "kSine", "roundrect": "kRoundRect", "rounded": "kRoundRect", "triangle": "kTriangle",
    "saw": "kSaw", "square": "kSquare", "pulse": "kPulse",
}
SUB_SHAPE_DEFAULT = "kSine"

# Short filter names -> Serum kParamType values (only offered when filters.json lists the value).
FILTER_ALIASES: dict[str, str] = {
    "lp6": "MgL6", "lp12": "MgL12", "lp18": "MgL18", "lp24": "MgL24",
    "clean_lp6": "L6", "clean_lp12": "L12", "clean_lp18": "L18", "clean_lp24": "L24",
    "hp6": "H6", "hp12": "H12", "hp18": "H18", "hp24": "H24",
    "bp12": "B12", "bp24": "B24", "notch12": "N12", "notch24": "N24",
    "ladder": "LadderMg", "acid": "LadderAcid", "dirty": "DirtyMg", "comb": "CombP",
    "formant": "FormantONE", "vowel": "FormantTWO", "diffuser": "Diffuser",
}
FILTER_DEFAULT = "MgL12"

# ``patch.fx.<name>`` -> effect section in fx-schema.json.
FX_ALIASES: dict[str, str] = {
    "distortion": "FXDistortion", "flanger": "FXFlanger", "phaser": "FXPhaser", "chorus": "FXChorus",
    "delay": "FXDelay", "compressor": "FXComp", "comp": "FXComp", "reverb": "FXReverb", "eq": "FXEQ",
    "filter": "FXFilter", "hyper": "FXHyperD", "dimension": "FXHyperD", "bode": "FXBode",
    "convolve": "FXConv", "utility": "FXUtils",
}
# Friendly effect kwargs -> state keys (a tuple writes several keys). Suffixes drive conversions:
# ``*_ms`` -> the key's time unit, ``*_hz`` -> Hz or normalised depending on the key's observed range.
_FX_KWARGS: dict[str, tuple[str, ...]] = {
    "mix": ("kParamWet",), "level": ("kParamLevelOut",), "type": ("kParamType",), "mode": ("kParamMode",),
    "size": ("kParamSize",), "predelay_ms": ("kParamPreDelay", "kParamPredelay"), "width": ("kParamWidth",),
    "feedback": ("kParamFeedback",), "rate": ("kParamRate",), "depth": ("kParamDepth",), "drive": ("kParamDrive",),
    "time_ms": ("kParamTimeL", "kParamTimeR"), "time_l_ms": ("kParamTimeL",), "time_r_ms": ("kParamTimeR",),
    "cutoff_hz": ("kParamFreq",), "freq_hz": ("kParamFreq",), "resonance": ("kParamReso",),
    "attack_ms": ("kParamAttack",), "release_ms": ("kParamRelease",), "ratio": ("kParamRatio",),
    "threshold": ("kParamThresh",), "makeup": ("kParamMakeup",), "unison": ("kParamUnison",),
    "detune": ("kParamDetune",), "damping": ("kParamDamping",), "tone": ("kParamTone",), "decay_s": ("kParamDecay",),
    "beat_sync": ("kParamBeatSync",), "stages": ("kParamNumStages",), "poles": ("kParamNumPoles",),
    "shift": ("kParamShift",), "balance": ("kParamBalance",), "hpf_hz": ("kParamHPF",), "lpf_hz": ("kParamLPF",),
    "delay_ms": ("kParamDelay",), "low_cut_hz": ("kParamFreq",), "high_cut": ("kParamLPHP",),
}
# Effect keys stored in seconds (friendly ``*_ms`` kwargs divide by 1000); every other time key is ms.
_FX_SECONDS_KEYS = {
    ("FXReverb", "kParamPreDelay"), ("FXDelay", "kParamTimeL"), ("FXDelay", "kParamTimeR"),
    ("FXConv", "kParamPredelay"), ("FXConv", "kParamAttack"), ("FXBode", "kParamDelayTime"),
}

BUILTIN_PARAMETERS: dict[str, ParameterSpec] = _builtin()

_DATA_FILES = ("parameter-map.json", "wavetables.json", "fx-schema.json", "filters.json", "mod-matrix.json")


@dataclass(frozen=True)
class WavetableInfo:
    relative_path: str
    num_frames: int
    frames_by_label: dict[str, tuple[int, ...]]

    def position(self, frame: int) -> float:
        if not 1 <= frame <= self.num_frames:
            raise PatchError(f"{self.relative_path} has frames 1..{self.num_frames}, not {frame}.")
        return 256.0 * frame / self.num_frames

    def frame_for(self, label: str) -> int:
        frames = self.frames_by_label.get(label.casefold())
        if not frames:
            raise PatchError(f"{self.relative_path} has no {label!r} frame; labels: "
                             f"{', '.join(sorted(k for k, v in self.frames_by_label.items() if v))}")
        return frames[0]


@dataclass(frozen=True)
class EffectInfo:
    section: str
    type: int
    label: str
    parameters: dict[str, dict[str, Any]]


@dataclass(frozen=True)
class ModSourceInfo:
    id: int
    name: str
    confidence: str


def _mod_source_key(name: str) -> str:
    return re.sub(r"[\s_\-]+", "", name).casefold()


@dataclass
class Schema:
    """Parameter, wavetable, filter and effect knowledge the builder converts musical requests with."""

    parameters: dict[str, ParameterSpec] = field(default_factory=lambda: dict(BUILTIN_PARAMETERS))
    rows: dict[str, dict[str, Any]] = field(default_factory=dict)
    enums: dict[str, dict[str, Any]] = field(default_factory=dict)
    wavetables: dict[str, WavetableInfo] = field(default_factory=dict)
    effects: dict[str, EffectInfo] = field(default_factory=dict)
    filters: dict[str, str] = field(default_factory=dict)
    sources: dict[str, str] = field(default_factory=dict)
    mod_sources: dict[str, ModSourceInfo] = field(default_factory=dict)
    mod_destinations: dict[tuple[str, str], int] = field(default_factory=dict)

    def spec(self, name: str) -> ParameterSpec:
        try:
            return self.parameters[name]
        except KeyError:
            section = name.split(".")[0]
            known = ", ".join(sorted(k for k in self.parameters if k.startswith(section + ".")))
            raise PatchError(f"No parameter {name!r} in the schema (missing data/parameter-map.json?). "
                             f"Known for {section}: {known or 'nothing'}") from None

    def row(self, path: str) -> dict[str, Any] | None:
        return self.rows.get(_normalise(path))

    def wavetable(self, relative_path: str) -> WavetableInfo | None:
        return self.wavetables.get(_table_key(relative_path))

    def filter_type(self, name: str) -> str:
        """Resolve ``lp24`` / ``MG Low 24`` / ``MgL24`` to the state value; the default returns ``MgL12``."""
        wanted = name.strip().casefold()
        if wanted in self.filters:
            return self.filters[wanted]
        if not self.filters and wanted in ("lp12", FILTER_DEFAULT.casefold(), "mg low 12"):
            return FILTER_DEFAULT
        offered = sorted({*FILTER_ALIASES} & {k for k in self.filters}) or ["lp12"]
        raise PatchError(f"Unknown filter type {name!r}. Short names: {', '.join(offered)}; "
                         f"Serum values and FL display names from data/filters.json are also accepted.")

    def mod_source(self, name: str | int) -> ModSourceInfo:
        """Resolve a modulation source (``"velocity"``, ``"lfo 2"``, ``"macro1"``, ``"env 1"`` or a raw id)."""
        if not isinstance(name, str):
            if isinstance(name, bool):
                raise PatchError("Modulation sources are names or integer ids, not booleans.")
            known = next((s for s in self.mod_sources.values() if s.id == name), None)
            return known or ModSourceInfo(name, f"source {name}", "unknown")
        if not self.mod_sources:
            raise NotImplementedError(
                f"Modulation source {name!r} needs data/mod-matrix.json ({self.sources.get('mod-matrix.json')}); "
                "pass a numeric source id instead.")
        key = _mod_source_key(name)
        if key in self.mod_sources:
            return self.mod_sources[key]
        offered = sorted({s.name for s in self.mod_sources.values() if s.confidence != "unknown"})
        raise PatchError(f"Unknown modulation source {name!r}. Known: {', '.join(offered)}; or a numeric id.")

    def mod_destination(self, section_type: str, param: str) -> int:
        """``destModuleParamID`` for a ``(destModuleTypeString, kParam name)`` pair from mod-matrix.json."""
        try:
            return self.mod_destinations[(section_type, param)]
        except KeyError:
            known = ", ".join(sorted(p for t, p in self.mod_destinations if t == section_type))
            raise PatchError(f"mod-matrix.json has no destination id for {section_type}/{param} "
                             f"({self.sources.get('mod-matrix.json')}). Known for {section_type}: {known or 'nothing'}") from None

    def effect(self, name: str) -> EffectInfo:
        if not self.effects:
            raise NotImplementedError(
                f"Serum effect {name!r} needs data/fx-schema.json, which is not available "
                f"({self.sources.get('fx-schema.json')}). Use a mixer effect (e.g. Pro-R 2 on the channel's mixer "
                "track) or patch.set(...) with a known FXRack key.")
        key = name.casefold()
        section = FX_ALIASES.get(key, name)
        for candidate in (section, f"FX{name}", name):
            for effect in self.effects.values():
                if effect.section.casefold() == candidate.casefold() or effect.label.casefold() == key:
                    return effect
        raise PatchError(f"Unknown effect {name!r}. fx-schema.json defines: "
                         f"{', '.join(sorted(k for k, v in FX_ALIASES.items() if any(e.section == v for e in self.effects.values())))}")


def _normalise(path: str) -> str:
    return "/".join(re.sub(r"\d+$", "{n}", part) if part != "plainParams" else part for part in path.split("/"))


def _table_key(relative_path: str) -> str:
    return relative_path.lstrip("/").replace("\\", "/").casefold()


def _read_json(directory: Path | None, name: str) -> Any:
    if directory is not None:
        path = directory / name
        return json.loads(path.read_text(encoding="utf-8")) if path.is_file() else None
    try:
        resource = resources.files("fruitylink_serum").joinpath("data").joinpath(name)
        if resource.is_file():
            return json.loads(resource.read_text(encoding="utf-8"))
    except (FileNotFoundError, ModuleNotFoundError, OSError):
        return None
    return None


def _load_parameter_map(schema: Schema, data: Any) -> None:
    rows = data.get("parameters") if isinstance(data, dict) else None
    if not isinstance(rows, list):
        return
    for raw in rows:
        if isinstance(raw, dict) and isinstance(raw.get("path"), str):
            schema.rows[raw["path"]] = dict(raw)
            values = raw.get("values")
            if isinstance(values, list) and values:
                schema.enums[raw["path"]] = {str(v): v for v in values}
    # The map is the source of truth for existence: keys it has never observed stay flagged.
    for name, spec in list(schema.parameters.items()):
        row = schema.rows.get(_normalise(spec.key))
        if row is None:
            if not spec.verified:
                schema.parameters[name] = replace(spec, notes=(spec.notes + " " if spec.notes else "")
                                                  + "not observed in the local library")
            continue
        unit = spec.unit
        if unit == "float" and isinstance(row.get("scale"), str):
            unit = _UNIT_BY_SCALE.get(row["scale"], unit)
        verified = spec.verified or row.get("confidence") == "verified"
        choices = spec.choices
        if spec.unit == "enum" and not spec.key.endswith("kParamType"):
            values = row.get("values")
            if isinstance(values, list):
                choices = tuple(str(v) for v in values)
        schema.parameters[name] = replace(spec, unit=unit, verified=verified, choices=choices,
                                          notes=str(row.get("confidence", spec.notes)))
    schema.sources["parameter-map.json"] = "loaded"


def _load_wavetables(schema: Schema, data: Any) -> None:
    tables = data.get("tables") if isinstance(data, dict) else None
    if not isinstance(tables, list):
        return
    for raw in tables:
        if not isinstance(raw, dict) or not isinstance(raw.get("relative_path"), str):
            continue
        labels = raw.get("frames_by_label") or {}
        info = WavetableInfo(
            relative_path=str(raw["relative_path"]), num_frames=int(raw.get("num_frames", 0)),
            frames_by_label={str(k).casefold(): tuple(int(f) for f in v) for k, v in labels.items() if isinstance(v, list)},
        )
        if info.num_frames > 0:
            schema.wavetables[_table_key(info.relative_path)] = info
    schema.sources["wavetables.json"] = "loaded"


def _load_fx(schema: Schema, data: Any) -> None:
    effects = data.get("effects") if isinstance(data, dict) else None
    if not isinstance(effects, dict):
        return
    for section, raw in effects.items():
        if not isinstance(raw, dict) or not isinstance(raw.get("type"), int):
            continue
        params = {str(p["key"]): dict(p) for p in raw.get("parameters", []) if isinstance(p, dict) and "key" in p}
        schema.effects[section] = EffectInfo(section=section, type=int(raw["type"]), label=str(raw.get("label", section)),
                                             parameters=params)
    schema.sources["fx-schema.json"] = "loaded"


def _load_filters(schema: Schema, data: Any) -> None:
    types = data.get("types") if isinstance(data, dict) else None
    if not isinstance(types, list):
        return
    values: set[str] = set()
    for raw in types:
        if isinstance(raw, dict) and isinstance(raw.get("state_value"), str):
            value = raw["state_value"]
            values.add(value)
            schema.filters[value.casefold()] = value
            guess = raw.get("fl_display_guess")
            if isinstance(guess, str):
                schema.filters[guess.casefold()] = value
    for alias, value in FILTER_ALIASES.items():
        if value in values:
            schema.filters[alias] = value
    for n in (0, 1):
        name = f"VoiceFilter{n}.type"
        if name in schema.parameters:
            schema.parameters[name] = replace(schema.parameters[name], choices=tuple(sorted(values)))
    schema.sources["filters.json"] = "loaded"


def _load_mod_matrix(schema: Schema, data: Any) -> None:
    if not isinstance(data, dict):
        return
    for raw in data.get("sources", []):
        if not isinstance(raw, dict) or not isinstance(raw.get("id"), int) or not isinstance(raw.get("name"), str):
            continue
        info = ModSourceInfo(int(raw["id"]), str(raw["name"]), str(raw.get("confidence", "unknown")))
        if info.confidence == "unknown":
            continue
        schema.mod_sources[_mod_source_key(info.name)] = info
    for raw in data.get("destinations", []):
        if isinstance(raw, dict) and isinstance(raw.get("type"), str) and isinstance(raw.get("param"), str) \
                and isinstance(raw.get("param_id"), int):
            schema.mod_destinations[(str(raw["type"]), str(raw["param"]))] = int(raw["param_id"])
    for alias, canonical in _MOD_SOURCE_ALIASES.items():
        target = schema.mod_sources.get(_mod_source_key(canonical))
        if target is not None:
            schema.mod_sources.setdefault(_mod_source_key(alias), target)
    schema.sources["mod-matrix.json"] = "loaded"


_MOD_SOURCE_ALIASES = {
    "vel": "Velocity", "key": "Note", "keytrack": "Note", "modwheel": "Mod Wheel", "wheel": "Mod Wheel",
    "env2": "Env 2 (probable)", "env3": "Env 3 (probable)", "env4": "Env 4 (probable)",
}
MOD_SLOT_COUNT = 64


def load_schema(directory: str | os.PathLike[str] | None = None) -> Schema:
    """Load the packaged schema data (or the JSON files in ``directory``) on top of the built-in map.

    The file formats are those generated by ``tools/schema/generate_data.py`` and described in
    ``docs/state-schema.md``: ``parameter-map.json`` (``parameters`` = list of rows with ``path`` in
    ``Section{n}/plainParams/kParam`` form, ``scale``, ``confidence``, ``values``), ``wavetables.json``
    (``tables`` = list with ``relative_path``, ``num_frames``, ``frames_by_label``), ``fx-schema.json``
    (``effects`` = section -> ``type``, ``label``, ``parameters``) and ``filters.json`` (``types`` = list
    of ``state_value`` / ``fl_display_guess``) and ``mod-matrix.json`` (``sources`` = id/name/confidence rows,
    ``destinations`` = ``type``/``param``/``param_id`` rows for ``ModSlot`` entries).
    """
    schema = Schema()
    base = Path(directory) if directory is not None else None
    _load_parameter_map(schema, _read_json(base, "parameter-map.json"))
    _load_wavetables(schema, _read_json(base, "wavetables.json"))
    _load_fx(schema, _read_json(base, "fx-schema.json"))
    _load_filters(schema, _read_json(base, "filters.json"))
    _load_mod_matrix(schema, _read_json(base, "mod-matrix.json"))
    for name in _DATA_FILES:
        schema.sources.setdefault(name, "missing (built-in fallback)")
    return schema


_DEFAULT_SCHEMA: Schema | None = None


def default_schema() -> Schema:
    global _DEFAULT_SCHEMA
    if _DEFAULT_SCHEMA is None:
        _DEFAULT_SCHEMA = load_schema()
    return _DEFAULT_SCHEMA


# ---------------------------------------------------------------------------- init base

DEFAULT_TABLE = "S2 Tables/Default Shapes.wav"

_INIT_SCALARS: dict[str, Any] = {
    "lockOversampling": False, "lockTuning": False, "mpeConfig": 0, "mpeEnabled": False, "mpePitchBendRange": 48,
    "url": "https://xferrecords.com/", "vendor": "Xfer Records",
}


def init_container(name: str = "Init") -> xfer.XferContainer:
    """An empty Serum 2 state: Serum omits every parameter at its default, so an init patch is the
    processor identity plus the top-level scalars a saved state carries and no sections at all."""
    metadata = {"fileType": "SerumPreset", "presetName": name, "product": "Serum2",
                "productVersion": loading.PROCESSOR_IDENTITY["productVersion"], "vendor": "Xfer Records",
                "version": loading.PROCESSOR_IDENTITY["version"]}
    state: dict[str, Any] = dict(_INIT_SCALARS)
    state.update({"product": "Serum2", "productVersion": metadata["productVersion"], "version": metadata["version"],
                  "presetName": name})
    return xfer.XferContainer(metadata=metadata, state=state)


# ---------------------------------------------------------------------------- builder

def _set_path(state: dict[str, Any], key: str, value: Any) -> None:
    parts = [p for p in key.split("/") if p]
    node: dict[str, Any] = state
    for part in parts[:-1]:
        child = node.get(part)
        if not isinstance(child, dict):
            child = {}
            node[part] = child
        node = child
    node[parts[-1]] = value


def _del_path(state: dict[str, Any], key: str) -> None:
    parts = [p for p in key.split("/") if p]
    node: Any = state
    for part in parts[:-1]:
        if not isinstance(node, dict) or part not in node:
            return
        node = node[part]
    if isinstance(node, dict):
        node.pop(parts[-1], None)


def _get_path(state: Mapping[str, Any], key: str) -> Any:
    node: Any = state
    for part in [p for p in key.split("/") if p]:
        if not isinstance(node, Mapping) or part not in node:
            return None
        node = node[part]
    return node


def _stored(value: Any) -> Any:
    return cbor.Float32(value) if isinstance(value, float) and not isinstance(value, cbor.Float32) else value


class _Effects:
    """``patch.fx.<effect>(**params)`` driven by ``fx-schema.json``."""

    def __init__(self, patch: SerumPatch) -> None:
        self._patch = patch

    def __getattr__(self, effect: str) -> Any:
        if effect.startswith("_"):
            raise AttributeError(effect)
        assert self._patch.schema is not None
        info = self._patch.schema.effect(effect)

        def apply(*, rack: int = 0, **params: Any) -> SerumPatch:
            return self._patch._apply_effect(info, params, rack)

        return apply


@dataclass
class SerumPatch:
    """Fluent Serum 2 patch description. Methods return ``self``; nothing touches FL until ``load``."""

    name: str = "Generated"
    base: str | os.PathLike[str] | xfer.XferContainer | None = None
    schema: Schema | None = None
    root: Path | str | None = None
    fx: _Effects = field(init=False, repr=False)
    _container: xfer.XferContainer = field(init=False, repr=False)
    _changes: dict[str, Any] = field(init=False, default_factory=dict, repr=False)

    def __post_init__(self) -> None:
        if self.schema is None:
            self.schema = default_schema()
        if self.base is None:
            self._container = init_container(self.name)
        elif isinstance(self.base, xfer.XferContainer):
            self._container = replace(self.base, state=cbor.decode(cbor.encode(self.base.state)))
        else:
            self._container = loading.read_preset(loading.resolve_preset(self.base, self.root))
        self._container.metadata["presetName"] = self.name
        if "presetName" in self._container.state:
            self._container.state["presetName"] = self.name
        self.fx = _Effects(self)

    # ---- generic writes ----

    def set(self, key: str, value: Any) -> SerumPatch:
        """Escape hatch: write a ``Section/plainParams/kParam`` (or any nested) state path verbatim."""
        _set_path(self._container.state, key, _stored(value))
        self._changes[key] = value
        return self

    def unset(self, key: str) -> SerumPatch:
        """Remove a key so Serum uses its default."""
        _del_path(self._container.state, key)
        self._changes[key] = None
        return self

    def get(self, key: str) -> Any:
        return _get_path(self._container.state, key)

    def _write(self, name: str, value: Any) -> None:
        assert self.schema is not None
        spec = self.schema.spec(name)
        if spec.range is not None and isinstance(value, (int, float)) and not spec.range[0] <= value <= spec.range[1]:
            raise PatchError(f"{name} = {value} is outside {spec.range}.")
        if spec.unit == "enum":
            if not isinstance(value, str):
                raise PatchError(f"{name} takes a name, not {value!r}.")
            if spec.key.endswith("kParamShape"):
                match = SUB_SHAPES.get(value.casefold()) or next(
                    (v for v in SUB_SHAPES.values() if v.casefold() == value.casefold()), None)
                if match is None:
                    raise PatchError(f"{name} must be one of: {', '.join(sorted(SUB_SHAPES))}.")
                if match == SUB_SHAPE_DEFAULT:
                    self.unset(spec.key)
                    return
                value = match
            elif spec.key.endswith("kParamType"):
                value = self.schema.filter_type(value)
                if value == FILTER_DEFAULT:
                    self.unset(spec.key)
                    return
            else:
                mapping = self.schema.enums.get(_normalise(spec.key)) or {c: c for c in spec.choices}
                match = next((k for k in mapping if k.casefold() in (value.casefold(), f"k{value}".casefold())), None)
                if match is None:
                    raise PatchError(f"{name} must be one of: {', '.join(sorted(mapping))}.")
                value = mapping[match]
        if spec.unit == "count":
            value = float(int(value))
        self.set(spec.key, value)

    # ---- oscillators ----

    def _oscillator(self, section: str, *, shape: str | None, table: str | None, frame: float | None,
                    level: float | None, level_db: float | None, unison: int | None, detune: float | None,
                    blend: float | None, width: float | None, octave: int | None, semi: int | None,
                    fine: float | None, pan: float | None, enable: bool) -> SerumPatch:
        assert self.schema is not None
        self._write(f"{section}.enable", 1.0 if enable else 0.0)
        info = self.schema.wavetable(table or DEFAULT_TABLE) if (shape is not None or table is not None) else None
        if shape is not None:
            table = table or DEFAULT_TABLE
            if info is None:
                raise NotImplementedError(
                    f"Choosing oscillator shape {shape!r} needs data/wavetables.json with the frame map of "
                    f"{table!r} ({self.schema.sources.get('wavetables.json')}). Pass table=/frame= explicitly, or use "
                    ".sub(shape=...) whose shape enum is verified.")
            frame = info.frame_for(shape)
        if table is not None:
            self._write(f"{section}.table", table)
        if frame is not None:
            # With a known table, ``frame`` is the 1-based frame index; otherwise it is the raw kParamTablePos.
            position = info.position(int(frame)) if info is not None else float(frame)
            self._write(f"{section}.frame", position)
        if level is not None and level_db is not None:
            raise PatchError("Give level (knob 0..1) or level_db, not both.")
        if level is not None:
            self._write(f"{section}.volume", knob_to_gain(level))
        if level_db is not None:
            self._write(f"{section}.volume", db_to_gain(level_db))
        if unison is not None:
            self._write(f"{section}.unison", unison)
        if detune is not None:
            self._write(f"{section}.detune", float(detune))
        if blend is not None:
            self._write(f"{section}.blend", float(blend))
        if width is not None:
            self._write(f"{section}.width", float(width))
        if octave is not None:
            self._write(f"{section}.octave", octave)
        if semi is not None:
            self._write(f"{section}.semi", semi)
        if fine is not None:
            self._write(f"{section}.fine", float(fine))
        if pan is not None:
            self._write(f"{section}.pan", float(pan))
        return self

    def osc_a(self, shape: str | None = None, *, table: str | None = None, frame: float | None = None,
              level: float | None = None, level_db: float | None = None, unison: int | None = None,
              detune: float | None = None, blend: float | None = None, width: float | None = None,
              octave: int | None = None, semi: int | None = None, fine: float | None = None,
              pan: float | None = None, enable: bool = True) -> SerumPatch:
        """Oscillator A (``Oscillator0``). ``shape`` is a frame label of ``table`` (default
        ``Default Shapes``: sine/triangle/saw/square/pulse); ``frame`` a 1-based frame index of a known
        table; ``level`` the 0..1 knob FL displays as a percent; ``width`` the unison stereo percent."""
        return self._oscillator("Oscillator0", shape=shape, table=table, frame=frame, level=level, level_db=level_db,
                                unison=unison, detune=detune, blend=blend, width=width, octave=octave, semi=semi,
                                fine=fine, pan=pan, enable=enable)

    def osc_b(self, shape: str | None = None, *, table: str | None = None, frame: float | None = None,
              level: float | None = None, level_db: float | None = None, unison: int | None = None,
              detune: float | None = None, blend: float | None = None, width: float | None = None,
              octave: int | None = None, semi: int | None = None, fine: float | None = None,
              pan: float | None = None, enable: bool = True) -> SerumPatch:
        """Oscillator B (``Oscillator1``)."""
        return self._oscillator("Oscillator1", shape=shape, table=table, frame=frame, level=level, level_db=level_db,
                                unison=unison, detune=detune, blend=blend, width=width, octave=octave, semi=semi,
                                fine=fine, pan=pan, enable=enable)

    def osc_c(self, shape: str | None = None, *, table: str | None = None, frame: float | None = None,
              level: float | None = None, level_db: float | None = None, unison: int | None = None,
              detune: float | None = None, blend: float | None = None, width: float | None = None,
              octave: int | None = None, semi: int | None = None, fine: float | None = None,
              pan: float | None = None, enable: bool = True) -> SerumPatch:
        """Oscillator C (``Oscillator2``)."""
        return self._oscillator("Oscillator2", shape=shape, table=table, frame=frame, level=level, level_db=level_db,
                                unison=unison, detune=detune, blend=blend, width=width, octave=octave, semi=semi,
                                fine=fine, pan=pan, enable=enable)

    def sub(self, shape: str = "sine", *, octave: int = 0, level: float | None = None,
            level_db: float | None = None, enable: bool = True) -> SerumPatch:
        """Sub oscillator (``Oscillator4``): shape sine/roundrect/triangle/saw/square/pulse, octave, level."""
        self._write("Oscillator4.enable", 1.0 if enable else 0.0)
        self._write("Oscillator4.shape", shape)
        self._write("Oscillator4.octave", octave)
        if level is not None:
            self._write("Oscillator4.volume", knob_to_gain(level))
        if level_db is not None:
            self._write("Oscillator4.volume", db_to_gain(level_db))
        return self

    def noise(self, *, level: float | None = None, level_db: float | None = None, enable: bool = True) -> SerumPatch:
        """Noise oscillator (``Oscillator3``)."""
        self._write("Oscillator3.enable", 1.0 if enable else 0.0)
        if level is not None:
            self._write("Oscillator3.volume", knob_to_gain(level))
        if level_db is not None:
            self._write("Oscillator3.volume", db_to_gain(level_db))
        return self

    # ---- filter / envelopes / modulation ----

    def filter(self, type: str = "lp12", *, cutoff_hz: float | None = None, resonance: float | None = None,
               drive: float | None = None, index: int = 0, enable: bool = True) -> SerumPatch:
        """Voice filter ``index`` (0 or 1). ``type`` is a short name (``lp12`` .. ``lp24``, ``hp12``, ``bp12``,
        ``notch12``, ``comb``, ``formant`` ...), an FL display name (``MG Low 24``) or a Serum value (``MgL24``);
        ``cutoff_hz`` converts to Serum's normalised frequency; ``resonance``/``drive`` are percent."""
        section = f"VoiceFilter{index}"
        self._write(f"{section}.enable", 1.0 if enable else 0.0)
        self._write(f"{section}.type", type)
        if cutoff_hz is not None:
            self._write(f"{section}.cutoff", hz_to_normalized(cutoff_hz))
        if resonance is not None:
            self._write(f"{section}.resonance", float(resonance))
        if drive is not None:
            self._write(f"{section}.drive", float(drive))
        return self

    def env(self, index: int, *, attack_ms: float | None = None, hold_ms: float | None = None,
            decay_ms: float | None = None, sustain: float | None = None, sustain_db: float | None = None,
            release_ms: float | None = None) -> SerumPatch:
        """Envelope ``index`` (0 = amplitude). Times in milliseconds; sustain as knob 0..1 or dB."""
        section = f"Env{index}"
        if attack_ms is not None:
            self._write(f"{section}.attack", attack_ms / 1000.0)
        if hold_ms is not None:
            self._write(f"{section}.hold", hold_ms / 1000.0)
        if decay_ms is not None:
            self._write(f"{section}.decay", decay_ms / 1000.0)
        if sustain is not None and sustain_db is not None:
            raise PatchError("Give sustain (knob 0..1) or sustain_db, not both.")
        if sustain is not None:
            self._write(f"{section}.sustain", float(sustain))
        if sustain_db is not None:
            self._write(f"{section}.sustain", sustain_db_to_knob(sustain_db))
        if release_ms is not None:
            self._write(f"{section}.release", release_ms / 1000.0)
        return self

    def amp_env(self, *, attack_ms: float | None = None, hold_ms: float | None = None, decay_ms: float | None = None,
                sustain: float | None = None, sustain_db: float | None = None,
                release_ms: float | None = None) -> SerumPatch:
        return self.env(0, attack_ms=attack_ms, hold_ms=hold_ms, decay_ms=decay_ms, sustain=sustain,
                        sustain_db=sustain_db, release_ms=release_ms)

    def lfo(self, index: int, *, rate: float | None = None, beat_sync: bool | None = None) -> SerumPatch:
        """LFO ``index``: ``rate`` is written to ``kParamRate`` verbatim (Hz in free mode; the beat-synced
        division encoding is unresolved, so pass numbers only). Drawn shapes (``curveData``) are not built."""
        if rate is not None:
            if isinstance(rate, str):
                raise PatchError("LFO rate divisions such as '1/4' are not decoded yet; pass a number.")
            self._write(f"LFO{index}.rate", float(rate))
        if beat_sync is not None:
            self._write(f"LFO{index}.beat_sync", 1.0 if beat_sync else 0.0)
        return self

    def macro(self, index: int, value: float) -> SerumPatch:
        """Macro ``index`` (0..7) value in percent."""
        self._write(f"Macro{index}.value", float(value))
        return self

    def mono(self, enabled: bool = True, *, porta_ms: float | None = None, porta_always: bool | None = None,
             legato: bool | None = None) -> SerumPatch:
        self._write("Global0.mono", 1.0 if enabled else 0.0)
        if porta_ms is not None:
            self._write("Global0.porta_time", porta_ms / 1000.0)
        if porta_always is not None:
            self._write("Global0.porta_always", 1.0 if porta_always else 0.0)
        if legato is not None:
            self._write("Global0.legato", 1.0 if legato else 0.0)
        return self

    def transpose(self, semitones: int) -> SerumPatch:
        self._write("Global0.transpose", semitones)
        return self

    def master_volume(self, *, knob: float | None = None, db: float | None = None) -> SerumPatch:
        """Master level as the 0..1 knob or as the dB value FL displays (``20*log10(gain) + 3``)."""
        if (knob is None) == (db is None):
            raise PatchError("Give knob or db.")
        self._write("Global0.master", knob_to_gain(knob) if knob is not None else master_db_to_gain(db or 0.0))
        return self

    def bend_range(self, up_semitones: int | None = None, down_semitones: int | None = None) -> SerumPatch:
        """Pitch-bend range in semitones (``Global0 kParamBendRangeUp`` / ``kParamBendRangeDn``).

        Loading a preset replaces the whole plugin state, so a bend range set through FL's parameter
        list before a load is lost; bake it into the preset here instead. ``down_semitones`` may be
        given as a positive count (stored negative, as Serum writes it: ``-12.0`` for 12 semitones).
        """
        if up_semitones is None and down_semitones is None:
            raise PatchError("Give up_semitones and/or down_semitones.")
        if up_semitones is not None:
            self._write("Global0.bend_up", abs(int(up_semitones)))
        if down_semitones is not None:
            self._write("Global0.bend_down", -abs(int(down_semitones)))
        return self

    def modulate(self, source: str | int, target: str, amount: float, *, aux: str | int | None = None,
                 bipolar: bool | None = None, slot: int | None = None) -> SerumPatch:
        """Add one mod-matrix row: ``source`` (``"velocity"``, ``"lfo 1"``, ``"env 2"``, ``"macro 1"``, ``"note"``,
        ``"mod wheel"`` or a raw id) to ``target`` (a builder name such as ``VoiceFilter0.cutoff`` /
        ``Oscillator0.volume`` / ``Global0.voice_amp``, or a raw ``Section{n}/plainParams/kParam`` path) with
        ``amount`` in percent (-100..100). ``aux`` is the "via" source, ``slot`` the ``ModSlot`` index (default:
        the first empty row). Source ids and ``destModuleParamID`` values come from ``mod-matrix.json``; the
        source names are inferred (see ``docs/state-schema.md``) and the row is written exactly as factory
        presets store it."""
        assert self.schema is not None
        if not -100.0 <= float(amount) <= 100.0:
            raise PatchError("Modulation amount is a percent in -100..100.")
        main = self.schema.mod_source(source)
        via = self.schema.mod_source(aux) if aux is not None else None
        key = target if "/" in target else self.schema.spec(target).key
        parts = [p for p in key.split("/") if p]
        if len(parts) < 3 or parts[-2] != "plainParams" or parts[0].startswith("FXRack"):
            raise PatchError(f"Modulation targets must be Section{{n}}/plainParams/kParam paths, not {key!r} "
                             "(effect-unit destinations are not built).")
        module = parts[-3]
        match = re.fullmatch(r"([A-Za-z]+)(\d*)", module)
        if match is None:
            raise PatchError(f"Cannot derive a module type from {module!r}.")
        section_type, digits = match.group(1), match.group(2)
        param = parts[-1]
        param_id = self.schema.mod_destination(section_type, param)
        index = slot if slot is not None else self._free_mod_slot()
        if not 0 <= index < MOD_SLOT_COUNT:
            raise PatchError(f"slot must be 0..{MOD_SLOT_COUNT - 1}.")
        plain: dict[str, Any] = {"kParamAmount": cbor.Float32(float(amount))}
        if bipolar:
            plain["kParamBipolar"] = cbor.Float32(1.0)
        row: dict[str, Any] = {
            "source": [int(main.id), int(via.id) if via is not None else 0],
            "destModuleTypeString": section_type, "destModuleID": int(digits or 0),
            "destModuleParamName": param, "destModuleParamID": int(param_id),
            "plainParams": plain,
        }
        self._container.state[f"ModSlot{index}"] = row
        self._changes[f"ModSlot{index}"] = {"source": main.name, "aux": via.name if via else None, "target": key,
                                             "amount": float(amount), "confidence": main.confidence}
        return self

    def velocity(self, amount: float = 1.0, *, target: str = "amp", bipolar: bool | None = None,
                 slot: int | None = None) -> SerumPatch:
        """Make note velocity matter: a Velocity -> ``target`` mod row with ``amount`` 0..1 (100 % = full range).

        ``target``: ``"amp"`` (``Global0 kParamVoiceAmp``, the per-voice level factory presets use for
        velocity), ``"filter"`` / ``"filter2"`` (``VoiceFilter0/1 kParamFreq``), ``"osc_a"`` .. ``"osc_c"`` /
        ``"sub"`` / ``"noise"`` (that oscillator's level), or any builder target name. A patch without such a
        row plays every note at full level regardless of velocity. The Velocity source id (16) is inferred from
        library statistics, not yet live-verified; see ``docs/patch-builder.md``."""
        if not 0.0 <= float(amount) <= 1.0:
            raise PatchError("velocity amount is 0..1.")
        targets = {"amp": "Global0.voice_amp", "filter": "VoiceFilter0.cutoff", "filter2": "VoiceFilter1.cutoff",
                   "osc_a": "Oscillator0.volume", "osc_b": "Oscillator1.volume", "osc_c": "Oscillator2.volume",
                   "noise": "Oscillator3.volume", "sub": "Oscillator4.volume"}
        return self.modulate("velocity", targets.get(target, target), 100.0 * float(amount), bipolar=bipolar, slot=slot)

    def _free_mod_slot(self) -> int:
        for index in range(MOD_SLOT_COUNT):
            row = self._container.state.get(f"ModSlot{index}")
            if not isinstance(row, dict) or "source" not in row:
                return index
        raise PatchError("All 64 mod slots are in use.")

    # ---- effects ----

    def _fx_value(self, info: EffectInfo, key: str, kwarg: str, value: Any) -> Any:
        row = info.parameters.get(key)
        if row is None:
            raise PatchError(f"Effect {info.label} has no parameter {key!r} in fx-schema.json; known: "
                             f"{', '.join(sorted(info.parameters))}")
        if row.get("type") == "enum" or isinstance(row.get("values"), list):
            values = [str(v) for v in row.get("values", [])]
            if not isinstance(value, str):
                raise PatchError(f"{info.label} {kwarg} takes a name from: {', '.join(values)}")
            match = next((v for v in values if v.casefold() in (value.casefold(), f"k{value}".casefold())), None)
            if match is None:
                raise PatchError(f"{info.label} {kwarg}={value!r} is not one of: {', '.join(values)}")
            return match
        number = float(value)
        if kwarg.endswith("_ms"):
            return number / 1000.0 if (info.section, key) in _FX_SECONDS_KEYS else number
        if kwarg.endswith("_hz") and float(row.get("max") or 0.0) <= 1.0:
            return hz_to_normalized(number)
        if kwarg == "level":
            return knob_to_gain(number)
        if kwarg == "enable" or kwarg == "beat_sync":
            return 1.0 if value else 0.0
        return number

    def _apply_effect(self, info: EffectInfo, params: Mapping[str, Any], rack: int) -> SerumPatch:
        if rack not in (0, 1, 2):
            raise PatchError("rack must be 0 (main chain), 1 or 2 (FX buses).")
        section = f"FXRack{rack}"
        container = self._container.state.setdefault(section, {})
        if not isinstance(container, dict):
            raise PatchError(f"{section} in the base state is not a map.")
        units = container.setdefault("FX", [])
        if not isinstance(units, list):
            raise PatchError(f"{section}/FX in the base state is not a list.")
        plain: dict[str, Any] = {}
        for kwarg, value in params.items():
            keys = (kwarg,) if kwarg.startswith("kParam") else _FX_KWARGS.get(kwarg)
            if keys is None:
                raise PatchError(f"Unknown effect argument {kwarg!r}; friendly names: {', '.join(sorted(_FX_KWARGS))}, "
                                 "or any kParam* key from fx-schema.json.")
            for key in keys:
                if len(keys) > 1 and key not in info.parameters:
                    continue  # e.g. predelay_ms covers kParamPreDelay (reverb) and kParamPredelay (convolve)
                plain[key] = _stored(self._fx_value(info, key, kwarg, value))
        unit: dict[str, Any] = {"type": info.type, info.section: {"plainParams": plain}, "kUIParamMixOrGain": cbor.Float32(0.0)}
        units.append(copy.deepcopy(unit))
        self._changes[f"{section}/FX[{len(units) - 1}]"] = {"effect": info.label, **{k: v for k, v in plain.items()}}
        return self

    # ---- outputs ----

    def to_state(self) -> dict[str, Any]:
        """A deep copy of the current state map."""
        return dict(cbor.decode(cbor.encode(self._container.state)))

    def container(self) -> xfer.XferContainer:
        return replace(self._container, metadata=dict(self._container.metadata), state=self.to_state())

    def changes(self) -> dict[str, Any]:
        """State keys written by this builder (for logging and for ``diff_states`` checks)."""
        return dict(self._changes)

    def save(self, path: str | os.PathLike[str] | None = None) -> Path:
        """Write a ``.SerumPreset`` (temporary directory when ``path`` is omitted)."""
        target = Path(path) if path is not None else Path(tempfile.mkdtemp(prefix="fruitylink-serum-")) / (
            loading._safe_file_name(self.name) + ".SerumPreset")
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_bytes(xfer.write_container(self.container()))
        return target

    def to_vstpreset(self, path: str | os.PathLike[str] | None = None) -> Path:
        """Write the loadable ``.vstpreset`` (normalised processor state, verified class id)."""
        return loading.to_vstpreset(self.container(), path)

    def load(self, fl: Any, channel: int, *, slot: int | None = None) -> str:
        """Load into the Serum 2 instance already on ``channel`` (or mixer ``channel``/``slot``).
        Read parameter displays back in a separate request; same-request readbacks can lag."""
        return loading.load_preset(fl, channel, self.to_vstpreset(), slot=slot, format="direct")


# ---------------------------------------------------------------------------- describe

def _flatten_scalars(node: Any, prefix: str, out: dict[str, Any]) -> None:
    if isinstance(node, dict):
        for key, value in node.items():
            path = f"{prefix}/{key}" if prefix else str(key)
            if isinstance(value, dict):
                _flatten_scalars(value, path, out)
            elif isinstance(value, list):
                for index, item in enumerate(value):
                    _flatten_scalars(item, f"{path}[{index}]", out)
            elif isinstance(value, (str, bool)) or cbor.is_finite_number(value):
                out[path] = value


def flatten_state(state: Mapping[str, Any]) -> dict[str, Any]:
    """Every scalar in the state as ``Section/.../key`` -> value (numbers, strings, booleans)."""
    out: dict[str, Any] = {}
    _flatten_scalars(dict(state), "", out)
    return out


def _musical(unit: str, v: float) -> str | None:
    return {
        "seconds": f"{v * 1000:.1f} ms",
        "gain": f"{20 * math.log10(v):.1f} dB" if v > 0 else "-inf dB",
        "knob": f"{40 * math.log10(v):.1f} dB" if v > 0 else "-inf dB",
        "normalized_hz": f"{normalized_to_hz(v):.0f} Hz",
        "percent": f"{v:.0f} %",
        "count": str(int(v)),
        "toggle": "on" if v >= 0.5 else "off",
        "hz": f"{v:.0f} Hz",
    }.get(unit)


def describe_parameters(container: xfer.XferContainer, *, schema: Schema | None = None) -> dict[str, list[dict[str, Any]]]:
    """Group a state's parameters by section with friendly names, units and musical values.

    Each entry: ``{"key", "value", "name", "unit", "musical"}`` where ``musical`` is the value
    converted back (Hz, ms, dB ...) when the schema knows the unit, either through a builder
    name or through the parameter map's scale for that key.
    """
    schema = schema or default_schema()
    by_key = {spec.key: (name, spec) for name, spec in schema.parameters.items()}
    grouped: dict[str, list[dict[str, Any]]] = {}
    for key, value in flatten_state(container.state).items():
        if "/" not in key:
            continue
        section = key.split("/")[0]
        name, spec = by_key.get(key, (None, None))
        unit: str | None = spec.unit if spec is not None else None
        if unit is None:
            row = schema.row(re.sub(r"\[\d+\]", "", key))
            if row is not None and isinstance(row.get("scale"), str):
                unit = _UNIT_BY_SCALE.get(row["scale"])
        musical: Any = None
        if unit is not None and isinstance(value, (int, float)) and not isinstance(value, bool):
            musical = _musical(unit, float(value))
        grouped.setdefault(section, []).append({
            "key": key, "value": value, "name": name, "unit": _UNITS.get(unit, unit) if unit else None,
            "musical": musical,
        })
    return grouped
