import json
import subprocess
import sys
from pathlib import Path

import pytest

from fruitylink import Endpoint, Studio, worker
from fruitylink.values import JsonValue
from fruitylink.worker import RESULT_LIMIT, STREAM_LIMIT, BoundedText, encode_response, execute


def test_failure_keeps_output_and_partial_result_assigned_before_raising(fl: Studio) -> None:
    result = execute("result={'tempo': fl.timebase.ppq}\nprint('read tempo')\nresult['step']=2\nraise KeyError('boom')", fl)
    assert result["ok"] is False
    assert "KeyError" in str(result["error"]) and "<fruitylink-script>" in str(result["traceback"])
    assert result["stdout"] == "read tempo\n"
    assert result["result"] == {"tempo": 96, "step": 2} and result["resultPartial"] is True


def test_failure_without_result_or_with_unserializable_result_reports_but_does_not_mask(fl: Studio) -> None:
    result = execute("raise ValueError('early')", fl)
    assert result["ok"] is False and result["result"] is None and "resultPartial" not in result
    result = execute("result=object()\nraise ValueError('late')", fl)
    assert result["ok"] is False and result["result"] is None and "ValueError: late" in str(result["error"])
    assert "resultPartial" not in result and "TypeError" in str(result["resultPartialError"])
    # A result that only fails at conversion after a clean run is still a plain failure.
    result = execute("result=object()", fl)
    assert result["ok"] is False and "resultPartial" not in result and "resultPartialError" not in result


def test_result_dicts_keyed_by_numbers_are_returned_not_refused(fl: Studio) -> None:
    # Checks 11 and 21 of the 2026-09-14 live run: {int: ...} reads (Sampler controls) and Serum's
    # explain_parameters enum table (keyed by the normalized value) raised
    # "JSON object keys must be strings." out of _safe_result and lost the whole result.
    result = execute("result = {'reads': {2: 256, 14: 1000}, 'values': {0.0: 'sine', 1.0: 'pulse'}}", fl)
    assert result["ok"] is True
    assert result["result"] == {"reads": {"2": 256, "14": 1000}, "values": {"0.0": "sine", "1.0": "pulse"}}
    json.dumps(result["result"], allow_nan=False)
    refused = execute("result = {(1, 2): 'bad'}", fl)
    assert refused["ok"] is False and "JSON object keys" in str(refused["error"])


def test_oversized_response_drops_result_but_keeps_output_and_error() -> None:
    response: dict[str, JsonValue] = {"ok": True, "result": "x" * 600, "stdout": "kept\n", "stderr": "",
                                      "stdoutTruncated": False, "stderrTruncated": False}
    small = encode_response(response, limit=1024)
    assert json.loads(small) == response
    reduced = json.loads(encode_response(response, limit=512))
    assert reduced["ok"] is False and reduced["result"] is None and reduced["resultDropped"] is True
    assert reduced["stdout"] == "kept\n" and "result value was dropped" in reduced["error"]
    failed = json.loads(encode_response(dict(response, ok=False, error="ValueError: bad"), limit=512))
    assert "Original error: ValueError: bad" in failed["error"]
    hopeless = json.loads(encode_response(dict(response, stdout="y" * 600), limit=512))
    assert hopeless == {"ok": False, "result": None, "error": "Response exceeds 0 MiB."}


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
