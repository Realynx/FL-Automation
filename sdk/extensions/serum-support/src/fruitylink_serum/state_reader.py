"""Read the Serum 2 state of a channel back out of a saved project file.

FruityLink can load a state into a running Serum instance
(``load_channel_plugin_state``) but, until the native read-state operation lands
in the C# bridge, cannot read it back directly. This module is the fallback: it
saves a snapshot copy of the current project through the SDK's permitted
``fl.project.save_copy(path)`` (a fresh path inside the MCP workspace), parses
the FLP event stream, and returns the wrapper's processor block for the wanted
channel as an :class:`fruitylink_serum.xfer.XferContainer`.

FLP layout used here (verified on Ember Tides v006, FL 26.1.3):

* ``"FLhd"`` | u32 length | i16 format | u16 channels | u16 ppq, then ``"FLdt"`` | u32 length
  followed by events: id < 64 → 1 byte value, < 128 → u16, < 192 → u32, else varint
  length + data.
* Event 64 (``NewChan``) carries the zero-based channel index; the events that follow
  until the next ``NewChan`` belong to that channel. Event 201 is the plugin name
  (UTF-16LE), event 213 the wrapper's plugin state; for Serum 2 that state carries the
  class-id bytes and the XferJson blocks :func:`xfer.split_wrapper_blocks` finds.
"""

from __future__ import annotations

import os
import struct
import time
from collections.abc import Iterator, Mapping
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Protocol

from . import xfer
from .builder import flatten_state

__all__ = [
    "EVENT_NEW_CHANNEL",
    "EVENT_PLUGIN_NAME",
    "EVENT_PLUGIN_STATE",
    "SERUM2_CLASS_ID_BYTES",
    "ChannelState",
    "diff_states",
    "encode_event",
    "iter_events",
    "read_channel_state",
    "read_channel_state_from_flp",
    "read_flp_channel_states",
]

EVENT_NEW_CHANNEL = 64
EVENT_PLUGIN_NAME = 201
EVENT_PLUGIN_STATE = 213
# The class id FL's wrapper stores in the plugin-state chunk ahead of Serum 2's XferJson blocks
# (memory order of GUID {56534558-6673-5073-6572-756D20320000}).
SERUM2_CLASS_ID_BYTES = bytes.fromhex("58455356736673506572756D20320000")
_PROCESSOR_KIND = 3
_CONTROLLER_KIND = 2


class _ProjectInfo(Protocol):
    path: str


class _Project(Protocol):
    @property
    def info(self) -> _ProjectInfo: ...

    def save_copy(self, path: str) -> None: ...


class _Studio(Protocol):
    @property
    def project(self) -> _Project: ...


@dataclass(frozen=True)
class ChannelState:
    """One channel's plugin state as saved by FL."""

    channel: int
    plugin_name: str
    processor: xfer.XferContainer | None
    controller: bytes | None
    raw: bytes


def iter_events(data: bytes) -> Iterator[tuple[int, int | bytes]]:
    """Yield ``(event id, value)`` pairs from an FLP file's data chunk."""
    if data[:4] != b"FLhd":
        raise ValueError("Not an FLP file (missing FLhd).")
    header_length = struct.unpack_from("<I", data, 4)[0]
    position = 8 + header_length
    if data[position : position + 4] != b"FLdt":
        raise ValueError("FLP data chunk (FLdt) not found.")
    end = position + 8 + struct.unpack_from("<I", data, position + 4)[0]
    position += 8
    while position < end:
        event = data[position]
        position += 1
        if event < 64:
            yield event, data[position]
            position += 1
        elif event < 128:
            yield event, struct.unpack_from("<H", data, position)[0]
            position += 2
        elif event < 192:
            yield event, struct.unpack_from("<I", data, position)[0]
            position += 4
        else:
            length = 0
            shift = 0
            while True:
                byte = data[position]
                position += 1
                length |= (byte & 0x7F) << shift
                shift += 7
                if not byte & 0x80:
                    break
            yield event, data[position : position + length]
            position += length


