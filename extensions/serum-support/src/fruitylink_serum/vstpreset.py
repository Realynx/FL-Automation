"""Write and read Steinberg VST3 preset files (``.vstpreset``).

The public layout (little-endian):

    "VST3" | i32 version (1) | 32 ASCII hex class id | i64 offset of the chunk list
    ... chunk data ...
    "List" | i32 count | count * ( 4-char id | i64 offset | i64 size )

Chunk ids: ``Comp`` = component (processor) state, ``Cont`` = controller state,
``Info`` = optional XML metadata. This module only frames bytes supplied by the
caller; it does not know plugin internals.
"""

from __future__ import annotations

import struct
from dataclasses import dataclass, field

__all__ = ["SERUM2_CLASS_ID", "VstPreset", "class_id_from_guid", "read_vstpreset", "write_vstpreset"]

_MAGIC = b"VST3"
_LIST = b"List"
_HEADER_SIZE = 4 + 4 + 32 + 8

# Serum 2 VST3 class id as FL's wrapper accepts it in a .vstpreset header: the GUID string from FL's
# plugin database entry ``ps_file_guid_0={56534558-6673-5073-6572-756D20320000}`` with braces and
# dashes removed (string order). Live-verified on FL 26.1.3: this id loads; the byte-swapped FUID
# memory order ("58455356...") is silently ignored by the wrapper.
SERUM2_CLASS_ID = "56534558667350736572756D20320000"


def class_id_from_guid(guid: str) -> str:
    """Convert a Windows GUID string (as FL's ``.nfo`` files print it) to the 32-hex id FL's wrapper
    accepts: braces and dashes removed, uppercase, digits kept in string order. FL's wrapper matched
    this order live; do NOT swap the first three GUID fields into FUID memory order."""
    text = guid.strip().strip("{}").replace("-", "").upper()
    if len(text) != 32 or any(c not in "0123456789ABCDEF" for c in text):
        raise ValueError("A GUID has 32 hex digits.")
    return text


@dataclass
class VstPreset:
    class_id: str
    component: bytes
    controller: bytes | None = None
    info: bytes | None = None
    extra: dict[str, bytes] = field(default_factory=dict)


def write_vstpreset(preset: VstPreset) -> bytes:
    class_id = preset.class_id.strip()
    if len(class_id) != 32 or any(c not in "0123456789abcdefABCDEF" for c in class_id):
        raise ValueError("The VST3 class id must be 32 hexadecimal characters.")
    chunks: list[tuple[bytes, bytes]] = [(b"Comp", preset.component)]
    if preset.controller is not None:
        chunks.append((b"Cont", preset.controller))
    if preset.info is not None:
        chunks.append((b"Info", preset.info))
    for key, value in preset.extra.items():
        chunks.append((key.encode("ascii"), value))
    body = bytearray()
    entries: list[tuple[bytes, int, int]] = []
    offset = _HEADER_SIZE
    for chunk_id, data in chunks:
        if len(chunk_id) != 4:
            raise ValueError("Chunk ids are exactly four ASCII characters.")
        entries.append((chunk_id, offset, len(data)))
        body += data
        offset += len(data)
    listing = bytearray(_LIST + struct.pack("<i", len(entries)))
    for chunk_id, chunk_offset, size in entries:
        listing += chunk_id + struct.pack("<qq", chunk_offset, size)
    header = _MAGIC + struct.pack("<i", 1) + class_id.encode("ascii") + struct.pack("<q", offset)
    return header + bytes(body) + bytes(listing)


def read_vstpreset(data: bytes) -> VstPreset:
    if len(data) < _HEADER_SIZE or data[:4] != _MAGIC:
        raise ValueError("Not a VST3 preset file.")
    version = struct.unpack_from("<i", data, 4)[0]
    if version != 1:
        raise ValueError(f"Unsupported VST3 preset version {version}.")
    class_id = data[8:40].decode("ascii")
    list_offset = struct.unpack_from("<q", data, 40)[0]
    if list_offset < _HEADER_SIZE or list_offset + 8 > len(data) or data[list_offset : list_offset + 4] != _LIST:
        raise ValueError("Chunk list not found.")
    count = struct.unpack_from("<i", data, list_offset + 4)[0]
    position = list_offset + 8
    component: bytes | None = None
    controller: bytes | None = None
    info: bytes | None = None
    extra: dict[str, bytes] = {}
    for _ in range(count):
        chunk_id = data[position : position + 4]
        offset, size = struct.unpack_from("<qq", data, position + 4)
        position += 20
        chunk = data[offset : offset + size]
        if chunk_id == b"Comp":
            component = chunk
        elif chunk_id == b"Cont":
            controller = chunk
        elif chunk_id == b"Info":
            info = chunk
        else:
            extra[chunk_id.decode("ascii", "replace")] = chunk
    if component is None:
        raise ValueError("The preset has no component (Comp) chunk.")
    return VstPreset(class_id=class_id, component=component, controller=controller, info=info, extra=extra)
