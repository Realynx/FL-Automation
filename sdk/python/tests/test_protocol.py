import io
import struct

import pytest

from fruitylink import ProtocolError, RemoteError
from fruitylink.protocol import MAX_FRAME_BYTES, encode_frame, read_frame, unwrap_response
from fruitylink.values import JsonValue


def test_fragmented_unicode_frame() -> None:
    expected: JsonValue = {"id": "1", "result": "Δ names: \"quoted\"\nmultiline"}
    data = io.BytesIO(encode_frame(expected))
    assert read_frame(lambda count: data.read(min(count, 2))) == expected


@pytest.mark.parametrize("data", [b"", b"\x01", struct.pack("<I", 0), struct.pack("<I", MAX_FRAME_BYTES + 1),
                                      struct.pack("<I", 3) + b"{}", struct.pack("<I", 3) + b"bad"])
def test_malformed_or_truncated_frames(data: bytes) -> None:
    with pytest.raises(ProtocolError):
        read_frame(io.BytesIO(data).read)


def test_nonfinite_json_is_rejected() -> None:
    with pytest.raises(ProtocolError):
        read_frame(io.BytesIO(struct.pack("<I", 3) + b"NaN").read)


def test_null_result_is_success_but_missing_result_is_not() -> None:
    assert unwrap_response({"id": "1", "result": None}, "1") is None
    with pytest.raises(ProtocolError):
        unwrap_response({"id": "1"}, "1")
    with pytest.raises(ProtocolError):
        unwrap_response({"id": "wrong", "result": None}, "1")


def test_remote_error_preserves_code_and_data() -> None:
    with pytest.raises(RemoteError) as caught:
        unwrap_response({"id": "a", "error": {"code": "unsupported", "message": "Unavailable", "data": {"operation": "query"}}}, "a")
    assert caught.value.code == "unsupported"
    assert caught.value.data == {"operation": "query"}
