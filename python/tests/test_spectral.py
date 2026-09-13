"""Analytical signal and resource-bound checks for the stdlib spectral backend."""

import json
from array import array
from collections.abc import Sequence
from math import cos, pi, sin
from typing import cast

import pytest

from fruitylink.analysis.audio import AudioSource
from fruitylink.analysis.spectral import spectral, spectral_windows
from fruitylink.values import JsonValue, json_object


def source(*channels: Sequence[float], rate: int = 8192, origin: int = 0) -> AudioSource:
    frames = len(channels[0])
    return AudioSource(sample_rate=rate, channels=tuple(array("d", channel) for channel in channels),
                       source="synthetic", source_kind="pcm", origin_frame=origin,
                       requested_start_seconds=origin / rate, requested_end_seconds=None,
                       total_source_frames=origin + frames, sample_format="float64")


def tone(frequency: float, frames: int = 1024, rate: int = 8192) -> list[float]:
    return [sin(2 * pi * frequency * index / rate) for index in range(frames)]


def objects(value: JsonValue) -> list[dict[str, JsonValue]]:
    assert isinstance(value, list)
    return [json_object(item, "spectral test") for item in value]


def test_bin_centered_tone_has_analytical_power_centroid_and_rolloff() -> None:
    result = spectral(source(tone(512)), fft_size=1024, rolloff_fraction=0.8)
    assert result["windowed_mean_square"] == pytest.approx(0.5, abs=1e-12)
    assert result["centroid_hz"] == pytest.approx(512, abs=1e-8)
    assert result["rolloff_hz"] == 512
    assert result["bin_spacing_hz"] == 8
    assert objects(result["dominant_bins"])[0]["frequency_hz"] == 512
    assert objects(result["dominant_bins"])[0]["power_fraction"] == pytest.approx(2 / 3)


def test_separated_tones_report_power_fractions_not_amplitude_fractions() -> None:
    audio = [low + 0.5 * high for low, high in zip(tone(512), tone(2048))]
    result = spectral(source(audio), fft_size=1024, band_edges_hz=(0, 1024, 4096))
    bands = objects(result["bands"])
    assert result["windowed_mean_square"] == pytest.approx(0.625, abs=1e-12)
    assert result["centroid_hz"] == pytest.approx(819.2, abs=1e-7)
    assert bands[0]["power_fraction"] == pytest.approx(0.8, abs=1e-12)
    assert bands[1]["power_fraction"] == pytest.approx(0.2, abs=1e-12)


def test_partial_band_edges_use_whole_spectrum_denominator() -> None:
    audio = [low + high for low, high in zip(tone(512), tone(2048))]
    result = spectral(source(audio), fft_size=1024, band_edges_hz=(100, 1024))
    assert objects(result["bands"])[0]["power_fraction"] == pytest.approx(0.5)
    assert result["nyquist_hz"] == 4096


def test_stereo_antiphase_preserves_power_and_independent_channel_mean() -> None:
    mono = tone(512)
    opposite = [-value for value in mono]
    result = spectral(source(mono, opposite), fft_size=1024)
    single_side = spectral(source(mono, [0.0] * len(mono)), fft_size=1024)
    assert result["windowed_mean_square"] == pytest.approx(0.5)
    assert result["centroid_hz"] == pytest.approx(512)
    assert result["channel_aggregation"] == "mean_channel_power"
    assert single_side["windowed_mean_square"] == pytest.approx(0.25)


def test_silence_has_null_normalized_descriptors_and_json_safe_output() -> None:
    result = spectral(source([0.0] * 1024))
    assert result["silence"] is True
    assert result["windowed_mean_square"] == 0
    assert result["centroid_hz"] is None
    assert result["rolloff_hz"] is None
    assert result["dominant_bins"] == []
    assert all(band["power_fraction"] is None for band in objects(result["bands"]))
    assert len(json.dumps(result, allow_nan=False)) < 5000


def test_hann_endpoint_zero_is_not_misreported_as_pcm_silence() -> None:
    # A single nonzero sample at the first Hann endpoint has zero window weight.
    result = spectral(source([1.0] + [0.0] * 1023), fft_size=1024)
    assert result["silence"] is False
    assert result["zero_spectral_power"] is True
    assert result["centroid_hz"] is None


def test_overlapping_temporal_windows_report_actual_quantized_hop() -> None:
    result = spectral_windows(source(tone(512)), window_seconds=0.1, hop_seconds=0.05, fft_size=512)
    items = objects(result["items"])
    assert result["window_frames"] == 819
    assert result["hop_frames"] == 410
    assert [item["start_frame"] for item in items] == [0, 410, 820]
    assert items[-1]["end_frame"] == 1024


def test_dominant_bin_output_can_be_omitted_and_has_strict_bound() -> None:
    audio = source(tone(512))
    assert spectral(audio, dominant_bins=0)["dominant_bins"] == []
    with pytest.raises(ValueError, match="dominant_bins"):
        spectral(audio, dominant_bins=17)


def test_parseval_matches_window_weighted_time_domain_mean_square() -> None:
    audio = [((index * 17) % 29 - 14) / 14 for index in range(128)]
    window = [0.5 - 0.5 * cos(2 * pi * index / len(audio)) for index in range(len(audio))]
    expected = sum((sample * weight) ** 2 for sample, weight in zip(audio, window))
    expected /= sum(weight**2 for weight in window)
    result = spectral(source(audio), fft_size=128)
    assert result["windowed_mean_square"] == pytest.approx(expected, abs=1e-12)


