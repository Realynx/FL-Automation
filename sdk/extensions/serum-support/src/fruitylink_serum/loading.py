"""Resolve, generate and load Serum 2 presets through the FruityLink host.

Three layers, each usable on its own:

* Library navigation: :func:`list_folders`, :func:`find_presets`, :func:`resolve_preset`
  work on preset FILES under a Serum root (``<root>/Presets/...``), complementing the
  metadata index queries in :mod:`fruitylink_serum.inventory`.
* File generation: :func:`read_preset`, :func:`parameters`, :func:`build_preset` decode the
  ``.SerumPreset`` container, apply named parameter overrides and write a new file; and
  :func:`normalize_processor_state` + :func:`to_vstpreset` turn a preset's state into the
  processor component state FL's wrapper accepts and wrap it as a VST3 ``.vstpreset``.
* Host loading: :func:`load_preset` hands a loadable file to the FruityLink operation
  ``load_channel_plugin_state`` / ``load_mixer_effect_state`` on a channel or FX slot that
  already hosts Serum 2, and returns the host's verification line.

Parameter override keys use ``Section/plainParams/kParamName`` (or ``Section/kParamName``
as shorthand). Values keep the float width of the value they replace. Overrides never
create sections that do not exist in the base preset, so a typo cannot silently add a
meaningless key. Nothing here decodes wavetables or audio; proprietary preset content is
read from the user's own library at run time and never bundled.

Verification status (FL 26.1.3.5570, Serum 2 VST3, 2026-09-13): a ``.vstpreset`` whose
class id is Serum's GUID string with braces/dashes removed loads through
``load_channel_plugin_state`` and changes the state in place (same instance, no controller
chunk needed); a generated preset (unison/detune overrides) read back its new values. Raw
``.SerumPreset`` files and the synthesised ``.fst`` are silently ignored by the wrapper, so
:func:`load_preset` converts ``.SerumPreset`` to ``.vstpreset`` by default and the
``format`` choice stays explicit for other cases.
"""

from __future__ import annotations

import base64
import os
import re
import tempfile
from collections.abc import Iterator, Mapping
from dataclasses import replace
from pathlib import Path
from typing import Any, Protocol

from . import cbor, vstpreset, xfer
from .inventory import PRESET_EXTENSIONS, PresetFile, discover_serum_roots, iter_presets

__all__ = [
    "LOADABLE_EXTENSIONS",
    "PROCESSOR_DROPPED_SECTIONS",
    "PROCESSOR_IDENTITY",
    "SERUM_PRESET_EXTENSION",
    "LoadResult",
    "automation_links",
    "build_preset",
    "find_presets",
    "list_folders",
    "load_preset",
    "normalize_processor_state",
    "parameters",
    "read_preset",
    "read_state",
    "resolve_preset",
    "snapshot_preset",
    "to_vstpreset",
]

SERUM_PRESET_EXTENSION = ".serumpreset"
LOADABLE_EXTENSIONS = frozenset({".fst", ".vstpreset", ".fxp", SERUM_PRESET_EXTENSION})
_FILE_ONLY_METADATA = ("fileType", "presetName", "presetAuthor", "presetDescription", "tags")

# Top-level CBOR sections present in ``.SerumPreset`` files but absent from the processor state FL's
# wrapper saves (UI/editor-only state plus file identity). Live on FL 26.1.3: a factory preset wrapped
# as-is was ignored by Serum's setState; the same preset with these keys removed and the processor
# identity fields below applied loaded and read back correctly. Keep this list explicit.
PROCESSOR_DROPPED_SECTIONS = frozenset({
    "Osc", "WTOsc", "Filter", "SerumGUI", "SpectralOsc", "GranularOsc", "MultiSampleOsc", "ClipPlayer",
    "arpBankDisplayName", "clipBankDisplayName", "fileType", "presetName", "presetAuthor", "presetDescription",
})
# Identity fields of the processor component state as saved by Serum 2.1.4 through FL's wrapper
# (metadata and top-level CBOR scalars agree). Whether Serum needs ``component`` alone or also the
# version fields was not isolated live; the whole tested set is applied.
PROCESSOR_IDENTITY: dict[str, Any] = {"component": "processor", "product": "Serum2", "productVersion": "2.1.4", "version": 10.0}


