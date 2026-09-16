"""Offline audio and symbolic analysis. No FL meters, rendering, or project edits are performed."""

from collections.abc import Mapping, Sequence
from pathlib import Path

from .amplitude import AmplitudeAnalysis
from .audio import AudioSource, from_pcm, load_wav
from .bands import DEFAULT_BANDS, Band, band_energy, compare_bands, masking_report
from .bands import span_frames as _span_frames
from .bars import (
    SCAN_BANDS,
    _Scan,
    _scan,
    _scan_page,
    describe_sections,
    describe_sections_with,
    scan_bars,
    scan_settings,
    transition,
)
from .density import note_density, tick_range_seconds
from .describe import (
    AudioComparison,
    AudioDescription,
    SampleTable,
    compare_audio,
    default_cache_dir,
    describe_audio,
    describe_samples,
)
from .loudness import Loudness
from .pitch import _page, _Track, _track, _validate, pitch_track
from .spectral import spectral, spectral_windows


class AudioAnalysis(AmplitudeAnalysis):
    """A local analysis object; return summary/pages to MCP, not this object or raw sample arrays."""

    def __init__(self, source: AudioSource) -> None:
        super().__init__(source)
        self._scans: dict[tuple[object, ...], _Scan] = {}
        self._tracks: dict[tuple[object, ...], _Track] = {}

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

    def scan_bars(self, bpm: float, *, beats_per_bar: int = 4, bands: Sequence[Band] = SCAN_BANDS,
                  step_db: float = 6.0, start_bar: int = 1, grid_offset_seconds: float = 0.0,
                  offset: int = 0, limit: int = 128) -> dict[str, object]:
        """Per-bar level/band scan with step flags; the scan is cached so pages are free."""
        grid, specs, step = scan_settings(self.source, bpm, beats_per_bar, bands, step_db, start_bar,
                                          grid_offset_seconds)
        key = (grid, specs, step)
        scan = self._scans.get(key)
        if scan is None:
            scan = self._scans[key] = _scan(self.source, grid, specs, step)
        return _scan_page(self.source, scan, offset, limit)

    def band_energy(self, bands: Sequence[Band] = DEFAULT_BANDS, *, start_seconds: float | None = None,
                    end_seconds: float | None = None) -> dict[str, object]:
        return band_energy(self.source, bands, start_seconds=start_seconds, end_seconds=end_seconds)

    def describe_sections(self, bpm: float, sections: Mapping[str, Sequence[int]], *, beats_per_bar: int = 4,
                          start_bar: int = 1, grid_offset_seconds: float = 0.0,
                          bands: Sequence[Band] = DEFAULT_BANDS, loudness: bool = True) -> dict[str, object]:
        """Per-section levels, crest, centroid, width, bands and LUFS; loudness state is cached."""
        if loudness and len(self.source.channels) <= 2 and self._loudness is None:
            self._check_extended_work(True, False)
            self._loudness = Loudness(self.source)
        return describe_sections_with(self.source, bpm, sections, beats_per_bar=beats_per_bar,
                                      start_bar=start_bar, grid_offset_seconds=grid_offset_seconds, bands=bands,
                                      meter=self._loudness if loudness else None, loudness=loudness)

    def pitch_track(self, *, start_seconds: float | None = None, end_seconds: float | None = None,
                    hop_seconds: float = 0.05, fmin: float = 30.0, fmax: float = 2000.0,
                    min_confidence: float = 0.5, offset: int = 0, limit: int = 64) -> dict[str, object]:
        """Paged monophonic f0 frames and glide summary; the track is cached so pages are free."""
        hop, low, high, confidence = _validate(self.source, hop_seconds, fmin, fmax, min_confidence)
        begin, end = _span_frames(self.source, start_seconds, end_seconds)
        key = (begin, end, hop, low, high, confidence)
        track = self._tracks.get(key)
        if track is None:
            track = self._tracks[key] = _track(self.source, begin, end, hop, low, high, confidence)
        return _page(self.source, track, offset, limit)

    def compare_bands(self, other: object, bands: Sequence[Band] = DEFAULT_BANDS, *,
                      start_seconds: float | None = None, end_seconds: float | None = None) -> dict[str, object]:
        """Band deltas of ``other`` (b) minus this audio (a) over the same window."""
        return compare_bands(self.source, other, bands, start_seconds=start_seconds, end_seconds=end_seconds)

    def masking_report(self, other: object, bands: Sequence[Band] = DEFAULT_BANDS, *,
                       start_seconds: float | None = None, end_seconds: float | None = None,
                       clash_threshold_db: float = 6.0, floor_db: float = -60.0) -> dict[str, object]:
        return masking_report(self.source, other, bands, start_seconds=start_seconds, end_seconds=end_seconds,
                              clash_threshold_db=clash_threshold_db, floor_db=floor_db)

    def transition(self, other: object, bpm: float, bar: int, *, window_seconds: float = 0.43,
                   beats_per_bar: int = 4, start_bar: int = 1, grid_offset_seconds: float = 0.0) -> dict[str, object]:
        """Windows before/after a bar line in this audio (a) and ``other`` (b)."""
        return transition(self.source, other, bpm, bar, window_seconds=window_seconds, beats_per_bar=beats_per_bar,
                          start_bar=start_bar, grid_offset_seconds=grid_offset_seconds)

    def describe(self, *, bpm: float | None = None, ppq: int | None = None, start_bar: int | None = None,
                 beats_per_bar: int = 4, detail: str = "normal") -> AudioDescription:
        """An agent-readable description (``.text``) of the selected audio; see ``describe_audio``."""
        return describe_audio(self.source, bpm=bpm, ppq=ppq, start_bar=start_bar, beats_per_bar=beats_per_bar,
                              detail=detail)


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
    compare_bands = staticmethod(compare_bands)
    masking_report = staticmethod(masking_report)
    transition = staticmethod(transition)
    describe = staticmethod(describe_audio)
    compare = staticmethod(compare_audio)
    describe_samples = staticmethod(describe_samples)


__all__ = ["Analysis", "AudioAnalysis", "AudioComparison", "AudioDescription", "AudioSource", "SampleTable",
           "band_energy", "compare_audio", "compare_bands", "default_cache_dir", "describe_audio",
           "describe_samples", "describe_sections", "from_pcm", "load_wav", "masking_report", "note_density",
           "pitch_track", "scan_bars", "tick_range_seconds", "transition"]
