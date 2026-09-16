"""Synthetic-signal checks for the agent-oriented describe/compare/browse layer."""

import json
import math
import random
import struct
from pathlib import Path
from typing import Any

import pytest

from fruitylink import Studio
from fruitylink.analysis import (
    Analysis,
    AudioDescription,
    _kernels,
    compare_audio,
    describe_audio,
    describe_samples,
    from_pcm,
)
from fruitylink.analysis.loudness import Loudness, integrated_loudness
from fruitylink.values import to_json

RATE = 44100


def sine(frequency: float, seconds: float, amplitude: float = 0.5, rate: int = RATE) -> list[float]:
    return [amplitude * math.sin(2 * math.pi * frequency * index / rate) for index in range(round(seconds * rate))]


def noise(seconds: float, amplitude: float = 0.5, seed: int = 1, rate: int = RATE) -> list[float]:
    generator = random.Random(seed)
    return [amplitude * generator.uniform(-1, 1) for _ in range(round(seconds * rate))]


def decaying(samples: list[float], tau: float, rate: int = RATE) -> list[float]:
    return [value * math.exp(-index / (tau * rate)) for index, value in enumerate(samples)]


def clicks(count: int, spacing: float, seconds: float, rate: int = RATE) -> list[float]:
    signal = [0.0] * round(seconds * rate)
    for hit in range(count):
        start = round(hit * spacing * rate)
        for index in range(start, min(len(signal), start + 40)):
            signal[index] = 0.9 * (1 - (index - start) / 40)
    return signal


def write_wav(path: Path, channels: list[list[float]], rate: int = RATE) -> Path:
    frames = len(channels[0])
    interleaved = [channel[index] for index in range(frames) for channel in channels]
    payload = struct.pack("<%dh" % len(interleaved), *(round(max(-1, min(1, value)) * 32767) for value in interleaved))
    fmt = struct.pack("<HHIIHH", 1, len(channels), rate, rate * 2 * len(channels), 2 * len(channels), 16)
    body = b"WAVE" + b"fmt " + struct.pack("<I", 16) + fmt + b"data" + struct.pack("<I", len(payload)) + payload
    path.write_bytes(b"RIFF" + struct.pack("<I", len(body)) + body)
    return path


def describe(*channels: list[float], **options: Any) -> AudioDescription:
    return describe_audio((list(channels), RATE), **options)


def test_sine_is_tonal_steady_with_root_and_json_safe_data() -> None:
    result = describe(sine(220, 1.0))
    data = result.data
    json.dumps(to_json(data), allow_nan=False)
    assert data["tonality"]["kind"] == "tonal"
    pitch = data["tonality"]["pitch"]
    assert pitch["note"] == "A3" and abs(pitch["cents"]) <= 5 and pitch["f0_hz"] == pytest.approx(220, abs=1)
    assert data["level"]["peak_dbfs"] == pytest.approx(-6.0, abs=0.1)
    assert data["level"]["rms_dbfs"] == pytest.approx(-9.0, abs=0.1)
    assert data["level"]["crest_db"] == pytest.approx(3.0, abs=0.1)
    assert data["loop"]["steady_state"] and "steady" in result.tags and "tonal" in result.tags
    assert data["stereo"]["width"] == "mono" and "mono" in result.tags
    assert data["spectral"]["segments"][0]["bands_rel_db"]["low"] == 0.0
    assert data["onsets"]["count"] == 1 and data["onsets"]["items"][0]["time"] == 0.0
    assert "root ~ A3" in result.text and "tonal" in result.text
    assert len(result.text.splitlines()) <= 40
    assert str(result) == result.text


def test_white_noise_burst_is_noisy_bright_and_harsh_with_a_decay_time() -> None:
    result = describe(decaying(noise(0.6), 0.05) + [0.0] * RATE)
    data = result.data
    assert data["tonality"]["kind"] == "noisy" and data["tonality"]["pitch"] is None
    assert {"noisy", "bright", "harsh 2-4 kHz", "one-shot"} <= set(result.tags)
    # 40 dB down is 4.6 time constants: 0.23 s at tau = 50 ms, measured on 5 ms blocks.
    assert data["decay"]["to_minus_40_db_seconds"] == pytest.approx(0.23, abs=0.03)
    assert data["decay"]["attack_ms"] is not None and data["decay"]["attack_ms"] <= 5
    assert data["silence"]["tail_seconds"] > 0.9
    assert data["spectral"]["segments"][0]["centroid_hz"] > 8000
    assert data["spectral"]["segments"][0]["tilt_db_per_octave"] == pytest.approx(3.0, abs=0.5)


