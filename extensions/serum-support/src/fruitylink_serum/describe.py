"""Human-readable summaries of a Serum 2 state (live channel or preset file).

FL's parameter list shows ``A WT Pos`` as a frame number but never which wavetable is
loaded; the decoded state does carry it (``Oscillator{n}/WTOsc{n}/relativePathToWT`` plus
``tableDisplayName`` when Serum wrote one, or ``embeddedWTData`` for a custom table).
:func:`describe_state` / :func:`describe_preset` decode the state and report, per section,
the musical values the schema knows: oscillators (enabled, wavetable, frame label, level,
unison), sub shape, filters, envelopes, LFOs, FX rack order, mod-matrix rows, macros and
globals. :func:`format_description` renders the same dictionary as text.

Everything absent from the state is a Serum default; the summary says ``"default"`` rather
than inventing a value, except for the few defaults the schema records (oscillator A on,
other oscillators and filters off, filter wet 100 %, sub shape sine, filter type ``MgL12``,
effect units enabled) which are labelled ``(default)``.

:func:`signal_path_notes` flags the patterns that made FL automation or note velocity
inaudible on real projects: every oscillator direct level at 0, FX-bus/direct routing,
disabled or fully-dry voice filters, and the absence of any velocity modulation.
"""

from __future__ import annotations

import math
import os
import re
from collections.abc import Mapping
from pathlib import Path
from typing import Any

from . import builder, loading, vstpreset, xfer
from . import schema as schema_data
from .builder import Schema, default_schema, normalized_to_hz

__all__ = [
    "DEFAULT_OSCILLATOR_ENABLED",
    "FX_PROXY_NOTE",
    "VELOCITY_SOURCE_ID",
    "describe_container",
    "describe_preset",
    "describe_state",
    "explain_parameter",
    "explain_parameters",
    "format_description",
    "fx_slot_names",
    "signal_path_notes",
]

OSCILLATOR_NAMES = {0: "A", 1: "B", 2: "C", 3: "Noise", 4: "Sub"}
# Inferred from library statistics: kParamEnable is written as 1.0 when an oscillator is on (2074 of 2083
# occurrences), so absence means off, except for oscillator A, which factory presets leave without the key
# while it clearly sounds (unison, detune and level set). Filters follow the same rule (only 1.0 observed).
DEFAULT_OSCILLATOR_ENABLED = {0: True, 1: False, 2: False, 3: False, 4: False}
FRAME_LENGTH = 2048
VELOCITY_SOURCE_ID = 16  # inferred; see mod-matrix.json
_SUB_SHAPE_NAMES = {"kSine": "sine", "kRoundRect": "roundrect", "kTriangle": "triangle", "kSaw": "saw",
                    "kSquare": "square", "kPulse": "pulse"}
_ROUTING_DEFAULT = "default (filter, inferred)"


def _number(value: Any) -> float | None:
    return float(value) if isinstance(value, (int, float)) and not isinstance(value, bool) and math.isfinite(value) else None


def _plain(state: Mapping[str, Any], section: str) -> dict[str, Any]:
    """``state[section]["plainParams"]`` as a dict; a missing or non-mapping section/plainParams (Serum writes
    the string ``"default"`` for an untouched sub-section) is an empty dict, meaning every value is a default."""
    node = state.get(section)
    plain = node.get("plainParams") if isinstance(node, Mapping) else None
    return dict(plain) if isinstance(plain, Mapping) else {}


def _gain(value: float | None) -> dict[str, Any] | str:
    if value is None:
        return "default"
    return {"gain": value, "knob_percent": round(100.0 * math.sqrt(max(value, 0.0)), 1),
            "db": round(20.0 * math.log10(value), 1) if value > 0 else "-inf"}


def _ms(value: float | None) -> float | str:
    return "default" if value is None else round(value * 1000.0, 2)