def test_dc_is_retained_and_nyquist_bin_is_not_doubled() -> None:
    dc = spectral(source([1.0] * 1024), fft_size=1024)
    nyquist = spectral(source([1.0 if index % 2 == 0 else -1.0 for index in range(1024)]), fft_size=1024)
    assert dc["windowed_mean_square"] == pytest.approx(1.0)
    assert nyquist["windowed_mean_square"] == pytest.approx(1.0)
    assert objects(dc["dominant_bins"])[0]["frequency_hz"] == 0
    assert objects(nyquist["dominant_bins"])[0]["frequency_hz"] == 4096
    assert sum(cast(float, item["power_fraction"]) for item in objects(nyquist["bands"])) == pytest.approx(1)


def test_temporal_windows_capture_brightness_change_with_absolute_provenance() -> None:
    audio = tone(256, 2048) + tone(2048, 2048)
    selected = source(audio, origin=8192)
    page = spectral_windows(selected, window_seconds=0.25, limit=1, fft_size=512)
    following = spectral_windows(selected, window_seconds=0.25, offset=1, limit=1, fft_size=512)
    first = objects(page["items"])[0]
    second = objects(following["items"])[0]
    assert page["total"] == 2 and page["next_offset"] == 1
    assert following["next_offset"] is None
    assert first["start_frame"] == 8192 and first["end_frame"] == 10240
    assert second["start_seconds"] == 1.25 and second["end_seconds"] == 1.5
    assert first["centroid_hz"] == pytest.approx(256)
    assert second["centroid_hz"] == pytest.approx(2048)


def test_transient_appears_only_in_its_window_and_remainder_is_not_dropped() -> None:
    audio = [0.0] * 1536
    audio[1250] = 1.0
    result = spectral_windows(source(audio), window_seconds=0.125, fft_size=512)
    items = objects(result["items"])
    assert items[0]["silence"] is True
    assert items[1]["silence"] is False
    assert items[1]["frame_count"] == 512
    assert items[1]["centroid_hz"] == pytest.approx(2048)
    whole = spectral(source(audio), fft_size=1024)
    assert whole["silence"] is False and whole["segment_count"] == 2


def test_final_non_aligned_segment_is_included_and_padding_is_explicit() -> None:
    audio = [0.0] * 1200
    audio[1150] = 1
    result = spectral(source(audio), fft_size=1024)
    assert result["segment_count"] == 2 and result["silence"] is False
    short = spectral(source(tone(512, 128)), fft_size=1024)
    assert short["zero_padded"] is True
    assert short["bin_spacing_hz"] == 8
    assert short["segment_resolution_hz"] == 64
    assert short["segment_frames"] == 128


def test_small_sample_count_and_low_sample_rate_are_defined() -> None:
    result = spectral(source([0.5], rate=20), fft_size=32)
    assert result["window"] == "single_sample"
    assert result["windowed_mean_square"] == pytest.approx(0.25)
    assert objects(result["bands"])[0]["low_hz"] == 0
    assert objects(result["bands"])[0]["high_hz"] == 10


@pytest.mark.parametrize("size", [0, 31, 127, 32768, True])
def test_invalid_fft_size_fails(size: int) -> None:
    with pytest.raises(ValueError, match="fft_size"):
        spectral(source(tone(512)), fft_size=size)


@pytest.mark.parametrize("edges", [(), (0,), (0, 0), (-1, 4000), (0, 5000), (0, float("nan"))])
def test_invalid_band_edges_fail(edges: tuple[float, ...]) -> None:
    with pytest.raises(ValueError, match="[Bb]and"):
        spectral(source(tone(512)), band_edges_hz=edges)


@pytest.mark.parametrize("fraction", [0.0, 1.1, float("nan"), float("inf"), True])
def test_invalid_rolloff_fails(fraction: float) -> None:
    with pytest.raises(ValueError, match="rolloff"):
        spectral(source(tone(512)), rolloff_fraction=fraction)


@pytest.mark.parametrize("seconds", [0.0, -1.0, 1e-10, float("inf"), float("nan"), True])
def test_invalid_temporal_window_fails(seconds: float) -> None:
    with pytest.raises(ValueError, match="window_seconds"):
        spectral_windows(source(tone(512)), window_seconds=seconds)


def test_pagination_bounds_and_empty_page() -> None:
    audio = source(tone(512))
    with pytest.raises(ValueError, match="offset"):
        spectral_windows(audio, offset=-1)
    with pytest.raises(ValueError, match="limit"):
        spectral_windows(audio, limit=65)
    assert spectral_windows(audio, offset=10)["items"] == []
    assert spectral_windows(audio, offset=10)["next_offset"] is None


def test_work_bound_rejects_before_transform_instead_of_truncating() -> None:
    audio = source([0.0] * 2_100_000)
    with pytest.raises(ValueError, match="work limit"):
        spectral(audio, fft_size=32)
    with pytest.raises(ValueError, match="work limit"):
        spectral_windows(audio, window_seconds=32, limit=64, fft_size=32)


def test_nonfinite_or_overflow_samples_cannot_escape_as_json_nan() -> None:
    for value in (float("inf"), float("nan"), 1e300):
        with pytest.raises(ValueError, match="nonfinite|numeric range"):
            spectral(source([value] * 32), fft_size=32)