def test_click_train_reports_onsets_in_seconds_bars_and_ticks() -> None:
    spacing = 60 / 100 / 2  # eighth notes at 100 bpm
    result = describe(clicks(8, spacing, 2.4), bpm=100, ppq=96, start_bar=5)
    onsets = result.data["onsets"]
    assert onsets["count"] == 8 and onsets["regular"]
    assert onsets["median_spacing_ms"] == pytest.approx(300, abs=6)
    assert [item["bar_beat"] for item in onsets["items"][:3]] == ["5:1.00", "5:1.50", "5:2.00"]
    assert [item["tick"] for item in onsets["items"][:3]] == [0, 48, 96]
    assert onsets["items"][3]["time"] == pytest.approx(0.9, abs=0.006)
    assert any(tag.startswith("rhythmic (8 hits") for tag in result.tags)
    assert "clicky attack" in result.tags
    assert result.data["loop"]["bars"] == pytest.approx(1.0) and result.data["loop"]["whole_bars"]


def test_stereo_width_distinguishes_wide_from_mono() -> None:
    left, right = noise(0.5, seed=1), noise(0.5, seed=2)
    wide = describe(left, right)
    mono = describe(left, left)
    assert wide.data["stereo"]["width"] in ("wide", "very wide") and "wide" in wide.tags
    assert abs(wide.data["stereo"]["correlation"]) < 0.1
    assert mono.data["stereo"]["width"] == "mono" and mono.data["stereo"]["correlation"] == 1.0
    assert "wide AND bright" in wide.tags  # the taste rule: wide is fine, wide and bright is flagged
    assert "wide AND bright" not in mono.tags


def test_head_and_tail_silence_are_measured_and_tagged() -> None:
    signal = [0.0] * round(0.1 * RATE) + sine(440, 0.5) + [0.0] * round(0.2 * RATE)
    result = describe(signal)
    assert result.data["silence"]["head_seconds"] == pytest.approx(0.1, abs=0.001)
    assert result.data["silence"]["tail_seconds"] == pytest.approx(0.2, abs=0.001)
    assert "leading silence 100 ms" in result.tags
    assert result.data["onsets"]["items"][0]["time"] == pytest.approx(0.1, abs=0.006)


def test_sub_heavy_tone_and_silent_input() -> None:
    result = describe(sine(45, 1.0))
    assert "sub-heavy" in result.tags and result.data["spectral"]["segments"][0]["bands_rel_db"]["sub"] == 0.0
    silent = describe([0.0] * RATE)
    assert silent.tags == ["silent"] and silent.data["silent"] and "silent" in silent.text


def test_loudness_fast_path_matches_recursive_meter(monkeypatch: pytest.MonkeyPatch) -> None:
    source = from_pcm([sine(997, 2.0, 0.5), sine(997, 2.0, 0.25)], RATE)
    expected = Loudness(source).integrated()
    assert expected is not None and expected == pytest.approx(integrated_loudness(source), abs=1e-6)
    if _kernels._numpy is None:
        pytest.skip("numpy is not installed; the pure path is the only path")
    monkeypatch.setattr(_kernels, "USE_NUMPY", True)
    assert integrated_loudness(source) == pytest.approx(expected, abs=0.01)


def test_pure_and_numpy_paths_agree(monkeypatch: pytest.MonkeyPatch) -> None:
    left = decaying(noise(0.4, seed=5), 0.1) + sine(330, 0.3, 0.3)
    right = decaying(noise(0.4, seed=6), 0.1) + sine(330, 0.3, 0.3)
    monkeypatch.setattr(_kernels, "USE_NUMPY", False)
    pure = describe(left, right, bpm=120)
    if _kernels._numpy is None:
        pytest.skip("numpy is not installed; the pure path is the only path")
    monkeypatch.setattr(_kernels, "USE_NUMPY", True)
    fast = describe(left, right, bpm=120)
    assert fast.tags == pure.tags
    assert fast.data["onsets"] == pure.data["onsets"]
    assert fast.data["envelope"]["sketch"] == pure.data["envelope"]["sketch"]
    for fast_segment, pure_segment in zip(fast.data["spectral"]["segments"], pure.data["spectral"]["segments"]):
        assert fast_segment["centroid_hz"] == pytest.approx(pure_segment["centroid_hz"], abs=1)
        assert fast_segment["bands_rel_db"] == pytest.approx(pure_segment["bands_rel_db"], abs=0.11)


