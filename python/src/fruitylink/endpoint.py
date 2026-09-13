"""Per-user discovery. Never include endpoint tokens in reprs or errors."""

import json
import os
import re
from dataclasses import dataclass, field
from pathlib import Path

from .errors import ConnectionError
from .values import JsonValue, json_object, to_json


@dataclass(frozen=True)
class Endpoint:
    pid: int
    instance_id: str
    pipe_name: str
    token: str = field(repr=False)
    api_version: int = 1
    server_pid: int | None = None

    def __post_init__(self) -> None:
        if self.api_version != 1 or self.pid <= 0 or not self.instance_id or not self.token:
            raise ConnectionError("Invalid scripting endpoint identity or API version.")
        if not re.fullmatch(r"[A-Za-z0-9_.-]{1,200}", self.pipe_name):
            raise ConnectionError("Endpoint must name a local Windows pipe.")
        if self.server_pid is not None and self.server_pid <= 0:
            raise ConnectionError("Invalid scripting pipe server identity.")

    @classmethod
    def from_json(cls, value: JsonValue) -> "Endpoint":
        data = json_object(value, "Endpoint")
        pid, version, server = data.get("pid"), data.get("apiVersion"), data.get("serverPid")
        strings = [data.get(key) for key in ("instanceId", "pipeName", "token")]
        if type(pid) is not int or type(version) is not int or not all(isinstance(v, str) for v in strings):
            raise ConnectionError("Malformed scripting endpoint.")
        if server is not None and type(server) is not int:
            raise ConnectionError("Malformed scripting server identity.")
        return cls(pid, str(strings[0]), str(strings[1]), str(strings[2]), version, server)


def discovery_directory() -> Path:
    local = os.environ.get("LOCALAPPDATA")
    if not local:
        raise ConnectionError("LOCALAPPDATA is unavailable; supply an endpoint or transport explicitly.")
    return Path(local) / "FruityLink" / "scripting"


def discover(pid: int | None = None, *, directory: Path | None = None) -> Endpoint:
    """Select one discovery record. A subsequent connect handshake proves liveness."""
    root = directory if directory is not None else discovery_directory()
    paths = [root / f"{pid}.json"] if pid is not None else sorted(root.glob("*.json"))
    if not paths:
        raise ConnectionError("No scripting instance found; enable the FruityLink scripting plugin.")
    if len(paths) != 1:
        raise ConnectionError("Multiple scripting instances found; select an explicit pid.")
    try:
        data = to_json(json.loads(paths[0].read_text(encoding="utf-8")))
        endpoint = Endpoint.from_json(data)
        if paths[0].stem != str(endpoint.pid) or endpoint.pipe_name != f"FruityLinkScripting-{endpoint.pid}":
            raise ConnectionError("Discovery file identity does not match its endpoint.")
        return endpoint
    except (OSError, ValueError, TypeError) as exc:
        raise ConnectionError("Cannot read a valid scripting discovery record.") from exc
