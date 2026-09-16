"""Optional Serum support kept outside the core FruityLink SDK."""

from . import schema
from .analysis import describe_audition, describe_wav
from .audition import audition_candidates
from .builder import (
    PatchError,
    Schema,
    SerumPatch,
    describe_parameters,
    flatten_state,
    init_container,
    load_schema,
)
from .describe import (
    describe_container,
    describe_preset,
    describe_state,
    format_description,
    signal_path_notes,
)
from .inventory import IndexedPreset, PresetFile, discover_serum_roots, iter_presets, query_index
from .loading import (
    LoadResult,
    automation_links,
    build_preset,
    find_presets,
    list_folders,
    load_preset,
    normalize_processor_state,
    parameters,
    read_preset,
    read_state,
    resolve_preset,
    snapshot_preset,
    to_vstpreset,
)
from .state_reader import ChannelState, diff_states, read_channel_state, read_channel_state_from_flp

__all__ = [
    "ChannelState",
    "IndexedPreset",
    "LoadResult",
    "PatchError",
    "PresetFile",
    "Schema",
    "SerumPatch",
    "audition_candidates",
    "automation_links",
    "build_preset",
    "describe_audition",
    "describe_container",
    "describe_parameters",
    "describe_preset",
    "describe_state",
    "describe_wav",
    "diff_states",
    "discover_serum_roots",
    "find_presets",
    "flatten_state",
    "format_description",
    "init_container",
    "iter_presets",
    "list_folders",
    "load_preset",
    "load_schema",
    "normalize_processor_state",
    "parameters",
    "query_index",
    "read_channel_state",
    "read_channel_state_from_flp",
    "read_preset",
    "read_state",
    "resolve_preset",
    "schema",
    "signal_path_notes",
    "snapshot_preset",
    "to_vstpreset",
]