class _Operations(Protocol):
    def load_channel_plugin_state(self, *, channel: int, path: str, use_channel_loader: bool = False) -> str: ...

    def load_mixer_effect_state(self, *, track: int, slot: int, path: str) -> str: ...

    def get_channel_plugin_state(self, *, channel: int) -> str: ...

    def get_mixer_effect_state(self, *, track: int, slot: int) -> str: ...


class _Studio(Protocol):
    @property
    def ops(self) -> _Operations: ...


class LoadResult(str):
    """The host's verification line (a ``str``, so existing callers keep working) plus safety fields.

    ``warnings`` lists what the load may have silently undone: the whole-state replacement (globals such
    as the bend range reset), FL automation clips on the target channel whose targets name this channel or
    one of its plugin parameters, and signal-path notes decoded from the preset (all oscillator direct
    levels at 0, bus/direct routing, disabled or dry voice filters, no velocity modulation). ``automation``
    holds the link records; ``signal_path`` the decoded notes. ``to_dict()`` is JSON-friendly.
    """

    verification: str
    path: Path
    warnings: tuple[str, ...]
    automation: tuple[dict[str, Any], ...]
    signal_path: tuple[str, ...]

    def __new__(cls, verification: str, *, path: Path, warnings: tuple[str, ...] = (),
                automation: tuple[dict[str, Any], ...] = (), signal_path: tuple[str, ...] = ()) -> LoadResult:
        result = super().__new__(cls, verification)
        result.verification = verification
        result.path = path
        result.warnings = tuple(warnings)
        result.automation = tuple(automation)
        result.signal_path = tuple(signal_path)
        return result

    def to_dict(self) -> dict[str, Any]:
        return {"verification": self.verification, "path": str(self.path), "warnings": list(self.warnings),
                "automation": [dict(link) for link in self.automation], "signal_path": list(self.signal_path)}


_AUTOMATION_MARKER = ": automation clip -> "
_EVENT_TARGET = re.compile(r"^event 0x([0-9a-fA-F]{1,8})$")


def automation_links(fl: Any, channel: int, *, max_channels: int = 1000) -> list[dict[str, Any]]:
    """FL automation clips whose targets appear to drive ``channel`` (best effort, read-only).

    Scans every channel's ``get_channel_plugin`` text (``"<clip>: automation clip -> <target>, ..."``) and
    attributes a target to ``channel`` when it names the channel (``"certain"``) or matches the exact name of
    one of the channel's plugin parameters (``"possible"``: another instance of the same plugin could own
    the link). Live, FL names no plugin-parameter target at all and prints ``event 0x...`` instead; those
    ids are decoded with ``AutomationTarget.from_event_id`` (``(channel << 16) | 0x8000 | index`` for a
    generator parameter; ``0x180cd`` = channel 1, parameter 205 "Filter 1 Freq"), so a decoded generator
    parameter of ``channel`` is ``"certain"`` and its name is read from the live parameter list. Targets
    that resolve to another channel, a built-in control (volume, pan, pitch, mixer) or no parameter of this
    one are ``"other"``; ids that decode to nothing are ``"unknown"``; neither counts as a hit.
    Each record: ``{"clip_channel", "clip_name", "target", "attribution", "parameter_index",
    "parameter_name", "decoded"}`` where ``decoded`` is the event id's ``{"kind", "index", "slot",
    "parameter"}`` or None.
    """
    ops = fl.ops
    count = int(ops.get_channel_count())
    target_name = str(ops.get_channel_name(channel=channel))
    links: list[dict[str, Any]] = []
    for index in range(min(count, max_channels)):
        if index == channel:
            continue
        text = str(ops.get_channel_plugin(channel=index))
        if _AUTOMATION_MARKER not in text:
            continue
        clip_name, targets = text.split(_AUTOMATION_MARKER, 1)
        for target in (t.strip() for t in targets.split(",") if t.strip()):
            record: dict[str, Any] = {"clip_channel": index, "clip_name": clip_name, "target": target,
                                      "attribution": "other", "parameter_index": None, "parameter_name": None,
                                      "decoded": None}
            event = _EVENT_TARGET.match(target)
            if event is not None:
                _attribute_event(ops, channel, int(event.group(1), 16), record)
                links.append(record)
                continue
            if target_name and target_name.casefold() in target.casefold():
                record["attribution"] = "certain"
            match = _find_parameter(ops, channel, target, target_name)
            if match is not None:
                record["parameter_index"], record["parameter_name"] = match
                if record["attribution"] != "certain":
                    record["attribution"] = "possible"
            links.append(record)
    return links


