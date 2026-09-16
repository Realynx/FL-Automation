"""Known plugin parameter scales: normalized 0..1 <-> display units, seeded from live measurements.

Plugin parameters are written as normalized 0..1 values, but the units a knob shows (Hz, ms, dB, %)
are not documented anywhere, so every session probed them with two or three calibration writes
per knob (Fruity Reeverb 2, Fruity Delay 3, Pro-L 2, Pro-Q 4, Super VHS, Serum 2 in the
2026-09-14 friction log). ``data/parameter-scales.json`` keeps that evidence as a small table and
:func:`scale_for` turns it into a :class:`ParameterScale` with ``to_display`` / ``to_normalized``.

Every entry carries a ``confidence`` (``verified``, ``measured``, ``inferred``, ``rough``) and the
session that produced it; treat ``inferred``/``rough`` entries as a first guess to verify with
``Parameters.set_verified`` and a display read in the following request. The table is data, not
plugin knowledge: add points with :func:`add_scale` at runtime or extend the JSON file.
"""

from __future__ import annotations

import fnmatch
import json
import math
from dataclasses import dataclass, field
from functools import lru_cache
from importlib import resources
from typing import Any

__all__ = ["ParameterScale", "add_scale", "known_plugins", "scale_for", "scales_for"]

_DATA_FILE = "parameter-scales.json"


def _canonical(name: str) -> str:
    return "".join(ch for ch in name.casefold() if ch.isalnum())


@dataclass(frozen=True)
class ParameterScale:
    """One parameter's normalized <-> display mapping and the evidence behind it.

    ``parameter`` may be a glob pattern (``"Band * Frequency"``, ``"Env * Attack"``) when the plugin
    repeats a layout; ``matches(name)`` tests a cleaned parameter name against it.
    """

    plugin: str
    parameter: str
    kind: str
    unit: str
    confidence: str
    evidence: str = ""
    note: str = ""
    spec: dict[str, Any] = field(default_factory=dict)

    def matches(self, name: str) -> bool:
        return fnmatch.fnmatchcase(name.casefold().strip(), self.parameter.casefold())

    def to_display(self, normalized: float) -> float | str:
        """Display value (float in ``unit``, or the enum label) for a normalized 0..1 value."""
        v = _check_normalized(normalized)
        kind, s = self.kind, self.spec
        if kind == "linear":
            out = s["a"] * v + s.get("b", 0.0)
            return float(round(out)) if s.get("round") else out
        if kind == "exp":
            return float(s["a"] * s["base"] ** v)
        if kind == "power":
            return float(s["a"] * v ** s["p"] + s.get("b", 0.0))
        if kind == "log_db":
            return s["k"] * math.log10(v) + s.get("b", 0.0) if v > 0 else -math.inf
        if kind == "table":
            return _interpolate(self._points(), v, log_y=bool(s.get("log")))
        if kind == "enum":
            anchors = self._anchors()
            return min(anchors, key=lambda item: abs(item[0] - v))[1]
        raise ValueError(f"Unknown scale kind {kind!r}")

    def to_normalized(self, display: float | str) -> float:
        """Normalized 0..1 value (clamped) for a display value or enum label."""
        kind, s = self.kind, self.spec
        if kind == "enum":
            wanted = str(display).casefold().strip()
            for value, label in self._anchors():
                if label.casefold() == wanted:
                    return value
            raise LookupError(f"{self.plugin} {self.parameter!r} has no value {display!r}; "
                              f"choices: {', '.join(label for _, label in self._anchors())}")
        d = float(display)
        if kind == "linear":
            v = (d - s.get("b", 0.0)) / s["a"]
        elif kind == "exp":
            v = math.log(d / s["a"]) / math.log(s["base"])
        elif kind == "power":
            v = ((d - s.get("b", 0.0)) / s["a"]) ** (1.0 / s["p"])
        elif kind == "log_db":
            v = 10 ** ((d - s.get("b", 0.0)) / s["k"])
        elif kind == "table":
            v = _interpolate([(y, x) for x, y in self._points()], d, log_x=bool(s.get("log")))
        else:
            raise ValueError(f"Unknown scale kind {kind!r}")
        return min(1.0, max(0.0, float(v)))

    def describe(self) -> str:
        """One line: plugin, parameter, formula or measured range, unit and confidence."""
        s = self.spec
        if self.kind == "table":
            pts = self._points()
            rule = (f"table {pts[0][0]}..{pts[-1][0]} -> {pts[0][1]}..{pts[-1][1]} {self.unit}".strip()
                    + (" (log)" if s.get("log") else ""))
        elif self.kind == "enum":
            rule = "enum " + ", ".join(f"{v}={label}" for v, label in self._anchors())
        elif self.kind == "linear":
            rule = f"{s['a']} v {s.get('b', 0):+g} {self.unit}".strip()
        elif self.kind == "exp":
            rule = f"{s['a']} * {s['base']}^v {self.unit}".strip()
        elif self.kind == "power":
            rule = f"{s['a']} v^{s['p']} {s.get('b', 0):+g} {self.unit}".strip()
        else:
            rule = f"{s['k']} log10(v) {s.get('b', 0):+g} {self.unit}".strip()
        text = f"{self.plugin} '{self.parameter}': {rule} [{self.confidence}]"
        return text + (f" - {self.note}" if self.note else "")

    def _points(self) -> list[tuple[float, float]]:
        points = sorted((float(x), float(y)) for x, y in self.spec["points"])
        if len(points) < 2:
            raise ValueError(f"{self.plugin} {self.parameter!r}: a table scale needs at least two points")
        return points

    def _anchors(self) -> list[tuple[float, str]]:
        return sorted((float(v), str(label)) for v, label in self.spec["anchors"])


