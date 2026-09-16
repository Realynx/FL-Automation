"""Minimal CBOR (RFC 8949) codec for Serum 2 state payloads.

Serum 2 stores its preset and plugin state as a CBOR map of named sections
(``"Oscillator0"``, ``"Env0"`` ...) whose ``"plainParams"`` maps hold named float
parameters. This codec covers the subset those payloads use: unsigned/negative
integers, byte and text strings, arrays, maps, tags, booleans, null and IEEE
floats. It preserves the float width read from the source so a re-encoded
payload keeps Serum's own representation. Nothing here interprets the
parameters or copies proprietary content; it only reads and writes bytes.
"""

from __future__ import annotations

import math
import struct
from typing import Any

__all__ = ["CborError", "Float32", "decode", "encode"]


class CborError(ValueError):
    """The bytes are not CBOR this codec understands."""


class Float32(float):
    """A float that round-trips as a CBOR 32-bit float (``0xfa``)."""

    __slots__ = ()


class Tagged:
    """A CBOR tagged value preserved verbatim."""

    __slots__ = ("tag", "value")

    def __init__(self, tag: int, value: Any) -> None:
        self.tag = tag
        self.value = value

    def __eq__(self, other: object) -> bool:
        return isinstance(other, Tagged) and other.tag == self.tag and other.value == self.value

    def __repr__(self) -> str:
        return f"Tagged({self.tag}, {self.value!r})"


def decode(data: bytes | bytearray | memoryview) -> Any:
    """Decode one CBOR item; trailing bytes are an error."""
    view = memoryview(data).toreadonly()
    value, consumed = _decode_item(view, 0)
    if consumed != len(view):
        raise CborError(f"{len(view) - consumed} trailing bytes after the CBOR item.")
    return value


def encode(value: Any) -> bytes:
    """Encode a Python value as CBOR bytes (definite lengths only)."""
    out = bytearray()
    _encode_item(value, out)
    return bytes(out)


def _read_head(view: memoryview, pos: int) -> tuple[int, int, int | None, int]:
    if pos >= len(view):
        raise CborError("Unexpected end of CBOR data.")
    initial = view[pos]
    major, info = initial >> 5, initial & 0x1F
    pos += 1
    if info < 24:
        return major, info, info, pos
    if info == 24:
        return major, info, view[pos], pos + 1
    if info == 25:
        return major, info, struct.unpack_from(">H", view, pos)[0], pos + 2
    if info == 26:
        return major, info, struct.unpack_from(">I", view, pos)[0], pos + 4
    if info == 27:
        return major, info, struct.unpack_from(">Q", view, pos)[0], pos + 8
    if info == 31:
        return major, info, None, pos
    raise CborError(f"Reserved additional information {info}.")


def _decode_item(view: memoryview, pos: int) -> tuple[Any, int]:
    major, info, argument, pos = _read_head(view, pos)
    if argument is None:
        raise CborError("Indefinite-length items are not used by Serum payloads and are not supported.")
    if major == 0:
        return argument, pos
    if major == 1:
        return -1 - argument, pos
    if major in (2, 3):
        end = pos + argument
        if end > len(view):
            raise CborError("String length exceeds the CBOR data.")
        raw = bytes(view[pos:end])
        return (raw if major == 2 else raw.decode("utf-8")), end
    if major == 4:
        items = []
        for _ in range(argument):
            item, pos = _decode_item(view, pos)
            items.append(item)
        return items, pos
    if major == 5:
        result: dict[Any, Any] = {}
        for _ in range(argument):
            key, pos = _decode_item(view, pos)
            item, pos = _decode_item(view, pos)
            result[key] = item
        return result, pos
    if major == 6:
        item, pos = _decode_item(view, pos)
        return Tagged(argument, item), pos
    if info == 20:
        return False, pos
    if info == 21:
        return True, pos
    if info == 22:
        return None, pos
    if info == 25:
        return Float32(struct.unpack_from(">e", view, pos - 2)[0]), pos
    if info == 26:
        return Float32(struct.unpack_from(">f", view, pos - 4)[0]), pos
    if info == 27:
        return struct.unpack_from(">d", view, pos - 8)[0], pos
    raise CborError(f"Unsupported simple value {info}.")


def _encode_head(major: int, argument: int, out: bytearray) -> None:
    base = major << 5
    if argument < 24:
        out.append(base | argument)
    elif argument < 0x100:
        out += struct.pack(">BB", base | 24, argument)
    elif argument < 0x10000:
        out += struct.pack(">BH", base | 25, argument)
    elif argument < 0x100000000:
        out += struct.pack(">BI", base | 26, argument)
    else:
        out += struct.pack(">BQ", base | 27, argument)


def _encode_item(value: Any, out: bytearray) -> None:
    if value is None:
        out.append(0xF6)
    elif value is True:
        out.append(0xF5)
    elif value is False:
        out.append(0xF4)
    elif isinstance(value, Float32):
        out += struct.pack(">Bf", 0xFA, float(value))
    elif isinstance(value, float):
        out += struct.pack(">Bd", 0xFB, value)
    elif isinstance(value, int):
        if value >= 0:
            _encode_head(0, value, out)
        else:
            _encode_head(1, -1 - value, out)
    elif isinstance(value, (bytes, bytearray, memoryview)):
        raw = bytes(value)
        _encode_head(2, len(raw), out)
        out += raw
    elif isinstance(value, str):
        raw = value.encode("utf-8")
        _encode_head(3, len(raw), out)
        out += raw
    elif isinstance(value, (list, tuple)):
        _encode_head(4, len(value), out)
        for item in value:
            _encode_item(item, out)
    elif isinstance(value, dict):
        _encode_head(5, len(value), out)
        for key, item in value.items():
            _encode_item(key, out)
            _encode_item(item, out)
    elif isinstance(value, Tagged):
        _encode_head(6, value.tag, out)
        _encode_item(value.value, out)
    else:
        raise CborError(f"Cannot encode {type(value).__name__} as CBOR.")


def is_finite_number(value: object) -> bool:
    return isinstance(value, (int, float)) and not isinstance(value, bool) and math.isfinite(value)
