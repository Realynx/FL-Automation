"""Two-phase live harvest: verify FL parameter -> Serum state-key mappings on a disposable channel.

Run from ``fl_execute_python`` (or the Python IDE) with the extension installed and a Serum 2
instance on a channel you do not mind changing. Parameter displays lag inside the request
that wrote them, so the loop is split into two requests:

Request 1 (write phase)::

    import sys; sys.path.insert(0, r"C:\\path\\to\\sdk\\extensions\\serum-support\\tools")
    import harvest_live
    result = harvest_live.write_phase(fl, channel=4, out_dir=r"C:\\FruityLink\\harvest")

Request 2 (read phase, a separate request)::

    import harvest_live
    result = harvest_live.read_phase(fl, channel=4, manifest=r"C:\\FruityLink\\harvest\\harvest-<stamp>.json")

Offline afterwards (any Python)::

    python tools/harvest_live.py apply C:\\FruityLink\\harvest\\harvest-<stamp>.json

The write phase loads the mod-matrix probe patch first when ``probe_mod_matrix`` is set (a
load replaces the whole state, so it must precede every write; live 2026-09-14 a load placed
after the writes wiped them and produced contradictory rows), reads the baseline state
(``loading.read_state``), then sets every planned FL parameter (default plan: the
``fl_anchors`` rows of ``parameter-map.json`` that are not yet ``verified``) to a probe value
through ``fl.channels[channel].parameters.set_named``, and finally writes a sentinel (``A
Level`` 0.5, whose state is the verified ``fl^2`` = 0.25, distinct from the probe patch's 0.49)
so the read phase can tell whether a load happened after the writes. The read phase reads the
state again, diffs it, reads each display and writes ``verified`` / ``changed_other_key`` /
``unchanged`` rows plus an ``ordering`` verdict. ``apply`` stamps ``confidence: "verified"`` on
the matching ``parameter-map.json`` rows and anchors. Nothing here runs without a live ``fl``;
``plan()`` and ``apply`` are testable offline.
"""

from __future__ import annotations

import json
import os
import re
import sys
import time
from pathlib import Path
from typing import Any

try:
    from fruitylink_serum import builder, loading, schema
except ModuleNotFoundError:  # running from a source checkout
    _HERE = Path(__file__).resolve().parent
    sys.path[:0] = [str(_HERE.parent / "src"), str(_HERE.parents[2] / "python" / "src")]
    from fruitylink_serum import builder, loading, schema

__all__ = ["apply_verified", "plan", "read_phase", "write_phase"]

DEFAULT_PROBE = 0.6
# Anchors whose probe value must stay inside a small range or that need a specific value to move.
PROBE_OVERRIDES: dict[str, float] = {"Sub Shape": 0.8, "A Enable": 1.0, "Sub Enable": 1.0}
# Ordering sentinel: a verified anchor (state = fl^2) that the mod probe patch also sets (osc A level 0.7 ->
# 0.49). Written last; if the read phase finds the patch's value instead of 0.25, a load followed the writes.
SENTINEL_FL, SENTINEL_PATH, SENTINEL_VALUE = "A Level", "Oscillator0/plainParams/kParamVolume", 0.5
_SENTINEL_TOLERANCE = 1e-3


def plan(*, include_verified: bool = False) -> list[dict[str, Any]]:
    """The set-then-read items: ``{"fl", "path", "rule", "confidence", "value"}`` from the parameter map's anchors."""
    items = []
    for row in schema.parameter_map()["fl_anchors"]:
        if row.get("confidence") == "verified" and not include_verified:
            continue
        items.append({"fl": row["fl"], "path": row["path"], "rule": row.get("fl_normalized_to_state"),
                      "confidence": row.get("confidence"), "value": PROBE_OVERRIDES.get(row["fl"], DEFAULT_PROBE)})
    return items


def _normalise(path: str) -> str:
    return "/".join(re.sub(r"\d+$", "{n}", p) if p != "plainParams" else p for p in path.split("/"))


