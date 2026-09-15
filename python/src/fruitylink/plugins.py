"""Plugin catalogue, loaded-plugin parameter enumeration and parameter writes."""

import math
import re
import struct
import time
from collections.abc import Callable, Iterable, Iterator
from dataclasses import dataclass, replace
from typing import Any

from ._collection import checked_index, iter_pages
from .errors import ProtocolError
from .models import Page, PluginParameterInfo
from .operations import Operations
from .scales import ParameterScale, add_scale, known_plugins, scale_for, scales_for

__all__ = [
    "NORMALIZED_TOLERANCE", "PAGE_LIMIT", "ParameterScale", "Parameters", "Plugins", "VerifiedWrite", "add_scale",
    "clean_parameter_name", "known_plugins", "normalized_from_raw", "scale_for", "scales_for", "unique_names",
]

NORMALIZED_TOLERANCE = 1.0 / (1 << 20)
"""Largest |readback - written| still counted as the same normalized value: the host stores writes as
norm * 2^30 fixed point and plugins report float32 bits, so exact bit equality is not guaranteed."""

PAGE_LIMIT = 512
"""Largest ``limit`` one ``query_plugin_parameters`` page accepts (the host bound). ``all()``, ``list()``
and ``iter()`` page automatically; Serum 2's 4240 slots take nine pages inside one request."""

_HINT_BLOCK = re.compile(r"\^\^.*?\^")
_HINT_CODE = re.compile(r"\^.")
_INDEX_SUFFIX = re.compile(r"^(?P<name>.*?)\s*\[(?P<index>\d+)\]$")


def clean_parameter_name(raw: str) -> str:
    """Strip FL's hint-formatting codes from a parameter name.

    Stock (non-wrapper) effects report names such as ``"^b^aWet level"``: ``^`` + one character is a
    formatting code (bold, icon) and ``^^text^`` is an inline hint block, e.g. Vintage Chorus band 0
    ``"^b^a^^(shift-click for I + II) ^Mode"`` -> ``"Mode"``. Wrapper (VST) names have no codes and
    pass through unchanged; a lone trailing ``^`` is kept.
    """
    text = _HINT_BLOCK.sub("", raw)
    text = _HINT_CODE.sub("", text)
    return text.strip() or raw


def unique_names(items: Iterable[PluginParameterInfo]) -> tuple[PluginParameterInfo, ...]:
    """Copies of ``items`` whose duplicated names carry the slot index: ``"Distortion [18]"``.

    Fruity Delay 3 exposes three parameters named ``Distortion`` (18, 19, 20); the plugin gives no
    section name, so the index is the only stable disambiguator. ``find()`` accepts the same
    ``"Name [index]"`` form, and unique names are left untouched.
    """
    rows = tuple(items)
    counts: dict[str, int] = {}
    for item in rows:
        counts[item.name] = counts.get(item.name, 0) + 1
    return tuple(replace(item, name=f"{item.name} [{item.index}]") if counts[item.name] > 1 else item for item in rows)


def normalized_from_raw(raw: int) -> float | None:
    """Decode a hosted plugin's raw value as the normalized 0..1 float it carries, or None.

    VST parameters report their value as float32 bits. Native FL plugins (Fruity Delay 3,
    Fruity Reeverb 2, ...) report plain integers instead: a switch reads raw 0 or 1 (live
    evidence 2026-09-14: Fruity Delay 3 "Tempo sync" On reads ``rawValue 1, displayValue "On"``),
    and those two integers ARE the normalized value - the float32 bit patterns 0 and 1 are +0.0
    and a 1.4e-45 denormal, so no plugin can mean anything else by them. Every other native
    integer scale (0..65535 knobs) decodes to a denormal or an out-of-range float and is rejected
    so it never masquerades as 0.0.
    """
    if raw in (0, 1):   # also the bool a native switch may arrive as
        return float(raw)
    value = struct.unpack("<f", struct.pack("<i", raw))[0]
    if not math.isfinite(value) or not -NORMALIZED_TOLERANCE <= value <= 1 + NORMALIZED_TOLERANCE:
        return None
    if value != 0.0 and abs(value) < 1e-30:
        return None
    return float(value)


