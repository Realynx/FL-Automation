"""Injectable request transport. Each native request owns a fresh pipe connection."""

import math
import uuid
from typing import Protocol

from ._winpipe import WindowsPipe
from .endpoint import Endpoint
from .protocol import encode_frame, read_frame, unwrap_response
from .values import JsonValue


class RequestTransport(Protocol):
    def request(self, method: str, params: dict[str, JsonValue]) -> JsonValue:
        """Return the response result or raise an exception."""
        ...


class NamedPipeTransport:
    def __init__(self, endpoint: Endpoint, *, timeout: float = 30.0) -> None:
        if not math.isfinite(timeout) or timeout <= 0:
            raise ValueError("Request timeout must be finite and positive.")
        self.endpoint = endpoint
        self.timeout = timeout

    def request(self, method: str, params: dict[str, JsonValue]) -> JsonValue:
        request_id = uuid.uuid4().hex
        frame = encode_frame({"id": request_id, "token": self.endpoint.token, "method": method, "params": params})
        with WindowsPipe(self.endpoint, self.timeout) as pipe:
            pipe.write(frame)
            return unwrap_response(read_frame(pipe.read), request_id)
