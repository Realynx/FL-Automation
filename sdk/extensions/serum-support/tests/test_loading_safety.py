"""load_preset warnings (state replacement, automation links, signal path) and LoadResult compatibility."""

from __future__ import annotations

import json
from dataclasses import dataclass
from pathlib import Path
from typing import Any

import pytest

from fruitylink_serum import cbor, loading, xfer
from fruitylink_serum.builder import SerumPatch

ZSTD = xfer.zstd_available()
needs_zstd = pytest.mark.skipif(not ZSTD, reason="zstd support requires Python 3.14 or the zstandard package")


@dataclass(frozen=True)
class Param:
    index: int
    name: str
    raw_value: int
    display_value: str


@dataclass(frozen=True)
class Page:
    items: tuple[Param, ...]
    next_offset: int | None
    total: int


class Ops:
    """Channels: 0 Kick, 1 Lead (Serum), 2 clip named for the channel, 3 clip naming a parameter only, 4 a mixer
    event id, 5 another channel's name, 6..9 the event ids FL printed live on 2026-09-14 (no target names at all)."""

    def __init__(self) -> None:
        self.calls: list[tuple[str, dict[str, Any]]] = []
        self.plugins = {
            0: "Kick: has a generator plugin (4 params)",
            1: "Lead: generator 'Serum 2' (4240 params)",
            2: "Lead - Filter 1 Freq: automation clip -> Lead - Filter 1 Freq",
            3: "Cutoff sweep: automation clip -> Filter 1 Freq, Main Vol",
            4: "Mystery: automation clip -> event 0x70401fc0",
            5: "Pad - A Level: automation clip -> Pad - A Level",
            6: "Ember - brightness arc: automation clip -> event 0x180cd",
            7: "tape stop: automation clip -> event 0x48007",
            8: "scratch-pump: automation clip -> event 0x30000, event 0x18000",
            9: "mute toggle: automation clip -> event 0x10007",
        }
        self.params = [Param(0, "Main Vol", 0, "50%"), Param(205, "Filter 1 Freq", 0, "937 Hz")]

    def get_channel_count(self) -> int:
        return len(self.plugins)

    def get_channel_name(self, *, channel: int) -> str:
        return self.plugins[channel].split(":")[0]

    def get_channel_plugin(self, *, channel: int) -> str:
        return self.plugins[channel]

    def query_plugin_parameters(self, *, channel_or_track: int, slot: int = -1, filter: str | None = None,
                                offset: int = 0, limit: int = 512) -> Page:
        self.calls.append(("query", {"channel": channel_or_track, "filter": filter, "offset": offset}))
        if filter is None:   # a raw slot page: ``offset`` is the first parameter index, ``limit`` the slot count
            items = tuple(p for p in self.params if offset <= p.index < offset + limit)
        else:
            items = tuple(p for p in self.params if filter.casefold() in p.name.casefold())
        return Page(items, None, len(items))

    def load_channel_plugin_state(self, *, channel: int, path: str, use_channel_loader: bool = False) -> str:
        self.calls.append(("load", {"channel": channel, "path": path}))
        return f"channel {channel}: loaded"

    def load_mixer_effect_state(self, *, track: int, slot: int, path: str) -> str:
        self.calls.append(("mixer", {"track": track, "slot": slot, "path": path}))
        return f"mixer {track}/{slot}: loaded"

    def get_channel_plugin_state(self, *, channel: int) -> str:
        raise AssertionError("unused")

    def get_mixer_effect_state(self, *, track: int, slot: int) -> str:
        raise AssertionError("unused")


class Studio:
    def __init__(self) -> None:
        self.ops = Ops()