def _wavetable(node: Mapping[str, Any], n: int, schema: Schema) -> dict[str, Any] | None:
    table = node.get(f"WTOsc{n}")
    if not isinstance(table, Mapping):
        return None
    path = table.get("relativePathToWT")
    position = _number(_plain(node, f"WTOsc{n}").get("kParamTablePos"))
    info = schema.wavetable(path) if isinstance(path, str) else None
    total = table.get("numFrames")
    num_frames = int(total) // FRAME_LENGTH if isinstance(total, int) and not isinstance(total, bool) and total >= FRAME_LENGTH else None
    if num_frames is None and info is not None:
        num_frames = info.num_frames
    frame = schema_data.frame_for_table_pos(position or 0.0, num_frames) if num_frames else None
    label = None
    if info is not None and frame is not None:
        label = next((name for name, frames in info.frames_by_label.items() if frame in frames), None)
    return {
        "path": path if isinstance(path, str) else None,
        "display_name": table.get("tableDisplayName") if isinstance(table.get("tableDisplayName"), str) else None,
        "embedded": "embeddedWTData" in table,
        "num_frames": num_frames,
        "position": position if position is not None else "default (0)",
        "frame": frame,
        "frame_label": label,
        "known_table": info is not None,
    }


def _routing(state: Mapping[str, Any], n: int) -> dict[str, Any]:
    plain = _plain(state, f"RoutingSlot{n}")
    dest = plain.get("kParamRoutingDest")
    return {
        "dest": dest if isinstance(dest, str) else _ROUTING_DEFAULT,
        "fx_bus1_level": _number(plain.get("kParamFXBus1Level")),
        "fx_bus2_level": _number(plain.get("kParamFXBus2Level")),
        "filter_balance": _number(plain.get("kParamFilterBalance")),
    }


def _oscillator(state: Mapping[str, Any], n: int, schema: Schema) -> dict[str, Any]:
    section = f"Oscillator{n}"
    node = state.get(section)
    node = node if isinstance(node, Mapping) else {}
    plain = _plain(state, section)
    enable = _number(plain.get("kParamEnable"))
    out: dict[str, Any] = {
        "name": OSCILLATOR_NAMES[n], "section": section,
        "enabled": enable >= 0.5 if enable is not None else DEFAULT_OSCILLATOR_ENABLED[n],
        "enabled_from": "state" if enable is not None else "default",
        "level": _gain(_number(plain.get("kParamVolume"))),
        "routing": _routing(state, n),
    }
    if n == 4:
        # Factory presets ("KY - Smart Future", "PD - Airy Chant") store SubOsc4 = {"plainParams": "default"}:
        # a string where a map is expected. Any non-mapping section or plainParams means "all defaults".
        value = _plain(node, "SubOsc4").get("kParamShape")
        out["shape"] = _SUB_SHAPE_NAMES.get(value, value) if isinstance(value, str) else "sine (default)"
        out["octave"] = _number(plain.get("kParamOctave"))
        return out
    if n == 3:
        out["type"] = "noise"
        return out
    kind = plain.get("kParamType")
    out["type"] = kind if isinstance(kind, str) else "kOsc_WT (default)"
    out["wavetable"] = _wavetable(node, n, schema)
    for key, name in (("kParamUnison", "unison"), ("kParamDetune", "detune"), ("kParamDetuneWid", "blend"),
                      ("kParamUnisonStereo", "width"), ("kParamOctave", "octave"), ("kParamPitch", "semi"),
                      ("kParamFine", "fine"), ("kParamPan", "pan"), ("kParamCoarsePit", "coarse")):
        value = _number(plain.get(key))
        if value is not None:
            out[name] = int(value) if name in ("unison", "octave", "semi") else value
    return out


def _filter(state: Mapping[str, Any], n: int, display_names: Mapping[str, str]) -> dict[str, Any]:
    plain = _plain(state, f"VoiceFilter{n}")
    enable = _number(plain.get("kParamEnable"))
    kind = plain.get("kParamType")
    cutoff = _number(plain.get("kParamFreq"))
    wet = _number(plain.get("kParamWet"))
    return {
        "section": f"VoiceFilter{n}",
        "enabled": enable >= 0.5 if enable is not None else False,
        "enabled_from": "state" if enable is not None else "default",
        "type": kind if isinstance(kind, str) else "MgL12 (default)",
        "type_display": display_names.get(kind if isinstance(kind, str) else "MgL12"),
        "cutoff_hz": round(normalized_to_hz(cutoff)) if cutoff is not None else "default",
        "cutoff_normalized": cutoff,
        "resonance": _number(plain.get("kParamReso")),
        "drive": _number(plain.get("kParamDrive")),
        "wet_percent": wet if wet is not None else "100 (default)",
    }


