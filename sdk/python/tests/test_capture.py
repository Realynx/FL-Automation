"""Offline coverage for live capture: planning, state parsing, file discovery/matching, measurement,
the envelope readout, the live/render policy and the full driver sequence against a fake FL."""

import math
import struct
from pathlib import Path
from typing import Any

import pytest
from conftest import RecordingTransport

from fruitylink import (
    RECORDING_FILTER_AUDIO,
    RECORDING_FILTER_AUTOMATION,
    RECORDING_FILTER_NOTES,
    CapturePolicy,
    CaptureResult,
    RecordingFilterChange,
    RemoteError,
    RenderRequired,
    SectionMeasurement,
    Studio,
    Timebase,
    envelope,
    measure_wav,
    plan_capture,
)
from fruitylink import recording_filter_flags, recording_filter_names
from fruitylink.capture import (
    METHOD_LIVE,
    METHOD_RENDER,
    PLACEHOLDER_NAME,
    Audio,
    CaptureError,
    match_recordings,
    new_recordings,
    parse_state,
    resolve_inserts,
    snapshot_folder,
    write_placeholder,
)
from fruitylink.values import JsonValue

PPQ = 96
BPM = 120.0
BAR_SECONDS = 4 * 60 / BPM  # 2 s
RATE = 16000


def write_wav(path: Path, channels: list[list[float]], rate: int = RATE) -> Path:
    frames = len(channels[0])
    payload = b"".join(struct.pack("<" + "f" * len(channels), *(c[i] for c in channels)) for i in range(frames))
    fmt = struct.pack("<HHIIHH", 3, len(channels), rate, rate * 4 * len(channels), 4 * len(channels), 32)
    chunks = b"fmt " + struct.pack("<I", len(fmt)) + fmt + b"data" + struct.pack("<I", len(payload)) + payload
    path.write_bytes(b"RIFF" + struct.pack("<I", len(chunks) + 4) + b"WAVE" + chunks)
    return path


def tone(seconds: float, amplitude: float, hz: float = 220.0, rate: int = RATE) -> list[float]:
    return [amplitude * math.sin(2 * math.pi * hz * i / rate) for i in range(round(seconds * rate))]


# ---------------------------------------------------------------- planning and parsing


def test_plan_converts_bars_to_ticks_and_seconds() -> None:
    plan = plan_capture(Timebase(PPQ), BPM, [3, 0, 3], 5, 8, tail_beats=2)
    assert plan.inserts == (3, 0)
    assert (plan.start_tick, plan.end_tick, plan.tail_ticks) == (4 * 4 * PPQ, 8 * 4 * PPQ, 2 * PPQ)
    assert plan.record_end_tick == 8 * 4 * PPQ + 2 * PPQ
    assert plan.seconds == pytest.approx(4 * BAR_SECONDS)
    assert plan.tail_seconds == pytest.approx(1.0)
    assert plan.to_dict()["record_end_tick"] == plan.record_end_tick


@pytest.mark.parametrize("bad", [(0, 1), (3, 2), (1.5, 2), ("1", 2)])
def test_plan_rejects_bad_bars(bad: tuple[Any, Any]) -> None:
    with pytest.raises(ValueError):
        plan_capture(Timebase(PPQ), BPM, "master", *bad)


def test_resolve_inserts() -> None:
    assert resolve_inserts("master") == (0,)
    assert resolve_inserts(None) == (0,)
    assert resolve_inserts([2, 2, 5]) == (2, 5)
    for bad in ([], [-1], [True], "insert 3", 3):
        with pytest.raises(ValueError):
            resolve_inserts(bad)  # type: ignore[arg-type]


def test_parse_state_reads_play_flag_tick_and_range() -> None:
    text = "mode=song playing=yes pos=bar 9 beat 1 (tick 3086) ppq=96 songLength=108 bars playRange=[1536..3071] tempo=100"
    state = parse_state(text)
    assert (state.playing, state.tick, state.play_range) == (True, 3086, (1536, 3071))
    assert parse_state("mode=song playing=no pos=bar 1 beat 1 (tick 0) ppq=96 songLength=0 bars playRange=[-1..-1] tempo=120").play_range is None
    with pytest.raises(CaptureError):
        parse_state("err:not ready")


# ---------------------------------------------------------------- file discovery and matching


def test_new_recordings_detects_only_fresh_files(tmp_path: Path) -> None:
    old = write_wav(tmp_path / "Old_1_Master.wav", [tone(0.01, 0.1)])
    before = snapshot_folder(tmp_path)
    (tmp_path / "notes.txt").write_text("x")
    fresh = write_wav(tmp_path / "Song_2_Pad Serum.wav", [tone(0.01, 0.1)])
    assert new_recordings(tmp_path, before) == [fresh]
    assert old not in new_recordings(tmp_path, before)


def test_match_recordings_by_track_name_suffix(tmp_path: Path) -> None:
    files = [tmp_path / "Song_3_Master.wav", tmp_path / "Song_4_Pad Serum.wav", tmp_path / "Song_5_Bass.wav"]
    names = {0: "Master", 5: "Pad Serum", 7: "Bass"}
    assert match_recordings(files, names) == {0: files[0], 5: files[1], 7: files[2]}


def test_match_recordings_pairs_the_single_leftover_and_refuses_ambiguity(tmp_path: Path) -> None:
    files = [tmp_path / "Song_3_Master.wav", tmp_path / "Song_4_untitled.wav"]
    assert match_recordings(files, {0: "Master", 9: "Insert 9"}) == {0: files[0], 9: files[1]}
    with pytest.raises(CaptureError, match="unmatched"):
        match_recordings(files, {0: "Master", 9: "Insert 9", 10: "Insert 10"})
    with pytest.raises(CaptureError, match="unmatched"):
        match_recordings([tmp_path / "Song_1_Lead.wav", tmp_path / "Song_2_Lead.wav"], {1: "Lead", 2: "Lead"})


# ---------------------------------------------------------------- measurement and envelope


def test_measure_wav_reports_levels_bands_and_bars(tmp_path: Path) -> None:
    quiet, loud = tone(BAR_SECONDS, 0.1), tone(BAR_SECONDS, 0.5)
    left = quiet + loud + tone(0.5, 0.5)  # two full bars plus a tail
    path = write_wav(tmp_path / "Song_1_Master.wav", [left, list(left)])
    record: Any = measure_wav(path, bpm=BPM, start_bar=17, end_bar=18)
    assert record["start_bar"] == 17 and record["end_bar"] == 18
    assert record["measured_seconds"] == pytest.approx(2 * BAR_SECONDS, abs=1e-3)
    assert record["partial"] is False and record["covered_bars"] == 2
    assert record["peak_dbfs"] == pytest.approx(20 * math.log10(0.5), abs=0.05)
    expected_rms = 10 * math.log10((0.1 ** 2 / 2 + 0.5 ** 2 / 2) / 2)
    assert record["rms_dbfs"] == pytest.approx(expected_rms, abs=0.05)
    assert isinstance(record["integrated_lufs"], float)
    assert set(record["bands"]) == {"<90", "90-250", "250-2000", "2000-4000", ">4000"}
    bars = record["bars"]
    assert [bar["bar"] for bar in bars] == [17, 18]
    assert bars[1]["rms_db"] - bars[0]["rms_db"] == pytest.approx(20 * math.log10(5), abs=0.1)
    assert bars[0]["partial"] is False and bars[0]["lufs"] is not None
    assert record["short_term_lufs"] is not None


