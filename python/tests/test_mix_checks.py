"""Synthetic-signal checks for the bar scan, band, section, transition, pitch and masking helpers."""

import json
import math
import random
import struct
from pathlib import Path
from typing import Any, cast

import pytest

from fruitylink.analysis import Analysis, _kernels, from_pcm
from fruitylink.analysis.bands import (
    Biquad,
    band_energy,
    band_label,
    band_specs,
    compare_bands,
    masking_report,
)
from fruitylink.analysis.bars import describe_sections, scan_bars, transition
from fruitylink.analysis.pitch import pitch_track
from fruitylink.values import to_json

RATE = 16000
BPM = 120  # one bar = 2 s at 4/4


def sine(frequency: float, seconds: float, amplitude: float, rate: int = RATE, phase: float = 0.0) -> list[float]:
    return [amplitude * math.sin(2 * math.pi * frequency * index / rate + phase)
            for index in range(round(seconds * rate))]


def add(*signals: list[float]) -> list[float]:
    return [sum(values) for values in zip(*signals)]


def gain(signal: list[float], factor: float, start: int = 0) -> list[float]:
    return [value * (factor if index >= start else 1) for index, value in enumerate(signal)]


def dbfs(amplitude: float) -> float:
    return 20 * math.log10(amplitude / math.sqrt(2))


def as_dict(value: object) -> dict[str, Any]:
    assert isinstance(value, dict)
    return cast(dict[str, Any], value)


def rows(value: object) -> list[dict[str, Any]]:
    assert isinstance(value, list)
    return [as_dict(item) for item in value]


@pytest.fixture(scope="module")
def stepped() -> Any:
    """Eight bars of 50 Hz + 3 kHz; from bar 5 the sub drops 12 dB and the top rises 8 dB."""
    half = 4 * 2 * RATE
    sub = gain(sine(50, 16, 0.5), 10 ** (-12 / 20), half)
    top = gain(sine(3000, 16, 0.1), 10 ** (8 / 20), half)
    left = add(sub, top)
    return Analysis.pcm([left, [0.5 * value for value in left]], RATE, source="stepped")


def test_scan_bars_levels_match_theory_and_flag_only_the_step(stepped: Any) -> None:
    scan = stepped.scan_bars(BPM, bands=((None, 90), (2000, None)))
    bars = rows(scan["bars"])
    assert [row["bar"] for row in bars] == list(range(1, 9))
    assert bars[1]["start_seconds"] == 2.0 and bars[1]["end_seconds"] == 4.0
    # full_db averages channel powers: mean of 1 and 0.25 times the mono power.
    mono_sub = 0.5 * 0.75  # mono mix of left and 0.5*left
    assert bars[1]["full_db"] == pytest.approx(10 * math.log10((0.5**2 + 0.1**2) / 2 * (1 + 0.25) / 2), abs=0.05)
    assert bars[1]["bands"]["<90"] == pytest.approx(dbfs(mono_sub), abs=0.3)
    assert bars[1]["bands"][">2000"] == pytest.approx(dbfs(0.1 * 0.75), abs=0.3)
    assert bars[5]["bands"]["<90"] == pytest.approx(dbfs(mono_sub) - 12, abs=0.3)
    assert bars[5]["bands"][">2000"] == pytest.approx(dbfs(0.1 * 0.75) + 8, abs=0.3)
    assert [row["flags"] for row in bars[:4]] == [[], [], [], []]
    assert bars[4]["flags"] == ["<90 -12.00 dB", ">2000 +8.00 dB"] or all(
        flag.startswith(("<90 -1", ">2000 +")) for flag in bars[4]["flags"])
    anomalies = rows(scan["anomalies"])
    assert {item["measure"] for item in anomalies} == {"<90", ">2000"}
    assert all(item["bar"] == 5 and item["start_seconds"] == 8.0 for item in anomalies)
    assert as_dict(scan["summary"])["flagged_bars"] == [5]
    assert as_dict(scan["summary"])["loudest_bar"] == 1 and as_dict(scan["summary"])["quietest_bar"] == 5
    assert scan["total"] == 8 and scan["next_offset"] is None
    json.dumps(to_json(scan), allow_nan=False)


