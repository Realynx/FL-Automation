"""Owned FL processes for scripts and build jobs; no MCP server or plugin is required."""

import json
import math
import os
import shutil
import time
import uuid
from pathlib import Path
from types import TracebackType

from ._session_broker import SessionBroker
from .analysis.audio import load_wav
from .audition import isolate_range
from .endpoint import discover
from .errors import ConnectionError, FruityLinkError
from .studio import Studio, verify_identity
from .transport import NamedPipeTransport
from .values import JsonValue


def _seconds(value: float) -> float:
    if isinstance(value, bool) or not math.isfinite(value) or value <= 0:
        raise ValueError("Timeout must be finite and positive.")
    return value


def _project(path: Path) -> None:
    with path.open("rb") as stream:
        header = stream.read(14)
    if len(header) < 14 or header[:4] != b"FLhd" or int.from_bytes(header[4:8], "little") != 6:
        raise ValueError(f"Not an FL Studio project: {path}")


def _copy_project(source: Path, destination: Path) -> None:
    _project(source)
    destination.parent.mkdir(parents=True, exist_ok=True)
    # Windows rename refuses existing targets. A failed copy never exposes a partial FLP.
    temporary = destination.with_name(destination.name + "." + uuid.uuid4().hex + ".tmp")
    try:
        with source.open("rb") as original, temporary.open("xb") as output:
            shutil.copyfileobj(original, output)
        _project(temporary)
        temporary.rename(destination)
    finally:
        temporary.unlink(missing_ok=True)


def _environment(discovery: Path) -> dict[str, JsonValue]:
    return {"FRUITYLINK_AUTOMATION": "1", "FRUITYLINK_AUTOMATION_DISCOVERY": str(discovery),
            "FL_MCP_SESSION_TOKEN": None, "FL_MCP_WORKSPACE": None}


def _remaining(deadline: float) -> float:
    remaining = deadline - time.monotonic()
    if remaining <= 0:
        raise TimeoutError("The owned FL operation exceeded its deadline; working copies remain on disk.")
    return remaining


def _check_process(broker: SessionBroker, diagnostics: Path, deadline: float | None = None,
                   *, rendering: bool = False) -> bool:
    deadline = deadline if deadline is not None else time.monotonic() + 10
    status = broker.request({"method": "status"}, timeout=min(10, _remaining(deadline)))
    if status.get("hasExited"):
        if status.get("exitCode") != 0:
            raise ConnectionError(f"FL exited with code {status.get('exitCode')}; project copies remain on disk.")
        return False
    inspected = broker.request({"method": "windows"}, timeout=min(10, _remaining(deadline)))
    windows = inspected.get("windows")
    if not isinstance(windows, list):
        raise ConnectionError("The SDK session host returned invalid window metadata.")
    modals = [window for window in windows if isinstance(window, dict) and window.get("modal")
              and not (rendering and window.get("className") == "TWAVRenderForm"
                       and str(window.get("title", "")).startswith("Rendering to "))]
    if modals:
        diagnostics.write_text(json.dumps(modals, indent=2), encoding="utf-8")
        titles = "; ".join(str(window.get("body") or window.get("title") or window.get("className"))
                           for window in modals)
        raise ConnectionError(f"FL requires dialog input: {titles}. Diagnostic: {diagnostics}. "
                              "No dialog was answered; the source project is unchanged.")
    return True


def _wait_studio(broker: SessionBroker, discovery: Path, project: Path, timeout: float) -> Studio:
    pid = broker.initial.get("processId")
    if type(pid) is not int or pid <= 0:
        raise ConnectionError("The SDK session host returned an invalid FL process identity.")
    deadline = time.monotonic() + timeout
    last_error: Exception | None = None
    while time.monotonic() < deadline:
        if not _check_process(broker, discovery.parent / "startup-dialog.json", deadline):
            raise ConnectionError("FL exited before its scripting endpoint became ready.")
        try:
            endpoint = discover(pid, directory=discovery)
            requests = NamedPipeTransport(endpoint, timeout=min(2, _remaining(deadline)))
            studio = Studio(requests)
            verify_identity(endpoint, studio.capabilities())
            requests.timeout = min(2, _remaining(deadline))
            if Path(studio.project.info.path).resolve() == project:
                # Discovery and capabilities verify exact PID/instance; path verifies project load completion.
                requests.timeout = 30
                return studio
        except (FruityLinkError, OSError) as error:
            last_error = error
        time.sleep(0.1)
    raise ConnectionError("Timed out waiting for the owned FL project's scripting endpoint. "
                          "Install the matching SDK framework/session host.") from last_error