def test_measure_wav_marks_short_captures_partial(tmp_path: Path) -> None:
    path = write_wav(tmp_path / "short.wav", [tone(1.5 * BAR_SECONDS, 0.2)])
    record: Any = measure_wav(path, bpm=BPM, start_bar=1, end_bar=4)
    assert record["partial"] is True and record["covered_bars"] == 1
    assert [bar["bar"] for bar in record["bars"]] == [1]


def test_measure_wav_with_offset_reads_the_section_only(tmp_path: Path) -> None:
    samples = tone(BAR_SECONDS, 0.5) + tone(BAR_SECONDS, 0.05)
    path = write_wav(tmp_path / "render.wav", [samples])
    record: Any = measure_wav(path, bpm=BPM, start_bar=2, end_bar=2, offset_seconds=BAR_SECONDS)
    assert record["peak_dbfs"] == pytest.approx(20 * math.log10(0.05), abs=0.05)


def test_envelope_is_compact_and_tracks_level_steps(tmp_path: Path) -> None:
    path = write_wav(tmp_path / "env.wav", [tone(BAR_SECONDS, 0.1) + tone(BAR_SECONDS, 0.5)])
    readout: Any = envelope(path, bpm=BPM, start_bar=1, end_bar=2, slices_per_bar=4)
    assert readout["slices"] == 8 and len(readout["rms_db"]) == 8 and len(readout["peak_db"]) == 8
    assert readout["rms_db"][7] - readout["rms_db"][0] == pytest.approx(20 * math.log10(5), abs=0.2)
    assert [row["bar"] for row in readout["bands_per_bar"]] == [1, 2]
    assert set(readout["bands_per_bar"][0]) == {"bar", "<90", "90-250", "250-2000", ">2000"}


# ---------------------------------------------------------------- policy


def test_policy_prefers_live_for_short_sections_and_render_for_long_ones() -> None:
    policy = CapturePolicy()
    assert policy.choose(16.0).method == METHOD_LIVE  # 8 bars at 120 bpm
    assert policy.choose(259.2).method == METHOD_RENDER  # the 108-bar Parking Lot Moon full song
    boundary = (policy.render_overhead_seconds - policy.live_overhead_seconds) / (1 - 1 / policy.render_speed_factor)
    assert policy.choose(boundary - 0.1).method == METHOD_LIVE
    assert policy.choose(boundary + 0.1).method == METHOD_RENDER
    assert policy.choose(60.0, per_insert=True).method == METHOD_LIVE
    assert policy.choose(10.0, prefer="render").method == METHOD_RENDER
    assert CapturePolicy(max_live_seconds=10).choose(12.0, prefer="auto").method == METHOD_RENDER
    with pytest.raises(ValueError):
        policy.choose(10.0, prefer="render", per_insert=True)
    with pytest.raises(ValueError):
        policy.choose(10.0, prefer="fast")
    with pytest.raises(ValueError):
        CapturePolicy(render_speed_factor=0)


# ---------------------------------------------------------------- the driver against a fake FL


