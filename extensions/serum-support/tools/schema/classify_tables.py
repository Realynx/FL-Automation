"""Classify wavetable frames by harmonic content (pure Python, derived labels only)."""
import cmath
import glob
import json
import math
import os
import struct

ROOT = os.path.join(os.environ.get("SERUM_PRESETS_PATH", os.path.expanduser(r"~\Documents\Xfer\Serum 2 Presets")), "Tables")
OUT = os.path.join(os.path.dirname(__file__), "wavetables-raw.json")
FRAME = 2048
NH = 32
MAX_FRAMES = 256

def read_table(path):
    b = open(path, "rb").read()
    pos = 12
    fmt = None
    data = None
    clm = None
    while pos + 8 <= len(b):
        cid = b[pos:pos + 4]
        sz = struct.unpack_from("<I", b, pos + 4)[0]
        body = b[pos + 8:pos + 8 + sz]
        if cid == b"fmt ":
            fmt = struct.unpack_from("<HHIIHH", body, 0)
        elif cid == b"data":
            data = body
        elif cid == b"clm ":
            clm = body.decode("latin-1", "replace")
        pos += 8 + sz + (sz & 1)
    if fmt is None or data is None:
        return None
    tag, ch, sr, _, _, bits = fmt
    if tag != 3 or bits != 32 or ch != 1:
        return None
    n = len(data) // 4
    samples = struct.unpack_from("<%df" % n, data, 0)
    return {"clm": clm, "samples": samples, "frames": n // FRAME}

# precompute twiddles
TW = [[cmath.exp(-2j * math.pi * k * n / FRAME) for n in range(FRAME)] for k in range(1, NH + 1)]

def harmonics(x):
    return [abs(sum(x[n] * TW[k][n] for n in range(FRAME))) for k in range(NH)]

def classify(h):
    peak = max(h) or 1e-12
    a = [v / peak for v in h]
    h1 = a[0]
    others = sum(a[1:])
    odd = [a[i] for i in range(0, NH, 2)]   # k=1,3,5.. (index 0 = k1)
    even = [a[i] for i in range(1, NH, 2)]  # k=2,4,6..
    even_ratio = sum(even) / (sum(odd) or 1e-12)
    # decay slope over the first 8 odd harmonics with meaningful energy
    pts = [(math.log(2 * i + 1), math.log(max(odd[i], 1e-6))) for i in range(8)]
    n = len(pts)
    sx = sum(p[0] for p in pts)
    sy = sum(p[1] for p in pts)
    sxx = sum(p[0] ** 2 for p in pts)
    sxy = sum(p[0] * p[1] for p in pts)
    slope = (n * sxy - sx * sy) / (n * sxx - sx * sx)
    # nulls: harmonics far below both neighbours (sinc pattern of pulses)
    nulls = sum(1 for i in range(1, 16) if a[i] < 0.15 * max(a[i - 1], a[i + 1]) and max(a[i - 1], a[i + 1]) > 0.05)
    metrics = {"h1": round(h1, 4), "others_over_h1": round(others / (h1 or 1e-12), 4), "even_over_odd": round(even_ratio, 4),
               "odd_decay_slope": round(slope, 3), "nulls": nulls, "top_harmonic": a.index(max(a)) + 1}
    if h1 >= 0.99 and others / h1 < 0.05:
        label = "sine"
    elif even_ratio < 0.08 and slope < -1.6 and h1 >= 0.99:
        label = "triangle"
    elif even_ratio < 0.08 and -1.5 <= slope <= -0.6 and h1 >= 0.99:
        label = "square"
    elif 0.3 <= even_ratio <= 1.2 and -1.5 <= slope <= -0.6 and nulls == 0 and h1 >= 0.99:
        label = "saw"
    elif nulls >= 1 and h1 >= 0.5:
        label = "pulse"
    else:
        label = "other"
    return label, metrics

def main():
    files = sorted(glob.glob(os.path.join(ROOT, "S2 Tables", "*.wav"))) + sorted(glob.glob(os.path.join(ROOT, "Analog", "Basic*.wav")))
    out = {"tables_root": ROOT, "frame_length": FRAME, "harmonics_analyzed": NH, "tables": []}
    for path in files:
        t = read_table(path)
        if t is None:
            continue
        rel = os.path.relpath(path, ROOT).replace("\\", "/")
        frames = []
        for f in range(min(t["frames"], MAX_FRAMES)):
            x = t["samples"][f * FRAME:(f + 1) * FRAME]
            label, m = classify(harmonics(x))
            frames.append({"frame": f + 1, "label": label, **m})
        out["tables"].append({"relative_path": rel, "clm": t["clm"], "frames": t["frames"], "classified": len(frames), "frame_labels": frames})
        print(rel, t["frames"], [fr["label"] for fr in frames][:24], flush=True)
    json.dump(out, open(OUT, "w"), indent=1)

main()
