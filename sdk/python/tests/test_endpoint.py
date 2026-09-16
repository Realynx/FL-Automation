import json
from pathlib import Path

import pytest

from fruitylink import ConnectionError, Endpoint, discover
from fruitylink.studio import verify_identity
from fruitylink.values import JsonValue


def write_endpoint(directory: Path, pid: int) -> None:
    (directory / f"{pid}.json").write_text(json.dumps({"apiVersion": 1, "pid": pid, "instanceId": "instance",
        "pipeName": f"FruityLinkScripting-{pid}", "token": "never-print-this", "createdAt": "2026-01-01T00:00:00Z"}), encoding="utf-8")


def test_discovery_selects_explicit_pid_and_rejects_ambiguity(tmp_path: Path) -> None:
    write_endpoint(tmp_path, 10)
    assert discover(directory=tmp_path).pid == 10
    write_endpoint(tmp_path, 20)
    with pytest.raises(ConnectionError, match="Multiple"):
        discover(directory=tmp_path)
    assert discover(20, directory=tmp_path).pid == 20


def test_discovery_missing_and_mismatched_record(tmp_path: Path) -> None:
    with pytest.raises(ConnectionError):
        discover(directory=tmp_path)
    write_endpoint(tmp_path, 10)
    (tmp_path / "10.json").rename(tmp_path / "11.json")
    with pytest.raises(ConnectionError, match="identity"):
        discover(directory=tmp_path)


def test_endpoint_repr_does_not_expose_token() -> None:
    endpoint = Endpoint(10, "instance", "FruityLinkScripting-10", "never-print-this")
    assert "never-print-this" not in repr(endpoint)


@pytest.mark.parametrize("pipe", [r"\\remote\pipe\name", "../pipe", "", "x" * 201])
def test_remote_or_invalid_pipes_rejected(pipe: str) -> None:
    with pytest.raises(ConnectionError):
        Endpoint(10, "instance", pipe, "token")


@pytest.mark.parametrize("key,value", [("pid", 11), ("instanceId", "stale"), ("apiVersion", 2)])
def test_stale_identity_is_rejected(key: str, value: JsonValue) -> None:
    endpoint = Endpoint(10, "instance", "FruityLinkScripting-10", "token")
    caps: dict[str, JsonValue] = {"apiVersion": 1, "pid": 10, "instanceId": "instance"}
    verify_identity(endpoint, caps)
    caps[key] = value
    with pytest.raises(ConnectionError):
        verify_identity(endpoint, caps)