class FakeFl:
    """Enough of FL for one capture: mixer arm state, transport state, and a folder FL 'records' into.

    Models the live FL 26.1.3 findings: the recording set FL uses is the armed set at the last
    arm-state change *excluding the insert that just changed* (so a single arm records nothing until
    one more change follows), and every recording adds a sample channel plus a playlist clip.
    """

    def __init__(self, folder: Path, *, names: dict[int, str] | None = None, bpm: float = BPM,
                 stop_early_at: int | None = None, lagging_arm: bool = True,
                 recording_filter: int = RECORDING_FILTER_AUTOMATION | RECORDING_FILTER_NOTES,
                 refuse_filter: bool = False, record_pressed: bool = False,
                 refuse_record_state: bool = False, swallow_record_toggles: int = 0,
                 lying_record_byte: bool = False, refuse_recording_active: bool = False,
                 song_mode: bool = True, loop_at: int | None = None,
                 toggles: dict[str, bool] | None = None, refuse_toggles: bool = False,
                 freeze_tick_at: int | None = None) -> None:
        self.folder = folder
        # FL's global recording filter. The default is the value found on a stock install (3 = automation +
        # notes, Audio OFF), which is exactly the state in which FL records and writes nothing.
        self.recording_filter = recording_filter
        self.filter_writes: list[int] = []
        self.refuse_filter = refuse_filter
        # FL's record button is sticky: it stays engaged after a pass, so a blind toggle turns the next
        # pass OFF. Recording latches from the button when playback starts (live FL 26.1.3).
        self.record_pressed = record_pressed
        self.record_toggles = 0
        self.refuse_record_state = refuse_record_state
        # FL declining the click on its own record button, and a build whose pressed byte does not describe
        # the button that is actually lit (both seen live on 26.1.3.5570).
        self.swallow_record_toggles = swallow_record_toggles
        self.lying_record_byte = lying_record_byte
        self.refuse_recording_active = refuse_recording_active
        self.names = names or {0: "Master", 5: "Pad Serum", 7: "Bass"}
        self.bpm = bpm
        self.armed: dict[int, bool] = {}
        self.registered: set[int] = set()
        self.lagging_arm = lagging_arm
        self.playing = False
        self.recording = False
        self.song_mode = song_mode
        # FL wraps the playhead at the end of the pattern (pattern mode) or of the song/loop range, so the
        # requested end tick is never reached and the pass used to run to its deadline (live 26.1.3).
        self.loop_at = loop_at
        # Live 26.1.3: the position readback is FL's toolbar song-position slider, so once recording runs
        # past the old song end it stops moving while FL keeps recording.
        self.freeze_tick_at = freeze_tick_at
        # FL's global transport toggles, off on a stock install.
        self.toggles = {"metronome": False, "countdown": False, "wait_for_input": False,
                        "loop_record": False, "blend_recorded_notes": False}
        self.toggles.update(toggles or {})
        self.toggle_writes: list[tuple[str, bool]] = []
        self.refuse_toggles = refuse_toggles
        self.tick = 0
        self.loop: tuple[int, int] | None = None
        self.stop_early_at = stop_early_at
        self.log: list[str] = []
        self.plays = 0
        self.advance_ticks = 500
        self.channels: list[dict[str, Any]] = [{"name": "Kick", "muted": False, "mixerTrack": 1, "sample": None}]
        self.clips: list[dict[str, Any]] = [{"track": 1, "start": 0, "length": 384, "kind": "pattern", "source": 1}]
        # Every replace_channel_sample the pass makes: (channel, new path, did the old sample still exist?).
        # The third element is what proves the repointing happened BEFORE FL's originals were deleted.
        self.sample_writes: list[tuple[int, str, bool]] = []

    def _project(self, operation: str, args: Any) -> tuple[bool, JsonValue]:
        """Channel-rack and playlist operations (the litter FL leaves behind); (False, None) if not one."""
        if operation == "query_channels":
            return True, [{"index": i, "name": c["name"], "mixerTrack": c["mixerTrack"], "muted": c["muted"],
                           "volume": 10000, "pan": 6400} for i, c in enumerate(self.channels)]
        if operation == "query_clips":
            items: list[JsonValue] = [{"index": i, "track": c["track"], "startTick": c["start"], "lengthTick": c["length"],
                                       "sourceKind": c["kind"], "sourceIndex": c["source"], "muted": False}
                                      for i, c in enumerate(self.clips)]
            return True, {"items": items, "nextOffset": None, "total": len(items)}
        if operation == "delete_clips":
            for index in sorted(set(args["clipIndices"]), reverse=True):
                del self.clips[index]
            return True, None
        if operation == "get_channel_name":
            return True, str(self.channels[args["channel"]]["name"])
        if operation == "set_channel_name":
            self.channels[args["channel"]]["name"] = args["name"]
            return True, None
        if operation == "set_channel_muted":
            self.channels[args["channel"]]["muted"] = bool(args["muted"])
            return True, None
        if operation == "set_channel_fx_route":
            self.channels[args["channel"]]["mixerTrack"] = args["mixerTrack"]
            return True, None
        if operation == "replace_channel_sample":
            channel = self.channels[args["channel"]]
            previous = channel["sample"]
            self.sample_writes.append((args["channel"], str(args["samplePath"]),
                                       previous is not None and Path(previous).exists()))
            channel["sample"] = str(args["samplePath"])
            return True, None
        return False, None

    def __call__(self, method: str, params: dict[str, JsonValue]) -> JsonValue:
        operation = str(params.get("operation", method))
        args: Any = params.get("arguments", {})
        self.log.append(operation)
        handled, value = self._project(operation, args)
        if handled:
            return value
        simple: dict[str, JsonValue] = {
            "get_ppq": PPQ, "get_tempo": self.bpm, "get_song_mode": self.song_mode,
            "query_mixer_tracks": [{"index": i, "name": n, "kind": "master" if i == 0 else "insert"}
                                   for i, n in self.names.items()]}
        if operation in simple:
            return simple[operation]
        if operation == "get_song_state":
            self._advance()
            loop = self.loop or (-1, -1)
            return (f"mode={'song' if self.song_mode else 'pattern'} playing={'yes' if self.playing else 'no'} "
                    f"pos=bar 1 beat 1 (tick {self.tick}) ppq={PPQ} songLength=64 bars playRange=[{loop[0]}..{loop[1]}]"
                    f" tempo={self.bpm}")
        if operation == "set_song_mode":
            self.song_mode = bool(args["song"])
            return None
        if operation == "get_recording_filter":
            return self.recording_filter
        if operation == "set_recording_filter":
            if self.refuse_filter:
                raise RemoteError("native_unavailable", "The native recording-filter symbols are not resolved.")
            self.filter_writes.append(int(args["flags"]))
            self.recording_filter = int(args["flags"])
            return None
        if operation == "set_loop_region":
            self.loop = None if args["endTick"] <= args["startTick"] else (args["startTick"], args["endTick"] - 1)
            return None
        if operation == "get_mixer_track_armed":
            return self.armed.get(args["track"], False)
        if operation == "set_mixer_track_armed":
            self.armed[args["track"]] = bool(args["armed"])
            self.registered = {t for t, on in self.armed.items() if on and (not self.lagging_arm or t != args["track"])}
            return None
        if operation == "seek":
            self.tick = int(args["tick"])
            return None
        if operation.startswith(("get_", "set_")) and operation[4:] in self.toggles:
            name = operation[4:]
            if self.refuse_toggles:
                raise RemoteError("native_unavailable", f"The native symbols for FL's {name} toggle are not resolved.")
            if operation.startswith("get_"):
                return self.toggles[name]
            self.toggles[name] = bool(args["on"])
            self.toggle_writes.append((name, self.toggles[name]))
            return None
        if operation == "get_recording_active":
            if self.refuse_recording_active:
                raise RemoteError("native_unavailable", "The native recording-state symbol is not resolved.")
            return bool(self.playing and self.recording)
        if operation == "get_record_pressed":
            if self.refuse_record_state:
                raise RemoteError("native_unavailable", "The native record-button symbols are not resolved.")
            return not self.record_pressed if self.lying_record_byte else self.record_pressed
        if operation == "transport_toggle_record":
            self.record_toggles += 1
            if self.swallow_record_toggles > 0:
                self.swallow_record_toggles -= 1      # FL ignored the click on its own record button
                return None
            self.record_pressed = not self.record_pressed
            return None
        if operation == "transport_play":
            assert not self.playing
            self.recording = self.record_pressed
            self.playing = True
            self.plays += 1
            self.play_start = self.tick
            return None
        if operation == "transport_stop":
            if self.playing and self.recording:
                self._write_files()
            self.playing = False
            self.recording = False      # the button itself stays engaged, exactly as FL leaves it
            return None
        raise AssertionError(f"Unexpected operation {operation}")

    def _advance(self) -> None:
        if self.playing:
            self.tick += self.advance_ticks
            if self.freeze_tick_at is not None:
                self.tick = min(self.tick, self.freeze_tick_at)   # the slider sticks; recording continues
            if self.loop_at is not None and self.tick >= self.loop_at:
                self.tick %= self.loop_at
            if self.stop_early_at is not None and self.tick >= self.stop_early_at:
                self.tick = self.stop_early_at
                if self.recording:
                    self._write_files()
                self.playing = False
                self.recording = False

    def _write_files(self) -> None:
        # Live FL 26.1.3: with Audio off in the recording filter the pass runs and no WAV is ever written.
        if not self.recording_filter & RECORDING_FILTER_AUDIO:
            return
        seconds = (self.tick - self.play_start) / (self.bpm / 60 * PPQ)
        for n, track in enumerate(sorted(self.registered), start=1):
            amplitude = 0.5 if track == 0 else 0.1
            path = write_wav(self.folder / f"Song_{n}_{self.names[track]}.wav", [tone(seconds, amplitude)] * 2)
            # FL litter: one sample channel and one playlist clip per recording, even with auto-create off.
            # The channel references the recording it was created for, which is what the pass must repoint.
            self.channels.append({"name": f"lc-00{n}_2026-09-14 17-53-26_{self.names[track]}", "muted": False,
                                  "mixerTrack": track, "sample": str(path)})
            self.clips.append({"track": 2, "start": self.play_start, "length": self.tick - self.play_start,
                               "kind": "audio", "source": len(self.channels) - 1})


