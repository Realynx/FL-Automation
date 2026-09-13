"""Finite normalized PCM, explicit frame ranges, and a bounded RIFF WAV reader."""

from __future__ import annotations

import math
import struct
from array import array
from collections.abc import Sequence
from dataclasses import dataclass
from pathlib import Path
from typing import BinaryIO

MAX_SAMPLES = 32_000_000


@dataclass(frozen=True)
class AudioSource:
    sample_rate: int
    channels: tuple[array[float], ...]
    source: str
    source_kind: str
    origin_frame: int
    requested_start_seconds: float
    requested_end_seconds: float | None
    total_source_frames: int
    sample_format: str

    @property
    def frame_count(self) -> int:
        return len(self.channels[0])

    @property
    def start_seconds(self) -> float:
        return self.origin_frame / self.sample_rate

    @property
    def end_seconds(self) -> float:
        return (self.origin_frame + self.frame_count) / self.sample_rate

    def provenance(self) -> dict[str, object]:
        return {"source": self.source, "source_kind": self.source_kind,
                "sample_rate": self.sample_rate, "channel_count": len(self.channels),
                "sample_format": self.sample_format, "source_frames": self.total_source_frames,
                "requested_start_seconds": self.requested_start_seconds,
                "requested_end_seconds": self.requested_end_seconds,
                "start_frame": self.origin_frame, "end_frame": self.origin_frame + self.frame_count,
                "start_seconds": self.start_seconds, "end_seconds": self.end_seconds,
                "rounding": "floor start, ceil exclusive end, clipped to source duration"}


def finite(value: float, name: str, *, positive: bool = False) -> float:
    if isinstance(value, bool) or not isinstance(value, (int, float)) or not math.isfinite(value):
        raise ValueError(f"{name} must be a finite number.")
    if positive and value <= 0:
        raise ValueError(f"{name} must be positive.")
    return float(value)


def frame_bounds(frames: int, sample_rate: int, start_seconds: float,
                 end_seconds: float | None) -> tuple[int, int]:
    start = finite(start_seconds, "start_seconds")
    end = frames / sample_rate if end_seconds is None else finite(end_seconds, "end_seconds")
    if start < 0 or end <= start or start >= frames / sample_rate:
        raise ValueError("Audio range must be nonempty, nonnegative, and start inside the source.")
    return math.floor(start * sample_rate), min(frames, math.ceil(min(end, frames / sample_rate) * sample_rate))


def from_pcm(channels: Sequence[Sequence[float]], sample_rate: int, *, source: str = "provided PCM",
             source_kind: str = "provided_pcm", start_seconds: float = 0,
             end_seconds: float | None = None) -> AudioSource:
    """Copy planar normalized PCM. Values above full scale are retained, never clipped."""
    if isinstance(sample_rate, bool) or not isinstance(sample_rate, int) or not 1 <= sample_rate <= 768000:
        raise ValueError("sample_rate must be an integer in 1..768000.")
    if not 1 <= len(channels) <= 32 or any(len(c) != len(channels[0]) for c in channels):
        raise ValueError("Provide 1..32 channels with equal frame counts.")
    begin, end = frame_bounds(len(channels[0]), sample_rate, start_seconds, end_seconds)
    _check_size(end - begin, len(channels))
    copied = tuple(array("d", c[begin:end]) for c in channels)
    _check_samples(copied)
    return AudioSource(sample_rate, copied, str(source), str(source_kind), begin, start_seconds,
                       end_seconds, len(channels[0]), "normalized_float64")


def _check_size(frames: int, channels: int) -> None:
    if frames * channels > MAX_SAMPLES:
        raise ValueError("Selected audio exceeds 32 million samples; select a smaller time range.")


def _check_samples(channels: tuple[array[float], ...]) -> None:
    if any(not math.isfinite(value) or abs(value) > 1e12 for channel in channels for value in channel):
        raise ValueError("PCM contains nonfinite or implausibly large samples (absolute limit 1e12).")


def _exact(stream: BinaryIO, count: int) -> bytes:
    data = stream.read(count)
    if len(data) != count:
        raise ValueError("Truncated WAV data.")
    return data


