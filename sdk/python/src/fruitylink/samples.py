"""Sample files behind channel-rack channels, described for an agent.

FL Studio exposes no operation that reads the file loaded into a built-in
Sampler channel, so this object remembers the paths that pass through
``fl.channels.add_sample`` and ``Channel.replace_sample`` in this session and
otherwise asks for an explicit ``path``. Describing never touches the project.
"""

from __future__ import annotations

from collections.abc import Iterable
from pathlib import Path
from typing import Any

from ._collection import checked_index
from .analysis import (
    AudioComparison,
    AudioDescription,
    SampleTable,
    compare_audio,
    describe_audio,
    describe_samples,
)
from .models import Page, SampleInfo
from .operations import Operations

PAGE_LIMIT = 512
"""Largest ``limit`` one ``query_samples`` page accepts (the host's shared query-page cap)."""


class Samples:
    def __init__(self, ops: Operations, registry: dict[int, str]) -> None:
        self._ops = ops
        self._registry = registry

    def query(self, filter: str | None = None, *, offset: int = 0, limit: int = 50) -> Page[SampleInfo]:
        """One structured page of installed samples, instead of parsing the ``list_samples`` text.

        ``filter`` is a case-insensitive substring of the full path (``"kick"``, ``"Clap"``,
        ``"Hats\\Closed"``). Each ``SampleInfo.entry`` is the verbatim ``[P]``/``[U]`` string to hand to
        ``fl.channels.add_sample`` or ``Channel.replace_sample``, so finding kick/clap/hat paths is one
        call rather than three rounds of reading a capped listing.

        Unlike the note and clip pages, ``offset`` and ``Page.total`` count MATCHED samples, not raw
        slots: filtering happens during the directory scan. Continue with the returned ``next_offset``,
        keeping the same filter and limit. The host indexes at most 10000 matches per call and every page
        re-scans the roots, so narrow the filter rather than paging through a whole library.
        ``fl.plugins.samples_text()`` still returns FL's original free-text listing.
        """
        return self._ops.query_samples(filter=filter, offset=checked_index(offset),
                                       limit=checked_index(limit, minimum=1, maximum=PAGE_LIMIT))

    def register(self, channel: int, path: str | Path) -> None:
        """Remember the sample file a channel plays, for channels loaded outside this session."""
        self._registry[checked_index(channel)] = str(path)

    def path_of(self, channel: int) -> str | None:
        """The last sample path this session loaded into the channel, or None when unknown."""
        return self._registry.get(checked_index(channel))

    def known(self) -> dict[int, str]:
        return dict(self._registry)

    def describe(self, channel: int, *, path: str | Path | None = None, **options: Any) -> AudioDescription:
        """Describe the channel's sample (``.text`` for a model); ``path`` overrides or supplies the file.

        Options are those of ``describe_audio`` (``bpm``, ``ppq``, ``start_bar``, ``detail``).
        """
        target = str(path) if path is not None else self.path_of(channel)
        if target is None:
            raise LookupError(f"Channel {channel} has no sample path known to this session; FL does not expose "
                              "the Sampler's file, so pass path=... or load it through fl.channels.add_sample.")
        return describe_audio(target, **options)

    def compare(self, a: int | str | Path, b: int | str | Path, **options: Any) -> AudioComparison:
        """A/B two samples given as channel indices or paths; b minus a."""
        return compare_audio(self._resolve(a), self._resolve(b), **options)

    def browse(self, paths: Iterable[str | Path], *, cache_dir: str | Path | None = None,
               detail: str = "brief", bpm: float | None = None) -> SampleTable:
        """One-line-per-file table for many candidates, cached; see ``describe_samples``."""
        return describe_samples(paths, cache_dir=cache_dir, detail=detail, bpm=bpm)

    def _resolve(self, value: int | str | Path) -> str:
        if isinstance(value, int) and not isinstance(value, bool):
            target = self.path_of(value)
            if target is None:
                raise LookupError(f"Channel {value} has no sample path known to this session; pass a path.")
            return target
        return str(value)