def make_studio(fake: FakeFl) -> tuple[Studio, RecordingTransport]:
    transport = RecordingTransport()
    transport.handler = fake
    studio = Studio(transport)
    studio.audio = Audio(studio, recorded_folder=fake.folder, sleep=lambda _: None, clock=_Clock())
    return studio, transport


class _Clock:
    def __init__(self) -> None:
        self.now = 0.0

    def __call__(self) -> float:
        self.now += 0.05
        return self.now


def test_capture_drives_arm_record_play_stop_and_measures(tmp_path: Path) -> None:
    fake = FakeFl(tmp_path)
    fl, _ = make_studio(fake)
    write_wav(tmp_path / "Song_0_Master.wav", [tone(0.01, 0.1)])  # an older recording must be ignored
    result = fl.audio.capture([5, 0], 3, 4, tail_beats=1, name="chorus-a")

    assert isinstance(result, CaptureResult)
    assert result.plan.inserts == (5, 0)
    assert set(result.paths) == {0, 5}
    assert result.path(0).name == "chorus-a-master.wav" and result.path(5).name == "chorus-a-5.wav"
    assert result.complete and result.captured_end_tick >= result.plan.end_tick
    assert result.stop_tick is not None and result.stop_tick >= result.plan.record_end_tick
    master: Any = result.measurements[0]
    assert master["peak_dbfs"] == pytest.approx(20 * math.log10(0.5), abs=0.05)
    assert result.measurements[5]["peak_dbfs"] == pytest.approx(20 * math.log10(0.1), abs=0.05)
    assert [bar["bar"] for bar in master["bars"]] == [3, 4]
    assert [w for w in result.warnings
            if not w.startswith(("Arm-refresh workaround applied", "FL added sample", "FL's recording filter",
                                 "Retired channel(s)"))] == []
    assert any("insert 7 (Bass)" in w for w in result.warnings)
    # The pass turned FL's Audio recording bit on for itself and handed the user's bitmask back afterwards.
    assert any("recording filter had Audio off (3" in w for w in result.warnings)
    assert result.recording_filter == RecordingFilterChange(3, 7, 3)
    assert fake.recording_filter == 3 and fake.filter_writes == [7, 3]
    assert fake.armed == {5: False, 0: False, 7: False} and not fake.playing and not fake.recording
    assert fake.plays == 1 and fake.play_start == 2 * 4 * PPQ
    order = [op for op in fake.log if op in ("set_mixer_track_armed", "seek", "transport_toggle_record",
                                             "transport_play", "transport_stop")]
    assert order == ["set_mixer_track_armed", "set_mixer_track_armed", "set_mixer_track_armed", "set_mixer_track_armed",
                     "seek", "transport_toggle_record", "transport_play", "transport_stop",
                     "transport_toggle_record",  # FL leaves the button engaged; the pass releases it again
                     "set_mixer_track_armed", "set_mixer_track_armed"]
    assert fake.record_pressed is False and fake.record_toggles == 2
    readout = result.envelope(5, slices_per_bar=2)
    assert readout["slices"] == 4
    record: Any = result.to_dict()
    assert record["method"] == METHOD_LIVE and record["measurements"]["5"]["start_bar"] == 3
    assert record["recording_filter"] == {"before": 3, "before_parts": ["automation", "notes"], "used": 7,
                                          "used_parts": ["automation", "notes", "audio"], "restored": 3,
                                          "restored_parts": ["automation", "notes"], "changed": True}
    # Litter cleanup: FL's two auto-created clips are deleted, its two channels retired, its originals removed.
    assert result.deleted_clips == 2 and len(fake.clips) == 1
    assert result.retired_channels == (1, 2) and record["retired_channels"] == [1, 2]
    assert all(c["name"].startswith("(unused) lc-00") and c["muted"] and c["mixerTrack"] == 0 for c in fake.channels[1:])
    assert sorted(result.removed_originals) == ["Song_1_Master.wav", "Song_2_Pad Serum.wav"]
    assert sorted(p.name for p in tmp_path.glob("*.wav")) == [
        "Song_0_Master.wav", "chorus-a-5.wav", "chorus-a-master.wav", PLACEHOLDER_NAME]
    # ... and both retired channels point at the placeholder instead of the deleted recordings.
    assert result.repointed_channels == (1, 2) and record["repointed_channels"] == [1, 2]
    assert result.placeholder == tmp_path / PLACEHOLDER_NAME and record["placeholder"] == str(result.placeholder)


def test_capture_keeps_fl_files_without_a_name_or_with_keep_originals(tmp_path: Path) -> None:
    fake = FakeFl(tmp_path)
    fl, _ = make_studio(fake)
    result = fl.audio.capture([5], 1, 1)
    assert result.removed_originals == () and result.path(5).name == "Song_1_Pad Serum.wav"
    result = fl.audio.capture([5], 1, 1, name="take2", keep_originals=True)
    assert result.removed_originals == () and result.path(5).name == "take2-5.wav"
    assert (tmp_path / "Song_1_Pad Serum.wav").exists() and (tmp_path / "take2-5.wav").exists()


def test_capture_without_cleanup_leaves_fl_litter_and_reports_nothing(tmp_path: Path) -> None:
    fake = FakeFl(tmp_path)
    fl, _ = make_studio(fake)
    result = fl.audio.capture([5], 1, 1, cleanup=False)
    assert result.deleted_clips == 0 and result.retired_channels == ()
    assert len(fake.clips) == 2 and len(fake.channels) == 2 and not fake.channels[1]["name"].startswith("(unused)")
    # Nothing is repointed and no placeholder is written: cleanup=False touches nothing at all.
    assert result.repointed_channels == () and result.placeholder is None
    assert fake.sample_writes == [] and not (tmp_path / PLACEHOLDER_NAME).exists()
    assert fake.channels[1]["sample"] == str(tmp_path / "Song_1_Pad Serum.wav")


# ---------------------------------------------------------------- the retired-channel placeholder


def test_write_placeholder_is_a_tiny_silent_wav_written_only_once(tmp_path: Path) -> None:
    from fruitylink.capture import _probe

    path = write_placeholder(tmp_path)
    assert path == tmp_path / PLACEHOLDER_NAME and path.stat().st_size < 1024
    assert _probe(path) == (48000, 2, 64)                       # a real, readable WAV FL can open
    stamp = path.stat().st_mtime_ns
    assert write_placeholder(tmp_path) == path and path.stat().st_mtime_ns == stamp