def test_scan_bars_pages_are_cached_and_partial_bars_are_marked(stepped: Any) -> None:
    page = stepped.scan_bars(BPM, offset=6, limit=1)
    assert [row["bar"] for row in rows(page["bars"])] == [7] and page["next_offset"] == 7
    cached = len(stepped._scans)
    assert stepped.scan_bars(BPM, offset=100)["bars"] == []
    assert len(stepped._scans) == cached
    trimmed = Analysis.pcm([[0.1] * (5 * RATE)], RATE, start_seconds=1.5)
    scan = trimmed.scan_bars(BPM)
    bars = rows(scan["bars"])
    assert [(row["bar"], row["partial"]) for row in bars] == [(1, True), (2, False), (3, True)]
    assert bars[0]["start_seconds"] == 1.5 and bars[2]["end_seconds"] == 5.0


def test_scan_bars_grid_offset_start_bar_and_silence_flags() -> None:
    audio = Analysis.pcm([[0.0] * (2 * RATE) + sine(100, 4, 0.5, RATE) + [0.0] * (2 * RATE)], RATE)
    scan = audio.scan_bars(BPM, start_bar=9, grid_offset_seconds=2.0, bands=((None, 200),))
    bars = rows(scan["bars"])
    assert [row["bar"] for row in bars] == [9, 10, 11]
    assert bars[0]["full_db"] == pytest.approx(dbfs(0.5), abs=0.01)
    assert bars[2]["full_db"] is None and bars[2]["flags"][0] == "full became silent"
    # The band keeps a few milliseconds of filter decay from the tone that stopped at the bar line.
    assert bars[2]["bands"]["<200"] < -25 and bars[2]["flags"][1].startswith("<200 -")
    assert as_dict(scan["summary"])["silent_bars"] == 1


@pytest.mark.parametrize("kwargs", [{"bpm": 0}, {"bpm": math.nan}, {"beats_per_bar": 0}, {"step_db": -1},
                                    {"bands": ()}, {"bands": ((9000, None),)}, {"bands": ((200, 100),)},
                                    {"bands": ((None, 90),) * 2}, {"limit": 0}, {"offset": -1},
                                    {"grid_offset_seconds": -1}, {"start_bar": 1.5}])
def test_scan_bars_rejects_invalid_settings(kwargs: dict[str, Any]) -> None:
    source = from_pcm([[0.0] * RATE], RATE)
    with pytest.raises(ValueError):
        scan_bars(source, **{"bpm": BPM, **kwargs})


def test_band_energy_isolates_tones_and_reports_peak() -> None:
    audio = Analysis.pcm([add(sine(50, 3, 0.5), sine(3000, 3, 0.1))], RATE)
    result = audio.band_energy(((None, 90), (300, 1000), (2000, None)), start_seconds=1, end_seconds=3)
    assert result["start_seconds"] == 1 and result["end_seconds"] == 3
    levels = as_dict(result["bands"])
    assert levels["<90"] == pytest.approx(dbfs(0.5), abs=0.2)
    assert levels[">2000"] == pytest.approx(dbfs(0.1), abs=0.2)
    assert levels["300-1000"] < -60
    assert result["full_db"] == pytest.approx(10 * math.log10((0.25 + 0.01) / 2), abs=0.01)
    assert result["peak_db"] == pytest.approx(20 * math.log10(0.6), abs=0.1)
    assert [item["label"] for item in rows(result["band_definitions"])] == ["<90", "300-1000", ">2000"]
    assert band_label(None, None) == "full" and band_label(2000.0, 4000.0) == "2000-4000"


def test_band_energy_of_silence_is_null_and_window_must_fit() -> None:
    audio = Analysis.pcm([[0.0] * RATE], RATE)
    result = audio.band_energy(((None, 90),))
    assert result["full_db"] is None and as_dict(result["bands"])["<90"] is None
    with pytest.raises(ValueError, match="inside the selected audio"):
        audio.band_energy(((None, 90),), start_seconds=0.5, end_seconds=2)


