import json
import math
import struct
from pathlib import Path
from typing import Any, cast

import pytest

from fruitylink.analysis import Analysis, from_pcm, load_wav, note_density, tick_range_seconds
from fruitylink.models import NoteInfo
from fruitylink.values import to_json


def measure(samples: list[float], rate: int = 1000, **kwargs: Any) -> dict[str, Any]:
    result = Analysis.pcm([samples], rate).summary(**kwargs)
    return cast(dict[str, Any], result["measurements"])


def tone(rate: int, seconds: float, frequency: float, peak: float, phase: float = 0) -> list[float]:
    return [peak * math.sin(2 * math.pi * frequency * i / rate + phase)
            for i in range(round(rate * seconds))]


def test_full_scale_sine_rms_energy_crest_and_dc() -> None:
    row = measure(tone(1000, 1, 100, 1))["channels"][0]
    assert row["rms"] == pytest.approx(math.sqrt(0.5))
    assert row["rms_dbfs"] == pytest.approx(-3.0102999566)
    assert row["energy"] == pytest.approx(0.5)
    assert row["crest_db"] == pytest.approx(20 * math.log10(row["sample_peak"] / row["rms"]))
    assert row["dc_offset"] == pytest.approx(0, abs=1e-14)


def test_silence_is_finite_json_and_unavailable_db() -> None:
    result = measure([0] * 24000, 8000, loudness=True, true_peak=True)
    json.dumps(to_json(result), allow_nan=False)
    assert result["channels"][0]["rms_dbfs"] is None
    assert result["channels"][0]["crest_db"] is None
    assert result["integrated_lufs"] is None
    assert result["psr_db"] is None
    assert result["psr_unavailable_reason"] == "silent 3-second window"


def test_occupancy_means_rms_block_occupancy_not_nonzero_sample_fraction() -> None:
    row = measure([0] * 50 + [0.1] * 50, occupancy_seconds=0.05,
                  occupancy_threshold_dbfs=-30)["channels"][0]
    assert row["audio_occupancy"] == 0.5
    assert row["energy"] == pytest.approx(0.0005)


def test_transient_windows_are_paged_and_report_absolute_rounded_frames() -> None:
    analysis = Analysis.pcm([[0] * 100 + [1] * 100], 1000, start_seconds=0.0502, end_seconds=0.1501,
                            source="stem:drums", source_kind="mixer_stem")
    page = cast(dict[str, Any], analysis.windows(window_seconds=0.02, hop_seconds=0.01, offset=3, limit=2))
    assert page["provenance"]["start_frame"] == 50
    assert page["provenance"]["end_frame"] == 151
    assert page["provenance"]["source_kind"] == "mixer_stem"
    assert page["total"] == 9 and page["next_offset"] == 5
    assert page["items"][0]["start_frame"] == 80
    assert page["items"][0]["end_frame"] == 100
    assert page["items"][1]["channels"][0]["energy"] == pytest.approx(0.01)
    assert analysis.windows(offset=100)["items"] == []


def test_stereo_is_not_summed_and_float_over_full_scale_is_retained() -> None:
    result = cast(dict[str, Any], Analysis.pcm([[2, -2], [-2, 2]], 1000).summary())
    for row in result["measurements"]["channels"]:
        assert row["rms"] == 2 and row["over_full_scale_frames"] == 2
        assert row["sample_peak_dbfs"] == pytest.approx(6.0205999)


@pytest.mark.parametrize("args", [{"limit": 0}, {"limit": 65}, {"offset": True}, {"offset": -1},
                                 {"window_seconds": 0}, {"hop_seconds": math.inf},
                                 {"window_seconds": 1e100}, {"window_seconds": 0.000001}])
def test_invalid_page_or_window_is_rejected(args: dict[str, Any]) -> None:
    with pytest.raises(ValueError):
        Analysis.pcm([[0] * 1000], 1000).windows(**args)


def test_multichannel_page_output_is_bounded() -> None:
    analysis = Analysis.pcm([[0] * 1000] * 32, 1000)
    with pytest.raises(ValueError, match="256"):
        analysis.windows(window_seconds=0.01, limit=64)
    json.dumps(to_json(analysis.windows(window_seconds=0.01, limit=8)), allow_nan=False)


def test_overlapping_window_and_true_peak_work_caps_fail_before_calculation(monkeypatch: pytest.MonkeyPatch) -> None:
    from fruitylink.analysis import amplitude

    analysis = Analysis.pcm([[0] * 1000], 1000)
    monkeypatch.setattr(amplitude, "MAX_AMPLITUDE_WORK", 1000)
    with pytest.raises(ValueError, match="Overlapping"):
        analysis.windows(window_seconds=0.9, hop_seconds=0.001, limit=2)
    monkeypatch.setattr(amplitude, "MAX_TRUE_PEAK_SAMPLES", 999)
    with pytest.raises(ValueError, match="True-peak FIR"):
        analysis.summary(true_peak=True)
    # Whole-range amplitude/loudness use the source bound, not the overlapping-page limit.
    assert analysis.summary()["measurements"]


def test_oversized_end_time_clamps_to_source_without_float_overflow() -> None:
    source = from_pcm([[0, 1]], 48000, end_seconds=1e308)
    assert source.frame_count == 2


@pytest.mark.parametrize("samples", [[math.nan], [math.inf], [1e20]])
def test_nonfinite_pcm_rejected(samples: list[float]) -> None:
    with pytest.raises(ValueError):
        from_pcm([samples], 48000)


@pytest.mark.parametrize("rate", [True, 0, 48000.5])
def test_invalid_pcm_sample_rate_rejected(rate: Any) -> None:
    with pytest.raises(ValueError):
        from_pcm([[0]], rate)


