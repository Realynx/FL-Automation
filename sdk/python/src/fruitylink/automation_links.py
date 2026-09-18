"""Which controls automation clip channels own, and the plain writes that collide with them.

An FL automation clip is not merely an envelope that plays while its clip sits on the playlist. The
LINK lives on the automation CHANNEL, and FL writes the clip's initial value (the value it holds
before the clip starts) into the linked control every time playback starts. Deleting the playlist
clip does not remove the link, and FL exposes no channel delete the bridge can call, so the target
stays pinned for the rest of the project's life: a plain write to that control reads back correctly
and is then silently overridden by the audio engine on the next play.

Live evidence (FL 26.1.3.5570, 2026-09-17): a pump on ``mixer_volume(5)`` created automation channel
12 with a clip at bars 33-36; after ``fl.clips.delete(...)`` removed the clip, ``fl.mixer[5].volume =
6400`` read back 6400 while a master capture of bars 1-2 measured the same RMS as at raw 12800 - the
engine kept insert 5 at the automation's value. Routing the channel to insert 10 instead gave the
expected -12.44 dB.

Every SDK write to a control an automation clip can own therefore consults this index first and warns
(``AutomationLinkedWarning``); ``fruitylink.automation.Automation.release`` flattens the curve so the
pinned value becomes the value the caller wants. Building the index costs one ``query_channels`` plus
one ``get_channel_plugin`` per channel: a link whose playlist clip was deleted has no clip left to
find, so the whole rack has to be described. The result is therefore cached per connection until
``fl.automation.refresh()`` (or the next ``fl.automation.create`` / ``pump`` / ``duck`` / ``tile``,
which invalidate it), and ``linked="ignore"`` skips the lookup entirely.
"""

import re
import warnings
import weakref
from collections.abc import Sequence
from dataclasses import dataclass
from typing import Literal

from ._collection import iter_pages
from .automation_records import AutomationChannelInfo, AutomationTarget
from .errors import FruityLinkError
from .levels import CHANNEL_PAN_MAX, CHANNEL_VOLUME_MAX, MIXER_PAN_MAX, MIXER_VOLUME_MAX
from .models import ClipInfo
from .operations import Operations

LinkMode = Literal["warn", "raise", "ignore"]
LINK_MODES: tuple[str, ...] = ("warn", "raise", "ignore")
"""Accepted ``linked=`` values: warn (default), raise (``AutomationLinkedWarning``), ignore (no lookup)."""

_LINK_MARKER = ": automation clip -> "
_EVENT_ID = re.compile(r"event 0x([0-9a-fA-F]{1,8})")


class AutomationLinkedWarning(UserWarning):
    """A write reached a control an automation clip channel is linked to.

    The value is accepted and reads back, but FL reapplies the automation clip's initial value to the
    same control every time playback starts, so the write is not audible after the next play. Flatten
    the curve with ``fl.automation.release(channel)``, move the material to another insert/channel, or
    pass ``linked="ignore"`` when the override is intended. ``linked="raise"`` raises this class
    instead of warning (it is an ``Exception`` subclass, like every warning category).
    """


@dataclass
class _LinkCache:
    """Per-connection memory: the scanned links, and whether the host refused to describe them."""

    links: tuple[AutomationChannelInfo, ...] | None = None
    unavailable: bool = False


_caches: "weakref.WeakKeyDictionary[Operations, _LinkCache]" = weakref.WeakKeyDictionary()


def _cache(ops: Operations) -> _LinkCache:
    return _caches.setdefault(ops, _LinkCache())


def parse_automation_link(description: str) -> str | None:
    """The ``<targets>`` part of a ``get_channel_plugin`` line for an automation clip, else None."""
    if not isinstance(description, str):
        return None
    _, marker, targets = description.partition(_LINK_MARKER)
    return targets.strip() if marker else None


def target_text(target: AutomationTarget | None) -> str:
    """Short human form of a decoded target (``"mixer_volume 5"``, ``"insert 4 slot 0 parameter 48"``)."""
    if target is None:
        return "undecoded"
    if target.kind == "plugin_parameter":
        where = f"channel {target.index}" if target.slot < 0 else f"insert {target.index} slot {target.slot}"
        return f"{where} parameter {target.parameter}"
    return f"{target.kind} {target.index}"


def scan(ops: Operations, *, with_points: bool = True) -> tuple[AutomationChannelInfo, ...]:
    """Describe every automation clip channel in the rack: targets, event ids, points, placements.

    The inventory behind ``fl.automation.list()``. One ``query_clips`` pass plus one
    ``get_channel_plugin`` per channel, and one ``query_automation_points`` per automation channel
    unless ``with_points=False``.
    """
    clips: tuple[ClipInfo, ...] = tuple(iter_pages(lambda offset: ops.query_clips(offset=offset)))
    found: list[AutomationChannelInfo] = []
    for channel in ops.query_channels():
        link = parse_automation_link(ops.get_channel_plugin(channel=channel.index))
        if link is None:
            continue
        event_ids = tuple(int(match, 16) for match in _EVENT_ID.findall(link))
        targets = tuple(AutomationTarget.from_event_id(event_id) for event_id in event_ids)
        count = len(ops.query_automation_points(channel=channel.index)) if with_points else -1
        placed = tuple(clip for clip in clips
                       if clip.source_kind == "channel" and clip.source_index == channel.index)
        found.append(AutomationChannelInfo(channel.index, channel.name, link, event_ids, targets, count, placed))
    return tuple(found)