class StudioSession:
    """An owned FL process. Closing terminates it without saving; save explicitly first.

    Background means a private Windows desktop, with FL's normal GUI engine still running.
    Each session owns its process tree and fresh project copy. Different sessions can run concurrently.
    Do not issue concurrent operations against one session.
    """

    def __init__(self, broker: SessionBroker, studio: Studio, executable: Path, launcher: Path,
                 project: Path, job: Path, background: bool) -> None:
        self._broker = broker
        self.studio = studio
        self.executable = executable
        self.launcher = launcher
        self.project_path = project
        self.job_directory = job
        self.background = background
        self.process_id = int(str(broker.initial["processId"]))
        self.desktop_name = str(broker.initial.get("desktopName", ""))
        self._closed = False

    def _require_open(self) -> None:
        if self._closed:
            raise ConnectionError("This owned FL session is closed.")

    def windows(self) -> list[JsonValue]:
        """Read process-scoped window metadata without displaying or activating its desktop."""
        self._require_open()
        value = self._broker.request({"method": "windows"}).get("windows")
        if not isinstance(value, list):
            raise ConnectionError("Invalid window snapshot.")
        return value

    def save(self, path: str | Path | None = None) -> Path:
        """Save the session's working copy, or save a new snapshot without changing its identity."""
        self._require_open()
        if path is None:
            self.studio.project.save_copy(str(self.project_path))
            target = self.project_path
        else:
            target = Path(path).resolve()
            if target.suffix.lower() != ".flp" or target.exists():
                raise ValueError("Snapshot must be a new .flp path.")
            target.parent.mkdir(parents=True, exist_ok=True)
            self.studio.project.save_copy(str(target))
        _project(target)
        return target

    def render(self, output: str | Path, *, timeout: float = 180) -> Path:
        """Save a fresh snapshot, close this editor, and export a WAV in an owned background process.

        Uses FL's command-line export settings. The session is closed after snapshot success,
        even if the renderer subsequently fails. Existing output files are never overwritten.
        """
        _seconds(timeout)
        self._require_open()
        output = _render_output(output)
        render_directory = self.job_directory / ("render-" + uuid.uuid4().hex)
        render_directory.mkdir()
        snapshot = self.save(render_directory / (output.stem + ".flp"))
        self.close()
        with_output = _render(self, snapshot, render_directory, timeout)
        # Header/chunk/sample-format validation, reading at most a tiny PCM section.
        try:
            load_wav(with_output, end_seconds=0.01)
        except (OSError, ValueError) as error:
            raise ConnectionError(f"FL did not produce a valid WAV; snapshot preserved at {snapshot}.") from error
        output.parent.mkdir(parents=True, exist_ok=True)
        with with_output.open("rb") as source, output.open("xb") as destination:
            shutil.copyfileobj(source, destination)
        return output

    def render_range(self, output: str | Path, *, start_tick: int, length_tick: int, cut_clips: bool = False,
                     tail_beats: float = 0, timeout: float = 180) -> Path:
        """Trim the live project to one tick range with ``isolate_range``, then ``render`` it.

        FL's exporter has no range option, so the section is isolated in the editor first: clips
        outside ``[start_tick, start_tick + length_tick)`` are deleted, the rest shift to tick 0
        and markers are removed, so the WAV ends at the last clip unless ``tail_beats`` > 0 keeps
        an End marker that many beats past the range for reverb and release tails. The working
        copy on disk is untouched unless ``save()`` is called afterwards; the render snapshot
        holds the trimmed project. See ``fruitylink.audition``.
        """
        _seconds(timeout)
        self._require_open()
        _render_output(output)
        isolate_range(self.studio, start_tick, length_tick, cut_clips=cut_clips, tail_beats=tail_beats)
        return self.render(output, timeout=timeout)

    def close(self) -> None:
        if not self._closed:
            self._closed = True
            try:
                self._broker.close()
            finally:
                self.studio.close()

    def __enter__(self) -> "StudioSession":
        return self

    def __exit__(self, exc_type: type[BaseException] | None, exc: BaseException | None,
                 traceback: TracebackType | None) -> None:
        self.close()


def _render_output(output: str | Path) -> Path:
    resolved = Path(output).resolve()
    if resolved.suffix.lower() != ".wav" or resolved.exists():
        raise ValueError("Render output must be a new .wav path.")
    return resolved


def _render(session: StudioSession, snapshot: Path, directory: Path, timeout: float) -> Path:
    deadline = time.monotonic() + timeout
    broker = SessionBroker(session.executable, session.launcher,
                           ["/R", "/Ewav", "/O" + str(directory), str(snapshot)],
                           _environment(session.job_directory / "scripting"), session.background,
                           startup_timeout=_remaining(deadline))
    try:
        while _check_process(broker, directory / "render-dialog.json", deadline, rendering=True):
            if time.monotonic() >= deadline:
                raise TimeoutError(f"FL render timed out; snapshot preserved at {snapshot}.")
            time.sleep(0.2)
        return snapshot.with_suffix(".wav")
    finally:
        broker.close()


def launch(executable: str | Path, project: str | Path, *, source: str | Path | None = None,
           background: bool = True, timeout: float = 60, launcher: str | Path | None = None) -> StudioSession:
    """Launch a new SDK-owned FL project, optionally copied from a supplied FLP.

    ``project`` must not exist. Default source is the installed Empty template. The installed
    SDK SessionHost owns the process/desktop lifetime; no MCP plugin is required. Requires Windows,
    licensed FL Studio and the matching FruityLink framework. Background is not renderer-free.
    """
    _seconds(timeout)
    if os.name != "nt":
        raise OSError("FL Studio sessions require Windows.")
    executable, project = Path(executable).resolve(), Path(project).resolve()
    helper = Path(launcher).resolve() if launcher is not None else (
        executable.parent / "FruityLink" / "tools" / "session-host" / "FruityLink.SessionHost.exe")
    if not executable.is_file() or not helper.is_file():
        raise FileNotFoundError("FL executable or FruityLink SessionHost is missing; install the matching framework.")
    if project.suffix.lower() != ".flp" or project.exists():
        raise ValueError("Project must be a new .flp path.")
    template = Path(source).resolve() if source is not None else (
        executable.parent / "Data" / "Templates" / "Empty" / "Empty.flp")
    _copy_project(template, project)
    job = project.parent / ".fruitylink" / uuid.uuid4().hex
    discovery = job / "scripting"
    discovery.mkdir(parents=True)
    deadline = time.monotonic() + timeout
    broker = SessionBroker(executable, helper, [str(project)], _environment(discovery), background,
                           startup_timeout=_remaining(deadline))
    try:
        studio = _wait_studio(broker, discovery, project, _remaining(deadline))
        broker.request({"method": "ready"}, timeout=min(10, _remaining(deadline)))
        return StudioSession(broker, studio, executable, helper, project, job, background)
    except BaseException:
        broker.close()
        raise
