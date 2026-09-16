import ctypes
import time

import pytest

from fruitylink import ConnectionError
from fruitylink._winpipe import WindowsPipe


class RetryKernel:
    def __init__(self) -> None:
        self.waits = 0
        self.opens = 0
        self.error = 0

    def WaitNamedPipeW(self, path: str, timeout: int) -> bool:
        self.waits += 1
        self.error = 2
        return self.waits > 1

    def CreateFileW(self, path: str, access: int, share: int, security: object,
                    disposition: int, flags: int, template: object) -> int | None:
        self.opens += 1
        self.error = 231
        return ctypes.c_void_p(-1).value if self.opens == 1 else 123


def test_pipe_open_retries_listener_gap_and_another_client_race(monkeypatch: pytest.MonkeyPatch) -> None:
    kernel = RetryKernel()
    pipe = object.__new__(WindowsPipe)
    pipe._deadline = time.monotonic() + 1
    monkeypatch.setattr(pipe, "_kernel", kernel, raising=False)
    monkeypatch.setattr(ctypes, "get_last_error", lambda: kernel.error, raising=False)
    assert pipe._open("local-pipe") == 123
    assert kernel.waits == 3 and kernel.opens == 2


def test_expired_pipe_deadline_stops_before_open() -> None:
    pipe = object.__new__(WindowsPipe)
    pipe._deadline = time.monotonic() - 1
    with pytest.raises(ConnectionError, match="timed out"):
        pipe._remaining_ms()