def test_compare_bands_reports_signed_deltas_and_extremes(tmp_path: Path) -> None:
    before = Analysis.pcm([add(sine(100, 2, 0.3), sine(3000, 2, 0.05))], RATE)
    after = [value for value in add(sine(100, 2, 0.3), sine(3000, 2, 0.2))]
    payload = struct.pack("<%df" % len(after), *after)
    fmt = struct.pack("<HHIIHH", 3, 1, RATE, RATE * 4, 4, 32)
    body = b"WAVE" + b"fmt " + struct.pack("<I", 16) + fmt + b"data" + struct.pack("<I", len(payload)) + payload
    path = tmp_path / "after.wav"
    path.write_bytes(b"RIFF" + struct.pack("<I", len(body)) + body)
    result = before.compare_bands(path, ((None, 200), (2000, None)), start_seconds=0.5, end_seconds=2)
    deltas = as_dict(result["bands"])
    assert deltas["<200"]["delta_db"] == pytest.approx(0, abs=0.1)
    assert deltas[">2000"]["delta_db"] == pytest.approx(20 * math.log10(4), abs=0.1)
    assert as_dict(result["largest_increase"])["band"] == ">2000"
    assert as_dict(result["b"])["source"] == str(path.resolve()) and as_dict(result["b"])["start_seconds"] == 0.5
    assert as_dict(Analysis.compare_bands(before.source, before, ((None, 200),))["full"])["delta_db"] == 0
    with pytest.raises(TypeError):
        compare_bands(before, 42, ((None, 200),))


def test_masking_report_lists_only_bands_within_threshold() -> None:
    lead = Analysis.pcm([add(sine(500, 2, 0.3), sine(3000, 2, 0.3))], RATE)
    pad = Analysis.pcm([add(sine(500, 2, 0.2), sine(3000, 2, 0.01))], RATE)
    result = lead.masking_report(pad, ((300, 1000), (2000, None)), clash_threshold_db=6)
    bands = as_dict(result["bands"])
    assert bands["300-1000"]["louder"] == "a"
    assert bands["300-1000"]["overlap_db"] == pytest.approx(20 * math.log10(0.2 / 0.3), abs=0.1)
    assert bands[">2000"]["overlap_db"] == pytest.approx(20 * math.log10(1 / 30), abs=0.3)
    assert result["clashes"] == ["300-1000"]
    assert masking_report(lead, pad, ((300, 1000),), clash_threshold_db=3)["clashes"] == []
    json.dumps(to_json(result), allow_nan=False)


def test_describe_sections_reports_levels_width_centroid_bands_and_lufs() -> None:
    low = sine(100, 8, 0.5)
    high = sine(3000, 8, 0.1)
    left = add(low, high)
    right = add(low, [-value for value in high])  # the high tone is anti-phase: side content
    audio = Analysis.pcm([left, right], RATE)
    result = audio.describe_sections(BPM, {"a": (1, 2), "b": [3, 4]}, bands=((None, 200), (2000, None)))
    sections = rows(result["sections"])
    assert [(item["name"], item["start_bar"], item["end_bar"], item["partial"]) for item in sections] == [
        ("a", 1, 2, False), ("b", 3, 4, False)]
    first = sections[0]
    assert first["start_seconds"] == 0 and first["end_seconds"] == 4
    assert first["rms_db"] == pytest.approx(10 * math.log10((0.25 + 0.01) / 2), abs=0.01)
    assert first["peak_db"] == pytest.approx(20 * math.log10(0.6), abs=0.1)
    assert first["crest_db"] == pytest.approx(first["peak_db"] - first["rms_db"], abs=0.02)
    width = as_dict(first["width"])
    assert width["correlation"] == pytest.approx((0.25 - 0.01) / (0.25 + 0.01), abs=0.01)
    assert width["side_mid_db"] == pytest.approx(10 * math.log10(0.01 / 0.25), abs=0.1)
    assert first["bands"]["<200"] == pytest.approx(dbfs(0.5), abs=0.2)
    assert first["bands"][">2000"] < -60  # anti-phase content cancels in the mono mix
    assert first["centroid_hz"] == pytest.approx(100, abs=5)
    whole = as_dict(audio.summary(loudness=True)["measurements"])
    assert first["integrated_lufs"] == pytest.approx(whole["integrated_lufs"], abs=0.3)
    assert audio._loudness is not None
    plain = rows(describe_sections(audio.source, BPM, {"a": (1, 2)}, loudness=False)["sections"])
    assert plain[0]["integrated_lufs"] is None


