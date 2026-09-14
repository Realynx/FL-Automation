"""Plugin catalogue, loaded-plugin parameter enumeration and parameter writes."""

import math
import struct
import time
from collections.abc import Callable, Iterator
from dataclasses import dataclass

from ._collection import checked_index, iter_pages
from .models import Page, PluginParameterInfo
from .operations import Operations

NORMALIZED_TOLERANCE = 1.0 / (1 << 20)
"""Largest |readback - written| still counted as the same normalized value: the host stores writes as
norm * 2^30 fixed point and plugins report float32 bits, so exact bit equality is not guaranteed."""


def normalized_from_raw(raw: int) -> float | None:
    """Decode a hosted plugin's raw value as the normalized 0..1 float it carries, or None.

    VST parameters report their value as float32 bits; native FL plugin scales (plain integers)
    decode to denormals or out-of-range floats and are rejected so they never masquerade as 0.0.
    """
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
    (within NORMALIZED_TOLERANCE) or differs from the pre-write raw value. ``display_changed``
    reports whether the plugin's display string moved away from ``display_before``; False can mean
    the display still lags the write (see set_verified) or that the new value shares a label.
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


class Parameters:
    def __init__(self, ops: Operations, channel_or_track: int, slot: int = -1) -> None:
        self._ops = ops
        self.channel_or_track = checked_index(channel_or_track)
        self.slot = checked_index(slot, minimum=-1, maximum=9)

    def list(self, filter: str | None = None) -> tuple[PluginParameterInfo, ...]:
        """Read every matching parameter. For bounded script results, use page()."""
        return tuple(self.iter(filter, page_size=512))

    def page(self, filter: str | None = None, *, offset: int = 0,
             limit: int = 64) -> Page[PluginParameterInfo]:
        """Read one raw-slot page; an empty filtered page may have next_offset.

        The filter is a case-insensitive name substring. Limit (1..512) bounds
        scanned slots, not matching items or serialized bytes. Continue with the
        returned next_offset, retaining the same filter and limit.
        """
        return self._ops.query_plugin_parameters(
            channel_or_track=self.channel_or_track, slot=self.slot, filter=filter,
            offset=checked_index(offset), limit=checked_index(limit, minimum=1, maximum=512))

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
        (skipped when the slot already held the value). ``verified`` is False when the
        raw value never changed; ``display_changed`` is False when the display did not
        move in time or the new value shares the old label - read again in a later
        request before quoting a display string.
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
        expect_display_change = settle_display and not (
            already is not None and abs(already - target) <= NORMALIZED_TOLERANCE)
        self.set(index, target)
        after = before
        used = 0
        for used in range(1, attempts + 1):
            after = self.read(index)
            applied = _write_applied(before.raw_value, after.raw_value, target)
            if applied and (after.display_value != before.display_value or not expect_display_change):
                break
            if used < attempts and delay > 0:
                sleep(delay)
        return VerifiedWrite(index, after.name, target, before.raw_value, after.raw_value,
                             before.display_value, after.display_value,
                             _write_applied(before.raw_value, after.raw_value, target), used,
                             normalized_from_raw(after.raw_value),
                             after.display_value != before.display_value)

    def find(self, name: str) -> PluginParameterInfo:
        """Find one exact, case-sensitive name; reject duplicate names.

        Native filtering avoids reading values for unrelated parameters. A unique
        match still requires checking later pages; ambiguity stops at two matches.
        """
        match = None
        for item in self.iter(name, page_size=512):
            if item.name != name:
                continue
            if match is not None:
                raise LookupError(f"Expected one parameter named {name!r}; found at least 2.")
            match = item
        if match is None:
            raise LookupError(f"Expected one parameter named {name!r}; found 0.")
        return match


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