def test_automation_links_attribute_targets_to_the_channel() -> None:
    fl = Studio()
    links = loading.automation_links(fl, 1)
    by_clip = {link["clip_channel"]: link for link in links}
    assert by_clip[2]["attribution"] == "certain" and by_clip[2]["parameter_index"] == 205
    assert by_clip[2]["parameter_name"] == "Filter 1 Freq" and by_clip[2]["decoded"] is None
    assert by_clip[4]["attribution"] == "other" and by_clip[4]["target"] == "event 0x70401fc0"
    assert by_clip[4]["decoded"] == {"kind": "mixer_volume", "index": 1, "slot": -1, "parameter": -1}
    assert by_clip[5]["attribution"] == "other"                         # names another channel, no bare parameter match
    clip3 = [link for link in links if link["clip_channel"] == 3]
    assert [(link["target"], link["attribution"], link["parameter_index"]) for link in clip3] == [
        ("Filter 1 Freq", "possible", 205), ("Main Vol", "possible", 0)]
    assert not any(link["clip_channel"] == 1 for link in links)
    # live 2026-09-14: FL names no plugin-parameter target; the event id decodes to channel 1, parameter 205
    assert by_clip[6]["attribution"] == "certain" and by_clip[6]["parameter_index"] == 205
    assert by_clip[6]["parameter_name"] == "Filter 1 Freq"
    assert by_clip[6]["decoded"] == {"kind": "plugin_parameter", "index": 1, "slot": -1, "parameter": 205}
    assert by_clip[7]["attribution"] == "other" and by_clip[7]["parameter_index"] is None
    assert by_clip[7]["decoded"] == {"kind": "plugin_parameter", "index": 4, "slot": -1, "parameter": 7}
    clip8 = [link for link in links if link["clip_channel"] == 8]
    assert [(link["attribution"], link["decoded"]["kind"], link["decoded"]["index"]) for link in clip8] == [
        ("other", "channel_volume", 3), ("certain", "plugin_parameter", 1)]
    assert clip8[1]["parameter_index"] == 0 and clip8[1]["parameter_name"] == "Main Vol"
    assert by_clip[9]["attribution"] == "unknown" and by_clip[9]["decoded"] is None
    assert [c for c in fl.ops.calls if c[0] == "query" and c[1]["filter"] is None] == [
        ("query", {"channel": 1, "filter": None, "offset": 205}), ("query", {"channel": 1, "filter": None, "offset": 0})]


def test_decoded_links_survive_a_missing_or_failing_parameter_query() -> None:
    class NoQuery(Ops):
        query_plugin_parameters = None  # type: ignore[assignment]

    class Failing(Ops):
        def query_plugin_parameters(self, **kwargs: Any) -> Page:
            if kwargs.get("filter") is None:                        # the raw slot page behind the name lookup
                raise RuntimeError("no parameter interface")
            return super().query_plugin_parameters(**kwargs)

    for ops in (NoQuery(), Failing()):
        fl = Studio()
        fl.ops = ops
        link = {row["clip_channel"]: row for row in loading.automation_links(fl, 1)}[6]
        assert link["attribution"] == "certain" and link["parameter_index"] == 205 and link["parameter_name"] is None