def write_wav(path: Path, tag: int, bits: int, payload: bytes, *, channels: int = 1,
              extensible: bool = False) -> None:
    width = bits // 8
    fmt = struct.pack("<HHIIHH", 0xFFFE if extensible else tag, channels, 48000,
                      48000 * channels * width, channels * width, bits)
    if extensible:
        fmt += struct.pack("<HHI", 22, bits, 0) + struct.pack("<I", tag) + bytes.fromhex("00001000800000aa00389b71")
    chunks = b"fmt " + struct.pack("<I", len(fmt)) + fmt + b"JUNK\x02\x00\x00\x00hi"
    chunks += b"data" + struct.pack("<I", len(payload)) + payload + (b"\0" if len(payload) % 2 else b"")
    path.write_bytes(b"RIFF" + struct.pack("<I", len(chunks) + 4) + b"WAVE" + chunks)


@pytest.mark.parametrize("tag,bits,payload,expected", [
    (1, 8, bytes([0, 128, 255]), [-1, 0, 127 / 128]),
    (1, 16, struct.pack("<hhh", -32768, 0, 16384), [-1, 0, 0.5]),
    (1, 24, bytes.fromhex("000080000000000040"), [-1, 0, 0.5]),
    (1, 32, struct.pack("<iii", -2147483648, 0, 1073741824), [-1, 0, 0.5]),
    (3, 32, struct.pack("<fff", -1, 0, 1.25), [-1, 0, 1.25]),
    (3, 64, struct.pack("<ddd", -1, 0, 1.25), [-1, 0, 1.25]),
])
@pytest.mark.parametrize("extensible", [False, True])
def test_wav_formats(tmp_path: Path, tag: int, bits: int, payload: bytes,
                     expected: list[float], extensible: bool) -> None:
    path = tmp_path / "test.wav"
    write_wav(path, tag, bits, payload, extensible=extensible)
    source = load_wav(path)
    assert list(source.channels[0]) == expected
    assert source.total_source_frames == 3


def test_wav_partial_frame_or_chunk_rejected(tmp_path: Path) -> None:
    path = tmp_path / "bad.wav"
    write_wav(path, 1, 16, b"\0\0\0")
    with pytest.raises(ValueError, match="partial"):
        load_wav(path)
    path.write_bytes(path.read_bytes()[:-3])
    with pytest.raises(ValueError, match="length"):
        load_wav(path)


def test_note_density_clips_boundaries_excludes_muted_and_does_not_count_audio() -> None:
    notes = [NoteInfo(0, 0, 60, 0, 200, 100, False), NoteInfo(1, 0, 64, 150, 100, 100, False),
             NoteInfo(2, 0, 67, 100, 200, 100, True)]
    result = note_density(notes, start_tick=100, end_tick=300, ppq=100)
    assert result["onsets"] == 1 and result["onsets_per_beat"] == 0.5
    assert result["occupied_fraction"] == 0.75 and result["average_polyphony"] == 1
    assert result["source_kind"] == "pattern_local_notes"
    assert tick_range_seconds(100, 300, ppq=100, bpm=120) == (0.5, 1.5)


@pytest.mark.parametrize("rate", [44100, 48000])
def test_ebu_stereo_reference_tone_loudness(rate: int) -> None:
    samples = tone(rate, 3, 1000, 10 ** (-23 / 20))
    row = cast(dict[str, Any], Analysis.pcm([samples, samples], rate).summary(loudness=True))["measurements"]
    assert row["integrated_lufs"] == pytest.approx(-23, abs=0.1)
    assert row["short_term_lufs"] == pytest.approx(-23, abs=0.1)
    assert row["momentary_lufs"] == pytest.approx(-23, abs=0.1)


def test_integrated_relative_gate_rejects_quiet_blocks() -> None:
    rate = 8000
    samples = tone(rate, 1, 1000, 10 ** (-50 / 20)) + tone(rate, 3, 1000, 10 ** (-23 / 20))
    row = cast(dict[str, Any], Analysis.pcm([samples, samples], rate).summary(loudness=True))["measurements"]
    # The transition blocks contribute according to the standard overlap and relative gate.
    assert -23.4 < row["integrated_lufs"] < -22.7


@pytest.mark.parametrize("divisor,phase", [(4, 0), (4, math.pi / 4), (6, math.pi / 3), (8, 3 * math.pi / 8)])
def test_true_peak_analytic_ebu_frequencies(divisor: int, phase: float) -> None:
    samples = tone(48000, 0.05, 48000 / divisor, 0.5, phase)
    # Standard 10ms fades avoid creating a different edge discontinuity signal.
    samples = [v * min(1, i / 480, (len(samples) - 1 - i) / 480) for i, v in enumerate(samples)]
    result = measure(samples, 48000, true_peak=True)
    assert -6.4206 <= result["true_peak_estimate_dbtp"] <= -5.8206


def test_psr_uses_three_seconds_and_not_peak_minus_rms() -> None:
    samples = tone(8000, 3.2, 1000, 0.5)
    page = cast(dict[str, Any], Analysis.pcm([samples], 8000).windows(
        window_seconds=0.1, hop_seconds=0.1, offset=28, limit=4, loudness=True, true_peak=True))
    assert page["items"][0]["psr_db"] is None
    assert page["items"][0]["psr_unavailable_reason"] == "requires 3 seconds within selected range"
    row = page["items"][1]
    assert row["short_term_range_frames"] == [0, 24000]
    assert row["psr_db"] == pytest.approx(row["true_peak_estimate_dbtp"] - row["short_term_lufs"], abs=0.01)
    assert abs(row["psr_db"] - row["channels"][0]["crest_db"]) > 0.01