def _envelope(state: Mapping[str, Any], n: int) -> dict[str, Any]:
    plain = _plain(state, f"Env{n}")
    sustain = _number(plain.get("kParamSustain"))
    return {
        "section": f"Env{n}", "role": "amplitude" if n == 0 else "assignable",
        "attack_ms": _ms(_number(plain.get("kParamAttack"))), "hold_ms": _ms(_number(plain.get("kParamHold"))),
        "decay_ms": _ms(_number(plain.get("kParamDecay"))), "release_ms": _ms(_number(plain.get("kParamRelease"))),
        "sustain": sustain if sustain is not None else "default",
        "sustain_db": (round(40.0 * math.log10(sustain), 1) if sustain > 0 else "-inf") if sustain is not None else "default",
    }


def _lfo(state: Mapping[str, Any], n: int) -> dict[str, Any] | None:
    node = state.get(f"LFO{n}")
    if not isinstance(node, Mapping):
        return None
    plain = _plain(state, f"LFO{n}")
    sync = _number(plain.get("kParamBeatSync"))
    return {"section": f"LFO{n}", "rate": _number(plain.get("kParamRate")),
            "beat_sync": sync >= 0.5 if sync is not None else "default",
            "drawn_shape": "curveData" in node, "keys": sorted(k for k in plain)}


def _fx(state: Mapping[str, Any], schema: Schema) -> list[dict[str, Any]]:
    labels = {info.section: info.label for info in schema.effects.values()}
    racks = []
    for rack in range(3):
        node = state.get(f"FXRack{rack}")
        units = node.get("FX") if isinstance(node, Mapping) else None
        if not isinstance(units, list) or not units:
            continue
        rows = []
        for index, unit in enumerate(units):
            if not isinstance(unit, Mapping):
                continue
            section = next((k for k in unit if isinstance(k, str) and k.startswith("FX")), None)
            plain = _plain(unit, section) if section else {}
            enable = _number(plain.get("kParamEnable"))
            wet = _number(plain.get("kParamWet"))
            rows.append({
                "index": index, "type": unit.get("type"), "section": section, "label": labels.get(section or "", section),
                "enabled": enable >= 0.5 if enable is not None else True,
                "mix_percent": wet if wet is not None else "100 (default)",
                "params": {k: (float(v) if _number(v) is not None else v) for k, v in plain.items()},
            })
        racks.append({"rack": rack, "role": "main chain" if rack == 0 else f"FX bus {rack}", "units": rows})
    return racks


def _mod_slots(state: Mapping[str, Any], schema: Schema) -> list[dict[str, Any]]:
    rows = []
    for n in range(builder.MOD_SLOT_COUNT):
        slot = state.get(f"ModSlot{n}")
        if not isinstance(slot, Mapping) or not isinstance(slot.get("source"), list):
            continue
        source = slot["source"]
        main = schema.mod_source(int(source[0])) if source and isinstance(source[0], int) else None
        aux = schema.mod_source(int(source[1])) if len(source) > 1 and isinstance(source[1], int) and source[1] else None
        plain = slot.get("plainParams")
        plain = dict(plain) if isinstance(plain, Mapping) else {}
        rows.append({
            "slot": n,
            "source": {"id": main.id, "name": main.name, "confidence": main.confidence} if main else None,
            "aux": {"id": aux.id, "name": aux.name, "confidence": aux.confidence} if aux else None,
            "destination": f"{slot.get('destModuleTypeString')}{slot.get('destModuleID')}/{slot.get('destModuleParamName')}",
            "amount": _number(plain.get("kParamAmount")),
            "bipolar": _number(plain.get("kParamBipolar")) == 1.0,
            "bypassed": _number(plain.get("kParamBypass")) == 1.0,
        })
    return rows


def _macros(state: Mapping[str, Any]) -> list[dict[str, Any]]:
    rows = []
    for n in range(8):
        node = state.get(f"Macro{n}")
        if not isinstance(node, Mapping):
            continue
        name = node.get("name")
        rows.append({"index": n, "name": name if isinstance(name, str) else None,
                     "value": _number(_plain(state, f"Macro{n}").get("kParamValue"))})
    return rows


