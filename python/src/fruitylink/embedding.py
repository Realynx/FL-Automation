"""Direct SDK transport for a host-owned CPython interpreter; never opens a pipe.

The host owns serialization, execution IDs, cancellation and interpreter lifetime.
This runs trusted code with the host's privileges, not in a security sandbox.
"""

import json
import sys
import threading
from collections.abc import Callable
from types import FrameType
from typing import Any

from .errors import ProtocolError, RemoteError
from .studio import Studio
from .values import JsonValue, json_object, to_json
from .worker import RESPONSE_LIMIT, execute

RequestCallback = Callable[[str, str, str], str]
CancelCallback = Callable[[str], bool]
TRACE_POLL_INTERVAL = 1024


class ExecutionCancelled(BaseException):
    """Cooperative cancellation delivered at a Python trace or SDK call boundary."""


class DirectTransport:
    """An execution-scoped, owner-thread-only connection to the host's dispatcher."""

    def __init__(self, scope: str, request: RequestCallback, cancelled: CancelCallback) -> None:
        self._scope, self._request, self._cancelled = scope, request, cancelled
        self._owner = threading.get_ident()
        self._active = True

    def close(self) -> None:
        """Revoke retained references when the script finishes."""
        self._active = False

    def request(self, method: str, params: dict[str, JsonValue]) -> JsonValue:
        if not self._active or threading.get_ident() != self._owner:
            raise RemoteError("unavailable", "The embedded FL connection is restricted to its active script thread.")
        if self._cancelled(self._scope):
            raise ExecutionCancelled("Embedded Python execution was cancelled.")
        payload = json.dumps(params, ensure_ascii=False, allow_nan=False, separators=(",", ":"))
        response = json_object(to_json(json.loads(self._request(self._scope, method, payload))), "Host response")
        error = response.get("error")
        if error is not None:
            details = json_object(error, "Host error")
            raise RemoteError(str(details.get("code", "internal_error")), str(details.get("message", "Host failed.")),
                              details.get("data"))
        if "result" not in response:
            raise ProtocolError("Embedded host response has neither a result nor an error.")
        return response.get("result")


def install_host_guards() -> None:
    """Called once by the native host, never by normal SDK imports or preflight.

    Only tracked Thread starts can be drained. Audit hooks are lifecycle checks,
    not a sandbox against hostile code or native extensions.
    """
    sys.addaudithook(_audit_thread_start)


def _audit_thread_start(event: str, arguments: tuple[object, ...]) -> None:
    if event not in ("_thread.start_new_thread", "_thread.start_joinable_thread"):
        return
    function = arguments[0]
    if isinstance(getattr(function, "__self__", None), threading.Thread) and getattr(function, "__name__", "") == "_bootstrap":
        return
    raise RuntimeError("Embedded Python requires threading.Thread so child work can be drained; raw _thread starts are unsupported.")


def execute_json(code: str, scope: str, request: RequestCallback, cancelled: CancelCallback) -> str:
    """Run one script, revoke its SDK connection, and drain Python threads it created.

    A blocking extension or child thread can delay cancellation indefinitely. The
    caller must keep this invocation alive until it returns; never finalize Python.
    """
    transport = DirectTransport(scope, request, cancelled)
    owner = threading.get_ident()
    existing = set(threading.enumerate())
    prior_trace, prior_thread_trace = sys.gettrace(), threading.gettrace()
    remaining_events = TRACE_POLL_INTERVAL
    entered_script = False

    def trace(frame: FrameType, event: str, arg: object) -> Any:
        nonlocal remaining_events, entered_script
        # Crossing the native callback and walking ancestry on every event makes
        # DSP loops unusably slow. Keep tracing, but bound that work to a poll.
        # Check initial entry immediately; native SDK calls also check separately.
        if not entered_script and frame.f_code.co_filename == "<fruitylink-embedded>":
            entered_script = True
            remaining_events = 0
        remaining_events -= 1
        if remaining_events > 0:
            return trace
        remaining_events = TRACE_POLL_INTERVAL
        # Includes imported helpers and nested exec, excludes error handling/cleanup.
        if _inside_script(frame) and cancelled(scope):
            raise ExecutionCancelled("Embedded Python execution was cancelled.")
        return trace

    def finish() -> None:
        transport.close()
        sys.settrace(prior_trace)
        # Keep stdout/stderr bounded until all Python child threads have stopped.
        # Joining after cancellation may wait indefinitely for a native extension.
        _drain_threads(existing, owner)

    try:
        sys.settrace(trace)
        threading.settrace(trace)
        result = execute(code, Studio(transport), filename="<fruitylink-embedded>", after_execution=finish)
    finally:
        transport.close()
        sys.settrace(prior_trace)
        threading.settrace(prior_thread_trace)
    payload = json.dumps(result, ensure_ascii=False, allow_nan=False, separators=(",", ":"))
    if len(payload.encode("utf-8")) > RESPONSE_LIMIT:
        return '{"ok":false,"result":null,"error":"Embedded response exceeds 1 MiB."}'
    return payload


def _inside_script(frame: FrameType | None) -> bool:
    while frame is not None:
        if frame.f_code.co_filename == "<fruitylink-embedded>":
            return True
        frame = frame.f_back
    return False


def _drain_threads(existing: set[threading.Thread], owner: int) -> None:
    while True:
        pending = [thread for thread in threading.enumerate()
                   if thread not in existing and thread.ident != owner]
        if not pending:
            return
        for thread in pending:
            thread.join()