def write_phase(fl: Any, channel: int, *, out_dir: str | os.PathLike[str], items: list[dict[str, Any]] | None = None,
                probe_mod_matrix: bool = False) -> dict[str, Any]:
    """Phase 1: load the probe patch (if any), snapshot the state, set every planned parameter, write the
    sentinel and the manifest; read back next request. A state load must never follow the writes."""
    items = list(items) if items is not None else plan()
    directory = Path(out_dir)
    directory.mkdir(parents=True, exist_ok=True)
    stamp = time.strftime("%Y%m%d-%H%M%S")
    probe = None
    if probe_mod_matrix:
        # First: the load replaces the whole state, so it has to precede the baseline read and every write.
        patch = builder.SerumPatch(name=f"harvest-{stamp}").osc_a("saw", level=0.7).velocity(1.0).bend_range(12, 12)
        probe = {"load": str(patch.load(fl, channel)), "changes": patch.changes(),
                 "expect": "ModSlot0 source [16, 0] -> Global0/kParamVoiceAmp; Bend Up 12 / Bend Down -12",
                 "order": "loaded before the baseline read and the parameter writes"}
    baseline = builder.flatten_state(loading.read_state(fl, channel).state)
    parameters = fl.channels[channel].parameters
    written = []
    for item in items:
        try:
            index = parameters.set_named(item["fl"], float(item["value"]))
            written.append({**item, "index": index, "error": None})
        except Exception as error:  # noqa: BLE001 - record and continue
            written.append({**item, "index": None, "error": f"{type(error).__name__}: {error}"})
    sentinel = _write_sentinel(parameters, written, baseline)
    manifest = {"channel": channel, "stamp": stamp, "baseline": baseline, "items": written, "mod_probe": probe,
                "sentinel": sentinel, "order": ["mod_probe", "baseline", "writes", "sentinel"],
                "next": "call read_phase(fl, channel, manifest=<this path>) in a NEW request"}
    path = directory / f"harvest-{stamp}.json"
    path.write_text(json.dumps(manifest, indent=1, default=str), encoding="utf-8")
    return {"manifest": str(path), "written": sum(1 for w in written if w["error"] is None), "errors":
            [w for w in written if w["error"]], "mod_probe": probe, "sentinel": sentinel}


def _write_sentinel(parameters: Any, written: list[dict[str, Any]], baseline: dict[str, Any]) -> dict[str, Any]:
    """Write ``A Level`` last (or reuse its planned write) and record the state the read phase must find."""
    planned = next((w for w in written if w["fl"] == SENTINEL_FL and w["error"] is None), None)
    value = float(planned["value"]) if planned is not None else SENTINEL_VALUE
    sentinel: dict[str, Any] = {"fl": SENTINEL_FL, "path": SENTINEL_PATH, "value": value, "expected_state": value * value,
                                "state_before": baseline.get(SENTINEL_PATH), "rule": "state = fl^2 (verified)",
                                "index": None, "error": None}
    if planned is not None:
        sentinel["index"] = planned["index"]
        return sentinel
    try:
        sentinel["index"] = parameters.set_named(SENTINEL_FL, value)
    except Exception as error:  # noqa: BLE001 - the harvest itself is still usable
        sentinel["error"] = f"{type(error).__name__}: {error}"
    return sentinel


def _ordering(sentinel: dict[str, Any] | None, after: dict[str, Any]) -> dict[str, Any]:
    """Compare the sentinel's live state with what its write must have produced."""
    if not sentinel or sentinel.get("error") or sentinel.get("index") is None:
        return {"status": "unchecked", "reason": "no sentinel was written"}
    actual = after.get(sentinel["path"])
    before = sentinel.get("state_before")
    expected = float(sentinel["expected_state"])
    verdict: dict[str, Any] = {"sentinel": sentinel["fl"], "path": sentinel["path"], "expected_state": expected,
                               "state_before": before, "state_after": actual}
    if isinstance(actual, (int, float)) and not isinstance(actual, bool) and abs(float(actual) - expected) <= _SENTINEL_TOLERANCE:
        return {**verdict, "status": "ok"}
    if isinstance(actual, (int, float)) and isinstance(before, (int, float)) and abs(float(actual) - float(before)) <= _SENTINEL_TOLERANCE:
        return {**verdict, "status": "load_after_write",
                "reason": f"{sentinel['fl']} still holds its pre-write state {before}; a preset/patch load (or an undo) replaced "
                          "the state after the parameter writes, so every state_after below reflects the load, not the write. "
                          "Rerun on a fresh channel with any load before the writes."}
    return {**verdict, "status": "unresolved", "reason": "the sentinel matches neither the write nor the pre-write state"}