def _attribute_event(ops: Any, channel: int, event_id: int, record: dict[str, Any]) -> None:
    """Fill ``record`` from a decoded native event id (see ``AutomationTarget.from_event_id``)."""
    from fruitylink.automation_records import AutomationTarget

    decoded = AutomationTarget.from_event_id(event_id)
    if decoded is None:
        record["attribution"] = "unknown"
        return
    record["decoded"] = {"kind": decoded.kind, "index": decoded.index, "slot": decoded.slot,
                         "parameter": decoded.parameter}
    if decoded.kind == "plugin_parameter" and decoded.slot == -1 and decoded.index == channel:
        record["attribution"] = "certain"
        record["parameter_index"] = decoded.parameter
        record["parameter_name"] = _parameter_name(ops, channel, decoded.parameter)


def _parameter_name(ops: Any, channel: int, index: int) -> str | None:
    """Name of generator parameter ``index`` on ``channel`` from the live list; None when unavailable."""
    query = getattr(ops, "query_plugin_parameters", None)
    if query is None:
        return None
    try:
        page = query(channel_or_track=channel, slot=-1, filter=None, offset=index, limit=1)
    except Exception:  # noqa: BLE001 - the name is advisory; the index is still reported
        return None
    for item in page.items:
        if int(item.index) == index:
            return str(item.name)
    return None


def _find_parameter(ops: Any, channel: int, target: str, channel_name: str) -> tuple[int, str] | None:
    """(index, name) of the plugin parameter on ``channel`` whose exact name is ``target`` (or ``target``
    minus a ``"<channel name> - "`` prefix); None when the host lacks the query or nothing matches."""
    query = getattr(ops, "query_plugin_parameters", None)
    if query is None:
        return None
    names = [target]
    for separator in (" - ", ": "):
        if channel_name and target.casefold().startswith(channel_name.casefold() + separator):
            names.append(target[len(channel_name) + len(separator):])
    for name in names:
        offset = 0
        while True:
            page = query(channel_or_track=channel, slot=-1, filter=name, offset=offset, limit=512)
            for item in page.items:
                if item.name == name:
                    return int(item.index), str(item.name)
            if page.next_offset is None or page.next_offset <= offset:
                break
            offset = page.next_offset
    return None


def _signal_path_warnings(path: Path) -> tuple[list[str], list[str]]:
    """Decode the preset about to be loaded and return (signal-path notes, decode problems)."""
    from . import describe  # local import: describe depends on this module

    try:
        if path.suffix.casefold() == ".vstpreset":
            container = xfer.read_container(vstpreset.read_vstpreset(path.read_bytes()).component)
        elif path.suffix.casefold() == SERUM_PRESET_EXTENSION:
            container = read_preset(path)
        else:
            return [], [f"signal-path check skipped: {path.suffix} files are not decoded"]
        return describe.signal_path_notes(container.state), []
    except Exception as error:  # noqa: BLE001 - advisory only
        return [], [f"signal-path check skipped: {type(error).__name__}: {error}"]


