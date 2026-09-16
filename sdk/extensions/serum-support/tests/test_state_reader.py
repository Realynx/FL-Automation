"""State reader tests on a synthetic FLP built with the event encoder (no proprietary content)."""

from __future__ import annotations

from pathlib import Path
from typing import Any

import pytest
from flp_fixture import flp_bytes, processor_block, wrapper_state

from fruitylink_serum import state_reader, xfer
from fruitylink_serum.builder import SerumPatch
from fruitylink_serum.inventory import PresetFile

ZSTD = xfer.zstd_available()
needs_zstd = pytest.mark.skipif(not ZSTD, reason="zstd support requires Python 3.14 or the zstandard package")


def test_event_codec_round_trips_every_width() -> None:
    stream = b"".join(state_reader.encode_event(e, v) for e, v in ((1, 7), (64, 300), (150, 70000), (213, b"x" * 200)))
    data = b"FLhd" + (6).to_bytes(4, "little") + bytes(6) + b"FLdt" + len(stream).to_bytes(4, "little") + stream
    assert list(state_reader.iter_events(data)) == [(1, 7), (64, 300), (150, 70000), (213, b"x" * 200)]
    with pytest.raises(ValueError):
        list(state_reader.iter_events(b"nope"))


@needs_zstd
def test_reader_finds_the_serum_channel_and_refuses_others(tmp_path: Path) -> None:
    serum = wrapper_state(processor_block({"Oscillator0": {"plainParams": {"kParamUnison": 5.0}}, "component": "processor"}),
                          b"XferJson\x00" + bytes(8) + b"{}" + bytes(8))
    other = wrapper_state(processor_block({"x": 1}), class_id=bytes(16))
    flp = tmp_path / "s.flp"
    flp.write_bytes(flp_bytes([{"index": 0, "plugin": "Sampler"}, {"index": 1, "plugin": "Fruity Wrapper", "state": serum},
                               {"index": 2, "plugin": "Fruity Wrapper", "state": other}]))
    states = state_reader.read_flp_channel_states(flp.read_bytes())
    assert sorted(states) == [1, 2] and states[1].plugin_name == "Fruity Wrapper"
    assert states[1].controller is not None and states[1].controller.startswith(xfer.MAGIC)
    container = state_reader.read_channel_state_from_flp(flp, 1)
    assert container.state["Oscillator0"]["plainParams"]["kParamUnison"] == 5.0
    with pytest.raises(LookupError, match="does not hold Serum 2"):
        state_reader.read_channel_state_from_flp(flp, 2)
    with pytest.raises(LookupError, match="no plugin state"):
        state_reader.read_channel_state_from_flp(flp, 0)
    assert state_reader.read_channel_state_from_flp(flp, 2, require_serum=False).state == {"x": 1}


@needs_zstd
def test_read_channel_state_saves_a_snapshot_copy_and_cleans_up(tmp_path: Path) -> None:
    project = tmp_path / "Song" / "session.flp"
    project.parent.mkdir()
    serum = wrapper_state(processor_block({"Env0": {"plainParams": {"kParamAttack": 0.01}}}))
    flp = flp_bytes([{"index": 0, "plugin": "Fruity Wrapper", "state": serum}])

    class Info:
        path = str(project)

    class Project:
        saved: list[str] = []

        @property
        def info(self) -> Info:  # fruitylink exposes project.info as a property
            return Info()

        def save_copy(self, path: str) -> None:
            self.saved.append(path)
            Path(path).write_bytes(flp)

    class Studio:
        project = Project()

    fl = Studio()
    container = state_reader.read_channel_state(fl, 0)
    assert container.state["Env0"]["plainParams"]["kParamAttack"] == pytest.approx(0.01)
    snapshot = Path(Studio.project.saved[-1])
    assert snapshot.parent == project.parent and snapshot.name.startswith("state-read-0-") and not snapshot.exists()
    kept = state_reader.read_channel_state(fl, 0, workspace_dir=tmp_path, keep=True)
    assert kept.state == container.state and Path(Studio.project.saved[-1]).exists()


def test_diff_states_reports_changed_flattened_keys() -> None:
    before = SerumPatch().osc_a(unison=3).container()
    after = SerumPatch().osc_a(unison=5).sub("saw").container()
    changed = state_reader.diff_states(before, after)
    assert changed["Oscillator0/plainParams/kParamUnison"] == (3.0, 5.0)
    assert changed["Oscillator4/SubOsc4/plainParams/kParamShape"] == (None, "kSaw")
    assert "mpePitchBendRange" not in changed


def test_preset_file_ergonomics(tmp_path: Path) -> None:
    root = tmp_path / "Serum 2 Presets"
    path = root / "Presets" / "Factory" / "Lead" / "LD - Glow.SerumPreset"
    preset = PresetFile(root=root, path=path, relative_path=path.relative_to(root), format="SerumPreset", size_bytes=3)
    assert preset.name == "LD - Glow" and preset.folder == "Factory/Lead"
    data: dict[str, Any] = preset.to_dict()
    assert data["name"] == "LD - Glow" and data["folder"] == "Factory/Lead" and isinstance(data["path"], str)


def test_read_channel_state_uses_the_info_property(tmp_path: Path) -> None:
    """fl.project.info is a property; calling it raised "'ProjectInfo' object is not callable" live."""

    class Info:
        path = str(tmp_path / "song.flp")

    class Project:
        @property
        def info(self) -> Info:
            return Info()

        def save_copy(self, path: str) -> None:
            Path(path).write_bytes(flp_bytes([]))

    class Studio:
        project = Project()

    with pytest.raises(LookupError):
        state_reader.read_channel_state(Studio(), 0)