def _write_applied(raw_before: int, raw_after: int, value: float) -> bool:
    decoded = normalized_from_raw(raw_after)
    return (decoded is not None and abs(decoded - value) <= NORMALIZED_TOLERANCE) or raw_after != raw_before


@dataclass(frozen=True)
class VerifiedWrite:
    """Outcome of Parameters.set_verified: the written index and the readback evidence.

    ``verified`` is the raw-value oracle: the readback decodes to the written normalized value
    (within NORMALIZED_TOLERANCE), differs from the pre-write raw value, or the slot already held
    the value before the write (then ``unchanged`` is True: nothing needed to move, the write is in
    place). A native FL switch reports raw 0/1, which decodes to the normalized value, so an
    already-on switch takes the ``unchanged`` path; a wider native integer scale carries no
    readable value, and only its movement can be checked.
    ``display_changed`` reports whether the plugin's display string moved away from
    ``display_before``; False can mean the display still lags the write (see set_verified), that
    the new value shares a label, or simply that the slot was already there (``unchanged``).
    """

    index: int
    name: str
    value: float
    raw_before: int
    raw_after: int
    display_before: str
    display_after: str
    verified: bool
    attempts: int
    normalized_after: float | None = None
    display_changed: bool = False
    unchanged: bool = False


class Parameters:
    def __init__(self, ops: Operations, channel_or_track: int, slot: int = -1) -> None:
        self._ops = ops
        self.channel_or_track = checked_index(channel_or_track)
        self.slot = checked_index(slot, minimum=-1, maximum=9)

    def list(self, filter: str | None = None) -> tuple[PluginParameterInfo, ...]:
        """Read every matching parameter, paging automatically (PAGE_LIMIT slots per request).

        For bounded script results, use page(). ``all()`` is the same call under the name
        scripts reach for; ``unique_names(parameters.list())`` disambiguates duplicated names.
        """
        return tuple(self.iter(filter, page_size=PAGE_LIMIT))

    def all(self, filter: str | None = None, *, unique: bool = False) -> tuple[PluginParameterInfo, ...]:
        """Every parameter (optionally name-filtered) across all pages; with ``unique=True`` duplicated
        names carry their index (``"Distortion [18]"``). Equivalent to ``list()``; exists because
        ``page(limit=4240)`` is refused (the host page cap is PAGE_LIMIT = 512) and the loop over
        ``next_offset`` should not be every script's job."""
        rows = self.list(filter)
        return unique_names(rows) if unique else rows

    def page(self, filter: str | None = None, *, offset: int = 0,
             limit: int = 64) -> Page[PluginParameterInfo]:
        """Read one raw-slot page; an empty filtered page may have next_offset.

        The filter is a case-insensitive name substring. Limit (1..PAGE_LIMIT = 512, the
        host's cap) bounds scanned slots, not matching items or serialized bytes. Continue
        with the returned next_offset, retaining the same filter and limit, or let ``all()`` /
        ``list()`` / ``iter()`` page for you.
        """
        if type(limit) is int and limit > PAGE_LIMIT:
            raise IndexError(f"Index must be an integer >= 1 and <= {PAGE_LIMIT}: one page scans at most "
                             f"{PAGE_LIMIT} slots (host cap); use all(), list() or iter() to page automatically.")
        return self._ops.query_plugin_parameters(
            channel_or_track=self.channel_or_track, slot=self.slot, filter=filter,
            offset=checked_index(offset), limit=checked_index(limit, minimum=1, maximum=PAGE_LIMIT))

    def iter(self, filter: str | None = None, *, page_size: int = 64) -> Iterator[PluginParameterInfo]:
        """Fetch pages lazily. Stopping iteration prevents further remote reads."""
        checked_index(page_size, minimum=1, maximum=512)
        return iter_pages(lambda offset: self.page(filter, offset=offset, limit=page_size))

    def __iter__(self) -> Iterator[PluginParameterInfo]:
        return self.iter()

    def list_text(self, filter: str | None = None) -> str:
        return self._ops.list_plugin_params(channel_or_track=self.channel_or_track, slot=self.slot, filter=filter)

    def set(self, index: int, value: float) -> None:
        self._ops.set_plugin_param(channel_or_track=self.channel_or_track, slot=self.slot,
                                   param_index=checked_index(index), value=value)

    def set_named(self, name: str, value: float) -> int:
        """Set one exact, case-sensitive unique parameter name and return its index.

        Name resolution and the subsequent write are separate operations. Callers
        that need the resulting value should query it separately.
        """
        index = self.find(name).index
        self.set(index, value)
        return index

    def read(self, index: int) -> PluginParameterInfo:
        """Read exactly one parameter slot by index (one bounded native page)."""
        page = self.page(None, offset=checked_index(index), limit=1)
        if not page.items or page.items[0].index != index:
            raise LookupError(f"Parameter {index} does not exist ({page.total} total).")
        return page.items[0]

    def read_at(self, index: int, tick: int, *, settle: float = 0.3, attempts: int = 6, delay: float = 0.1,
                sleep: Callable[[float], None] = time.sleep) -> PluginParameterInfo:
        """Seek the song to ``tick`` and read one parameter once the host has applied automation there.

        A display read in the same request right after a seek returns the PREVIOUS position's
        automated value (live evidence, Parking Lot Moon 2026-09-14: -4/-4/-8 dB instead of
        -12/-8/-4 dB for three seeks; with 300 ms of settling every value was right). This waits for
        the playhead to settle, sleeps ``settle`` seconds and reads until two consecutive readings
        agree (see ``Transport.read_at``). Verify sub-beat envelopes from the point list instead: a
        stopped seek lands 14-20 ticks late.
        """
        from .project import Transport

        checked_index(index)
        return Transport(self._ops).read_at(tick, lambda: self.read(index), settle=settle, attempts=attempts,
                                            delay=delay, sleep=sleep)

    def display_at(self, index: int, tick: int, **options: Any) -> str:
        """``read_at(...).display_value``: the automated display string at ``tick``."""
        return self.read_at(index, tick, **options).display_value

    def set_verified(self, parameter: int | str, value: float, *, attempts: int = 6,
                     delay: float = 0.05, sleep: Callable[[float], None] = time.sleep,
                     settle_display: bool = True) -> VerifiedWrite:
        """Write one parameter, then read it back until the host reports the new value.

        Why this exists (live evidence, Ember Tides v006/v014, Serum 2 and Pro-L 2): the
        host's raw value (the wrapper's own getParamValue) reflects a bus write at once,
        but the display string is produced by the plugin instance, which only sees the
        change after FL has delivered it (audio-thread/idle sync). Read in the same
        request right after a write, the display can still show the previous value;
        the following request is always correct. No bridge-side call is known that
        forces that delivery, so this helper waits for it instead.

        It reads the slot before writing, writes once, then re-reads up to ``attempts``
        times, ``delay`` seconds apart, until the raw value decodes to the written
        normalized value (within NORMALIZED_TOLERANCE; compare values, never display
        strings) or differs from the pre-write raw value. With ``settle_display`` it
        keeps re-reading, within the same budget, until the display string also moved
        (skipped when the slot already held the value). A slot that already holds the
        value is reported ``verified=True, unchanged=True`` after one readback: the value is
        in place, so a batch summary must not count it as a failed write. That covers native
        FL switches, which report a plain 0/1 rather than float32 bits: writing 1.0 to a
        "Tempo sync" that already reads raw 1 returns after one readback (live evidence
        2026-09-14, Fruity Delay 3; before the 0/1 decode it burned every attempt and reported
        ``verified=False``). ``verified`` is False only when the raw value neither matched nor
        moved, which for a wider native integer scale (a 0..65535 knob, whose value the SDK
        cannot decode) is also what a write onto the value the slot already held looks like.
        ``display_changed`` is False when the display did not move in time, the new value
        shares the old label, or nothing had to change - read again in a later request before
        quoting a display.
        """
        if isinstance(value, bool) or not isinstance(value, (int, float)) or not 0 <= value <= 1:
            raise ValueError("Plugin parameter value must be a number within 0..1.")
        checked_index(attempts, minimum=1, maximum=100)
        if isinstance(delay, bool) or not isinstance(delay, (int, float)) or not 0 <= delay <= 5:
            raise ValueError("Readback delay must be 0..5 seconds.")
        index = self.find(parameter).index if isinstance(parameter, str) else checked_index(parameter)
        before = self.read(index)
        target = float(value)
        already = normalized_from_raw(before.raw_value)
        unchanged = already is not None and abs(already - target) <= NORMALIZED_TOLERANCE
        expect_display_change = settle_display and not unchanged
        self.set(index, target)
        after = before
        used = 0
        for used in range(1, attempts + 1):
            after = self.read(index)
            applied = unchanged or _write_applied(before.raw_value, after.raw_value, target)
            if applied and (after.display_value != before.display_value or not expect_display_change):
                break
            if used < attempts and delay > 0:
                sleep(delay)
        return VerifiedWrite(index, after.name, target, before.raw_value, after.raw_value,
                             before.display_value, after.display_value,
                             unchanged or _write_applied(before.raw_value, after.raw_value, target), used,
                             normalized_from_raw(after.raw_value),
                             after.display_value != before.display_value, unchanged)

    def find(self, name: str) -> PluginParameterInfo:
        """Find one exact, case-sensitive name; refuse duplicated names, listing their indices.

        Names are matched after FL's hint codes are stripped (``"Wet level"``, not
        ``"^b^aWet level"``; the raw form still matches). When several slots share the name
        (Fruity Delay 3: ``Distortion`` at 18, 19, 20) the error lists every index and the
        ``"Name [index]"`` form, which this method accepts: ``find("Distortion [19]")``.
        Native filtering avoids reading values for unrelated parameters; a unique match
        still requires checking later pages.
        """
        wanted, index = name, None
        suffix = _INDEX_SUFFIX.match(name)
        if suffix is not None:
            wanted, index = suffix.group("name"), int(suffix.group("index"))
        needle = clean_parameter_name(wanted)
        matches: list[PluginParameterInfo] = []
        offset: int | None = 0
        more = False
        while offset is not None:
            page = self.page(needle, offset=offset, limit=PAGE_LIMIT)
            matches.extend(item for item in page.items if item.name == needle or item.raw_name == wanted)
            more = page.next_offset is not None
            if index is not None and any(item.index == index for item in matches):
                break
            if index is None and len(matches) >= 2:
                break   # ambiguity is settled: do not scan the remaining pages
            if page.next_offset is not None and page.next_offset <= offset:
                raise ProtocolError("Query continuation did not advance; refusing an infinite page loop.")
            offset = page.next_offset
        if index is not None:
            for item in matches:
                if item.index == index:
                    return item
            raise LookupError(f"Expected parameter {needle!r} at index {index}; "
                              f"found it at {[item.index for item in matches] or 'no index'}.")
        if len(matches) == 1:
            return matches[0]
        if not matches:
            raise LookupError(f"Expected one parameter named {name!r}; found 0.")
        indices = ", ".join(str(item.index) for item in matches)
        count = f"at least {len(matches)}" if more else str(len(matches))
        raise LookupError(f"Expected one parameter named {name!r}; found {count} at indices {indices}. "
                          f"Address it as {needle!r} + ' [index]' (e.g. {needle + ' [' + str(matches[0].index) + ']'!r}) "
                          "or by index.")


class Plugins:
    def __init__(self, ops: Operations) -> None:
        self._ops = ops

    def available_text(self, *, effects: bool = False) -> str:
        return self._ops.list_available_plugins(effects=effects)

    def samples_text(self, filter: str | None = None) -> str:
        return self._ops.list_samples(filter=filter)

    def channel_parameters(self, channel: int) -> Parameters:
        return Parameters(self._ops, channel)

    def effect_parameters(self, track: int, slot: int) -> Parameters:
        return Parameters(self._ops, track, checked_index(slot, maximum=9))