_UNSAFE_NAME = re.compile(r'[\\/:*?"<>|]+')


def _safe_file_name(name: str) -> str:
    return _UNSAFE_NAME.sub("_", name).strip() or "preset"


def _library_relative(preset: PresetFile) -> str:
    """Path relative to ``<root>/Presets`` with POSIX separators."""
    parts = preset.relative_path.parts
    return Path(*parts[1:]).as_posix() if parts and parts[0] == "Presets" else preset.relative_path.as_posix()


def _root(root: Path | str | None) -> Path:
    if root is not None:
        return Path(root).expanduser().resolve()
    roots = discover_serum_roots()
    if not roots:
        raise FileNotFoundError("No Serum preset root found; pass root= explicitly.")
    return roots[0]


def list_folders(root: Path | str | None = None) -> tuple[str, ...]:
    """Relative folder paths (POSIX separators) under ``<root>/Presets`` that contain preset files."""
    base = _root(root) / "Presets"
    if not base.is_dir():
        raise FileNotFoundError(f"Serum preset directory not found: {base}")
    folders: set[str] = set()
    for path in base.rglob("*"):
        if path.is_file() and path.suffix.casefold() in PRESET_EXTENSIONS:
            folders.add(path.parent.relative_to(base).as_posix())
    return tuple(sorted(folders, key=str.casefold))


def find_presets(
    root: Path | str | None = None,
    *,
    folder: str | None = None,
    text: str | None = None,
    extensions: frozenset[str] | None = None,
    limit: int = 50,
) -> tuple[PresetFile, ...]:
    """Preset files under the root, optionally restricted to a folder prefix and a name substring."""
    if limit <= 0:
        raise ValueError("limit must be positive.")
    base = _root(root)
    prefix = folder.replace("\\", "/").strip("/").casefold() if folder else None
    needle = text.casefold() if text else None
    wanted = extensions or PRESET_EXTENSIONS
    found: list[PresetFile] = []
    for preset in iter_presets(base):
        if preset.path.suffix.casefold() not in wanted:
            continue
        relative = _library_relative(preset).casefold()
        if prefix and not relative.startswith(prefix + "/"):
            continue
        if needle and needle not in preset.path.stem.casefold() and needle not in relative:
            continue
        found.append(preset)
        if len(found) >= limit:
            break
    return tuple(found)


def resolve_preset(name_or_path: str | os.PathLike[str], root: Path | str | None = None) -> Path:
    """Resolve an absolute/relative file path, or a unique preset name within the root, to a file."""
    candidate = Path(name_or_path).expanduser()
    if candidate.suffix.casefold() in LOADABLE_EXTENSIONS and candidate.is_file():
        return candidate.resolve()
    base = _root(root)
    if candidate.suffix.casefold() in LOADABLE_EXTENSIONS and (base / "Presets" / candidate).is_file():
        return (base / "Presets" / candidate).resolve()
    wanted = str(name_or_path).casefold()
    library = base / "Presets"
    if not library.is_dir():
        raise FileNotFoundError(f"Serum preset directory not found: {library}")
    matches = sorted(
        (path for path in library.rglob("*")
         if path.is_file() and path.suffix.casefold() in LOADABLE_EXTENSIONS and path.stem.casefold() == wanted),
        key=lambda path: str(path).casefold(),
    )
    if len(matches) == 1:
        return matches[0].resolve()
    if not matches:
        raise FileNotFoundError(f"No preset named {name_or_path!r} under {base}.")
    listing = ", ".join(path.relative_to(library).as_posix() for path in matches[:6])
    raise LookupError(f"{len(matches)} presets are named {name_or_path!r}; pass a relative path instead: {listing}")


