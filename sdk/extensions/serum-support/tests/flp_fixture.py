"""Build minimal synthetic FLP files for state-reader tests (no proprietary content)."""

from __future__ import annotations

import struct
from typing import Any

from fruitylink_serum import state_reader, xfer


def wrapper_state(processor: bytes | None, controller: bytes | None = None, *, class_id: bytes = state_reader.SERUM2_CLASS_ID_BYTES) -> bytes:
    """A wrapper plugin-state chunk shaped like FL's: header with class-id bytes, then XferJson blocks."""
    chunk = b"\x01\x00\x00\x00" + b"\x00" * 20 + class_id + b"\x00" * 8
    if processor is not None:
        chunk += struct.pack("<III", 3, len(processor), 0) + processor
    if controller is not None:
        chunk += struct.pack("<III", 2, len(controller), 0) + controller
    return chunk


def flp_bytes(channels: list[dict[str, Any]], *, ppq: int = 96) -> bytes:
    """``channels``: ``[{"index": 0, "plugin": "Fruity Wrapper", "state": bytes | None}, ...]``."""
    events = bytearray()
    for channel in channels:
        events += state_reader.encode_event(state_reader.EVENT_NEW_CHANNEL, int(channel["index"]))
        events += state_reader.encode_event(state_reader.EVENT_PLUGIN_NAME, (str(channel.get("plugin", "")) + "\x00").encode("utf-16le"))
        if channel.get("state") is not None:
            events += state_reader.encode_event(state_reader.EVENT_PLUGIN_STATE, bytes(channel["state"]))
    header = b"FLhd" + struct.pack("<I", 6) + struct.pack("<hHH", 0, len(channels), ppq)
    return header + b"FLdt" + struct.pack("<I", len(events)) + bytes(events)


def processor_block(state: dict[str, Any]) -> bytes:
    return xfer.write_container(xfer.XferContainer(metadata={"component": "processor", "product": "Serum2"}, state=state))
