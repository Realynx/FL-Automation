"""Compact, evidence-first descriptions of known preset audition audio."""

from __future__ import annotations

import math
from collections.abc import Sequence
from pathlib import Path
from typing import Any, cast

from fruitylink.analysis import Analysis, AudioAnalysis

_MAX_AUDITION_SECONDS = 15.0
_MAX_AUDITION_SAMPLES = 2_000_000


def describe_wav(path: str | Path, *, start_seconds: float = 0,
                 end_seconds: float | None = None,
                 preset_name: str | None = None) -> dict[str, object]:
    """Describe a WAV known by the caller to be one preset audition.

    The result reports measured audio traits and rule evidence. It does not
    infer oscillator waveforms or prove that the file contains only Serum.
    """
    audio = Analysis.wav(path, start_seconds=start_seconds, end_seconds=end_seconds,
                         source_kind="caller_attributed_serum_audition")
    return describe_audition(audio, preset_name=preset_name)


def describe_audition(audio: AudioAnalysis, *, preset_name: str | None = None) -> dict[str, object]:
    """Return bounded descriptors suitable for compact JSON tool results."""
    source = audio.source
    duration = source.frame_count / source.sample_rate
    if duration > _MAX_AUDITION_SECONDS:
        raise ValueError("A Serum audition may be at most 15 seconds; select a shorter WAV range.")
    if source.frame_count * len(source.channels) > _MAX_AUDITION_SAMPLES:
        raise ValueError("A Serum audition may contain at most 2 million scalar samples; select a shorter range.")

    amplitude = audio.summary()
    spectrum = audio.spectral(fft_size=_fft_size(source.frame_count))
    envelope = _envelope(source.channels, source.sample_rate)
    spatial = _spatial(source.channels)
    traits = _traits(amplitude, spectrum, envelope, spatial)
    return {
        "schema": "fruitylink-serum-audition-description/1",
        "provenance": source.provenance(),
        "attribution": {
            "source": "caller-supplied audio",
            "preset_name": preset_name,
            "instrument_identity": "unverified",
        },
        "descriptors": {
            "amplitude": amplitude["measurements"],
            "spectrum": spectrum,
            "envelope": envelope,
            "spatial": spatial,
        },
        "traits": traits,
        "limitations": [
            "Traits are deterministic rules over measured audio, not a trained classifier.",
            "Oscillator waveform, preset architecture, and musical pitch are not inferred.",
            "Results depend on the audition note, velocity, duration, effects, and render settings.",
        ],
    }


def _fft_size(frames: int) -> int:
    size = 32
    while size < min(frames, 4096):
        size *= 2
    return size


def _envelope(channels: Sequence[Sequence[float]], rate: int) -> dict[str, object]:
    width = max(1, round(rate * 0.01))
    frames = len(channels[0])
    windows: list[tuple[float, int]] = []
    for start in range(0, frames, width):
        end = min(frames, start + width)
        count = (end - start) * len(channels)
        power = math.fsum(value * value for channel in channels for value in channel[start:end]) / count
        windows.append((math.sqrt(power), end - start))
    rms = [value for value, _ in windows]
    peak = max(rms, default=0.0)
    if peak == 0:
        return {
            "window_seconds": width / rate, "window_count": len(rms),
            "time_to_90_percent_peak_from_selection_seconds": None,
            "peak_window_seconds": None, "early_to_late_db": None,
        }
    peak_index = rms.index(peak)
    threshold = peak * 0.9
    attack_index = next((index for index, value in enumerate(rms) if value >= threshold), None)
    early = windows[:max(1, round(0.1 * rate / width))]
    late = windows[-max(1, round(0.2 * rate / width)):]
    early_rms = _weighted_rms(early)
    late_rms = _weighted_rms(late)
    return {
        "window_seconds": width / rate,
        "window_count": len(rms),
        "peak_window_seconds": peak_index * width / rate,
        "time_to_90_percent_peak_from_selection_seconds": (
            None if attack_index is None else attack_index * width / rate),
        "early_to_late_db": _ratio_db(early_rms, late_rms),
    }


