"""Length-prefixed JSON codec, independent of Windows transport."""

import json
import struct
from collections.abc import Callable

from .errors import ProtocolError, RemoteError
from .values import JsonValue, to_json

MAX_FRAME_BYTES = 4 * 1024 * 1024


def encode_frame(value: JsonValue) -> bytes:
    body = json.dumps(value, ensure_ascii=False, allow_nan=False, separators=(",", ":")).encode("utf-8")
    if len(body) > MAX_FRAME_BYTES:
        raise ProtocolError("Scripting request exceeds 4 MiB.")
    return struct.pack("<I", len(body)) + body


def read_exact(read: Callable[[int], bytes], count: int) -> bytes:
    chunks = bytearray()
    while len(chunks) < count:
        chunk = read(count - len(chunks))
        if not chunk:
            raise ProtocolError("Scripting pipe closed before the complete response arrived.")
        if len(chunk) > count - len(chunks):
            raise ProtocolError("Transport returned more bytes than requested.")
        chunks.extend(chunk)
    return bytes(chunks)


def read_frame(read: Callable[[int], bytes]) -> JsonValue:
    count = struct.unpack("<I", read_exact(read, 4))[0]
    if count == 0 or count > MAX_FRAME_BYTES:
        raise ProtocolError("Invalid scripting response frame length.")
    try:
        return to_json(json.loads(read_exact(read, count).decode("utf-8")))
    except (ValueError, TypeError, UnicodeError, RecursionError) as exc:
        raise ProtocolError("Scripting response is not valid finite JSON.") from exc


def unwrap_response(value: JsonValue, request_id: str) -> JsonValue:
    if not isinstance(value, dict) or value.get("id") != request_id:
        raise ProtocolError("Scripting response identity mismatch.")
    error = value.get("error")
    if error is not None:
        if not isinstance(error, dict) or not isinstance(error.get("message"), str):
            raise ProtocolError("Malformed scripting error response.")
        code = error.get("code")
        if not isinstance(code, (str, int)):
            raise ProtocolError("Malformed scripting error code.")
        raise RemoteError(str(code), str(error["message"]), error.get("data"))
    if "result" not in value:
        raise ProtocolError("Scripting response has neither a result nor an error.")
    return value["result"]