def test_capture_repoints_retired_channels_before_deleting_the_originals(tmp_path: Path) -> None:
    """Live 26.1.3.5570 (2026-09-17): a project whose retired channel points at a deleted recording hangs
    FL's command-line renderer (no output at 300 s or 600 s); repointed, the same snapshot rendered in 7.5 s."""
    fake = FakeFl(tmp_path)
    fl, _ = make_studio(fake)
    result = fl.audio.capture([5, 0], 1, 1, name="take")
    placeholder = tmp_path / PLACEHOLDER_NAME

    assert result.placeholder == placeholder and placeholder.exists()
    assert result.repointed_channels == result.retired_channels == (1, 2)
    # Each repoint names the placeholder and happened while FL's own recording was still on disk.
    assert [(channel, Path(path).name, existed) for channel, path, existed in fake.sample_writes] == [
        (1, PLACEHOLDER_NAME, True), (2, PLACEHOLDER_NAME, True)]
    assert sorted(result.removed_originals) == ["Song_1_Master.wav", "Song_2_Pad Serum.wav"]
    assert all(channel["sample"] == str(placeholder) for channel in fake.channels[1:])
    assert any("repointed at the silent placeholder" in w for w in result.warnings)


def test_capture_repoints_even_when_fl_keeps_its_originals(tmp_path: Path) -> None:
    """Without ``name`` the recordings stay, but the project must not depend on a file the user may delete."""
    fake = FakeFl(tmp_path)
    fl, _ = make_studio(fake)
    result = fl.audio.capture([5], 1, 1)
    assert result.removed_originals == () and result.repointed_channels == (1,)
    assert fake.channels[1]["sample"] == str(tmp_path / PLACEHOLDER_NAME)
    assert (tmp_path / "Song_1_Pad Serum.wav").exists()


def test_capture_creates_the_placeholder_once_and_reuses_it(tmp_path: Path) -> None:
    fake = FakeFl(tmp_path)
    fl, _ = make_studio(fake)
    first = fl.audio.capture([5], 1, 1, name="one")
    placeholder = tmp_path / PLACEHOLDER_NAME
    stamp = placeholder.stat().st_mtime_ns
    second = fl.audio.capture([5], 1, 1, name="two")

    assert first.placeholder == second.placeholder == placeholder
    assert placeholder.stat().st_mtime_ns == stamp                     # written once, reused after that
    assert (first.repointed_channels, second.repointed_channels) == ((1,), (2,))
    assert [channel for channel, _, _ in fake.sample_writes] == [1, 2]
    assert second.measurements[5]["peak_dbfs"] is not None              # the placeholder is not a recording


def test_capture_warns_when_a_retired_channel_cannot_be_repointed(tmp_path: Path) -> None:
    fake = FakeFl(tmp_path)
    original = fake.__call__

    def refuse(method: str, params: dict[str, JsonValue]) -> JsonValue:
        if params.get("operation") == "replace_channel_sample":
            raise RemoteError("native_unavailable", "FL refused the sample replacement.")
        return original(method, params)

    fl, transport = make_studio(fake)
    transport.handler = refuse
    result = fl.audio.capture([5], 1, 1, name="take")
    assert set(result.paths) == {5} and result.repointed_channels == ()
    assert result.retired_channels == (1,) and result.placeholder == tmp_path / PLACEHOLDER_NAME
    assert any("could not be repointed" in w and "renderer hangs" in w for w in result.warnings)


def test_single_insert_needs_the_arm_refresh_and_the_error_names_fl_settings(tmp_path: Path) -> None:
    fake = FakeFl(tmp_path)
    fl, _ = make_studio(fake)
    with pytest.raises(CaptureError) as info:
        fl.audio.capture([5], 1, 1, file_timeout=1.0, arm_refresh=False)
    message = str(info.value)
    assert "wrote no WAV" in message and "The SDK set every FL setting it can reach" in message
    # Everything the SDK can set is reported as already settled; only the unsettable parts are advice.
    assert "the recording filter was set to 7 (automation, notes, audio), which includes Audio" in message
    assert "arm_refresh=False was passed" in message and "Options > Audio settings" in message
    assert "Auto-create audio clip" not in message and "RecordingFilter2" not in message
    assert fake.armed == {5: False} and fake.recording_filter == 3  # restored even though the pass failed
    result = fl.audio.capture([5], 1, 1)  # the default refresh (Master, since it is not requested) makes FL record
    assert set(result.paths) == {5} and fake.armed == {5: False, 0: False}
    assert any("insert 0 (Master)" in w for w in result.warnings)


def test_arm_refresh_is_skipped_with_a_warning_when_no_other_insert_exists(tmp_path: Path) -> None:
    fake = FakeFl(tmp_path, names={0: "Master", 5: "Pad Serum"}, lagging_arm=False)
    fl, _ = make_studio(fake)
    result = fl.audio.capture([5, 0], 1, 1)
    assert set(result.paths) == {0, 5}
    assert any(w.startswith("Arm-refresh workaround skipped") for w in result.warnings)


# ---------------------------------------------------------------- the recording filter


def test_recording_filter_ops_read_set_and_combine_parts(tmp_path: Path) -> None:
    fake = FakeFl(tmp_path)
    fl, _ = make_studio(fake)
    assert fl.transport.recording_filter == 3 and fl.transport.recording_filter_parts == ("automation", "notes")
    fl.transport.recording_filter = RECORDING_FILTER_AUDIO
    assert fake.recording_filter == 4 and fake.filter_writes == [4]
    # ensure_* only touches the parts it is given and reports the bitmask it found.
    assert fl.transport.ensure_recording_filter(audio=True, notes=True) == 4
    assert fake.recording_filter == 6 and fl.transport.recording_filter_parts == ("notes", "audio")
    assert fl.transport.ensure_recording_filter(audio=True) == 6 and fake.filter_writes == [4, 6]  # no redundant write
    assert fl.transport.ensure_recording_filter(audio=False) == 6 and fake.recording_filter == 2
    for bad in (-1, 16, True, 1.0):
        with pytest.raises(ValueError):
            fl.transport.recording_filter = bad  # type: ignore[assignment]


def test_recording_filter_helpers_are_pure() -> None:
    assert recording_filter_flags(3, audio=True) == 7
    assert recording_filter_flags(7, audio=False) == 3
    assert recording_filter_flags(3, notes=None) == 3
    assert recording_filter_names(7) == ("automation", "notes", "audio")
    assert recording_filter_names(0) == ()
    with pytest.raises(ValueError):
        recording_filter_flags(3, midi=True)
    with pytest.raises(ValueError):
        recording_filter_flags(3, audio="yes")  # type: ignore[arg-type]


def test_capture_turns_audio_on_by_itself_and_restores_the_users_bitmask(tmp_path: Path) -> None:
    """The whole point: a stock FL (filter 3, Audio off) records nothing; the pass fixes it and puts it back."""
    fake = FakeFl(tmp_path, recording_filter=RECORDING_FILTER_AUTOMATION | RECORDING_FILTER_NOTES)
    fl, _ = make_studio(fake)
    result = fl.audio.capture([5], 1, 1)
    assert set(result.paths) == {5}
    assert result.recording_filter == RecordingFilterChange(3, 7, 3)
    assert fake.filter_writes == [7, 3] and fake.recording_filter == 3