def _spatial(channels: Sequence[Sequence[float]]) -> dict[str, object]:
    if len(channels) != 2:
        return {"available": False, "reason": "requires stereo audio"}
    left, right = channels
    mid_power = math.fsum(((left_value + right_value) * 0.5) ** 2
                          for left_value, right_value in zip(left, right))
    side_power = math.fsum(((left_value - right_value) * 0.5) ** 2
                           for left_value, right_value in zip(left, right))
    left_power = math.fsum(value * value for value in left)
    right_power = math.fsum(value * value for value in right)
    cross = math.fsum(left_value * right_value for left_value, right_value in zip(left, right))
    correlation = cross / math.sqrt(left_power * right_power) if left_power and right_power else None
    return {
        "available": True,
        "side_to_mid_db": _ratio_db(math.sqrt(side_power), math.sqrt(mid_power)),
        "mid_silent": mid_power == 0 and side_power > 0,
        "left_to_right_db": _ratio_db(math.sqrt(left_power), math.sqrt(right_power)),
        "dominant_channel": (
            "left" if left_power > 0 and right_power == 0 else
            "right" if right_power > 0 and left_power == 0 else None),
        "correlation": correlation,
        "interpretation": "energy, balance, and correlation evidence; no stereo-width verdict is inferred",
    }


def _ratio_db(numerator: float, denominator: float) -> float | None:
    if numerator <= 0 or denominator <= 0:
        return None
    return 20 * math.log10(numerator / denominator)


def _weighted_rms(windows: Sequence[tuple[float, int]]) -> float:
    frames = sum(count for _, count in windows)
    return math.sqrt(math.fsum(value * value * count for value, count in windows) / frames)


def _traits(amplitude: dict[str, object], spectrum: dict[str, object],
            envelope: dict[str, object], spatial: dict[str, object]) -> list[dict[str, object]]:
    measurements = cast(dict[str, Any], amplitude["measurements"])
    channels = cast(list[dict[str, Any]], measurements["channels"])
    silence_threshold = float(measurements["occupancy_threshold_dbfs"])
    crests = [float(channel["crest_db"]) for channel in channels
              if channel["crest_db"] is not None and channel["rms_dbfs"] is not None
              and float(channel["rms_dbfs"]) >= silence_threshold]
    crest = max(crests) if crests else None
    centroid = spectrum.get("centroid_hz")
    early_late = envelope["early_to_late_db"]
    side_mid = spatial.get("side_to_mid_db")
    mid_silent = spatial.get("mid_silent") is True
    balance = spatial.get("left_to_right_db")
    dominant_channel = spatial.get("dominant_channel")
    candidates = [
        _trait("transient", isinstance(crest, float) and crest >= 12,
               {"crest_db": crest, "threshold": ">= 12 dB",
                "channel_minimum_rms_dbfs": silence_threshold}),
        _trait("decaying", isinstance(early_late, float) and early_late >= 8,
               {"early_to_late_db": early_late, "threshold": ">= 8 dB"}),
        _trait("bright", isinstance(centroid, (int, float)) and centroid >= 3000,
               {"spectral_centroid_hz": centroid, "threshold": ">= 3000 Hz"}),
        _trait("side_energy_present", mid_silent or isinstance(side_mid, float) and side_mid >= -12,
               {"side_to_mid_db": side_mid, "mid_silent": mid_silent,
                "threshold": ">= -12 dB, or nonzero side with silent mid"}),
        _trait("stereo_imbalanced", dominant_channel is not None or
               isinstance(balance, float) and abs(balance) >= 6,
               {"left_to_right_db": balance, "dominant_channel": dominant_channel,
                "threshold": "absolute value >= 6 dB, or signal in only one channel"}),
    ]
    return [trait for trait in candidates if trait["matched"]]


def _trait(name: str, matched: bool, evidence: dict[str, object]) -> dict[str, object]:
    return {"name": name, "matched": matched, "method": "fixed_rule_v1", "evidence": evidence}