def _check_normalized(value: float) -> float:
    if isinstance(value, bool) or not isinstance(value, (int, float)) or not 0 <= value <= 1:
        raise ValueError("Normalized parameter values are 0..1.")
    return float(value)


def _interpolate(points: list[tuple[float, float]], x: float, *, log_y: bool = False, log_x: bool = False) -> float:
    """Piecewise-linear through sorted points, extrapolating the outer segments; optional log axes."""
    xs = [math.log(px) if log_x else px for px, _ in points]
    ys = [math.log(py) if log_y else py for _, py in points]
    xv = math.log(x) if log_x else x
    index = max(1, min(len(points) - 1, next((i for i, px in enumerate(xs) if px >= xv), len(points) - 1)))
    x0, x1, y0, y1 = xs[index - 1], xs[index], ys[index - 1], ys[index]
    y = y0 if x1 == x0 else y0 + (y1 - y0) * (xv - x0) / (x1 - x0)
    return math.exp(y) if log_y else y


@lru_cache(maxsize=1)
def _table() -> dict[str, Any]:
    with resources.files(__package__).joinpath("data", _DATA_FILE).open("r", encoding="utf-8") as handle:
        return dict(json.load(handle))


_runtime: list[ParameterScale] = []


def _entries() -> list[ParameterScale]:
    rows: list[ParameterScale] = []
    for plugin in _table().get("plugins", ()):
        for row in plugin.get("parameters", ()):
            spec = {k: v for k, v in row.items() if k not in ("name", "kind", "unit", "confidence", "note", "evidence")}
            rows.append(ParameterScale(plugin["plugin"], row["name"], row["kind"], row.get("unit", ""),
                                       row.get("confidence", "inferred"), row.get("evidence", plugin.get("evidence", "")),
                                       row.get("note", ""), spec))
    return rows + list(_runtime)


def _plugin_matches(entry_plugin: str, wanted: str, aliases: dict[str, tuple[str, ...]]) -> bool:
    names = (entry_plugin, *aliases.get(entry_plugin, ()))
    want = _canonical(wanted)
    return any(_canonical(n) == want or (len(want) >= 4 and (want in _canonical(n) or _canonical(n) in want)) for n in names)


def _aliases() -> dict[str, tuple[str, ...]]:
    return {p["plugin"]: tuple(p.get("aliases", ())) for p in _table().get("plugins", ())}


def known_plugins() -> tuple[str, ...]:
    """Plugins with at least one known scale (display names as in the FL plugin database)."""
    return tuple(dict.fromkeys(entry.plugin for entry in _entries()))


def scales_for(plugin: str) -> tuple[ParameterScale, ...]:
    """Every known scale of one plugin (name matched case-insensitively, aliases and containment allowed)."""
    aliases = _aliases()
    return tuple(entry for entry in _entries() if _plugin_matches(entry.plugin, plugin, aliases))


def scale_for(plugin: str, parameter: str | int) -> ParameterScale:
    """The scale of one parameter, by cleaned display name (``"Low cut"``; globs in the table such as
    ``"Band * Frequency"`` match ``"Band 3 Frequency"``). Raises ``LookupError`` naming the plugin's known
    parameters, or the known plugins when the plugin itself is unknown. Integer indices are not stable across
    plugin versions, so the table is keyed by name; pass ``Parameters.read(index).name``.
    """
    if isinstance(parameter, int):
        raise LookupError("Scales are keyed by parameter name; pass Parameters.read(index).name.")
    candidates = scales_for(plugin)
    if not candidates:
        raise LookupError(f"No known scales for plugin {plugin!r}; known: {', '.join(known_plugins())}.")
    wanted = parameter.strip()
    exact = [c for c in candidates if c.parameter.casefold() == wanted.casefold()]
    if exact:
        return exact[0]
    patterns = [c for c in candidates if c.matches(wanted)]
    if patterns:
        return patterns[0]
    raise LookupError(f"No known scale for {candidates[0].plugin} parameter {parameter!r}; "
                      f"known: {', '.join(c.parameter for c in candidates)}.")


def add_scale(scale: ParameterScale) -> None:
    """Register a scale for this process (a session's own calibration); the JSON table is not modified."""
    _runtime.append(scale)