def _global(state: Mapping[str, Any]) -> dict[str, Any]:
    plain = _plain(state, "Global0")
    master = _number(plain.get("kParamMasterVolume"))
    porta = _number(plain.get("kParamPortamentoTime"))
    gain = _gain(master)
    if isinstance(gain, dict) and master:
        gain["fl_display_db"] = round(20.0 * math.log10(master) + 3.0, 1)  # FL shows the master +3 dB
    return {
        "master": gain,
        "mono": _number(plain.get("kParamMonoToggle")), "legato": _number(plain.get("kParamLegato")),
        "porta_ms": _ms(porta), "porta_always": _number(plain.get("kParamPortaAlways")),
        "bend_up_semitones": _number(plain.get("kParamBendRangeUp")), "bend_down_semitones": _number(plain.get("kParamBendRangeDn")),
        "transpose": _number(plain.get("kParamTranspose")),
        "fx_bus1_vol": _number(plain.get("kParamFXBus1Vol")), "fx_bus2_vol": _number(plain.get("kParamFXBus2Vol")),
        "voice_amp": _number(plain.get("kParamVoiceAmp")),
    }


def signal_path_notes(state: Mapping[str, Any], *, schema: Schema | None = None) -> list[str]:
    """Heuristic warnings about a state whose voice filter or velocity response may be out of the path.

    Rules (inferred from the library and from the Ember Tides v007/v016 incidents): all enabled
    oscillators A/B/C/Noise at direct level 0; FX-bus sends or non-filter routing on an enabled
    oscillator; no voice filter enabled; a filter with ``kParamWet`` 0; no mod row with the Velocity
    source. Every note is advisory.
    """
    schema = schema or default_schema()
    notes: list[str] = []
    enabled = [n for n in range(4) if _oscillator(state, n, schema)["enabled"]]
    levels = {n: _number(_plain(state, f"Oscillator{n}").get("kParamVolume")) for n in enabled}
    if enabled and all(levels[n] == 0.0 for n in enabled):
        sub = _oscillator(state, 4, schema)
        notes.append("all enabled oscillator direct levels (A/B/C/Noise) are 0: the sound comes from level "
                     "modulation" + (", the sub oscillator" if sub["enabled"] else "") + " or FX-bus sends, so the voice "
                     "filter carries little of it and FL automation of 'Filter 1 Freq' may be inaudible")
    for n in enabled + ([4] if _oscillator(state, 4, schema)["enabled"] else []):
        routing = _routing(state, n)
        name = OSCILLATOR_NAMES[n]
        if routing["dest"] not in (_ROUTING_DEFAULT, "kRoutingDestFilter"):
            notes.append(f"oscillator {name} routes to {routing['dest']} (not through the voice filters)")
        for bus, level in (("1", routing["fx_bus1_level"]), ("2", routing["fx_bus2_level"])):
            if level:
                notes.append(f"oscillator {name} sends {level:.0f} % to FX bus {bus}, bypassing the voice filters")
    globals_ = _plain(state, "Global0")
    if any(_number(globals_.get(k)) for k in ("kParamFXBus1Vol", "kParamFXBus2Vol")):
        notes.append("FX bus levels are set in Global0 (kParamFXBus1Vol/kParamFXBus2Vol): bus routing is active")
    display = {row["state_value"]: row.get("fl_display_guess") or row["state_value"] for row in schema_data.filter_types()}
    filters = [_filter(state, n, display) for n in (0, 1)]
    if not any(f["enabled"] for f in filters):
        notes.append("no voice filter is enabled (VoiceFilter kParamEnable absent/0), so filter cutoff automation does nothing")
    for f in filters:
        if f["enabled"] and f["wet_percent"] == 0.0:
            notes.append(f"{f['section']} wet is 0 % (bypassed); absent kParamWet would mean 100 %")
    if not any(row["source"] and row["source"]["id"] == VELOCITY_SOURCE_ID for row in _mod_slots(state, schema)):
        notes.append("no mod-matrix row uses Velocity (source id 16, inferred): note velocity will not change the level; "
                     "add SerumPatch.velocity() when velocity matters")
    return notes