def test_capture_leaves_a_filter_that_already_had_audio_alone(tmp_path: Path) -> None:
    fake = FakeFl(tmp_path, recording_filter=RECORDING_FILTER_AUDIO | RECORDING_FILTER_NOTES)
    fl, _ = make_studio(fake)
    result = fl.audio.capture([5], 1, 1)
    assert result.recording_filter == RecordingFilterChange(6, 6, 6) and not result.recording_filter.changed
    assert fake.filter_writes == [] and not any("recording filter" in w for w in result.warnings)


def test_capture_can_keep_the_filter_it_set_and_can_be_told_not_to_touch_it(tmp_path: Path) -> None:
    fake = FakeFl(tmp_path)
    fl, _ = make_studio(fake)
    result = fl.audio.capture([5], 1, 1, restore_recording_filter=False)
    assert result.recording_filter == RecordingFilterChange(3, 7, 7) and fake.recording_filter == 7
    fake.recording_filter = 3
    with pytest.raises(CaptureError) as info:  # opting out reproduces the original failure
        fl.audio.capture([5], 1, 1, file_timeout=1.0, ensure_recording_filter=False)
    assert "the recording filter was NOT touched" in str(info.value)
    assert fake.filter_writes == [7] and fake.recording_filter == 3


def test_capture_restores_the_filter_when_the_pass_fails(tmp_path: Path) -> None:
    fake = FakeFl(tmp_path)
    original = fake.__call__

    def failing(method: str, params: dict[str, JsonValue]) -> JsonValue:
        if params.get("operation") == "transport_play":
            raise RuntimeError("bridge fault")
        return original(method, params)

    fl, transport = make_studio(fake)
    transport.handler = failing
    with pytest.raises(RuntimeError):
        fl.audio.capture([5], 1, 1)
    assert fake.recording_filter == 3 and fake.filter_writes == [7, 3]


def test_capture_reports_a_refused_filter_restore_instead_of_losing_the_capture(tmp_path: Path) -> None:
    fake = FakeFl(tmp_path)
    fl, transport = make_studio(fake)
    original = fake.__call__

    def refuse_second_write(method: str, params: dict[str, JsonValue]) -> JsonValue:
        if params.get("operation") == "set_recording_filter" and fake.filter_writes:
            raise RemoteError("native_unavailable", "FL refused the write.")
        return original(method, params)

    transport.handler = refuse_second_write
    result = fl.audio.capture([5], 1, 1)
    assert set(result.paths) == {5} and result.recording_filter == RecordingFilterChange(3, 7, 7)
    assert any("recording filter was left at 7" in w for w in result.warnings)


def test_capture_surfaces_a_build_without_the_recording_filter_symbols(tmp_path: Path) -> None:
    fake = FakeFl(tmp_path, refuse_filter=True)
    fl, _ = make_studio(fake)
    with pytest.raises(RemoteError):
        fl.audio.capture([5], 1, 1)


# ---------------------------------------------------------------- the transport record button


def test_back_to_back_captures_both_record(tmp_path: Path) -> None:
    """The blind-toggle bug: FL leaves the record button engaged, so pass two used to switch it OFF."""
    fake = FakeFl(tmp_path)
    fl, _ = make_studio(fake)
    first = fl.audio.capture([5], 1, 1, name="one")
    assert fake.record_pressed is False
    second = fl.audio.capture([5], 1, 1, name="two")
    assert set(first.paths) == {5} and set(second.paths) == {5}
    assert fake.record_pressed is False and fake.record_toggles == 4


def test_capture_leaves_an_already_engaged_record_button_engaged(tmp_path: Path) -> None:
    fake = FakeFl(tmp_path, record_pressed=True)
    fl, _ = make_studio(fake)
    result = fl.audio.capture([5], 1, 1)
    assert set(result.paths) == {5}
    assert fake.record_pressed is True and fake.record_toggles == 0
    assert any("record button read as already engaged" in w for w in result.warnings)


def test_capture_falls_back_to_a_blind_toggle_without_the_record_state_symbols(tmp_path: Path) -> None:
    fake = FakeFl(tmp_path, refuse_record_state=True)
    fl, _ = make_studio(fake)
    result = fl.audio.capture([5], 1, 1)
    assert set(result.paths) == {5} and fake.record_toggles == 1
    assert any("record-button state could not be read" in w for w in result.warnings)


def test_recording_active_reports_the_engine_not_the_button(tmp_path: Path) -> None:
    fake = FakeFl(tmp_path)
    fl, _ = make_studio(fake)
    assert fl.transport.recording_active is False
    fl.transport.toggle_record()                     # armed, but the engine is not recording yet
    assert fl.transport.record_pressed is True and fl.transport.recording_active is False
    fl.transport.play()
    assert fl.transport.recording_active is True
    fl.transport.stop()
    assert fl.transport.recording_active is False and fl.transport.record_pressed is True


def test_capture_retoggles_when_fl_swallows_the_first_record_click(tmp_path: Path) -> None:
    """Live 26.1.3: FL can ignore the click on its own record button, so play() runs without recording."""
    fake = FakeFl(tmp_path, swallow_record_toggles=1)
    fl, _ = make_studio(fake)
    result = fl.audio.capture([5], 1, 1)
    assert set(result.paths) == {5}
    assert any("playing but not recording" in w for w in result.warnings)
    assert fake.record_toggles == 3  # swallowed, retry, release


def test_capture_recovers_when_the_pressed_byte_describes_the_wrong_object(tmp_path: Path) -> None:
    """Live 26.1.3: get_record_pressed read the opposite of the lit button. The byte is only a hint now --
    the engine counter settles it after play, so the pass still records instead of skipping the toggle."""
    fake = FakeFl(tmp_path, lying_record_byte=True)
    fl, _ = make_studio(fake)
    result = fl.audio.capture([5], 1, 1)
    assert set(result.paths) == {5}
    assert any("playing but not recording" in w for w in result.warnings)   # recovered, not failed
    assert fake.recording is False and fake.record_pressed is False


def test_capture_gives_up_after_a_second_toggle_that_still_does_not_record(tmp_path: Path) -> None:
    fake = FakeFl(tmp_path, swallow_record_toggles=5)
    fl, _ = make_studio(fake)
    with pytest.raises(CaptureError) as info:
        fl.audio.capture([5], 1, 1, file_timeout=1.0)
    assert "even after a second record toggle" in str(info.value)
    assert fake.recording_filter == 3  # the filter is still handed back


def test_capture_says_so_when_the_build_cannot_verify_recording(tmp_path: Path) -> None:
    fake = FakeFl(tmp_path, refuse_recording_active=True)
    fl, _ = make_studio(fake)
    result = fl.audio.capture([5], 1, 1)
    assert set(result.paths) == {5}
    assert any("cannot report the engine recording state" in w for w in result.warnings)


