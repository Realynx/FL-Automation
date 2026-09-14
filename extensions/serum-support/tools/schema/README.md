# Schema data generators

Offline scripts that produced `src/fruitylink_serum/data/*.json` (see `docs/state-schema.md`).
They read the locally installed Serum 2 library (`SERUM_PRESETS_PATH` or
`~/Documents/Xfer/Serum 2 Presets`) and write derived statistics only. Run with a zstd-capable
interpreter (Python 3.14 or the `zstandard` package):

    python tools/schema/aggregate.py        # -> tools/schema/aggregate.json
    python tools/schema/classify_tables.py  # -> tools/schema/wavetables-raw.json
    python tools/schema/generate_data.py    # -> src/fruitylink_serum/data/*.json (parameter-map, fx-schema, filters, wavetables)
    python tools/schema/mod_matrix.py       # -> src/fruitylink_serum/data/mod-matrix.json

Unit/scale annotations and FL anchors live in `generate_data.py` (`RULES`, `FL_ANCHORS`).
`mod_matrix.py` counts `ModSlot` source ids and `(type, param) -> destModuleParamID` pairs and
cross-checks them against the parameter enums found as declaration strings in `Serum2.vst3`
(`BINARY_ENUMS`); source-id names (`SOURCE_NAMES`) are inferred and carry a `confidence`.

Every row in the generated files carries a `confidence` (`verified`, `inferred`,
`observed-only`, `derived-labels`, `pattern`, `unknown`, `binary-enum`, `library-majority`).
`tools/harvest_live.py` upgrades rows to `verified` from a live set-then-read loop (two
`fl_execute_python` requests: `write_phase`, then `read_phase`; then `harvest_live.py apply
<result.json>` offline).
Set `SERUM_EXTRA_PRESETS` to a folder of additional `.SerumPreset` files (for example
states extracted from a project) to include them. Intermediate JSON files are ignored by git.

## Consumers

`fruitylink_serum.builder.SerumPatch` and `fruitylink_serum.schema` load the generated files at
import time through `importlib.resources`: `parameter-map.json` (keys, scales, FL anchors),
`wavetables.json` (shape names → table path and `kParamTablePos`), `filters.json` (short filter
names → `kParamType`) and `fx-schema.json` (effect type ids and per-effect keys). Regenerate all
four together; the builder's tests assert the anchor rows they depend on.