def test_compare_reports_deltas_in_the_same_vocabulary() -> None:
    dull = decaying(sine(200, 1.5, 0.6), 0.08)
    bright = [a + b for a, b in zip(decaying(sine(200, 1.5, 0.6), 0.25), decaying(noise(1.5, 0.3), 0.25))]
    result = compare_audio((dull, RATE), (bright, RATE))
    deltas = result.data["deltas"]
    assert result.data["delta_sign"] == "b minus a"
    assert deltas["centroid_octaves"] > 1 and deltas["decay_40_ratio"] > 1.5
    assert result.data["bands_dbfs_delta"]["pres"] > 10
    assert result.data["largest_rise"] in ("pres", "high", "air")
    assert "b is brighter" in result.data["verdicts"] and "b has a longer tail" in result.data["verdicts"]
    assert "b is harsher in 2-4 kHz" in result.data["verdicts"]
    assert "compare (b minus a)" in result.text and "verdict:" in result.text
    # Descriptions are accepted unchanged, so an agent can reuse work.
    again = compare_audio(describe_audio((dull, RATE)), describe_audio((bright, RATE)))
    assert again.data["deltas"] == deltas


def test_describe_samples_caches_by_content_and_reports_errors(tmp_path: Path) -> None:
    first = write_wav(tmp_path / "snare_a.wav", [decaying(noise(0.3), 0.04)])
    second = write_wav(tmp_path / "tone.wav", [sine(110, 0.5)])
    bogus = tmp_path / "loop.mp3"
    bogus.write_bytes(b"ID3")
    cache = tmp_path / "cache"
    table = describe_samples([first, second, bogus, tmp_path / "missing.wav"], cache_dir=cache)
    assert [row["cached"] for row in table.rows[:2]] == [False, False]
    assert table.rows[1]["root"] == "A2" and table.rows[0]["root"] is None
    assert "unsupported format" in table.rows[2]["error"] and table.rows[3]["error"]
    assert len(list(cache.glob("*.json"))) == 2
    again = describe_samples([first, second], cache_dir=cache)
    assert [row["cached"] for row in again.rows] == [True, True]
    assert again.descriptions[str(first)].text == table.descriptions[str(first)].text
    lines = table.text.splitlines()
    assert lines[0].startswith("file") and len(lines) == 5 and "error:" in lines[3]
    assert "snare_a.wav" in lines[1] and "noisy" in lines[1]
    write_wav(first, [sine(330, 0.3)])  # edited content re-analyzes even with the same name
    edited = describe_samples([first], cache_dir=cache)
    assert edited.rows[0]["cached"] is False and edited.rows[0]["root"] == "E4"
    assert describe_samples([], cache_dir="").rows == []


def test_analysis_object_and_studio_hooks(tmp_path: Path, fl: Studio, transport: Any) -> None:
    path = write_wav(tmp_path / "kick.wav", [decaying(sine(55, 0.4, 0.8), 0.12)])
    audio = Analysis.wav(path)
    described = audio.describe(detail="brief")
    assert described.data["source"]["path"] == str(path) and "sub-heavy" in described.tags
    assert Analysis.describe(path).tags == described.tags
    assert Analysis.describe(audio).data["detail"] == "normal"
    transport.responses["add_sample_channel"] = 3
    channel = fl.channels.add_sample(str(path))
    assert fl.samples.path_of(channel.index) == str(path)
    assert fl.samples.describe(3, detail="brief").text == described.text
    with pytest.raises(LookupError):
        fl.samples.describe(4)
    fl.samples.register(4, path)
    assert fl.samples.compare(3, 4).data["verdicts"] == []
    channel.replace_sample(str(path))
    assert fl.samples.known() == {3: str(path), 4: str(path)}
    assert fl.samples.browse([path], cache_dir=tmp_path / "cache").rows[0]["root"] == "A1"


@pytest.mark.parametrize("options", [{"detail": "loud"}, {"ppq": 96}, {"bpm": 0}, {"bpm": 120, "ppq": 0},
                                     {"bpm": 120, "beats_per_bar": 0}])
def test_invalid_options_are_rejected(options: dict[str, Any]) -> None:
    with pytest.raises(ValueError):
        describe(sine(440, 0.2), **options)


def test_pcm_shapes_and_bad_inputs() -> None:
    mono = describe_audio((sine(440, 0.2), RATE))
    planar = describe_audio(([sine(440, 0.2), sine(440, 0.2)], RATE))
    assert mono.data["source"]["channels"] == 1 and planar.data["source"]["channels"] == 2
    with pytest.raises(TypeError):
        describe_audio(("not audio", RATE))
    with pytest.raises(TypeError):
        describe_audio(42)