def read_preset(path: str | os.PathLike[str]) -> xfer.XferContainer:
    """Decode a ``.SerumPreset`` file (or any XferJson container file)."""
    return xfer.read_container(Path(path).read_bytes())


def parameters(container: xfer.XferContainer, *, section: str | None = None) -> dict[str, float]:
    """Flatten named parameters as ``Section/plainParams/kParamName`` -> value (nested sections included)."""
    result: dict[str, float] = {}

    def walk(node: Any, prefix: str) -> None:
        if not isinstance(node, dict):
            return
        for key, value in node.items():
            if not isinstance(key, str):
                continue
            path = f"{prefix}/{key}" if prefix else key
            if isinstance(value, dict):
                walk(value, path)
            elif cbor.is_finite_number(value) and prefix.endswith("plainParams"):
                result[path] = float(value)

    for key, value in container.state.items():
        if isinstance(key, str) and (section is None or key == section):
            walk(value, key)
    return result


def _apply_override(state: dict[str, Any], key: str, value: Any) -> None:
    parts = [part for part in re.split(r"[/.]", key) if part]
    if len(parts) < 2:
        raise KeyError(f"Override key {key!r} must name a section and a parameter.")
    node: Any = state
    for part in parts[:-1]:
        if not isinstance(node, dict) or part not in node:
            if isinstance(node, dict) and part != "plainParams" and "plainParams" in node and parts[-1] in node["plainParams"]:
                node = node["plainParams"]
                break
            raise KeyError(f"Override key {key!r}: section {part!r} does not exist in the base preset.")
        node = node[part]
    if isinstance(node, dict) and parts[-1] not in node and "plainParams" in node and isinstance(node["plainParams"], dict):
        node = node["plainParams"]
    if not isinstance(node, dict):
        raise KeyError(f"Override key {key!r}: {parts[-2]!r} is not a parameter map.")
    if parts[-1] not in node:
        # Serum omits parameters that sit at their defaults, so a named parameter may legitimately be
        # absent from the base; it can be introduced only inside an existing ``plainParams`` map.
        if parts[-2] != "plainParams" or not parts[-1].startswith("kParam") or not cbor.is_finite_number(value):
            raise KeyError(f"Override key {key!r}: parameter {parts[-1]!r} does not exist in the base preset.")
        node[parts[-1]] = cbor.Float32(value)
        return
    current = node[parts[-1]]
    if isinstance(current, bool) or not cbor.is_finite_number(current):
        if type(current) is not type(value):
            raise TypeError(f"Override {key!r} must keep type {type(current).__name__}.")
        node[parts[-1]] = value
        return
    if not cbor.is_finite_number(value):
        raise TypeError(f"Override {key!r} must be a finite number.")
    node[parts[-1]] = cbor.Float32(value) if isinstance(current, cbor.Float32) else (float(value) if isinstance(current, float) else int(value))


def build_preset(
    base: str | os.PathLike[str] | xfer.XferContainer,
    overrides: Mapping[str, Any] | None = None,
    *,
    name: str | None = None,
    output_dir: str | os.PathLike[str] | None = None,
    root: Path | str | None = None,
) -> Path:
    """Write a new ``.SerumPreset`` derived from ``base`` with named parameter overrides applied.

    ``base`` is a preset name, a file path, or an already decoded container. The result goes to
    ``output_dir`` (default: a fresh temporary directory) and is named after ``name`` or the base
    preset with a ``-generated`` suffix. Returns the written path.
    """
    container = base if isinstance(base, xfer.XferContainer) else read_preset(resolve_preset(base, root))
    state = cbor.decode(cbor.encode(container.state))  # deep copy preserving float widths
    for key, value in (overrides or {}).items():
        _apply_override(state, key, value)
    metadata = dict(container.metadata)
    base_name = str(metadata.get("presetName") or (Path(base).stem if not isinstance(base, xfer.XferContainer) else "preset"))
    preset_name = name or f"{base_name}-generated"
    metadata["presetName"] = preset_name
    metadata.setdefault("fileType", "SerumPreset")
    if "presetName" in state:
        state["presetName"] = preset_name
    generated = replace(container, metadata=metadata, state=state)
    directory = Path(output_dir) if output_dir is not None else Path(tempfile.mkdtemp(prefix="fruitylink-serum-"))
    directory.mkdir(parents=True, exist_ok=True)
    target = directory / (_safe_file_name(preset_name) + ".SerumPreset")
    target.write_bytes(xfer.write_container(generated))
    return target