def test_describe_sections_clips_partial_sections_and_rejects_outside_or_bad_input() -> None:
    audio = Analysis.pcm([sine(100, 5, 0.5)], RATE)
    item = rows(audio.describe_sections(BPM, {"tail": (2, 3)}, loudness=False)["sections"])[0]
    assert item["partial"] is True and item["end_seconds"] == 5 and as_dict(item["width"])["correlation"] is None
    assert item["integrated_lufs"] is None
    long = Analysis.pcm([sine(100, 5, 0.5)], RATE).describe_sections(BPM, {"tail": (2, 3)})
    reference = Analysis.pcm([sine(100, 5, 0.5)], RATE, start_seconds=2).summary(loudness=True)
    assert rows(long["sections"])[0]["integrated_lufs"] == pytest.approx(
        as_dict(reference["measurements"])["integrated_lufs"], abs=0.3)
    invalid: list[Any] = [{"x": (4, 5)}, {"x": (2, 1)}, {"x": (1,)}, {}, {"x": (1.0, 2)}]
    for sections in invalid:
        with pytest.raises(ValueError):
            audio.describe_sections(BPM, sections, loudness=False)


def test_transition_measures_both_sides_and_the_jump() -> None:
    line = 4 * RATE  # bar 3 starts at 4 s
    quiet = sine(200, 6, 0.1)
    a = quiet[:line] + gain(quiet[line:], 2)  # +6 dB after the drop
    b = quiet[:line] + [value * 4 for value in sine(3000, 2, 0.1)]  # +12 dB and much brighter
    result = Analysis.pcm([a], RATE).transition(Analysis.pcm([b], RATE), BPM, 3, window_seconds=0.5)
    assert result["bar_line_seconds"] == 4.0
    before, after = as_dict(result["before"]), as_dict(result["after"])
    assert as_dict(before["a"])["rms_db"] == pytest.approx(dbfs(0.1), abs=0.05)
    assert as_dict(before["delta"])["rms_db"] == 0
    assert as_dict(after["delta"])["rms_db"] == pytest.approx(6.02, abs=0.1)
    assert as_dict(after["b"])["centroid_hz"] == pytest.approx(3000, abs=20)
    jump = as_dict(result["jump"])
    assert as_dict(jump["a"])["rms_db"] == pytest.approx(6.02, abs=0.1)
    assert as_dict(jump["b"])["rms_db"] == pytest.approx(12.04, abs=0.1)
    assert as_dict(jump["b"])["centroid_hz"] == pytest.approx(2800, abs=25)
    with pytest.raises(ValueError, match="both windows"):
        transition(Analysis.pcm([a], RATE), Analysis.pcm([b[:line]], RATE), BPM, 3)
    with pytest.raises(ValueError, match="later bar"):
        transition(Analysis.pcm([a], RATE), Analysis.pcm([b], RATE), BPM, 1)


def glide(start_hz: float, octaves: float, seconds: float, rate: int = RATE) -> list[float]:
    phase = 0.0
    samples = []
    for index in range(round(seconds * rate)):
        phase += 2 * math.pi * start_hz * 2 ** (octaves * index / (seconds * rate)) / rate
        samples.append(0.4 * math.sin(phase) + 0.2 * math.sin(2 * phase) + 0.1 * math.sin(3 * phase))
    return samples


def test_pitch_track_follows_a_two_octave_glide() -> None:
    audio = Analysis.pcm([glide(110, 2, 3)], RATE, start_seconds=0)
    result = audio.pitch_track(fmin=60, fmax=1000, limit=256)
    summary = as_dict(result["summary"])
    items = rows(result["items"])
    assert result["total"] == len(items) == summary["frame_count"]
    assert summary["voiced_fraction"] == 1
    # Frame centres sit inside the glide, so expected values follow the centre times.
    expected_start = 110 * 2 ** (2 * items[0]["time"] / 3)
    expected_end = 110 * 2 ** (2 * items[-1]["time"] / 3)
    assert summary["start_f0_hz"] == pytest.approx(expected_start, rel=0.02)
    assert summary["end_f0_hz"] == pytest.approx(expected_end, rel=0.02)
    assert summary["glide_semitones"] == pytest.approx(24 * (items[-1]["time"] - items[0]["time"]) / 3, abs=0.5)
    midis = [item["midi"] for item in items]
    assert all(later >= earlier - 0.2 for earlier, later in zip(midis, midis[1:]))
    assert all(item["confidence"] > 0.9 for item in items)
    assert summary["start_midi"] == pytest.approx(69 + 12 * math.log2(expected_start / 440), abs=0.4)
    assert len(audio._tracks) == 1 and audio.pitch_track(fmin=60, fmax=1000, offset=3, limit=2)["next_offset"] == 5
    json.dumps(to_json(result), allow_nan=False)


