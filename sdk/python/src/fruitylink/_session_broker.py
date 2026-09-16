"""Bounded stdio client for the SDK's Windows process/desktop owner."""

import json
import os
import queue
import subprocess
import threading
from pathlib import Path

from .errors import ConnectionError
from .values import JsonValue, json_object, to_json


class SessionBroker:
    def __init__(self, executable: Path, launcher: Path, arguments: list[str],
                 environment: dict[str, JsonValue], background: bool, *, startup_timeout: float = 60) -> None:
        self._lock = threading.Lock()
        self._failed = False
        self._lines: queue.Queue[str | None] = queue.Queue()
        self._process = subprocess.Popen(
            [str(launcher)], stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL,
            text=True, encoding="utf-8", creationflags=0x08000000 if os.name == "nt" else 0)
        self._reader = threading.Thread(target=self._read, daemon=True)
        self._reader.start()
        try:
            self.initial = self.request({"executable": str(executable), "arguments": list(arguments),
                                         "environment": environment, "background": background,
                                         "startupTimeoutSeconds": startup_timeout}, timeout=startup_timeout)
        except BaseException:
            self.close()
            raise

    def _read(self) -> None:
        assert self._process.stdout is not None
        try:
            for line in self._process.stdout:
                self._lines.put(line)
        finally:
            self._lines.put(None)

    def request(self, value: dict[str, JsonValue], timeout: float = 10) -> dict[str, JsonValue]:
        with self._lock:
            if self._failed or self._process.poll() is not None or self._process.stdin is None:
                raise ConnectionError("The SDK session host has exited.")
            try:
                self._process.stdin.write(json.dumps(value, allow_nan=False) + "\n")
                self._process.stdin.flush()
                line = self._lines.get(timeout=timeout)
            except (OSError, queue.Empty) as error:
                self._failed = True
                raise ConnectionError("The SDK session host did not respond before its deadline.") from error
            if line is None:
                raise ConnectionError("The SDK session host closed its response stream.")
            try:
                response = json_object(to_json(json.loads(line)), "Session host response")
            except (ValueError, TypeError) as error:
                self._failed = True
                raise ConnectionError("Invalid SDK session-host response.") from error
            if "error" in response:
                raise ConnectionError(str(response["error"]))
            return response

    def close(self) -> None:
        with self._lock:
            if self._process.stdin is not None and not self._process.stdin.closed:
                try:
                    self._process.stdin.close()
                except OSError:
                    pass
            try:
                self._process.wait(timeout=10)
            except subprocess.TimeoutExpired:
                self._process.kill()  # Closing the broker's job handle kills only its owned FL tree.
                self._process.wait(timeout=5)
            self._reader.join(timeout=2)
            if self._process.stdout is not None:
                self._process.stdout.close()