def test_record_pressed_op_and_ensure_round_trip(tmp_path: Path) -> None:
    fake = FakeFl(tmp_path)
    fl, _ = make_studio(fake)
    assert fl.transport.record_pressed is False
    assert fl.transport.ensure_record_pressed() is False and fake.record_pressed is True
    assert fl.transport.ensure_record_pressed(True) is True and fake.record_toggles == 1
    assert fl.transport.ensure_record_pressed(False) is True and fake.record_pressed is False


# ---------------------------------------------------------------- pattern mode and a looping playhead


def test_capture_selects_song_mode_for_the_pass_and_says_so(tmp_path: Path) -> None:
    """Pattern mode loops the current pattern, so the span's end tick is never reached."""
    fake = FakeFl(tmp_path, song_mode=False)
    fl, _ = make_studio(fake)
    result = fl.audio.capture([5], 1, 1)
    assert set(result.paths) == {5}
    assert any("FL was in pattern mode" in w for w in result.warnings)
    assert fake.song_mode is False      # restored


def test_capture_stops_at_a_playhead_wrap_instead_of_waiting_out_the_deadline(tmp_path: Path) -> None:
    fake = FakeFl(tmp_path, loop_at=PPQ * 8)     # the song loops after 2 bars of the requested 8
    fl, _ = make_studio(fake)
    result = fl.audio.capture([5], 1, 8)
    assert set(result.paths) == {5}
    wrap = [w for w in result.warnings if "playhead wrapped" in w]
    assert len(wrap) == 1 and "stopped at the wrap" in wrap[0]
    assert not any("did not pass tick" in w for w in result.warnings)
    assert result.stop_tick is not None and result.stop_tick < PPQ * 8


# ---------------------------------------------------------------- transport toggles


def test_transport_toggle_properties_read_write_and_validate(tmp_path: Path) -> None:
    fake = FakeFl(tmp_path)
    fl, _ = make_studio(fake)
    assert fl.transport.metronome is False
    fl.transport.metronome = True
    assert fake.toggles["metronome"] is True and fake.toggle_writes == [("metronome", True)]
    for name in ("countdown", "wait_for_input", "loop_record", "blend_recorded_notes"):
        setattr(fl.transport, name, True)
        assert getattr(fl.transport, name) is True
    with pytest.raises(ValueError):
        fl.transport.metronome = 1          # type: ignore[assignment]


def test_transport_settings_and_ensure_round_trip(tmp_path: Path) -> None:
    fake = FakeFl(tmp_path, toggles={"metronome": True, "countdown": True})
    fl, _ = make_studio(fake)
    assert fl.transport.settings() == {"song_mode": True, "metronome": True, "countdown": True,
                                       "wait_for_input": False, "loop_record": False,
                                       "blend_recorded_notes": False}
    previous = fl.transport.ensure(metronome=False, countdown=False, loop_record=False)
    assert previous == {"metronome": True, "countdown": True, "loop_record": False}
    assert fake.toggle_writes == [("metronome", False), ("countdown", False)]   # loop_record already off
    assert fl.transport.ensure(**previous) == {"metronome": False, "countdown": False, "loop_record": False}
    assert fake.toggles["metronome"] is True and fake.toggles["countdown"] is True
    with pytest.raises(ValueError):
        fl.transport.ensure(swing=True)


def test_transport_settings_omits_what_the_build_cannot_report(tmp_path: Path) -> None:
    fake = FakeFl(tmp_path, refuse_toggles=True)
    fl, _ = make_studio(fake)
    assert fl.transport.settings() == {"song_mode": True}       # nothing guessed


def test_capture_switches_off_the_toggles_that_would_spoil_the_pass(tmp_path: Path) -> None:
    fake = FakeFl(tmp_path, toggles={"countdown": True, "wait_for_input": True, "metronome": True})
    fl, _ = make_studio(fake)
    result = fl.audio.capture([5], 1, 1)
    assert set(result.paths) == {5}
    assert result.toggles == {"countdown": True, "wait_for_input": True, "loop_record": False, "metronome": True}
    assert any("would have spoilt the pass" in w for w in result.warnings)
    # every one handed back, and blend_recorded_notes was never touched
    assert fake.toggles == {"metronome": True, "countdown": True, "wait_for_input": True,
                            "loop_record": False, "blend_recorded_notes": False}
    assert result.to_dict()["toggles"]["metronome"] is True


def test_capture_leaves_already_correct_toggles_alone(tmp_path: Path) -> None:
    fake = FakeFl(tmp_path)
    fl, _ = make_studio(fake)
    result = fl.audio.capture([5], 1, 1)
    assert fake.toggle_writes == [] and not any("spoilt the pass" in w for w in result.warnings)
    assert result.toggles == {"countdown": False, "wait_for_input": False, "loop_record": False, "metronome": False}


def test_capture_warns_and_proceeds_when_toggles_are_unavailable(tmp_path: Path) -> None:
    fake = FakeFl(tmp_path, refuse_toggles=True)
    fl, _ = make_studio(fake)
    result = fl.audio.capture([5], 1, 1)
    assert set(result.paths) == {5} and result.toggles == {}
    assert any("transport toggles could not be set" in w for w in result.warnings)


def test_capture_can_be_told_not_to_touch_the_toggles(tmp_path: Path) -> None:
    fake = FakeFl(tmp_path, toggles={"metronome": True})
    fl, _ = make_studio(fake)
    result = fl.audio.capture([5], 1, 1, ensure_toggles=False)
    assert result.toggles == {} and fake.toggle_writes == [] and fake.toggles["metronome"] is True


def test_capture_stops_on_the_wall_clock_when_the_position_readback_freezes(tmp_path: Path) -> None:
    """Live 26.1.3: audio recording extends the song, the toolbar slider stops moving at the old song end,
    and neither the end tick nor a wrap can fire. The pass must end on real time instead of its deadline."""
    fake = FakeFl(tmp_path, freeze_tick_at=PPQ * 2)          # stuck half-way through the requested 4 bars
    fl, _ = make_studio(fake)
    result = fl.audio.capture([5], 1, 4)
    assert set(result.paths) == {5}
    assert fake.recording is False and not fake.playing      # the pass did stop FL
    clock = [w for w in result.warnings if "stopped on" in w and "the clock" in w]
    assert len(clock) == 1 and f"reached tick {PPQ * 2}" in clock[0]
    assert not any("did not pass tick" in w for w in result.warnings)   # no deadline warning any more
    assert result.stop_tick == PPQ * 2


def test_capture_prefers_the_tick_readback_when_it_tracks(tmp_path: Path) -> None:
    fake = FakeFl(tmp_path)
    fl, _ = make_studio(fake)
    result = fl.audio.capture([5], 1, 1)
    assert set(result.paths) == {5}
    assert not any("stopped on" in w and "the clock" in w for w in result.warnings)
    assert result.stop_tick is not None and result.stop_tick >= result.plan.record_end_tick


