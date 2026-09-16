"""Read and write Serum 2's ``XferJson`` state container.

Layout (little-endian), verified against local ``.SerumPreset`` files and the
Serum 2 VST3 state stored by FL Studio's wrapper:

    "XferJson\\0"  | u32 metaLength | u32 zero | metaLength bytes of JSON
    u32 decompressedLength | u32 codec (2 = zstd) | zstd frame of a CBOR map

The metadata JSON carries descriptive fields (``fileType``, ``presetName``,
``product``, ``productVersion``, ``hash`` ...). The CBOR map holds the actual
state as named sections with ``plainParams`` maps of named float parameters.
Only container framing is implemented here; proprietary payloads are neither
interpreted beyond CBOR structure nor bundled with this package.

zstd support uses ``compression.zstd`` (Python 3.14, the FL embedded runtime)
or the third-party ``zstandard`` package when present. Without either, reading
and writing raise :class:`XferUnavailableError`.
"""

from __future__ import annotations

import hashlib
import json
import struct
from collections.abc import Callable
from dataclasses import dataclass
from typing import Any

from . import cbor

__all__ = ["MAGIC", "XferContainer", "XferError", "XferUnavailableError", "read_container", "write_container"]

MAGIC = b"XferJson\x00"
_CODEC_ZSTD = 2


class XferError(ValueError):
    """The bytes are not an XferJson container this module understands."""


class XferUnavailableError(RuntimeError):
    """No zstd implementation is available in this Python runtime."""


def _zstd() -> tuple[Callable[[bytes], bytes], Callable[[bytes], bytes]]:
    try:
        # Python 3.14+ (the FL embedded runtime) ships zstd in the standard library.
        from compression import zstd as std_zstd  # type: ignore[import-not-found,unused-ignore]

        return (lambda data: bytes(std_zstd.compress(data))), (lambda data: bytes(std_zstd.decompress(data)))
    except ImportError:
        pass
    try:
        import zstandard  # type: ignore[import-not-found,unused-ignore]

        return (lambda data: bytes(zstandard.ZstdCompressor().compress(data))), (
            lambda data: bytes(zstandard.ZstdDecompressor().decompress(data))
        )
    except ImportError as error:  # pragma: no cover - depends on the interpreter
        raise XferUnavailableError("zstd support requires Python 3.14 (compression.zstd) or the zstandard package.") from error


def zstd_available() -> bool:
    try:
        _zstd()
    except XferUnavailableError:
        return False
    return True


@dataclass
class XferContainer:
    """A decoded XferJson container: descriptive metadata plus the CBOR state map."""

    metadata: dict[str, Any]
    state: dict[str, Any]
    codec: int = _CODEC_ZSTD

    @property
    def product(self) -> str | None:
        value = self.metadata.get("product")
        return value if isinstance(value, str) else None

    @property
    def product_version(self) -> str | None:
        value = self.metadata.get("productVersion")
        return value if isinstance(value, str) else None


def read_container(data: bytes | bytearray | memoryview) -> XferContainer:
    """Parse one XferJson container (a whole ``.SerumPreset`` file or one wrapper state block)."""
    blob = bytes(data)
    if not blob.startswith(MAGIC):
        raise XferError("Missing XferJson magic.")
    if len(blob) < len(MAGIC) + 8:
        raise XferError("Truncated XferJson header.")
    meta_length, reserved = struct.unpack_from("<II", blob, len(MAGIC))
    if reserved != 0:
        raise XferError(f"Unexpected reserved header value {reserved}.")
    meta_start = len(MAGIC) + 8
    meta_end = meta_start + meta_length
    if meta_end + 8 > len(blob):
        raise XferError("Metadata length exceeds the container.")
    try:
        metadata = json.loads(blob[meta_start:meta_end].decode("utf-8"))
    except (UnicodeDecodeError, json.JSONDecodeError) as error:
        raise XferError("Metadata is not UTF-8 JSON.") from error
    if not isinstance(metadata, dict):
        raise XferError("Metadata JSON must be an object.")
    decompressed_length, codec = struct.unpack_from("<II", blob, meta_end)
    if codec != _CODEC_ZSTD:
        raise XferError(f"Unsupported payload codec {codec}; only zstd (2) is known.")
    _, decompress = _zstd()
    raw = bytes(decompress(blob[meta_end + 8 :]))
    if len(raw) != decompressed_length:
        raise XferError(f"Payload decompressed to {len(raw)} bytes; header declares {decompressed_length}.")
    state = cbor.decode(raw)
    if not isinstance(state, dict):
        raise XferError("State payload is not a CBOR map.")
    return XferContainer(metadata=metadata, state=state, codec=codec)


def write_container(container: XferContainer) -> bytes:
    """Serialise a container back to XferJson bytes (zstd-compressed CBOR)."""
    if container.codec != _CODEC_ZSTD:
        raise XferError(f"Unsupported payload codec {container.codec}.")
    compress, _ = _zstd()
    raw = cbor.encode(container.state)
    compressed = bytes(compress(raw))
    # Serum stamps ``hash`` = MD5 of the compressed payload (verified on local preset files and on
    # the processor/controller blocks FL saved); recompute it so edited payloads stay consistent.
    metadata = dict(container.metadata)
    metadata["hash"] = hashlib.md5(compressed).hexdigest()  # noqa: S324 - format identity, not security
    meta = json.dumps(metadata, separators=(",", ":"), ensure_ascii=False, sort_keys=True).encode("utf-8")
    return MAGIC + struct.pack("<II", len(meta), 0) + meta + struct.pack("<II", len(raw), _CODEC_ZSTD) + compressed


def split_wrapper_blocks(state_blob: bytes) -> list[tuple[int, bytes]]:
    """Locate the XferJson blocks inside an FL wrapper state chunk.

    FL's wrapper stores each VST3 component state as ``u32 kind | u32 length | u32 zero``
    followed by ``length`` bytes of XferJson (kind 3 = processor, 2 = controller).
    Returns ``(kind, block)`` pairs in file order. Anything else in the chunk is left alone.
    """
    blocks: list[tuple[int, bytes]] = []
    position = 0
    while True:
        index = state_blob.find(MAGIC, position)
        if index < 0 or index < 12:
            break
        kind, length, zero = struct.unpack_from("<III", state_blob, index - 12)
        if zero != 0 or length <= 0 or index + length > len(state_blob):
            position = index + 1
            continue
        blocks.append((kind, state_blob[index : index + length]))
        position = index + length
    return blocks
