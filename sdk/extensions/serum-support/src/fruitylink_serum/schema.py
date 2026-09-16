"""Access to the derived Serum 2 state-schema data shipped with this package.

The JSON files under ``fruitylink_serum/data`` were generated from statistics over a locally
installed Serum 2 library and this project's FL-saved states (see ``docs/state-schema.md``).
They describe parameter names, observed ranges, units, enums, effect-unit layouts, filter
types and wavetable frame labels. They contain no preset payloads. Everything here is
read-only and pure Python.
"""

from __future__ import annotations

import json
import math
import re
from functools import lru_cache
from importlib import resources
from typing import Any

__all__ = [
    "DATA_FILES",
    "fl_anchor",
    "filter_types",
    "find_frames",
    "frame_for_table_pos",
    "fx_effect",
    "fx_schema",
    "fx_type_for",
    "load_data",
    "mod_destination",
    "mod_matrix",
    "mod_source",
    "parameter",
    "parameter_map",
    "parameters_in",
    "table_pos_for_frame",
    "wavetable",
    "wavetables",
]

DATA_FILES = ("parameter-map.json", "fx-schema.json", "filters.json", "wavetables.json", "mod-matrix.json")


@lru_cache(maxsize=None)
def load_data(name: str) -> dict[str, Any]:
    """Load one of :data:`DATA_FILES` as a dict."""
    if name not in DATA_FILES:
        raise KeyError(f"Unknown schema data file {name!r}; expected one of {DATA_FILES}.")
    text = resources.files("fruitylink_serum").joinpath("data").joinpath(name).read_text(encoding="utf-8")
    loaded = json.loads(text)
    if not isinstance(loaded, dict):
        raise ValueError(f"{name} must contain a JSON object.")
    return loaded


def parameter_map() -> dict[str, Any]:
    return load_data("parameter-map.json")


def fx_schema() -> dict[str, Any]:
    return load_data("fx-schema.json")


def filter_types() -> list[dict[str, Any]]:
    types = load_data("filters.json")["types"]
    return list(types) if isinstance(types, list) else []


def wavetables() -> dict[str, Any]:
    return load_data("wavetables.json")


def mod_matrix() -> dict[str, Any]:
    """Mod-matrix layout: inferred source ids and ``(type, param) -> destModuleParamID`` rows."""
    return load_data("mod-matrix.json")


def mod_source(name_or_id: str | int) -> dict[str, Any] | None:
    """One ``sources`` row of mod-matrix.json by id or (case-insensitive) name."""
    for row in mod_matrix()["sources"]:
        if row["id"] == name_or_id or (isinstance(name_or_id, str) and str(row["name"]).casefold() == name_or_id.casefold()):
            return dict(row)
    return None


def mod_destination(section_type: str, param: str) -> dict[str, Any] | None:
    """One ``destinations`` row of mod-matrix.json (``"Global"``, ``"kParamVoiceAmp"`` -> id 2)."""
    for row in mod_matrix()["destinations"]:
        if row["type"] == section_type and row["param"] == param:
            return dict(row)
    return None


def _normalise(path: str) -> str:
    """Map a concrete path (``Oscillator0/plainParams/kParamVolume``) to the map's ``{n}`` form."""
    parts = []
    for segment in path.split("/"):
        parts.append(re.sub(r"\d+$", "{n}", segment) if segment != "plainParams" else segment)
    return "/".join(parts)


def parameter(path: str) -> dict[str, Any] | None:
    """Look up one parameter row by concrete or ``{n}``-normalised path."""
    wanted = _normalise(path)
    for row in parameter_map()["parameters"]:
        if row["path"] == wanted:
            return dict(row)
    return None


def parameters_in(section: str) -> list[dict[str, Any]]:
    """All parameter rows whose section starts with ``section`` (``Oscillator``, ``VoiceFilter``, ...)."""
    wanted = _normalise(section)
    return [dict(row) for row in parameter_map()["parameters"] if str(row["section"]).startswith(wanted)]


def fl_anchor(fl_name: str) -> dict[str, Any] | None:
    """The state path and scale rule for an FL wrapper parameter display name (``"A Level"``)."""
    for row in parameter_map()["fl_anchors"]:
        if row["fl"].casefold() == fl_name.casefold():
            return dict(row)
    return None


def fx_effect(name: str) -> dict[str, Any] | None:
    """Effect-unit description by section name (``FXReverb``) or label (``Reverb``)."""
    effects = fx_schema()["effects"]
    if name in effects:
        return dict(effects[name])
    for section, row in effects.items():
        if str(row.get("label", "")).casefold() == name.casefold() or section.casefold() == f"fx{name}".casefold():
            return dict(row)
    return None


def fx_type_for(section: str) -> int:
    """The ``type`` integer a unit map must carry for the given ``FX*`` section name."""
    row = fx_effect(section)
    if row is None:
        raise KeyError(f"Unknown effect section {section!r}.")
    return int(row["type"])


def wavetable(relative_path: str) -> dict[str, Any] | None:
    """Frame labels for one factory table by its ``relativePathToWT`` (leading '/' tolerated)."""
    wanted = relative_path.lstrip("/").replace("\\", "/").casefold()
    for table in wavetables()["tables"]:
        if str(table["relative_path"]).casefold() == wanted:
            return dict(table)
    return None


def find_frames(label: str, relative_path: str = "S2 Tables/Default Shapes.wav") -> list[int]:
    """1-based frame indices in a table whose derived label matches (``sine``, ``saw``, ``square`` ...)."""
    table = wavetable(relative_path)
    if table is None:
        return []
    frames = table["frames_by_label"].get(label, [])
    return [int(frame) for frame in frames]


def table_pos_for_frame(frame: int, num_frames: int) -> float:
    """``kParamTablePos`` value (0..256 scale) that selects a 1-based frame (frame = round(value/256*n))."""
    if num_frames <= 0 or not 1 <= frame <= num_frames:
        raise ValueError("frame must be within 1..num_frames.")
    return 256.0 * frame / num_frames


def frame_for_table_pos(value: float, num_frames: int) -> int:
    """1-based frame a ``kParamTablePos`` value selects (inverse of :func:`table_pos_for_frame`)."""
    if num_frames <= 0:
        raise ValueError("num_frames must be positive.")
    return max(1, min(num_frames, int(math.floor(value / 256.0 * num_frames + 0.5))))
