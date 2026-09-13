from collections.abc import Callable

import pytest

from fruitylink import Studio
from fruitylink.values import JsonValue


class RecordingTransport:
    def __init__(self) -> None:
        self.calls: list[tuple[str, dict[str, JsonValue]]] = []
        self.responses: dict[str, JsonValue] = {"get_ppq": 96}
        self.handler: Callable[[str, dict[str, JsonValue]], JsonValue] | None = None

    def request(self, method: str, params: dict[str, JsonValue]) -> JsonValue:
        self.calls.append((method, params))
        if self.handler is not None:
            return self.handler(method, params)
        return self.responses.get(str(params.get("operation", method)))


@pytest.fixture
def transport() -> RecordingTransport:
    return RecordingTransport()


@pytest.fixture
def fl(transport: RecordingTransport) -> Studio:
    return Studio(transport)