def test_pitch_track_prefers_the_fundamental_and_marks_silence_unvoiced() -> None:
    tone = [0.1 * math.sin(2 * math.pi * 220 * i / RATE) + 0.3 * math.sin(2 * math.pi * 440 * i / RATE)
            for i in range(RATE)]
    audio = Analysis.pcm([[0.0] * RATE + tone + [0.0] * RATE], RATE)
    result = pitch_track(audio.source, fmin=80, fmax=1500, hop_seconds=0.1, limit=64)
    items = rows(result["items"])
    voiced = [item for item in items if item["f0_hz"] is not None]
    assert 0.95 < len(voiced) / len(items) * 3 < 1.1
    assert all(item["f0_hz"] == pytest.approx(220, rel=0.01) for item in voiced)
    assert all(item["confidence"] == 0 for item in items[:8])
    assert as_dict(result["summary"])["median_f0_hz"] == pytest.approx(220, rel=0.01)
    assert result["decimation"] == 1 and result["analysis_rate_hz"] == RATE


@pytest.mark.parametrize("kwargs", [{"fmin": 500, "fmax": 100}, {"fmax": 5000}, {"hop_seconds": 0},
                                    {"min_confidence": 2}, {"limit": 300}, {"offset": -1},
                                    {"start_seconds": 0.9, "end_seconds": 0.91}])
def test_pitch_track_rejects_invalid_settings(kwargs: dict[str, Any]) -> None:
    with pytest.raises(ValueError):
        pitch_track(from_pcm([sine(220, 1, 0.5)], RATE), **kwargs)


def test_butterworth_biquad_passband_and_stopband() -> None:
    specs = band_specs(((None, 100.0), (1000.0, None)), RATE)
    assert [spec.label for spec in specs] == ["<100", ">1000"]
    source = from_pcm([add(sine(50, 2, 0.5), sine(2000, 2, 0.5))], RATE)
    bands = as_dict(band_energy(source, ((None, 100), (1000, None), (None, None)), start_seconds=1)["bands"])
    assert bands["<100"] == pytest.approx(dbfs(0.5), abs=0.1)
    assert bands[">1000"] == pytest.approx(dbfs(0.5), abs=0.1)
    assert bands["full"] == pytest.approx(10 * math.log10(0.25), abs=0.01)
    # 24 dB/octave: a tone two octaves into the stopband sits about 48 dB down.
    stop = as_dict(band_energy(from_pcm([sine(400, 2, 0.5)], RATE), ((None, 100),), start_seconds=1)["bands"])
    assert -52 < stop["<100"] - dbfs(0.5) < -44
    section = Biquad(1, 0, 0, 0, 0)
    assert list(section.process([0.5, -0.25])) == [0.5, -0.25]


def test_kernels_agree_between_pure_python_and_numpy(monkeypatch: pytest.MonkeyPatch) -> None:
    random.seed(3)
    left = [random.uniform(-1, 1) for _ in range(5000)]
    right = [random.uniform(-1, 1) for _ in range(5000)]
    source = from_pcm([left, right], RATE)
    audio = Analysis.pcm([left, right], RATE)
    monkeypatch.setattr(_kernels, "USE_NUMPY", False)
    pure = (list(_kernels.mono_mix(source.channels, 10, 4000)), _kernels.dot(left, right),
            _kernels.sum_squares(left), list(_kernels.box_decimate(left, 3)), _kernels.peak(source.channels, 0, 5000),
            _kernels.mean_channel_power(source.channels, 0, 5000), _kernels.autocorrelation(left[:400], 2, 50))
    scan = audio.scan_bars(BPM)
    if not _kernels.USE_NUMPY and _kernels._numpy is None:
        pytest.skip("numpy is not installed; the pure path is the only path")
    monkeypatch.setattr(_kernels, "USE_NUMPY", True)
    fast = (list(_kernels.mono_mix(source.channels, 10, 4000)), _kernels.dot(left, right),
            _kernels.sum_squares(left), list(_kernels.box_decimate(left, 3)), _kernels.peak(source.channels, 0, 5000),
            _kernels.mean_channel_power(source.channels, 0, 5000), _kernels.autocorrelation(left[:400], 2, 50))
    for expected, actual in zip(pure, fast):
        assert actual == pytest.approx(expected, rel=1e-9, abs=1e-12)
    assert Analysis.pcm([left, right], RATE).scan_bars(BPM)["bars"] == scan["bars"]