def normalize_processor_state(container: xfer.XferContainer, *, identity: Mapping[str, Any] | None = None) -> xfer.XferContainer:
    """Turn a preset-file container into the processor component state Serum's setState accepts.

    Drops :data:`PROCESSOR_DROPPED_SECTIONS` from the CBOR map, removes file-only metadata, and stamps
    :data:`PROCESSOR_IDENTITY` (``component``/``product``/``productVersion``/``version``) into both the
    metadata and the state's top-level scalars. Containers that already carry
    ``component == "processor"`` are returned unchanged apart from identity stamping.
    """
    stamp = dict(PROCESSOR_IDENTITY)
    if identity:
        stamp.update(identity)
    state = {key: value for key, value in container.state.items() if not (isinstance(key, str) and key in PROCESSOR_DROPPED_SECTIONS)}
    metadata = {key: value for key, value in container.metadata.items() if key not in _FILE_ONLY_METADATA}
    for key, value in stamp.items():
        metadata[key] = value
        state[key] = value
    return replace(container, metadata=metadata, state=state)


def to_vstpreset(
    source: str | os.PathLike[str] | xfer.XferContainer,
    output: str | os.PathLike[str] | None = None,
    *,
    class_id: str = vstpreset.SERUM2_CLASS_ID,
    controller: bytes | None = None,
    root: Path | str | None = None,
    normalize: bool = True,
    force_class_id: bool = False,
) -> Path:
    """Wrap a preset's state as the VST3 component state of a ``.vstpreset`` file.

    By default the state is passed through :func:`normalize_processor_state` (the transformation
    that made factory ``.SerumPreset`` files load live); ``normalize=False`` wraps the container
    verbatim, which is only useful for state that already came from the wrapper. An optional
    controller chunk can be supplied verbatim; live loads did not need one. The class id must be the
    GUID-string order (:data:`vstpreset.SERUM2_CLASS_ID`).
    """
    if class_id.upper() != vstpreset.SERUM2_CLASS_ID and not force_class_id:
        raise ValueError(
            f"class_id {class_id!r} is not the Serum 2 id FL's wrapper accepts ({vstpreset.SERUM2_CLASS_ID}); a wrong "
            "id is ignored silently by the host. Pass force_class_id=True only for another plugin.")
    container = source if isinstance(source, xfer.XferContainer) else read_preset(resolve_preset(source, root))
    processor = normalize_processor_state(container) if normalize else container
    if normalize and processor.state.get("component") != "processor":
        raise ValueError("Normalisation did not produce a processor state; the container is not a Serum 2 state map.")
    component = xfer.write_container(processor)
    data = vstpreset.write_vstpreset(vstpreset.VstPreset(class_id=class_id, component=component, controller=controller))
    if output is None:
        stem = _safe_file_name(str(container.metadata.get("presetName") or "preset"))
        output = Path(tempfile.mkdtemp(prefix="fruitylink-serum-")) / (stem + ".vstpreset")
    target = Path(output)
    target.parent.mkdir(parents=True, exist_ok=True)
    target.write_bytes(data)
    return target