def _chunks(stream: BinaryIO, file_size: int) -> tuple[bytes, int, int]:
    header = _exact(stream, 12)
    if header[:4] != b"RIFF" or header[8:] != b"WAVE":
        raise ValueError("Expected little-endian RIFF WAVE; RF64/compressed audio is not supported.")
    riff_end = struct.unpack_from("<I", header, 4)[0] + 8
    if not 12 <= riff_end <= file_size:
        raise ValueError("Invalid WAV RIFF length.")
    fmt, offset, size = b"", -1, -1
    while stream.tell() + 8 <= riff_end:
        kind, length = struct.unpack("<4sI", _exact(stream, 8))
        position = stream.tell()
        if position + length > riff_end:
            raise ValueError("Truncated WAV chunk.")
        if kind == b"fmt ":
            if fmt or not 16 <= length <= 65536:
                raise ValueError("Invalid or duplicate WAV format chunk.")
            fmt = _exact(stream, length)
        elif kind == b"data":
            if offset >= 0:
                raise ValueError("Multiple WAV data chunks are unsupported.")
            offset, size = position, length
        stream.seek(position + length + (length % 2))
    if not fmt or offset < 0:
        raise ValueError("WAV format or data chunk is missing.")
    return fmt, offset, size


def _format(fmt: bytes) -> tuple[int, int, int, int, str]:
    tag, channels, rate, byte_rate, align, bits = struct.unpack_from("<HHIIHH", fmt)
    if tag == 0xFFFE:
        if len(fmt) < 40 or struct.unpack_from("<H", fmt, 16)[0] < 22:
            raise ValueError("Truncated extensible WAV format.")
        valid = struct.unpack_from("<H", fmt, 18)[0]
        guid = fmt[24:40]
        if guid[4:] != bytes.fromhex("00001000800000aa00389b71") or valid not in (0, bits):
            raise ValueError("Unsupported extensible WAV subformat or valid-bit alignment.")
        tag = struct.unpack_from("<I", guid)[0]
    if not 1 <= channels <= 32 or not 1 <= rate <= 768000 or bits % 8:
        raise ValueError("Invalid WAV channel count, rate, or sample width.")
    supported = (tag == 1 and bits in (8, 16, 24, 32)) or (tag == 3 and bits in (32, 64))
    if not supported or align != channels * (bits // 8) or byte_rate != rate * align:
        raise ValueError("Unsupported PCM/float WAV encoding or inconsistent frame alignment.")
    return tag, channels, rate, align, f"{'pcm' if tag == 1 else 'float'}{bits}le"


def _decode(data: bytes, tag: int, width: int, channels: tuple[array[float], ...]) -> None:
    count = len(channels)
    for i, position in enumerate(range(0, len(data), width)):
        raw = data[position:position + width]
        if tag == 3:
            value = struct.unpack("<f" if width == 4 else "<d", raw)[0]
        elif width == 1:
            value = (raw[0] - 128) / 128
        else:
            value = int.from_bytes(raw, "little", signed=True) / (2 ** (width * 8 - 1))
        channels[i % count].append(value)


def load_wav(path: str | Path, *, start_seconds: float = 0, end_seconds: float | None = None,
             source_kind: str = "audio_file") -> AudioSource:
    """Read PCM8/16/24/32 or IEEE float32/64 WAV; source attribution is supplied by the caller."""
    target = Path(path).expanduser().resolve(strict=True)
    with target.open("rb") as stream:
        fmt, offset, size = _chunks(stream, target.stat().st_size)
        tag, count, rate, align, label = _format(fmt)
        if size % align:
            raise ValueError("WAV data ends in a partial audio frame.")
        frames = size // align
        begin, end = frame_bounds(frames, rate, start_seconds, end_seconds)
        _check_size(end - begin, count)
        channels = tuple(array("d") for _ in range(count))
        stream.seek(offset + begin * align)
        remaining = end - begin
        while remaining:
            chunk = min(remaining, 8192)
            _decode(_exact(stream, chunk * align), tag, align // count, channels)
            remaining -= chunk
    _check_samples(channels)
    return AudioSource(rate, channels, str(target), str(source_kind), begin, start_seconds, end_seconds,
                       frames, label)