def test_measurements_are_always_dicts(tmp_path: Path, monkeypatch: pytest.MonkeyPatch) -> None:
    import fruitylink.capture as module

    fake = FakeFl(tmp_path)
    fl, _ = make_studio(fake)

    def broken(path: Path, **kwargs: Any) -> dict[str, object]:
        raise ValueError("synthetic analysis failure")

    monkeypatch.setattr(module, "measure_wav", broken)
    result = fl.audio.capture([5, 0], 1, 1)
    for track, record in result.measurements.items():
        assert isinstance(record, dict) and str(record["error"]).startswith("ValueError: synthetic")
        assert record["source_kind"] == f"fl_insert_{track}"
    coerced = CaptureResult(result.plan, result.files, result.captured_end_tick, None,
                            {0: "not a record"}, tmp_path)  # type: ignore[dict-item]
    assert coerced.measurements[0] == {"error": "measurement for insert 0 is not a record: 'not a record'"}
    serialised: Any = coerced.to_dict()
    assert isinstance(serialised["measurements"]["0"], dict)


def test_capture_restores_loop_and_pattern_mode_and_keeps_prearmed_inserts(tmp_path: Path) -> None:
    fake = FakeFl(tmp_path, names={0: "Master", 5: "Pad Serum", 6: "Bass"})
    fake.loop = (100, 199)
    fake.song_mode = False
    fake.armed[6] = True  # armed by the user beforehand: records too, stays armed, its file is ignored
    fl, _ = make_studio(fake)
    result = fl.audio.capture("master", 1, 1)
    assert set(result.paths) == {0}
    assert fake.loop == (100, 199) and fake.song_mode is False
    assert fake.armed == {6: True, 0: False, 5: False}  # 5 was the arm-refresh insert (Master was requested)
    assert "set_loop_region" in fake.log


def test_capture_refuses_while_playing_and_unknown_inserts(tmp_path: Path) -> None:
    fake = FakeFl(tmp_path)
    fl, _ = make_studio(fake)
    with pytest.raises(CaptureError, match="not addressable"):
        fl.audio.capture([42], 1, 2)
    fake.playing = True
    with pytest.raises(CaptureError, match="playing"):
        fl.audio.capture("master", 1, 2)
    assert fake.armed == {}


def test_capture_reports_fl_stopping_early_as_partial(tmp_path: Path) -> None:
    fake = FakeFl(tmp_path, stop_early_at=3 * 4 * PPQ)  # FL reaches its song end after bar 3
    fl, _ = make_studio(fake)
    result = fl.audio.capture("master", 1, 4)
    assert not result.complete
    assert result.captured_end_tick == pytest.approx(3 * 4 * PPQ, abs=2)
    assert result.measurements[0]["partial"] is True and result.measurements[0]["covered_bars"] == 3
    assert any("stopped by itself" in warning for warning in result.warnings)
    assert fake.armed == {0: False, 5: False}


def test_capture_disarms_when_the_transport_fails(tmp_path: Path) -> None:
    fake = FakeFl(tmp_path)
    original = fake.__call__

    def failing(method: str, params: dict[str, JsonValue]) -> JsonValue:
        if params.get("operation") == "transport_play":
            raise RuntimeError("bridge fault")
        return original(method, params)

    fl, transport = make_studio(fake)
    transport.handler = failing
    with pytest.raises(RuntimeError):
        fl.audio.capture([5], 1, 2)
    assert fake.armed == {5: False, 0: False}


def test_capture_times_out_when_no_file_appears(tmp_path: Path) -> None:
    fake = FakeFl(tmp_path)
    fake._write_files = lambda: None  # type: ignore[method-assign]
    fl, _ = make_studio(fake)
    with pytest.raises(CaptureError, match="wrote no WAV.*workaround was applied"):
        fl.audio.capture("master", 1, 1, file_timeout=1.0)


def test_capture_reports_a_partial_file_set_with_the_count(tmp_path: Path) -> None:
    fake = FakeFl(tmp_path)
    original = fake._write_files

    def only_master() -> None:
        fake.registered = {0}
        original()

    fake._write_files = only_master  # type: ignore[method-assign]
    fl, _ = make_studio(fake)
    with pytest.raises(CaptureError, match="wrote 1 new WAV.*expected 2"):
        fl.audio.capture([5, 0], 1, 1, file_timeout=1.0)


def test_measure_section_uses_live_capture_for_short_sections(tmp_path: Path) -> None:
    fake = FakeFl(tmp_path)
    fl, _ = make_studio(fake)
    result = fl.audio.measure_section(1, 4)
    assert isinstance(result, SectionMeasurement)
    assert result.method == METHOD_LIVE and result.capture is not None
    assert result.measurements[0]["start_bar"] == 1 and result.files[0].exists()
    record: Any = result.to_dict()
    assert record["decision"]["method"] == METHOD_LIVE


def test_measure_section_hands_long_sections_to_the_render_owner(tmp_path: Path) -> None:
    fake = FakeFl(tmp_path)
    fl, _ = make_studio(fake)
    with pytest.raises(RenderRequired) as info:
        fl.audio.measure_section(1, 108)
    assert info.value.suggested_tool_call == {"tool": "fl_project_render", "startBar": 1, "endBar": 108, "tailBeats": 0.0}
    assert info.value.decision.method == METHOD_RENDER
    assert fake.plays == 0 and fake.armed == {}

    calls: list[tuple[int, int, float]] = []

    def render(start_bar: int, end_bar: int, tail_beats: float) -> Path:
        calls.append((start_bar, end_bar, tail_beats))
        return write_wav(tmp_path / "section.wav", [tone(4 * BAR_SECONDS, 0.3)] * 2)

    result = fl.audio.measure_section(1, 4, prefer="render", tail_beats=2, render=render)
    assert result.method == METHOD_RENDER and calls == [(1, 4, 2.0)]
    assert result.measurements[0]["source_kind"] == "master_render"
    assert result.measurements[0]["peak_dbfs"] == pytest.approx(20 * math.log10(0.3), abs=0.05)
    assert fake.plays == 0

    with pytest.raises(ValueError, match="live capture"):
        fl.audio.measure_section(1, 4, [5], prefer="render", render=render)


def test_mixer_track_armed_property_uses_the_new_operations(fl: Studio, transport: RecordingTransport) -> None:
    transport.responses["get_mixer_track_armed"] = True
    assert fl.mixer[3].armed is True
    fl.mixer[3].armed = False
    assert transport.calls == [
        ("invoke", {"operation": "get_mixer_track_armed", "arguments": {"track": 3}}),
        ("invoke", {"operation": "set_mixer_track_armed", "arguments": {"track": 3, "armed": False}})]
