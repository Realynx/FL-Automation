import json
import subprocess
import sys
import threading
import types
from pathlib import Path

import pytest

from fruitylink.embedding import TRACE_POLL_INTERVAL, DirectTransport, ExecutionCancelled, execute_json
from fruitylink.errors import RemoteError


def request(scope: str, method: str, params: str) -> str:
    assert scope == "test"
    value = json.loads(params)
    assert method == "invoke" and value["operation"] == "get_tempo"
    return '{"result":120}'


def never_cancel(scope: str) -> bool:
    return False


def test_direct_calls_capture_unicode_and_typed_result() -> None:
    result = json.loads(execute_json("print('音楽')\nresult=fl.ops.get_tempo()", "test", request, never_cancel))
    assert result["ok"] is True and result["result"] == 120 and result["stdout"] == "音楽\n"


def test_closed_transport_and_background_thread_cannot_call_host() -> None:
    transport = DirectTransport("test", request, never_cancel)
    errors: list[str] = []

    def outside() -> None:
        try:
            transport.request("invoke", {"operation": "get_tempo"})
        except RemoteError as error:
            errors.append(error.code)

    thread = threading.Thread(target=outside)
    thread.start()
    thread.join()
    assert errors == ["unavailable"]
    transport.close()
    with pytest.raises(RemoteError, match="active script thread"):
        transport.request("invoke", {})


def test_remote_errors_and_cancellation_keep_context() -> None:
    def failed(scope: str, method: str, params: str) -> str:
        return '{"error":{"code":"unavailable","message":"version layout"}}'

    transport = DirectTransport("test", failed, never_cancel)
    with pytest.raises(RemoteError, match="unavailable: version layout"):
        transport.request("invoke", {})
    cancelled = DirectTransport("test", request, lambda _: True)
    with pytest.raises(ExecutionCancelled):
        cancelled.request("invoke", {})


@pytest.mark.parametrize("code", ["while True: pass", "exec('while True: pass')"])
def test_trace_cancels_user_and_nested_code_and_restores_streams(code: str) -> None:
    # A cancellation regression must fail within a deadline, not hang the suite.
    script = """
import json, sys
from fruitylink.embedding import execute_json
checks = 0
def cancelled(scope):
    global checks
    checks += 1
    return checks > 20
trace, stdout, stderr = sys.gettrace(), sys.stdout, sys.stderr
result = json.loads(execute_json(sys.argv[1], "test", lambda *args: "{}", cancelled))
assert result["ok"] is False and "ExecutionCancelled" in result["error"], result
assert sys.gettrace() is trace and sys.stdout is stdout and sys.stderr is stderr
"""
    process = subprocess.run([sys.executable, "-c", script, code],
                             cwd=Path(__file__).resolve().parents[1] / "src",
                             capture_output=True, text=True, timeout=10)
    assert process.returncode == 0, process.stderr


def test_child_threads_drain_while_output_remains_captured() -> None:
    result = json.loads(execute_json("""
import threading, time
def child():
    time.sleep(0.02)
    print('child')
threading.Thread(target=child).start()
result=1
""", "test", request, never_cancel))
    assert result["ok"] is True and result["stdout"] == "child\n"


def test_compute_loop_batches_managed_cancellation_callback() -> None:
    checks = 0

    def cancelled(scope: str) -> bool:
        nonlocal checks
        checks += 1
        return False

    iterations = 100_000
    result = json.loads(execute_json(
        f"total=0\nfor i in range({iterations}):\n total+=i\nresult=total", "test", request, cancelled))
    assert result["ok"] is True
    assert result["result"] == iterations * (iterations - 1) // 2
    # Even the legacy opcode fallback must batch many iterations per host call.
    assert 1 < checks < iterations // 16


def test_cancelled_before_entry_does_not_execute_first_statement() -> None:
    result = json.loads(execute_json("print('must not run')\nresult=1", "test", request, lambda _: True))
    assert result["ok"] is False and "ExecutionCancelled" in result["error"]
    assert result["stdout"] == ""


def test_periodic_poll_cancels_imported_helper_with_bounded_progress(monkeypatch: pytest.MonkeyPatch) -> None:
    helper = types.ModuleType("fruitylink_cancel_test_helper")
    exec(compile("steps=0\ndef run():\n global steps\n while True:\n  steps+=1\n",
                 "external_helper.py", "exec"), helper.__dict__)
    monkeypatch.setitem(sys.modules, helper.__name__, helper)
    checks = 0

    def cancelled(scope: str) -> bool:
        nonlocal checks
        checks += 1
        return checks >= 3

    previous_trace, previous_thread_trace = sys.gettrace(), threading.gettrace()
    result = json.loads(execute_json("import fruitylink_cancel_test_helper as helper\nhelper.run()",
                                     "test", request, cancelled))
    assert result["ok"] is False and "ExecutionCancelled" in result["error"]
    assert checks == 3
    assert 1 <= helper.__dict__["steps"] <= 2 * TRACE_POLL_INTERVAL
    assert sys.gettrace() is previous_trace and threading.gettrace() is previous_thread_trace
    recovered = json.loads(execute_json("result=42", "test", request, never_cancel))
    assert recovered["ok"] is True and recovered["result"] == 42
