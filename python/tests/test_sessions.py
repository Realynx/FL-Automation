from __future__ import annotations

from pathlib import Path
from types import SimpleNamespace
from typing import Any, cast

import pytest

from fruitylink import ConnectionError, Studio, sessions
from fruitylink._session_broker import SessionBroker


def flp_bytes(payload: bytes = b"project") -> bytes:
    return b"FLhd" + (6).to_bytes(4, "little") + b"\0" * 6 + payload


class FakeProject:
    def __init__(self, project: Path) -> None:
        self.path = project
        self.saved: list[tuple[str, Path]] = []

    @property
    def info(self) -> SimpleNamespace:
        return SimpleNamespace(path=str(self.path))

    def save(self, path: str) -> None:
        target = Path(path)
        target.write_bytes(flp_bytes(b"saved"))
        self.saved.append(("save", target))

    def save_copy(self, path: str) -> None:
        target = Path(path)
        target.write_bytes(flp_bytes(b"snapshot"))
        self.saved.append(("copy", target))


class FakeStudio:
    def __init__(self, project: Path) -> None:
        self.project = FakeProject(project)
        self.closed = False

    def close(self) -> None:
        self.closed = True


class FakeBroker:
    def __init__(self, responses: list[dict[str, Any]] | None = None, *, pid: int = 41) -> None:
        self.initial: dict[str, Any] = {"processId": pid, "desktopName": "job-desktop"}
        self.responses = list(responses or [])
        self.closed = False
        self.timeouts: list[float] = []

    def request(self, value: dict[str, Any], timeout: float = 10) -> dict[str, Any]:
        self.timeouts.append(timeout)
        if not self.responses:
            raise AssertionError(f"Unexpected broker request: {value}")
        return self.responses.pop(0)

    def close(self) -> None:
        self.closed = True


def test_session_save_uses_verified_raw_flp_snapshot_writer(tmp_path: Path) -> None:
    project = tmp_path / "owned.flp"
    project.write_bytes(flp_bytes())
    studio = FakeStudio(project)
    session = sessions.StudioSession(cast(SessionBroker, FakeBroker()), cast(Studio, studio),
                                     tmp_path / "FL64.exe", tmp_path / "SessionHost.exe", project, tmp_path, True)
    assert session.save() == project
    assert studio.project.saved == [("copy", project)]
    assert project.read_bytes().startswith(b"FLhd")


def test_render_progress_is_allowed_only_during_rendering(tmp_path: Path) -> None:
    progress = {"modal": True, "className": "TWAVRenderForm", "title": "Rendering to song.wav"}
    for rendering in (True, False):
        broker = cast(SessionBroker, FakeBroker([{"hasExited": False}, {"windows": [progress]}]))
        if rendering:
            assert sessions._check_process(broker, tmp_path / "dialog.json", rendering=True)
        else:
            with pytest.raises(ConnectionError, match="dialog input"):
                sessions._check_process(broker, tmp_path / "dialog.json")


def test_render_progress_does_not_hide_an_error_dialog(tmp_path: Path) -> None:
    windows = [{"modal": True, "className": "TWAVRenderForm", "title": "Rendering to song.wav"},
               {"modal": True, "className": "TMsgForm", "title": "Missing plugin"}]
    broker = cast(SessionBroker, FakeBroker([{"hasExited": False}, {"windows": windows}]))
    with pytest.raises(ConnectionError, match="Missing plugin"):
        sessions._check_process(broker, tmp_path / "dialog.json", rendering=True)


def test_launch_closes_owned_broker_when_endpoint_startup_fails(
        tmp_path: Path, monkeypatch: pytest.MonkeyPatch) -> None:
    executable = tmp_path / "FL64.exe"
    launcher = tmp_path / "SessionHost.exe"
    source = tmp_path / "source.flp"
    project = tmp_path / "work" / "owned.flp"
    executable.write_bytes(b"exe")
    launcher.write_bytes(b"helper")
    original = flp_bytes(b"source stays unchanged")
    source.write_bytes(original)
    broker = FakeBroker()

    monkeypatch.setattr("fruitylink.sessions.os.name", "nt")
    monkeypatch.setattr(sessions, "SessionBroker", lambda *args, **kwargs: broker)
    monkeypatch.setattr(sessions, "_wait_studio",
                        lambda *args, **kwargs: (_ for _ in ()).throw(ConnectionError("not ready")))

    with pytest.raises(ConnectionError, match="not ready"):
        sessions.launch(executable, project, source=source, launcher=launcher)

    assert broker.closed
    assert source.read_bytes() == original
    assert project.read_bytes() == original