def read_phase(fl: Any, channel: int, *, manifest: str | os.PathLike[str]) -> dict[str, Any]:
    """Phase 2 (separate request): re-read the state and displays, attribute each write to a state key."""
    data = json.loads(Path(manifest).read_text(encoding="utf-8"))
    baseline: dict[str, Any] = data["baseline"]
    after = builder.flatten_state(loading.read_state(fl, channel).state)
    changed = {k: (baseline.get(k), after.get(k)) for k in sorted(set(baseline) | set(after)) if baseline.get(k) != after.get(k)}
    parameters = fl.channels[channel].parameters
    rows = []
    for item in data["items"]:
        if item.get("error") or item.get("index") is None:
            rows.append({**item, "status": "not_written"})
            continue
        info = parameters.read(int(item["index"]))
        expected = item["path"]
        hits = [k for k in changed if _normalise(k) == _normalise(expected)]
        if hits:
            status = "verified"
        elif changed:
            status = "changed_other_key"
        else:
            status = "unchanged"
        rows.append({**item, "status": status, "display": info.display_value, "raw": info.raw_value,
                     "state_before": baseline.get(expected), "state_after": after.get(expected),
                     "matched_keys": hits})
    ordering = _ordering(data.get("sentinel"), after)
    out = {"channel": channel, "stamp": data["stamp"], "rows": rows, "changed_keys": changed,
           "mod_probe": data.get("mod_probe"), "ordering": ordering,
           "verified": [r["fl"] for r in rows if r["status"] == "verified"],
           "unresolved": [r["fl"] for r in rows if r["status"] != "verified"], "warnings": []}
    if ordering["status"] == "load_after_write":
        out["warnings"].append("load after write: " + str(ordering["reason"]) + " No row of this run is trustworthy.")
        out["verified"] = []
        out["unresolved"] = [r["fl"] for r in rows]
    if data.get("mod_probe"):
        out["mod_probe_result"] = {k: after.get(k) for k in after if k.startswith(("ModSlot0", "Global0/plainParams/kParamBend"))}
    result_path = Path(manifest).with_name(Path(manifest).stem + "-result.json")
    result_path.write_text(json.dumps(out, indent=1, default=str), encoding="utf-8")
    out["result"] = str(result_path)
    return out


def apply_verified(result: str | os.PathLike[str], *, data_dir: str | os.PathLike[str] | None = None) -> dict[str, Any]:
    """Stamp ``confidence: "verified"`` on parameter-map rows/anchors that a harvest result verified."""
    data = json.loads(Path(result).read_text(encoding="utf-8"))
    directory = Path(data_dir) if data_dir is not None else Path(__file__).resolve().parent.parent / "src" / "fruitylink_serum" / "data"
    path = directory / "parameter-map.json"
    parameter_map = json.loads(path.read_text(encoding="utf-8"))
    verified_paths = {_normalise(r["path"]) for r in data["rows"] if r.get("status") == "verified"}
    verified_names = {r["fl"] for r in data["rows"] if r.get("status") == "verified"}
    touched = 0
    for row in parameter_map["parameters"]:
        if row["path"] in verified_paths and row.get("confidence") != "verified":
            row["confidence"] = "verified"
            row["verified_by"] = f"harvest {data.get('stamp')}"
            touched += 1
    for row in parameter_map["fl_anchors"]:
        if row["fl"] in verified_names and row.get("confidence") != "verified":
            row["confidence"] = "verified"
            touched += 1
    path.write_text(json.dumps(parameter_map, indent=1) + "\n", encoding="utf-8")
    return {"file": str(path), "rows_updated": touched, "verified": sorted(verified_names)}


if __name__ == "__main__":
    if len(sys.argv) >= 3 and sys.argv[1] == "apply":
        print(json.dumps(apply_verified(sys.argv[2]), indent=1))
    else:
        print(json.dumps(plan(), indent=1))
        print("usage: harvest_live.py apply <harvest-result.json>; write_phase/read_phase need a live fl", file=sys.stderr)