def describe_container(container: xfer.XferContainer | Mapping[str, Any], *, schema: Schema | None = None) -> dict[str, Any]:
    """Structured summary of a decoded Serum 2 state (see the module docstring for the sections)."""
    schema = schema or default_schema()
    state = container.state if isinstance(container, xfer.XferContainer) else container
    metadata = container.metadata if isinstance(container, xfer.XferContainer) else {}
    display = {row["state_value"]: row.get("fl_display_guess") or row["state_value"] for row in schema_data.filter_types()}
    name = metadata.get("presetName") or state.get("presetName")
    return {
        "name": name if isinstance(name, str) else None,
        "product": {k: state.get(k) for k in ("product", "productVersion", "version", "component") if k in state},
        "oscillators": [_oscillator(state, n, schema) for n in range(3)],
        "noise": _oscillator(state, 3, schema),
        "sub": _oscillator(state, 4, schema),
        "filters": [_filter(state, n, display) for n in (0, 1)],
        "envelopes": [_envelope(state, n) for n in range(4) if f"Env{n}" in state or n == 0],
        "lfos": [row for row in (_lfo(state, n) for n in range(10)) if row is not None],
        "fx": _fx(state, schema),
        "mod_slots": _mod_slots(state, schema),
        "macros": _macros(state),
        "global": _global(state),
        "signal_path": signal_path_notes(state, schema=schema),
        "sections": sorted(k for k in state if isinstance(k, str) and isinstance(state[k], Mapping)),
        "defaults_note": "values marked 'default' are absent from the state (Serum omits parameters at their defaults); "
                         "oscillator/filter enable defaults and the Velocity source id are inferred, not live-verified",
    }


def describe_preset(path: str | os.PathLike[str], *, root: Path | str | None = None,
                    schema: Schema | None = None) -> dict[str, Any]:
    """Summary of a ``.SerumPreset`` (or the component state of a ``.vstpreset``) resolved like ``load_preset``."""
    resolved = loading.resolve_preset(path, root)
    if resolved.suffix.casefold() == ".vstpreset":
        preset = vstpreset.read_vstpreset(resolved.read_bytes())
        container = xfer.read_container(preset.component)
    else:
        container = loading.read_preset(resolved)
    description = describe_container(container, schema=schema)
    description["path"] = str(resolved)
    return description


def describe_state(fl: Any, channel: int, *, slot: int | None = None, schema: Schema | None = None) -> dict[str, Any]:
    """Summary of the Serum 2 instance on ``channel`` (or mixer ``channel``/``slot``) via :func:`loading.read_state`."""
    description = describe_container(loading.read_state(fl, channel, slot=slot), schema=schema)
    description["channel"] = channel
    if slot is not None:
        description["slot"] = slot
    return description


FX_PROXY_NOTE = (
    "FL's wrapper lists Serum 2's FX as 'FX Main Param 1..16', 'FX Bus1 Param 1..16' and 'FX Bus2 Param 1..16': "
    "these are Serum's host-automation proxy slots for each rack. Which unit parameter a slot drives is assigned "
    "in Serum's FX-rack UI (state key FXRack{n}/proxyParams) and every preset in the library stores proxyParams as "
    "null, so no name can be derived; the wrapper exposes no 'add effect' or effect-type parameter either. Set FX "
    "through the state instead: SerumPatch(...).fx.chorus(...) -> load_preset, or edit the rack in the GUI and "
    "read it back with describe_state."
)
_FX_PROXY = re.compile(r"^FX\s*(Main|Bus\s*1|Bus\s*2)\s*Param\s*(\d+)$", re.IGNORECASE)
_WT_POS = re.compile(r"^([ABC])\s*WT\s*Pos$", re.IGNORECASE)
_RACK_INDEX = {"main": 0, "bus1": 1, "bus2": 2}


