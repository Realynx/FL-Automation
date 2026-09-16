"""Aggregate Serum 2 state statistics across the local library (derived stats only)."""
import collections
import glob
import json
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path[:0] = [os.path.join(HERE, "..", "..", "src"), os.path.join(HERE, "..", "..", "..", "..", "python", "src")]
from fruitylink_serum import xfer  # noqa: E402

ROOT = os.environ.get("SERUM_PRESETS_PATH", os.path.expanduser(r"~\Documents\Xfer\Serum 2 Presets"))
OUT = os.path.join(os.path.dirname(__file__), "aggregate.json")

stats = {}          # path -> record
fx_types = collections.defaultdict(collections.Counter)   # type number -> Counter of FX section names
fx_keys = collections.defaultdict(collections.Counter)    # "FXName" -> Counter of param keys
files = 0
errors = []
sub_shape = collections.Counter()
mod_sources = collections.Counter()
mod_dest_types = collections.Counter()
filter_types = collections.Counter()
wt_paths = collections.Counter()

def norm(seg):
    return re.sub(r"\d+$", "{n}", seg)

def rec(path):
    r = stats.get(path)
    if r is None:
        r = stats[path] = {"count": 0, "types": collections.Counter(), "min": None, "max": None, "values": collections.Counter(), "files": 0}
    return r

def visit(node, path, seen_paths):
    if isinstance(node, dict):
        for k, v in node.items():
            visit(v, path + "/" + norm(k) if path else norm(k), seen_paths)
    elif isinstance(node, list):
        # FX lists: record type/section pairs; otherwise treat as an array leaf
        if path.endswith("/FX") and all(isinstance(x, dict) for x in node):
            for i, x in enumerate(node):
                t = x.get("type")
                secs = [k for k in x if k.startswith("FX")]
                for s in secs:
                    fx_types[str(t)][s] += 1
                    pp = x[s].get("plainParams") if isinstance(x[s], dict) else None
                    if isinstance(pp, dict):
                        for pk, pv in pp.items():
                            fx_keys[s][pk] += 1
                            visit(pv, f"{path}[{s}]/plainParams/{pk}", seen_paths)
                    elif isinstance(x[s], dict):
                        for ok, ov in x[s].items():
                            if ok != "plainParams":
                                visit(ov, f"{path}[{s}]/{ok}", seen_paths)
                for ok, ov in x.items():
                    if not ok.startswith("FX") and ok != "type":
                        visit(ov, f"{path}[*]/{ok}", seen_paths)
            r = rec(path)
            r["count"] += 1
            r["types"]["fxlist"] += 1
            r["values"][f"len={len(node)}"] += 1
            return
        r = rec(path)
        r["count"] += 1
        r["types"]["array"] += 1
        r["values"][f"len={len(node)}"] += 1
        if node and all(isinstance(x, (int, float)) and not isinstance(x, bool) for x in node):
            lo, hi = min(node), max(node)
            r["min"] = lo if r["min"] is None else min(r["min"], lo)
            r["max"] = hi if r["max"] is None else max(r["max"], hi)
        return
    else:
        r = rec(path)
        r["count"] += 1
        seen_paths.add(path)
        if isinstance(node, bool):
            r["types"]["bool"] += 1
            r["values"][str(node)] += 1
        elif isinstance(node, (int, float)):
            r["types"]["float" if isinstance(node, float) else "int"] += 1
            r["min"] = node if r["min"] is None else min(r["min"], node)
            r["max"] = node if r["max"] is None else max(r["max"], node)
            if len(r["values"]) < 64:
                r["values"][repr(round(node, 4))] += 1
        elif isinstance(node, str):
            r["types"]["str"] += 1
            if len(r["values"]) < 64:
                r["values"][node[:80]] += 1
        else:
            r["types"][type(node).__name__] += 1

def main():
    global files
    paths = []
    for ext in ("*.SerumPreset", "*.SerumFX", "*.SerumFXRack"):
        paths += glob.glob(os.path.join(ROOT, "**", ext), recursive=True)
    extra = glob.glob(os.path.join(os.environ["SERUM_EXTRA_PRESETS"], "*.SerumPreset")) if os.environ.get("SERUM_EXTRA_PRESETS") else []
    for p in sorted(paths) + extra:
        try:
            c = xfer.read_container(open(p, "rb").read())
        except Exception as e:
            errors.append([p, repr(e)[:120]])
            continue
        files += 1
        seen = set()
        visit(c.state, "", seen)
        for sp in seen:
            stats[sp]["files"] += 1
        st = c.state
        o4 = st.get("Oscillator4", {})
        if isinstance(o4, dict):
            for sk, sv in o4.items():
                if sk.startswith("SubOsc") and isinstance(sv, dict):
                    pp = sv.get("plainParams")
                    if isinstance(pp, dict) and "kParamShape" in pp:
                        sub_shape[repr(pp["kParamShape"])] += 1
        for i in range(2):
            vf = st.get(f"VoiceFilter{i}", {})
            pp = vf.get("plainParams") if isinstance(vf, dict) else None
            if isinstance(pp, dict):
                for k in pp:
                    if "Type" in k or "Model" in k or "Mode" in k:
                        filter_types[f"{k}={pp[k]!r}"] += 1
        for k, v in st.items():
            if k.startswith("ModSlot") and isinstance(v, dict):
                mod_sources[repr(v.get("source"))] += 1
                mod_dest_types[f"{v.get('destModuleTypeString')}:{v.get('destModuleParamName')}"] += 1
        for k, v in st.items():
            if k.startswith("Oscillator") and isinstance(v, dict):
                for sk, sv in v.items():
                    if sk.startswith("WTOsc") and isinstance(sv, dict):
                        wt_paths[str(sv.get("relativePathToWT"))] += 1
    out = {"files": files, "errors": errors,
           "stats": {p: {"count": r["count"], "files": r["files"], "types": dict(r["types"]), "min": r["min"], "max": r["max"],
                         "values": dict(r["values"].most_common(64))} for p, r in sorted(stats.items())},
           "fx_types": {t: dict(c) for t, c in sorted(fx_types.items(), key=lambda x: int(x[0]) if x[0].lstrip('-').isdigit() else 999)},
           "fx_keys": {s: dict(c) for s, c in sorted(fx_keys.items())},
           "sub_shape": dict(sub_shape), "filter_types": dict(filter_types.most_common(200)),
           "mod_sources": dict(mod_sources.most_common(120)), "mod_dest_types": dict(mod_dest_types.most_common(200)),
           "wt_paths": dict(wt_paths.most_common(300))}
    json.dump(out, open(OUT, "w", encoding="utf-8"), indent=1)
    print("files", files, "errors", len(errors), "paths", len(stats))

main()
