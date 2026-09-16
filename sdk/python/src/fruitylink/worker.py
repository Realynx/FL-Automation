"""Execute user Python in a separate process, with a dedicated response file.

This is an execution boundary, not a security sandbox. The launching application
must enforce its wall timeout and terminate the worker's process tree on cancellation.
"""

import argparse
import contextlib
import io
import json
import math
import os
import sys
import traceback
from collections.abc import Callable
from pathlib import Path

from .endpoint import Endpoint
from .studio import Studio, connect
from .values import JsonValue, json_object, to_json

STREAM_LIMIT = 64 * 1024
RESULT_LIMIT = 512 * 1024
REQUEST_LIMIT = 4 * 1024 * 1024
RESPONSE_LIMIT = 1024 * 1024


class BoundedText(io.TextIOBase):
    """Keep a UTF-8 byte-bounded prefix while accepting further writes."""

    def __init__(self, limit: int = STREAM_LIMIT) -> None:
        self._limit = limit
        self._data = bytearray()
        self.truncated = False

    encoding = "utf-8"

    def writable(self) -> bool:
        return True

    def write(self, text: str) -> int:
        data = text.encode("utf-8", errors="replace")
        remaining = self._limit - len(self._data)
        self._data.extend(data[:remaining])
        self.truncated |= len(data) > remaining
        return len(text)

    def getvalue(self) -> str:
        return self._data.decode("utf-8", errors="ignore")


def _bounded(text: str, limit: int = STREAM_LIMIT) -> str:
    return text.encode("utf-8", errors="replace")[:limit].decode("utf-8", errors="ignore")


def _safe_result(value: object) -> JsonValue:
    converted = to_json(value)
    if len(json.dumps(converted, ensure_ascii=False, allow_nan=False).encode("utf-8")) > RESULT_LIMIT:
        raise ValueError("Script result exceeds 512 KiB; write large artifacts to a file instead.")
    return converted


def execute(code: str, studio: Studio, *, filename: str = "<fruitylink-script>",
            after_execution: Callable[[], None] | None = None) -> dict[str, JsonValue]:
    """Run with globals ``fl`` and ``result``. Return bounded output on success or failure.

    On failure the response keeps everything the script produced before raising: captured
    ``stdout``/``stderr`` and, when ``result`` was assigned and is still serializable, its
    value under ``result`` with ``resultPartial`` set. Successful responses are unchanged.
    """
    stdout, stderr = BoundedText(), BoundedText()
    result: dict[str, JsonValue] = {"ok": False, "result": None}
    scope: dict[str, object] = {"__name__": "__main__", "__file__": filename, "fl": studio, "result": None}
    executed = False
    try:
        with contextlib.redirect_stdout(stdout), contextlib.redirect_stderr(stderr):
            try:
                exec(compile(code, filename, "exec"), scope, scope)
            finally:
                if after_execution is not None:
                    after_execution()
            executed = True
            result["result"] = _safe_result(scope.get("result"))
        result["ok"] = True
    except BaseException as exc:
        result["error"] = _bounded(f"{type(exc).__name__}: {exc}")
        result["traceback"] = _bounded(traceback.format_exc())
        if not executed:
            _recover_partial_result(result, scope.get("result"))
    result.update(stdout=stdout.getvalue(), stderr=stderr.getvalue(),
                  stdoutTruncated=stdout.truncated, stderrTruncated=stderr.truncated)
    return result


def _recover_partial_result(result: dict[str, JsonValue], value: object) -> None:
    """Attach whatever ``result`` held when the script raised, if it can still be serialized."""
    if value is None:
        return
    try:
        result["result"] = _safe_result(value)
        result["resultPartial"] = True
    except Exception as exc:  # noqa: BLE001 - the original failure is already reported.
        result["resultPartialError"] = _bounded(f"{type(exc).__name__}: {exc}")


def encode_response(result: dict[str, JsonValue], *, limit: int = RESPONSE_LIMIT, origin: str = "Response") -> bytes:
    """Serialize a response within ``limit`` bytes, dropping ``result`` before any other field.

    Captured output, the error and the traceback survive so a caller still learns what happened.
    """
    data = json.dumps(result, ensure_ascii=False, allow_nan=False, separators=(",", ":")).encode("utf-8")
    if len(data) <= limit:
        return data
    reduced = dict(result, ok=False, result=None, resultPartial=False)
    reduced["resultDropped"] = True
    reduced["error"] = _bounded(f"{origin} exceeds {limit // (1024 * 1024)} MiB; the result value was dropped "
                                f"({len(data)} bytes serialized). Write large artifacts to a file instead."
                                + (f" Original error: {result['error']}" if "error" in result else ""))
    data = json.dumps(reduced, ensure_ascii=False, allow_nan=False, separators=(",", ":")).encode("utf-8")
    if len(data) <= limit:
        return data
    return json.dumps({"ok": False, "result": None, "error": f"{origin} exceeds {limit // (1024 * 1024)} MiB."},
                      ensure_ascii=False, separators=(",", ":")).encode("utf-8")


def run_request(value: JsonValue) -> dict[str, JsonValue]:
    request = json_object(value, "Worker request")
    code = request.get("code")
    if not isinstance(code, str):
        raise ValueError("Worker request requires a code string.")
    endpoint_data = request.get("endpoint")
    endpoint = Endpoint.from_json(endpoint_data) if endpoint_data is not None else None
    timeout = request.get("timeoutSeconds", 30)
    if type(timeout) not in (float, int) or not isinstance(timeout, (float, int)):
        raise ValueError("Worker timeoutSeconds must be a number from 1 to 300.")
    if not math.isfinite(timeout) or not 1 <= timeout <= 300:
        raise ValueError("Worker timeoutSeconds must be a number from 1 to 300.")
    with connect(endpoint=endpoint, timeout=float(timeout)) as studio:
        return execute(code, studio)


def _read_request(path: Path | None) -> JsonValue:
    if path is None:
        data = sys.stdin.buffer.readline(REQUEST_LIMIT + 1)
    else:
        with path.open("rb") as stream:
            data = stream.read(REQUEST_LIMIT + 1)
    if len(data) > REQUEST_LIMIT:
        raise ValueError("Worker request exceeds 4 MiB.")
    return to_json(json.loads(data))


def _write_response(path: Path, result: dict[str, JsonValue]) -> None:
    data = encode_response(result, origin="Worker response")
    temporary = path.with_name(path.name + f".{os.getpid()}.tmp")
    try:
        temporary.write_bytes(data)
        temporary.replace(path)
    finally:
        temporary.unlink(missing_ok=True)


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--request", type=Path, help="Read JSON from a file instead of one stdin JSON line.")
    parser.add_argument("--response", type=Path, required=True, help="Private response JSON file controlled by the caller.")
    args = parser.parse_args()
    try:
        result = run_request(_read_request(args.request))
    except BaseException as exc:
        result = {"ok": False, "result": None, "error": _bounded(f"{type(exc).__name__}: {exc}"),
                  "traceback": _bounded(traceback.format_exc()), "stdout": "", "stderr": "",
                  "stdoutTruncated": False, "stderrTruncated": False}
    _write_response(args.response, result)


if __name__ == "__main__":
    main()
