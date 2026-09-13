"""Offline audio and symbolic analysis. No FL meters, rendering, or project edits are performed."""

from collections.abc import Sequence
from pathlib import Path

from .amplitude import AmplitudeAnalysis
from .audio import AudioSource, from_pcm, load_wav
from .density import note_density, tick_range_seconds
from .spectral import spectral, spectral_windows


class AudioAnalysis(AmplitudeAnalysis):
    """A local analysis object; return summary/pages to MCP, not this object or raw sample arrays."""

    def spectral(self, *, fft_size: int = 2048, band_edges_hz: Sequence[float] | None = None,
                 rolloff_fraction: float = 0.85, dominant_bins: int = 5) -> dict[str, object]:
        return dict(spectral(self.source, fft_size=fft_size, band_edges_hz=band_edges_hz,
                             rolloff_fraction=rolloff_fraction, dominant_bins=dominant_bins))

    def spectral_windows(self, *, window_seconds: float = 1, hop_seconds: float | None = None,
                         offset: int = 0, limit: int = 16, fft_size: int = 2048,
                         band_edges_hz: Sequence[float] | None = None,
                         rolloff_fraction: float = 0.85, dominant_bins: int = 5) -> dict[str, object]:
        return dict(spectral_windows(self.source, window_seconds=window_seconds, hop_seconds=hop_seconds,
                                     offset=offset, limit=limit, fft_size=fft_size, band_edges_hz=band_edges_hz,
                                     rolloff_fraction=rolloff_fraction, dominant_bins=dominant_bins))


class Analysis:
    """Analysis factories also available through Studio.analysis; no connection is required."""

    @staticmethod
    def wav(path: str | Path, *, start_seconds: float = 0, end_seconds: float | None = None,
            source_kind: str = "audio_file") -> AudioAnalysis:
        return AudioAnalysis(load_wav(path, start_seconds=start_seconds, end_seconds=end_seconds,
                                      source_kind=source_kind))

    @staticmethod
    def pcm(channels: Sequence[Sequence[float]], sample_rate: int, *, source: str = "provided PCM",
            source_kind: str = "provided_pcm", start_seconds: float = 0,
            end_seconds: float | None = None) -> AudioAnalysis:
        return AudioAnalysis(from_pcm(channels, sample_rate, source=source, source_kind=source_kind,
                                      start_seconds=start_seconds, end_seconds=end_seconds))

    note_density = staticmethod(note_density)
    tick_range_seconds = staticmethod(tick_range_seconds)


__all__ = ["Analysis", "AudioAnalysis", "AudioSource", "from_pcm", "load_wav", "note_density", "tick_range_seconds"]
