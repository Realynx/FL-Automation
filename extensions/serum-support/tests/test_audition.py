"""audition_candidates scaffolding against a stub Studio that records every host call."""

from __future__ import annotations

from dataclasses import dataclass
from pathlib import Path
from typing import Any

import pytest
from fruitylink.records import NoteSpec

from fruitylink_serum import audition, xfer
from fruitylink_serum.builder import SerumPatch

needs_zstd = pytest.mark.skipif(not xfer.zstd_available(), reason="SerumPatch candidates need zstd to write the .vstpreset")


@dataclass(frozen=True)
class Note:
    index: int
    channel: int
    key: int
    start_tick: int
    length_tick: int
    velocity: int
    muted: bool


@dataclass(frozen=True)
class Clip:
    index: int
    track: int
    start_tick: int
    length_tick: int
    source_kind: str
    source_index: int
    muted: bool


@dataclass(frozen=True)
class PatternInfo:
    index: int
    name: str
    length_tick: int | None
    note_count: int | None
    current: bool


@dataclass(frozen=True)
class Page:
    items: tuple[Any, ...]
    next_offset: int | None
    total: int


class Ops:
    def __init__(self) -> None:
        self.calls: list[tuple[str, dict[str, Any]]] = []
        self.channels = ["Kick", "Lead"]
        self.patterns = 3
        self.markers = 2
        self.notes = [Note(0, 1, 60, 0, 96, 100, False), Note(1, 1, 64, 96, 96, 80, False),
                      Note(2, 1, 67, 192, 300, 90, False), Note(3, 1, 72, 0, 48, 50, True)]

    def _log(self, op: str, **kwargs: Any) -> None:
        self.calls.append((op, kwargs))

    def get_ppq(self) -> int:
        return 96

    def get_tempo(self) -> float:
        return 120.0

    def query_notes(self, *, pattern: int, channel: int = -1, offset: int = 0, limit: int = 512) -> Page:
        assert pattern == 2
        return Page(tuple(self.notes[offset:offset + 2]), offset + 2 if offset + 2 < len(self.notes) else None, len(self.notes))

    def query_patterns(self) -> tuple[PatternInfo, ...]:
        return (PatternInfo(1, "Drums", 384, 4, False), PatternInfo(2, "Lead line", None, 4, True))

    def query_clips(self, *, track: int = -1, offset: int = 0, limit: int = 512) -> Page:
        return Page((Clip(0, 1, 0, 384, "pattern", 1, False), Clip(1, 3, 0, 384, "channel", 5, False)), None, 2)

    def get_channel_name(self, *, channel: int) -> str:
        return self.channels[channel]

    def add_channel(self, *, plugin_name: str) -> int:
        self._log("add_channel", plugin_name=plugin_name)
        self.channels.append(plugin_name)
        return len(self.channels) - 1

    def set_channel_name(self, *, channel: int, name: str) -> None:
        self._log("set_channel_name", channel=channel, name=name)
        self.channels[channel] = name

    def set_channel_fx_route(self, *, channel: int, mixer_track: int) -> None:
        self._log("route", channel=channel, mixer_track=mixer_track)

    def load_channel_plugin_state(self, *, channel: int, path: str, use_channel_loader: bool = False) -> str:
        self._log("load", channel=channel, path=path)
        return f"channel {channel}: loaded"

    def load_mixer_effect_state(self, *, track: int, slot: int, path: str) -> str:
        return "unused"

    def get_channel_count(self) -> int:
        return len(self.channels)

    def get_channel_plugin(self, *, channel: int) -> str:
        return f"{self.channels[channel]}: generator 'Serum 2'"

    def create_pattern(self) -> int:
        self.patterns += 1
        self._log("create_pattern")
        return self.patterns

    def set_pattern_name(self, *, index: int, name: str) -> None:
        self._log("set_pattern_name", index=index, name=name)

    def add_notes(self, *, pattern: int, notes: list[NoteSpec]) -> None:
        self._log("add_notes", pattern=pattern, notes=list(notes))

    def add_pattern_clip(self, *, pattern: int, track: int, start_tick: int, length_tick: int) -> None:
        self._log("add_pattern_clip", pattern=pattern, track=track, start_tick=start_tick, length_tick=length_tick)

    def list_markers(self) -> str:
        return f"{self.markers} markers:\nIntro @ tick 0 (bar 1)\nEnd @ tick 9999 (bar 27)" if self.markers else "0 markers:"

    def delete_marker(self, *, index: int) -> None:
        assert index == 0 and self.markers > 0
        self.markers -= 1
        self._log("delete_marker", index=index)