def encode_event(event: int, value: int | bytes) -> bytes:
    """Inverse of :func:`iter_events` for one event (used to build fixtures and tests)."""
    if event < 64:
        return bytes([event, int(value) & 0xFF])
    if event < 128:
        return bytes([event]) + struct.pack("<H", int(value))
    if event < 192:
        return bytes([event]) + struct.pack("<I", int(value))
    payload = bytes(value) if isinstance(value, (bytes, bytearray)) else bytes([int(value)])
    length = len(payload)
    varint = bytearray()
    while True:
        byte = length & 0x7F
        length >>= 7
        if length:
            varint.append(byte | 0x80)
        else:
            varint.append(byte)
            break
    return bytes([event]) + bytes(varint) + payload


def read_flp_channel_states(data: bytes) -> dict[int, ChannelState]:
    """Map channel index → plugin state for every channel that carries a wrapper state event."""
    states: dict[int, ChannelState] = {}
    current: int | None = None
    name = ""
    for event, value in iter_events(data):
        if event == EVENT_NEW_CHANNEL and isinstance(value, int):
            current, name = value, ""
        elif event == EVENT_PLUGIN_NAME and isinstance(value, bytes) and current is not None:
            name = value.decode("utf-16le", "replace").rstrip("\x00")
        elif event == EVENT_PLUGIN_STATE and isinstance(value, bytes) and current is not None and current not in states:
            processor: xfer.XferContainer | None = None
            controller: bytes | None = None
            for kind, block in xfer.split_wrapper_blocks(value):
                if kind == _PROCESSOR_KIND and processor is None:
                    processor = xfer.read_container(block)
                elif kind == _CONTROLLER_KIND and controller is None:
                    controller = block
            states[current] = ChannelState(current, name, processor, controller, value)
    return states


def read_channel_state_from_flp(path: str | os.PathLike[str], channel: int, *, require_serum: bool = True) -> xfer.XferContainer:
    """Processor state of ``channel`` from a saved FLP; refuses channels without a Serum 2 state."""
    states = read_flp_channel_states(Path(path).read_bytes())
    state = states.get(channel)
    if state is None:
        raise LookupError(f"Channel {channel} has no plugin state in {path} (channels with state: {sorted(states)}).")
    if require_serum and SERUM2_CLASS_ID_BYTES not in state.raw:
        raise LookupError(f"Channel {channel} ({state.plugin_name or 'unnamed plugin'}) does not hold Serum 2.")
    if state.processor is None:
        raise LookupError(f"Channel {channel} has no XferJson processor block.")
    return state.processor


def read_channel_state(
    fl: _Studio,
    channel: int,
    *,
    workspace_dir: str | os.PathLike[str] | None = None,
    keep: bool = False,
) -> xfer.XferContainer:
    """Save a snapshot copy of the open project and return ``channel``'s Serum 2 processor state.

    The copy is written next to the current project (or into ``workspace_dir``) as
    ``state-read-<channel>-<timestamp>.flp`` and deleted afterwards unless ``keep``. The
    snapshot path must be a fresh file inside the MCP workspace; the host refuses others.
    This is the fallback until the bridge exposes a native read-state operation.
    """
    info = fl.project.info  # a property on fruitylink.project.Project, not a method
    directory = Path(workspace_dir) if workspace_dir is not None else Path(info.path).parent
    if not str(info.path) and workspace_dir is None:
        raise ValueError("The project has no path yet; pass workspace_dir.")
    snapshot = directory / f"state-read-{channel}-{time.strftime('%Y%m%d-%H%M%S')}-{os.getpid()}.flp"
    fl.project.save_copy(str(snapshot))
    try:
        return read_channel_state_from_flp(snapshot, channel)
    finally:
        if not keep:
            try:
                snapshot.unlink()
            except OSError:
                pass


def diff_states(before: xfer.XferContainer | Mapping[str, Any], after: xfer.XferContainer | Mapping[str, Any]) -> dict[str, tuple[Any, Any]]:
    """Flattened keys whose value differs (missing keys appear as ``None``)."""
    a = flatten_state(before.state if isinstance(before, xfer.XferContainer) else before)
    b = flatten_state(after.state if isinstance(after, xfer.XferContainer) else after)
    changed: dict[str, tuple[Any, Any]] = {}
    for key in sorted(set(a) | set(b)):
        if a.get(key) != b.get(key):
            changed[key] = (a.get(key), b.get(key))
    return changed