def load_preset(
    fl: _Studio,
    channel: int,
    name_or_path: str | os.PathLike[str],
    *,
    slot: int | None = None,
    format: str = "auto",
    root: Path | str | None = None,
    use_channel_loader: bool = False,
) -> LoadResult:
    """Load a preset into the Serum 2 instance already on ``channel`` (or mixer ``channel``/``slot``).

    ``format``: ``"auto"`` passes ``.fst``/``.vstpreset``/``.fxp`` files straight to the host and
    converts ``.SerumPreset`` files to ``.vstpreset`` first (the format FL's wrapper accepted live);
    ``"direct"`` hands any file to the host unchanged (a raw ``.SerumPreset`` is then ignored);
    ``"vstpreset"`` always converts. ``use_channel_loader`` is only meaningful for FL ``.fst`` files and
    is not recommended: live it renamed the channel and could start FL's transport.
    Returns the host's verification line as a :class:`LoadResult` (a ``str`` with ``warnings``,
    ``automation`` and ``signal_path`` fields). The load is never blocked: the warnings say that the whole
    plugin state is replaced (globals such as the bend range reset; bake them into the preset), which FL
    automation clips on the channel drive plugin parameters that the new state may take out of the signal
    path, and what the decoded preset implies (all oscillator direct levels at 0, bus routing, dry/disabled
    filters, no velocity modulation). Confirm the sound with parameter displays in a separate request or an
    isolated render.
    """
    path = resolve_preset(name_or_path, root)
    if format not in {"auto", "direct", "vstpreset"}:
        raise ValueError("format must be 'auto', 'direct' or 'vstpreset'.")
    warnings = ["replaces the whole plugin state of the target instance: globals such as the pitch-bend range "
                "(kParamBendRangeUp/Dn), mono/legato and macros reset to the preset's values; bake them into the "
                "preset (SerumPatch.bend_range, .mono, .macro) instead of setting them before the load"]
    links: list[dict[str, Any]] = []
    if slot is None:
        try:
            links = automation_links(fl, channel)
        except Exception as error:  # noqa: BLE001 - advisory only
            warnings.append(f"automation links could not be inspected: {type(error).__name__}: {error}")
        for link in links:
            if link["attribution"] not in ("certain", "possible"):
                continue
            if link["decoded"] is not None:
                name = f" '{link['parameter_name']}'" if link["parameter_name"] else ""
                warnings.append(f"channel {channel} automates plugin parameter {link['parameter_index']}{name} "
                                f"(clip '{link['clip_name']}' on channel {link['clip_channel']}); the preset replaces "
                                "the state that link drives, so check that it stays audible")
                continue
            where = f" (parameter {link['parameter_index']})" if link["parameter_index"] is not None else ""
            qualifier = "" if link["attribution"] == "certain" else " (name match; the link may belong to another channel)"
            warnings.append(f"FL automation clip {link['clip_channel']} '{link['clip_name']}' drives '{link['target']}'"
                            f"{where} on this channel{qualifier}; check that the new state keeps it audible")
        unknown = sum(1 for link in links if link["attribution"] == "unknown")
        if unknown:
            warnings.append(f"{unknown} automation link(s) report an event id that does not decode to a channel, "
                            "mixer or plugin-parameter target and could not be attributed")
    signal_path, problems = _signal_path_warnings(path)
    warnings.extend(problems)
    warnings.extend(f"preset: {note}" for note in signal_path)
    if format == "vstpreset" or (format == "auto" and path.suffix.casefold() == SERUM_PRESET_EXTENSION):
        path = to_vstpreset(path)
    if slot is None:
        verification = fl.ops.load_channel_plugin_state(channel=channel, path=str(path), use_channel_loader=use_channel_loader)
    else:
        verification = fl.ops.load_mixer_effect_state(track=channel, slot=slot, path=str(path))
    return LoadResult(str(verification), path=path, warnings=tuple(warnings), automation=tuple(links),
                      signal_path=tuple(signal_path))


