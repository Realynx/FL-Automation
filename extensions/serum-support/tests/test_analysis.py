import math
from pathlib import Path
from typing import Any, cast

import pytest
from fruitylink.analysis import Analysis

from fruitylink_serum import describe_audition, describe_wav


def test_bright_decaying_stereo_audition_reports_evidence() -> None:
    rate = 16_000
    frames = rate
    left = [math.sin(2 * math.pi * 4_000 * index / rate) * math.exp(-index / 1800) for index in range(frames)]
    right = [-value for value in left]

    result = describe_audition(Analysis.pcm([left, right], rate, source="synthetic"))

    names = {trait["name"] for trait in cast(list[dict[str, Any]], result["traits"])}
    assert "bright" in names
    assert "decaying" in names
    assert "side_energy_present" in names
    assert cast(dict[str, Any], result["attribution"])["instrument_identity"] == "unverified"
    assert "waveform" in cast(list[str], result["limitations"])[1].lower()


def test_long_audition_is_refused_before_expensive_analysis() -> None:
    audio = Analysis.pcm([[0.0] * 1601], 100, source="too long")
    with pytest.raises(ValueError, match="at most 15 seconds"):
        describe_audition(audio)


def test_wav_missing_path_preserves_sdk_error(tmp_path: Path) -> None:
    with pytest.raises(FileNotFoundError):
        describe_wav(tmp_path / "missing.wav")


def test_silence_has_no_peak_time_or_traits() -> None:
    result = describe_audition(Analysis.pcm([[0.0] * 1000], 1000, source="silence"))
    descriptors = cast(dict[str, Any], result["descriptors"])
    assert descriptors["envelope"]["peak_window_seconds"] is None
    assert result["traits"] == []


def test_near_silent_channel_crest_does_not_mark_audible_tone_transient() -> None:
    rate = 1000
    numerical_noise = [1e-8] + [0.0] * (rate - 1)
    audible_tone = [0.03 * math.sin(2 * math.pi * index / 20) for index in range(rate)]

    result = describe_audition(Analysis.pcm([numerical_noise, audible_tone], rate, source="noise"))

    descriptors = cast(dict[str, Any], result["descriptors"])
    channels = descriptors["amplitude"]["channels"]
    assert channels[0]["rms_dbfs"] < descriptors["amplitude"]["occupancy_threshold_dbfs"]
    assert channels[0]["crest_db"] >= 12
    assert channels[1]["rms_dbfs"] > descriptors["amplitude"]["occupancy_threshold_dbfs"]
    assert channels[1]["crest_db"] < 12
    names = {trait["name"] for trait in cast(list[dict[str, Any]], result["traits"])}
    assert "transient" not in names


def test_hard_panned_signal_is_imbalanced_without_width_claim() -> None:
    left = [math.sin(2 * math.pi * index / 20) for index in range(1000)]
    result = describe_audition(Analysis.pcm([left, [0.0] * 1000], 1000, source="hard pan"))
    names = {trait["name"] for trait in cast(list[dict[str, Any]], result["traits"])}
    assert "stereo_imbalanced" in names
    assert all("wide" not in trait["name"] for trait in cast(list[dict[str, Any]], result["traits"]))


def test_antiphase_signal_reports_side_energy_and_negative_correlation() -> None:
    left = [math.sin(2 * math.pi * index / 20) for index in range(1000)]
    result = describe_audition(Analysis.pcm([left, [-value for value in left]], 1000, source="anti"))
    descriptors = cast(dict[str, Any], result["descriptors"])
    assert descriptors["spatial"]["correlation"] == pytest.approx(-1)
    names = {trait["name"] for trait in cast(list[dict[str, Any]], result["traits"])}
    assert "side_energy_present" in names


def test_late_onset_time_is_relative_to_selected_audio() -> None:
    signal = [0.0] * 500 + [1.0] * 500
    result = describe_audition(Analysis.pcm([signal], 1000, source="late onset"))
    descriptors = cast(dict[str, Any], result["descriptors"])
    assert descriptors["envelope"]["time_to_90_percent_peak_from_selection_seconds"] == pytest.approx(0.5)