def fx_slot_names(state: Mapping[str, Any], *, schema: Schema | None = None) -> dict[str, Any]:
    """What is knowable about the FX racks from a decoded state: per rack, the loaded units in processing
    order with their labels and the parameter keys they store, plus the note on why the wrapper's proxy
    parameters stay anonymous. ``units`` is empty for a rack without effects."""
    racks = _fx(state, schema or default_schema())
    by_rack = {rack["rack"]: rack for rack in racks}
    return {
        "racks": [
            {"rack": r, "role": "main chain" if r == 0 else f"FX bus {r}", "fl_prefix": f"FX {'Main' if r == 0 else f'Bus{r}'} Param",
             "units": [{"index": u["index"], "label": u["label"], "section": u["section"], "enabled": u["enabled"],
                        "parameters": sorted(u["params"])} for u in by_rack.get(r, {"units": []})["units"]]}
            for r in range(3)
        ],
        "proxy_parameters": FX_PROXY_NOTE,
    }


def explain_parameter(name: str, normalized: float | None = None, *, state: Mapping[str, Any] | None = None,
                      schema: Schema | None = None) -> dict[str, Any]:
    """Name the meaning of one FL wrapper parameter value where the schema knows it.

    ``Sub Shape``: the enum label for the normalized value (0.0 sine, 0.2 roundrect, 0.4 triangle, 0.6 saw,
    0.8 square, 1.0 pulse; live 0.25 read back as RoundRect). ``A/B/C WT Pos``: the frame number and, with a
    decoded ``state`` (``loading.read_state``), the loaded table and the frame's derived label. ``FX ... Param n``:
    the rack's loaded units from ``state`` and :data:`FX_PROXY_NOTE`. Anything else returns ``known=False``.
    """
    schema = schema or default_schema()
    clean = name.strip()
    if clean.casefold() == "sub shape":
        table = schema_data.wavetables()["sub_oscillator_shapes"]["fl_normalized"]
        labels = {float(v): _SUB_SHAPE_NAMES.get(k, k) for k, v in table.items()}
        out: dict[str, Any] = {"parameter": clean, "known": True, "kind": "enum",
                               "values": {v: labels[v] for v in sorted(labels)}}
        if normalized is not None:
            out["value"] = labels[min(labels, key=lambda v: abs(v - normalized))]
        return out
    match = _WT_POS.match(clean)
    if match:
        n = "ABC".index(match.group(1).upper())
        out = {"parameter": clean, "known": True, "kind": "wavetable frame",
               "rule": "normalized * 256 = table position; frame = round(position / 256 * num_frames), minimum 1"}
        if state is not None:
            node = state.get(f"Oscillator{n}")
            wt = _wavetable(node, n, schema) if isinstance(node, Mapping) else None
            if wt:
                out.update(table=wt["display_name"] or wt["path"], num_frames=wt["num_frames"], frame=wt["frame"],
                           frame_label=wt["frame_label"], known_table=wt["known_table"])
                if normalized is not None and wt["num_frames"]:
                    frame = schema_data.frame_for_table_pos(normalized * 256.0, wt["num_frames"])
                    info = schema.wavetable(wt["path"]) if wt["path"] else None
                    out["value"] = {"frame": frame, "label": next((lbl for lbl, frames in info.frames_by_label.items()
                                                                   if frame in frames), None) if info else None}
        else:
            out["note"] = "pass state=loading.read_state(fl, channel) to resolve the table and frame label"
        return out
    match = _FX_PROXY.match(clean)
    if match:
        rack = _RACK_INDEX[match.group(1).replace(" ", "").casefold()]
        out = {"parameter": clean, "known": False, "kind": "fx proxy slot", "rack": rack, "slot": int(match.group(2)),
               "note": FX_PROXY_NOTE}
        if state is not None:
            out["units"] = [u["label"] for u in fx_slot_names(state, schema=schema)["racks"][rack]["units"]]
        return out
    return {"parameter": clean, "known": False}


def explain_parameters(fl: Any, channel: int, *, slot: int | None = None, filter: str | None = None,
                       state: Mapping[str, Any] | None = None, schema: Schema | None = None) -> list[dict[str, Any]]:
    """The wrapper parameter list of a Serum 2 instance with ``meaning`` attached where the schema knows it.

    Pages ``parameters.all(filter)`` (512 slots per request) and reads the state once through
    ``loading.read_state`` unless ``state`` is given; a state read failure leaves the enum labels and marks
    wavetable rows ``state_unavailable``. Each row: ``index``, ``name``, ``display``, ``normalized``, ``meaning``.
    """
    parameters = fl.mixer[channel].effects[slot].parameters if slot is not None else fl.channels[channel].parameters
    if state is None:
        try:
            state = loading.read_state(fl, channel, slot=slot).state
        except Exception as error:  # noqa: BLE001 - diagnostics must not hide the parameter list
            state = None
            failure = f"{type(error).__name__}: {error}"
    rows = []
    for item in parameters.all(filter):
        meaning = explain_parameter(item.name, item.normalized, state=state, schema=schema)
        if state is None and meaning.get("kind") == "wavetable frame":
            meaning["state_unavailable"] = failure
        rows.append({"index": item.index, "name": item.name, "display": item.display_value,
                     "normalized": item.normalized, "meaning": meaning})
    return rows