_STATE_KINDS = {"processor": 3, "controller": 2}


def read_state(fl: _Studio, channel: int, *, slot: int | None = None, component: str = "processor") -> xfer.XferContainer:
    """Decode the live Serum 2 state of the instance on ``channel`` (or mixer ``channel``/``slot``).

    Uses the host's ``get_channel_plugin_state`` / ``get_mixer_effect_state`` operations, which read the
    wrapper's plugin-data record through a temporary project copy (the live project is unchanged), then
    returns the ``processor`` (default) or ``controller`` XferJson block as a decoded container. Together
    with :func:`parameters` this is the set-then-read loop for learning what a host parameter maps to:
    change one FL parameter, read the state, diff the flattened parameters.
    """
    if component not in _STATE_KINDS:
        raise ValueError("component must be 'processor' or 'controller'.")
    if slot is None:
        encoded = fl.ops.get_channel_plugin_state(channel=channel)
    else:
        encoded = fl.ops.get_mixer_effect_state(track=channel, slot=slot)
    blob = base64.b64decode(encoded)
    blocks = xfer.split_wrapper_blocks(blob)
    if not blocks:
        raise LookupError("The plugin state holds no XferJson block; the slot does not host Serum 2.")
    for kind, block in blocks:
        if kind == _STATE_KINDS[component]:
            return xfer.read_container(block)
    raise LookupError(f"The plugin state has no {component} block (kinds present: {sorted(k for k, _ in blocks)}).")


def snapshot_preset(
    fl: _Studio,
    channel: int,
    name: str,
    *,
    slot: int | None = None,
    output_dir: str | os.PathLike[str] | None = None,
) -> Path:
    """Write the live Serum 2 state of a channel/slot as ``<name>.SerumPreset`` and return its path.

    The file carries the processor state as captured (Serum's UI-only sections are absent, which the
    loader does not need) with preset-file metadata (``fileType``, ``presetName``) so it can be found,
    diffed, edited with :func:`build_preset` and loaded again with :func:`load_preset`. Default
    ``output_dir`` is a fresh temporary directory. ``name`` may also be a file path (``.SerumPreset``
    or a path with a directory part): the file is then written there and the preset takes the stem as
    its name.
    """
    if not name.strip():
        raise ValueError("A preset name is required.")
    given = Path(name)
    target: Path | None = None
    if given.suffix.casefold() == SERUM_PRESET_EXTENSION or given.parent != Path("."):
        # A path was passed as the name (live 2026-09-14: it was flattened into a %TEMP% file name).
        target = given.expanduser() if given.suffix.casefold() == SERUM_PRESET_EXTENSION else given.with_suffix(".SerumPreset")
        name = given.stem if given.suffix.casefold() == SERUM_PRESET_EXTENSION else given.name
    container = read_state(fl, channel, slot=slot)
    state = dict(container.state)
    for key in ("component",):
        state.pop(key, None)
    state["fileType"] = "SerumPreset"
    state["presetName"] = name
    metadata = {k: v for k, v in container.metadata.items() if k != "component"}
    metadata.update({"fileType": "SerumPreset", "presetName": name})
    if target is None:
        directory = Path(output_dir) if output_dir is not None else Path(tempfile.mkdtemp(prefix="fruitylink-serum-"))
        target = directory / (_safe_file_name(name) + ".SerumPreset")
    elif output_dir is not None and not target.is_absolute():
        target = Path(output_dir) / target
    target.parent.mkdir(parents=True, exist_ok=True)
    target.write_bytes(xfer.write_container(xfer.XferContainer(metadata=metadata, state=state, codec=container.codec)))
    return target


def iter_loadable(root: Path | str | None = None) -> Iterator[PresetFile]:
    """Preset files whose extension the host or the converter can handle."""
    for preset in iter_presets(_root(root)):
        if preset.path.suffix.casefold() in LOADABLE_EXTENSIONS:
            yield preset