def index(ops: Operations) -> tuple[AutomationChannelInfo, ...]:
    """The cached link inventory (``point_count`` is -1: the cache never reads envelopes).

    Built on first use and kept until ``invalidate()``. Errors from the host propagate; the write
    guard swallows them instead of failing a write.
    """
    cache = _cache(ops)
    if cache.links is None:
        cache.links = scan(ops, with_points=False)
        cache.unavailable = False
    return cache.links


def invalidate(ops: Operations) -> None:
    """Drop the cached inventory (and any "the host cannot answer" memory) for this connection."""
    cache = _cache(ops)
    cache.links = None
    cache.unavailable = False


def links_to(ops: Operations, target: AutomationTarget) -> tuple[AutomationChannelInfo, ...]:
    """Every cached automation clip channel whose decoded target equals ``target``."""
    if not isinstance(target, AutomationTarget):
        raise TypeError("target must be an AutomationTarget.")
    return tuple(item for item in index(ops) if any(known == target for known in item.targets))


def target_of(ops: Operations, channel: int) -> AutomationTarget | None:
    """The decoded target of one automation clip channel from the cached inventory, or None."""
    return next((item.target for item in index(ops) if item.channel == channel), None)


def check(ops: Operations, target: AutomationTarget, *, linked: LinkMode = "warn",
          stacklevel: int = 3) -> tuple[str, ...]:
    """Warn (or raise) when an automation clip channel owns ``target``; returns the messages written.

    ``linked="ignore"`` skips the lookup and returns ``()``. The guard never fails a write of its own
    accord: a host that cannot answer ``query_channels`` / ``get_channel_plugin`` (an older build, or
    a project without the automation capability) is remembered as unavailable and the check is skipped
    until ``invalidate()``.
    """
    if linked not in LINK_MODES:
        raise ValueError('linked must be "warn", "raise" or "ignore".')
    if not isinstance(target, AutomationTarget):
        raise TypeError("target must be an AutomationTarget.")
    if linked == "ignore":
        return ()
    cache = _cache(ops)
    if cache.unavailable:
        return ()
    try:
        owners = links_to(ops, target)
    except (FruityLinkError, LookupError, ValueError):
        cache.unavailable = True   # an unreadable inventory must never fail the caller's write
        return ()
    if not owners:
        return ()
    message = linked_message(target, owners)
    if linked == "raise":
        raise AutomationLinkedWarning(message)
    warnings.warn(AutomationLinkedWarning(message), stacklevel=max(1, stacklevel))
    return (message,)


def linked_message(target: AutomationTarget, owners: Sequence[AutomationChannelInfo]) -> str:
    """The text ``check`` warns with: what is linked, by which channels, and how to release it."""
    who = ", ".join(f"channel {item.channel} {item.name!r}" for item in owners)
    first = owners[0].channel if owners else -1
    return (f"{target_text(target)} is linked to automation clip {who}: FL writes that clip's initial value "
            f"to this control every time playback starts, so this write is not audible after the next play. "
            f"Flatten the curve with fl.automation.release({first}), move the material to another "
            f"insert/channel, or pass linked='ignore'.")


def normalized_value(ops: Operations, target: AutomationTarget) -> float | None:
    """The target control's current value as the 0..1 an automation point carries, or None.

    Volume and pan are read through their native scales (mixer volume 0..16000, mixer pan the signed
    -6400..6400, channel volume and pan 0..12800); a plugin parameter through its decoded
    ``normalized``. None means the SDK cannot express the control as an automation value: FL's channel
    pitch (cents) has no documented normalized mapping, and a native plugin scale that is not float32
    bits does not decode (see ``fruitylink.plugins.normalized_from_raw``).
    """
    if not isinstance(target, AutomationTarget):
        raise TypeError("target must be an AutomationTarget.")
    if target.kind == "mixer_volume":
        return _clamped(ops.get_mixer_volume(track=target.index) / MIXER_VOLUME_MAX)
    if target.kind == "mixer_pan":
        return _clamped((ops.get_mixer_pan(track=target.index) + MIXER_PAN_MAX) / (2 * MIXER_PAN_MAX))
    if target.kind == "channel_volume":
        return _clamped(ops.get_channel_volume(channel=target.index) / CHANNEL_VOLUME_MAX)
    if target.kind == "channel_pan":
        return _clamped(ops.get_channel_pan(channel=target.index) / CHANNEL_PAN_MAX)
    if target.kind == "plugin_parameter":
        page = ops.query_plugin_parameters(channel_or_track=target.index, slot=target.slot,
                                           offset=target.parameter, limit=1)
        item = next((row for row in page.items if row.index == target.parameter), None)
        return None if item is None or item.normalized is None else _clamped(item.normalized)
    return None


def _clamped(value: float) -> float:
    return min(1.0, max(0.0, float(value)))


__all__ = ["LINK_MODES", "AutomationLinkedWarning", "LinkMode", "check", "index", "invalidate", "links_to",
           "linked_message", "normalized_value", "parse_automation_link", "scan", "target_of", "target_text"]