def _fmt_level(level: Any) -> str:
    return f"{level['knob_percent']}% [{level['db']} dB]" if isinstance(level, dict) else str(level)


def format_description(description: Mapping[str, Any]) -> str:
    """Render :func:`describe_container` output as indented text."""
    lines = [f"Serum 2 state: {description.get('name') or '(unnamed)'}"]
    for osc in description["oscillators"]:
        wt = osc.get("wavetable") or {}
        table = wt.get("display_name") or wt.get("path") or ("embedded table" if wt.get("embedded") else "default table")
        frame = f" frame {wt['frame']}/{wt['num_frames']}" if wt.get("frame") else ""
        label = f" ({wt['frame_label']})" if wt.get("frame_label") else ""
        extras = ", ".join(f"{k} {osc[k]}" for k in ("unison", "detune", "blend", "width", "octave", "semi", "fine", "pan") if k in osc)
        state = "on" if osc["enabled"] else "off"
        lines.append(f"  Osc {osc['name']}: {state}, {osc['type']}, {table}{frame}{label}, level {_fmt_level(osc['level'])}"
                     + (f", {extras}" if extras else "") + f", routing {osc['routing']['dest']}")
    sub, noise = description["sub"], description["noise"]
    lines.append(f"  Sub: {'on' if sub['enabled'] else 'off'}, {sub['shape']}, octave {sub['octave']}, level {_fmt_level(sub['level'])}")
    lines.append(f"  Noise: {'on' if noise['enabled'] else 'off'}, level {_fmt_level(noise['level'])}")
    for f in description["filters"]:
        lines.append(f"  {f['section']}: {'on' if f['enabled'] else 'off'}, {f['type_display'] or f['type']}, cutoff {f['cutoff_hz']} Hz, "
                     f"reso {f['resonance']}, drive {f['drive']}, wet {f['wet_percent']}")
    for e in description["envelopes"]:
        lines.append(f"  {e['section']} ({e['role']}): A {e['attack_ms']} ms, H {e['hold_ms']} ms, D {e['decay_ms']} ms, "
                     f"S {e['sustain_db']} dB, R {e['release_ms']} ms")
    for lfo in description["lfos"]:
        lines.append(f"  {lfo['section']}: rate {lfo['rate']}, beat sync {lfo['beat_sync']}, drawn shape {lfo['drawn_shape']}")
    for rack in description["fx"]:
        chain = " -> ".join(f"{u['label']}{'' if u['enabled'] else ' (bypassed)'} mix {u['mix_percent']}" for u in rack["units"])
        lines.append(f"  FXRack{rack['rack']} ({rack['role']}): {chain}")
    for row in description["mod_slots"]:
        source = row["source"]["name"] if row["source"] else "?"
        via = f" via {row['aux']['name']}" if row["aux"] else ""
        lines.append(f"  Mod {row['slot']}: {source}{via} -> {row['destination']} {row['amount']} %"
                     + (" bipolar" if row["bipolar"] else "") + (" (bypassed)" if row["bypassed"] else ""))
    for macro in description["macros"]:
        lines.append(f"  Macro {macro['index'] + 1}: {macro['name'] or '(unnamed)'} = {macro['value']}")
    g = description["global"]
    lines.append(f"  Global: master {_fmt_level(g['master'])}, mono {g['mono']}, legato {g['legato']}, porta {g['porta_ms']} ms, "
                 f"bend +{g['bend_up_semitones']}/{g['bend_down_semitones']} st, transpose {g['transpose']}")
    for note in description["signal_path"]:
        lines.append(f"  note: {note}")
    return "\n".join(lines)
