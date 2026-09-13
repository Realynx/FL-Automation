import json
import subprocess
import sys
from pathlib import Path

import pytest

from fruitylink import Endpoint, Studio, worker
from fruitylink.values import JsonValue
from fruitylink.worker import RESULT_LIMIT, STREAM_LIMIT, BoundedText, execute


def test_worker_runs_code_with_fl_and_serializes_records(fl: Studio) -> None:
    result = execute("from fruitylink import NoteSpec\nprint(fl.timebase.ppq)\nresult=NoteSpec(0,60,0,96,100)", fl)
    assert result["ok"] is True
    assert result["stdout"] == "96\n"
    assert result["result"] == {"channel": 0, "key": 60, "startTick": 0, "lengthTick": 96, "velocity": 100}


def test_worker_captures_error_output_and_traceback(fl: Studio) -> None:
    result = execute("import sys\nprint('before')\nprint('err',file=sys.stderr)\nraise ValueError('bad script')", fl)
    assert result["ok"] is False
    assert result["stdout"] == "before\n" and result["stderr"] == "err\n"
    assert "ValueError: bad script" in str(result["error"])
    assert "<fruitylink-script>" in str(result["traceback"])


def test_unicode_output_is_byte_bounded() -> None:
    stream = BoundedText(5)
    assert stream.write("♫♫♫") == 3
    assert stream.getvalue() == "♫"
    assert stream.truncated


def test_output_caps_and_user_result_key_preservation(fl: Studio) -> None:
    result = execute(f"print('x'*{STREAM_LIMIT + 100})\nresult={{'user_key':3}}", fl)
    assert len(str(result["stdout"]).encode()) == STREAM_LIMIT
    assert result["stdoutTruncated"] is True
    assert result["result"] == {"user_key": 3}


@pytest.mark.parametrize("code", ["result=object()", f"result='x'*{RESULT_LIMIT + 1}", "raise SystemExit(2)"])
def test_invalid_result_or_systemexit_produces_error(code: str, fl: Studio) -> None:
    assert execute(code, fl)["ok"] is False


def test_worker_cli_malformed_request_writes_dedicated_response(tmp_path: Path) -> None:
    response = tmp_path / "response.json"
    process = subprocess.run([sys.executable, "-m", "fruitylink.worker", "--response", str(response)],
                             input='{"code":123}\n', text=True, capture_output=True, timeout=10, check=True)
    result = json.loads(response.read_text(encoding="utf-8"))
    assert result["ok"] is False and "code string" in result["error"]
    assert process.stdout == ""
    assert not list(tmp_path.glob("*.tmp"))


@pytest.mark.parametrize("timeout", [0, 301, True, "30", float("nan")])
def test_worker_rejects_invalid_rpc_timeout(timeout: JsonValue) -> None:
    with pytest.raises(ValueError, match="timeoutSeconds"):
        worker.run_request({"code": "result=1", "timeoutSeconds": timeout})


def test_worker_applies_rpc_timeout(monkeypatch: pytest.MonkeyPatch, fl: Studio) -> None:
    timeouts: list[float] = []

    def connect_stub(*, endpoint: Endpoint | None, timeout: float) -> Studio:
        timeouts.append(timeout)
        return fl

    monkeypatch.setattr(worker, "connect", connect_stub)
    result = worker.run_request({"code": "result=2", "timeoutSeconds": 45})
    assert result["result"] == 2 and timeouts == [45.0]