def test_load_preset_result_is_a_string_with_warnings(tmp_path: Path) -> None:
    root = tmp_path / "root"
    fst = root / "Presets" / "User" / "Captured.fst"
    fst.parent.mkdir(parents=True)
    fst.write_bytes(b"FLhd")
    fl = Studio()
    result = loading.load_preset(fl, 1, fst, root=root)
    assert result == "channel 1: loaded" and isinstance(result, str) and isinstance(result, loading.LoadResult)
    assert json.dumps({"load": result}) == '{"load": "channel 1: loaded"}'
    assert result.path == fst.resolve() and result.verification == "channel 1: loaded"
    assert result.warnings[0].startswith("replaces the whole plugin state") and "kParamBendRangeUp/Dn" in result.warnings[0]
    assert any("automation clip 2 'Lead - Filter 1 Freq' drives 'Lead - Filter 1 Freq' (parameter 205) on this channel;" in w
               for w in result.warnings)
    assert any("'Filter 1 Freq' (parameter 205) on this channel (name match" in w for w in result.warnings)
    assert ("channel 1 automates plugin parameter 205 'Filter 1 Freq' (clip 'Ember - brightness arc' on channel 6); "
            "the preset replaces the state that link drives, so check that it stays audible") in result.warnings
    assert any("channel 1 automates plugin parameter 0 'Main Vol' (clip 'scratch-pump' on channel 8)" in w
               for w in result.warnings)
    assert not any("parameter 7" in w or "tape stop" in w for w in result.warnings)    # channel 4's link
    assert any(w.startswith("1 automation link(s) report an event id that does not decode") for w in result.warnings)
    assert any(".fst files are not decoded" in w for w in result.warnings)
    assert len(result.automation) == 10 and result.signal_path == ()
    data = result.to_dict()
    assert data["verification"] == "channel 1: loaded" and data["warnings"] == list(result.warnings) and data["automation"][0]["clip_channel"] == 2
    # mixer-slot loads skip the channel automation scan
    slot_result = loading.load_preset(fl, 3, "Captured", root=root, slot=0)
    assert slot_result == "mixer 3/0: loaded" and slot_result.automation == () and len(slot_result.warnings) == 2


@needs_zstd
def test_load_preset_reports_the_presets_signal_path(tmp_path: Path) -> None:
    root = tmp_path / "root"
    folder = root / "Presets" / "Factory"
    folder.mkdir(parents=True)
    state = {
        "Oscillator0": {"plainParams": {"kParamVolume": 0.0, "kParamUnison": 4.0}},
        "Oscillator1": {"plainParams": {"kParamEnable": 1.0, "kParamVolume": 0.0}},
        "Oscillator4": {"plainParams": {"kParamEnable": 1.0, "kParamVolume": cbor.Float32(0.75)}},
        "VoiceFilter0": {"plainParams": {"kParamEnable": 1.0, "kParamFreq": cbor.Float32(0.5)}},
        "Global0": {"plainParams": {"kParamBendRangeDn": -12.0}},
        "presetName": "Smart",
    }
    preset = folder / "Smart.SerumPreset"
    preset.write_bytes(xfer.write_container(xfer.XferContainer(metadata={"fileType": "SerumPreset", "presetName": "Smart"}, state=state)))
    fl = Studio()
    result = loading.load_preset(fl, 1, "Smart", root=root)
    assert result.path.suffix == ".vstpreset"
    assert any(w.startswith("preset: all enabled oscillator direct levels") and "Filter 1 Freq" in w for w in result.warnings)
    assert any("no mod-matrix row uses Velocity" in w for w in result.warnings)
    assert len(result.signal_path) == 2
    healthy = SerumPatch(name="Healthy").osc_a("saw", level=0.7).filter("lp12", cutoff_hz=900).velocity().save(folder / "Healthy.SerumPreset")
    assert loading.load_preset(fl, 1, healthy, root=root).signal_path == ()
    broken = folder / "Broken.SerumPreset"
    broken.write_bytes(b"not a container")
    with pytest.raises(Exception):
        loading.load_preset(fl, 1, broken, root=root)               # conversion still fails loudly; warnings never mask errors
    direct = loading.load_preset(fl, 1, broken, root=root, format="direct")
    assert any(w.startswith("signal-path check skipped:") for w in direct.warnings)


def test_automation_scan_failure_is_a_warning_not_an_error(tmp_path: Path) -> None:
    fst = tmp_path / "Presets" / "X.fst"
    fst.parent.mkdir(parents=True)
    fst.write_bytes(b"FLhd")

    class BrokenOps(Ops):
        def get_channel_count(self) -> int:
            raise RuntimeError("no channel query on this host")

    class BrokenStudio:
        ops = BrokenOps()

    result = loading.load_preset(BrokenStudio(), 1, fst, root=tmp_path)
    assert result == "channel 1: loaded"
    assert any(w.startswith("automation links could not be inspected: RuntimeError") for w in result.warnings)