class Studio:
    def __init__(self) -> None:
        self.ops = Ops()


@needs_zstd
def test_layout_places_candidates_back_to_back_on_free_tracks(tmp_path: Path) -> None:
    fst = tmp_path / "root" / "Presets" / "Factory" / "Bright.fst"
    fst.parent.mkdir(parents=True)
    fst.write_bytes(b"FLhd")
    fl = Studio()
    patch = SerumPatch(name="Built square").osc_a("square")
    layout = audition.audition_candidates(fl, source_pattern=2, mixer_track=7, candidates=[fst, patch, 1],
                                          accompaniment_patterns=[1], gap_bars=1, root=tmp_path / "root")
    # three unmuted notes end at tick 492 -> 2 bars (768) + 1 gap bar (384) per slot
    assert layout["slot_ticks"] == 1152 and layout["gap_ticks"] == 384 and layout["notes_copied"] == 3
    assert layout["track"] == 2 and layout["accompaniment_tracks"] == [4]      # tracks 1 and 3 hold clips
    starts = [c["start_tick"] for c in layout["candidates"]]
    assert starts == [0, 1152, 2304] and layout["total_ticks"] == 3 * 1152 - 384
    assert [c["end_tick"] - c["start_tick"] for c in layout["candidates"]] == [768, 768, 768]
    assert layout["candidates"][0]["start_seconds"] == 0 and layout["candidates"][1]["start_seconds"] == pytest.approx(6.0)
    assert layout["total_seconds"] == pytest.approx(16.0) and layout["candidates"][2]["end_seconds"] == pytest.approx(16.0)
    labels = [c["label"] for c in layout["candidates"]]
    assert labels == ["Bright", "Built square", "Lead"]
    channels = [c["channel"] for c in layout["candidates"]]
    assert channels == [2, 3, 1]                                                # two added, one existing
    assert layout["candidates"][0]["load"] == "channel 2: loaded" and "replaces the whole plugin state" in layout["candidates"][0]["warnings"][0]
    assert layout["candidates"][1]["load"] == "channel 3: loaded" and layout["candidates"][2]["load"] is None
    assert layout["markers_deleted"] == 2 and fl.ops.markers == 0
    routes = [c for c in fl.ops.calls if c[0] == "route"]
    assert [r[1]["mixer_track"] for r in routes] == [7, 7, 7]
    notes_calls = [c for c in fl.ops.calls if c[0] == "add_notes"]
    assert [n.channel for n in notes_calls[0][1]["notes"]] == [2, 2, 2]        # notes retargeted, muted one dropped
    assert notes_calls[2][1]["notes"][1] == NoteSpec(1, 64, 96, 96, 80)
    clips = [c[1] for c in fl.ops.calls if c[0] == "add_pattern_clip"]
    assert [(c["pattern"], c["track"], c["start_tick"]) for c in clips] == [
        (4, 2, 0), (1, 4, 0), (5, 2, 1152), (1, 4, 1152), (6, 2, 2304), (1, 4, 2304)]
    loads = [c[1] for c in fl.ops.calls if c[0] == "load"]
    assert Path(loads[0]["path"]) == fst.resolve() and Path(loads[1]["path"]).suffix == ".vstpreset"
    assert fl.ops.calls[0] == ("add_channel", {"plugin_name": "Serum 2"})
    assert fl.ops.channels[2] == "Audition 1: Bright" and fl.ops.channels[3] == "Audition 2: Built square"


def test_start_track_and_validation() -> None:
    fl = Studio()
    layout = audition.audition_candidates(fl, source_pattern=2, mixer_track=1, candidates=[1], start_track=10, delete_markers=False)
    assert layout["track"] == 10 and layout["markers_deleted"] == 2 - fl.ops.markers == 0
    assert audition.free_playlist_tracks(fl, 3) == [2, 4, 5]
    with pytest.raises(ValueError):
        audition.audition_candidates(fl, source_pattern=2, mixer_track=1, candidates=[])
    with pytest.raises(ValueError):
        audition.audition_candidates(fl, source_pattern=2, mixer_track=1, candidates=[1], gap_bars=-1)
    with pytest.raises(TypeError):
        audition.audition_candidates(fl, source_pattern=2, mixer_track=1, candidates=[2.5])
    fl.ops.notes = []
    with pytest.raises(ValueError):
        audition.audition_candidates(fl, source_pattern=2, mixer_track=1, candidates=[1])