@pytest.mark.parametrize("target_name", ["existing.flp", "new.wav"])
def test_launch_rejects_invalid_target_before_copy_or_broker(
        tmp_path: Path, monkeypatch: pytest.MonkeyPatch, target_name: str) -> None:
    executable = tmp_path / "FL64.exe"
    launcher = tmp_path / "SessionHost.exe"
    source = tmp_path / "source.flp"
    target = tmp_path / target_name
    executable.write_bytes(b"exe")
    launcher.write_bytes(b"helper")
    original = flp_bytes(b"source")
    source.write_bytes(original)
    if target.suffix == ".flp":
        target.write_bytes(b"do not overwrite")
    monkeypatch.setattr("fruitylink.sessions.os.name", "nt")
    monkeypatch.setattr(sessions, "SessionBroker",
                        lambda *args, **kwargs: pytest.fail("broker must not start"))

    with pytest.raises(ValueError, match="new .flp"):
        sessions.launch(executable, target, source=source, launcher=launcher)

    assert source.read_bytes() == original
    if target.exists():
        assert target.read_bytes() == b"do not overwrite"


def test_launch_rejects_invalid_source_without_creating_target(
        tmp_path: Path, monkeypatch: pytest.MonkeyPatch) -> None:
    executable = tmp_path / "FL64.exe"
    launcher = tmp_path / "SessionHost.exe"
    source = tmp_path / "invalid.flp"
    target = tmp_path / "owned.flp"
    executable.write_bytes(b"exe")
    launcher.write_bytes(b"helper")
    source.write_bytes(b"not a project")
    monkeypatch.setattr("fruitylink.sessions.os.name", "nt")
    monkeypatch.setattr(sessions, "SessionBroker",
                        lambda *args, **kwargs: pytest.fail("broker must not start"))

    with pytest.raises(ValueError, match="Not an FL Studio project"):
        sessions.launch(executable, target, source=source, launcher=launcher)

    assert source.read_bytes() == b"not a project"
    assert not target.exists()


def test_failed_project_copy_leaves_no_target_or_temporary_file(
        tmp_path: Path, monkeypatch: pytest.MonkeyPatch) -> None:
    source = tmp_path / "source.flp"
    target = tmp_path / "owned.flp"
    source.write_bytes(flp_bytes(b"source"))

    def fail_after_partial_copy(original: Any, output: Any) -> None:
        output.write(original.read(5))
        raise OSError("disk full")

    monkeypatch.setattr("fruitylink.sessions.shutil.copyfileobj", fail_after_partial_copy)

    with pytest.raises(OSError, match="disk full"):
        sessions._copy_project(source, target)

    assert source.read_bytes() == flp_bytes(b"source")
    assert not target.exists()
    assert not list(tmp_path.glob("owned.flp.*.tmp"))


def test_process_checks_forward_remaining_deadline_to_broker(
        tmp_path: Path, monkeypatch: pytest.MonkeyPatch) -> None:
    broker = FakeBroker([{"hasExited": False}, {"windows": []}])
    monkeypatch.setattr("fruitylink.sessions.time.monotonic", lambda: 40.0)

    assert sessions._check_process(cast(SessionBroker, broker), tmp_path / "dialog.json", deadline=42.5)
    assert broker.timeouts == [2.5, 2.5]


def test_render_failure_closes_editor_preserves_snapshot_and_does_not_create_output(
        tmp_path: Path, monkeypatch: pytest.MonkeyPatch) -> None:
    project = tmp_path / "owned.flp"
    project.write_bytes(flp_bytes())
    authoring = FakeBroker()
    studio = FakeStudio(project)
    session = sessions.StudioSession(cast(SessionBroker, authoring), cast(Studio, studio), tmp_path / "FL64.exe",
                                     tmp_path / "SessionHost.exe", project, tmp_path / "job", True)
    session.job_directory.mkdir()
    renderer = FakeBroker([{"hasExited": True, "exitCode": 7}])
    monkeypatch.setattr(sessions, "SessionBroker", lambda *args, **kwargs: renderer)
    output = tmp_path / "result.wav"

    with pytest.raises(ConnectionError, match="code 7"):
        session.render(output)

    assert authoring.closed and studio.closed and renderer.closed
    assert not output.exists()
    snapshots = list(session.job_directory.glob("render-*/*.flp"))
    assert len(snapshots) == 1 and snapshots[0].read_bytes() == flp_bytes(b"snapshot")


def test_existing_render_output_is_untouched_and_session_stays_open(tmp_path: Path) -> None:
    project = tmp_path / "owned.flp"
    project.write_bytes(flp_bytes())
    broker = FakeBroker()
    studio = FakeStudio(project)
    session = sessions.StudioSession(cast(SessionBroker, broker), cast(Studio, studio), tmp_path / "FL64.exe",
                                     tmp_path / "SessionHost.exe", project, tmp_path / "job", True)
    output = tmp_path / "result.wav"
    output.write_bytes(b"keep")

    with pytest.raises(ValueError, match="new .wav"):
        session.render(output)

    assert output.read_bytes() == b"keep"
    assert not broker.closed and not studio.closed


def test_close_still_closes_studio_when_broker_cleanup_fails(tmp_path: Path) -> None:
    class FailingBroker(FakeBroker):
        def close(self) -> None:
            self.closed = True
            raise OSError("broker cleanup failed")

    broker = FailingBroker()
    studio = FakeStudio(tmp_path / "owned.flp")
    session = sessions.StudioSession(cast(SessionBroker, broker), cast(Studio, studio), tmp_path / "FL64.exe",
                                     tmp_path / "SessionHost.exe", studio.project.path, tmp_path / "job", True)

    with pytest.raises(OSError, match="broker cleanup failed"):
        session.close()

    assert broker.closed and studio.closed


def test_startup_modal_writes_diagnostic_and_stops_waiting(tmp_path: Path) -> None:
    modal = {"modal": True, "title": "Missing samples", "body": "Locate files", "className": "TMsgForm"}
    broker = FakeBroker([{"hasExited": False}, {"windows": [modal]}])
    discovery = tmp_path / "scripting"
    discovery.mkdir()

    with pytest.raises(ConnectionError, match="Locate files.*startup-dialog.json"):
        sessions._wait_studio(cast(SessionBroker, broker), discovery, tmp_path / "owned.flp", 5)

    assert (tmp_path / "startup-dialog.json").read_text(encoding="utf-8").find("Missing samples") >= 0


def test_render_timeout_closes_renderer_and_preserves_snapshot(
        tmp_path: Path, monkeypatch: pytest.MonkeyPatch) -> None:
    project = tmp_path / "owned.flp"
    snapshot = tmp_path / "render" / "result.flp"
    snapshot.parent.mkdir()
    snapshot.write_bytes(flp_bytes(b"preserve"))
    session = sessions.StudioSession(cast(SessionBroker, FakeBroker()), cast(Studio, FakeStudio(project)),
                                     tmp_path / "FL64.exe",
                                     tmp_path / "SessionHost.exe", project, tmp_path / "job", True)
    renderer = FakeBroker([{"hasExited": False}, {"windows": []}])
    monkeypatch.setattr(sessions, "SessionBroker", lambda *args, **kwargs: renderer)
    times = iter([10.0, 10.1, 11.0])
    monkeypatch.setattr("fruitylink.sessions.time.monotonic", lambda: next(times))

    with pytest.raises(TimeoutError, match="deadline"):
        sessions._render(session, snapshot, snapshot.parent, 0.5)

    assert renderer.closed
    assert snapshot.read_bytes() == flp_bytes(b"preserve")
    assert not snapshot.with_suffix(".wav").exists()
