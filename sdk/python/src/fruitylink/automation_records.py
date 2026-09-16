"""Typed automation targets and linear envelopes; native event ids are decoded, never required."""

import math
from collections.abc import Mapping, Sequence
from dataclasses import dataclass

from .models import ClipInfo

TENSION_EASE_OUT = 0.5
"""Positive tension: the segment moves fast at first and eases into its end point (a compressor-style
recovery). Live evidence 2026-09-14: +0.3 reached 89 % of the travel at 50 % of the time."""

TENSION_EASE_IN = -0.5
"""Negative tension: the segment starts slowly and accelerates into its end point (a swell that lands on
a downbeat). Live evidence 2026-09-14: -0.3 was still at roughly a third of the travel half-way through."""


@dataclass(frozen=True)
class AutomationTarget:
    """Where an automation clip writes. ``kind`` is one of channel_volume, channel_pan, channel_pitch,
    mixer_volume, mixer_pan or plugin_parameter; ``index`` is the channel or mixer track; ``slot`` -1 means
    a generator channel and 0..9 a mixer effect slot; ``parameter`` is the plugin parameter index.

    Mixer send levels have no target: FL's send-level event ids are not known to the bridge, so a send
    "throw" is automated on the return insert's own ``mixer_volume`` (moves every source feeding it) or on
    the send effect's wet parameter through ``plugin_parameter`` (per source). See the Python API guide.
    """

    kind: str
    index: int
    slot: int = -1
    parameter: int = -1

    def __post_init__(self) -> None:
        kinds = {"channel_volume", "channel_pan", "channel_pitch", "mixer_volume", "mixer_pan", "plugin_parameter"}
        if self.kind not in kinds:
            raise ValueError("Unsupported automation target kind.")
        if type(self.index) is not int or self.index < 0:
            raise ValueError("Target index must be a nonnegative integer.")
        if type(self.slot) is not int or type(self.parameter) is not int:
            raise ValueError("Target slot and parameter must be integers.")
        if self.kind == "plugin_parameter":
            if not -1 <= self.slot <= 9 or self.parameter < 0:
                raise ValueError("Plugin target requires slot -1 or 0..9 and a nonnegative parameter.")
        elif self.slot != -1 or self.parameter != -1:
            raise ValueError("Built-in targets do not accept a slot or parameter.")

    @classmethod
    def channel_volume(cls, channel: int) -> "AutomationTarget":
        return cls("channel_volume", channel)

    @classmethod
    def channel_pan(cls, channel: int) -> "AutomationTarget":
        return cls("channel_pan", channel)

    @classmethod
    def channel_pitch(cls, channel: int) -> "AutomationTarget":
        return cls("channel_pitch", channel)

    @classmethod
    def mixer_volume(cls, track: int) -> "AutomationTarget":
        return cls("mixer_volume", track)

    @classmethod
    def mixer_pan(cls, track: int) -> "AutomationTarget":
        return cls("mixer_pan", track)

    @classmethod
    def plugin_parameter(cls, *positional: int, slot: int = -1, parameter: int | None = None,
                         param: int | None = None, channel_or_track: int | None = None,
                         track: int | None = None, channel: int | None = None) -> "AutomationTarget":
        """A plugin parameter target, in either the SDK order or the record order.

        Positional forms: ``plugin_parameter(index, parameter)`` (generator on a channel, or ``slot=``
        for a mixer effect) and ``plugin_parameter(track, slot, parameter)`` (the ``track/slot/param``
        order automation records use, e.g. ``1/0/556`` -> ``plugin_parameter(1, 0, 556)``). Keywords:
        ``track=``/``channel=``/``channel_or_track=`` for the index, ``slot=``, ``parameter=``/``param=``.
        ``generator_parameter(channel, parameter)`` and ``effect_parameter(track, slot, parameter)`` are
        the unambiguous spellings.
        """
        if len(positional) > 3:
            raise TypeError("plugin_parameter takes (index, parameter) or (track, slot, parameter).")
        index_keys = [value for value in (channel_or_track, track, channel) if value is not None]
        parameter_keys = [value for value in (parameter, param) if value is not None]
        if len(index_keys) > 1 or len(parameter_keys) > 1:
            raise TypeError("Give the target index and the parameter once each.")
        index = index_keys[0] if index_keys else None
        parameter_index = parameter_keys[0] if parameter_keys else None
        if len(positional) == 3:
            if index is not None or parameter_index is not None or slot != -1:
                raise TypeError("(track, slot, parameter) cannot be combined with index, slot or parameter keywords.")
            index, slot, parameter_index = positional
        elif len(positional) == 2:
            if index is not None or parameter_index is not None:
                raise TypeError("(index, parameter) cannot be combined with index or parameter keywords.")
            index, parameter_index = positional
        elif len(positional) == 1:
            if index is not None:
                raise TypeError("The target index was given twice.")
            index = positional[0]
        if index is None or parameter_index is None:
            raise TypeError("plugin_parameter needs a target index and a parameter index.")
        return cls("plugin_parameter", index, slot, parameter_index)

    @classmethod
    def generator_parameter(cls, channel: int, parameter: int) -> "AutomationTarget":
        """A generator plugin parameter on a channel (``slot`` -1)."""
        return cls("plugin_parameter", channel, -1, parameter)

    @classmethod
    def effect_parameter(cls, track: int, slot: int, parameter: int) -> "AutomationTarget":
        """A mixer effect parameter: insert ``track``, effect ``slot`` 0..9, ``parameter`` index."""
        return cls("plugin_parameter", track, slot, parameter)

    @classmethod
    def from_record(cls, record: "Mapping[str, object] | Sequence[int] | str") -> "AutomationTarget":
        """Build a plugin parameter target from a ``(track, slot, parameter)`` triple, a ``"1/0/556"``
        string or a mapping with ``track``/``channel``/``index``, ``slot`` and ``parameter``/``param`` keys."""
        if isinstance(record, str):
            parts = record.replace(":", "/").split("/")
            if len(parts) != 3 or not all(part.strip().lstrip("-").isdigit() for part in parts):
                raise ValueError("A record string must look like 'track/slot/parameter'.")
            return cls.effect_parameter(*(int(part) for part in parts))
        if isinstance(record, Mapping):
            index = next((record[key] for key in ("track", "channel", "index", "channel_or_track") if key in record), None)
            parameter = next((record[key] for key in ("parameter", "param") if key in record), None)
            slot = record.get("slot", -1)
            if not all(type(value) is int for value in (index, parameter, slot)):
                raise ValueError("A record mapping needs integer track/channel, slot and parameter fields.")
            return cls("plugin_parameter", index, slot, parameter)  # type: ignore[arg-type]
        values = tuple(record)
        if len(values) != 3 or any(type(value) is not int for value in values):
            raise ValueError("A record triple is (track, slot, parameter).")
        return cls.effect_parameter(*values)

    @classmethod
    def from_event_id(cls, event_id: int) -> "AutomationTarget | None":
        """Decode a native FL event id (as ``get_channel_plugin`` prints ``event 0x...``) into a target.

        Channel ids are ``(channel << 16) + control``: control 0 volume, 1 pan, 4 pitch, and
        ``0x8000 + index`` for the channel's generator parameter ``index``. Mixer ids are
        ``0x70000000 + ((track * 0x40 + slot) << 16) + control`` with ``0x1fc0`` volume, ``0x1fc1``
        pan and ``0x8000 + index`` for an effect-slot parameter. The channel part is FL's persistent
        record id, which equals the channel index unless channels were reordered or deleted.
        Returns None for ids outside those forms (mute, routing, other host controls). Live
        evidence (2026-09-14): ``0x180cd`` = channel 1 parameter 205, ``0x48007`` = channel 4
        parameter 7, ``0x30000`` = channel 3 volume, ``0x70401fc0`` = mixer track 1 volume.
        """
        if isinstance(event_id, bool) or type(event_id) is not int or event_id < 0 or event_id > 0xFFFFFFFF:
            raise ValueError("event_id must be an unsigned 32-bit integer.")
        if event_id >= _MIXER_EVENT_BASE:
            offset = event_id - _MIXER_EVENT_BASE
            unit, control = offset >> 16, offset & 0xFFFF
            track, slot = divmod(unit, 0x40)
            if control & 0x8000 and 0 <= slot <= 9:
                return cls("plugin_parameter", track, slot, control & 0x7FFF)
            if slot == 0 and control == _MIXER_VOLUME:
                return cls("mixer_volume", track)
            if slot == 0 and control == _MIXER_PAN:
                return cls("mixer_pan", track)
            return None
        channel, control = event_id >> 16, event_id & 0xFFFF
        if control & 0x8000:
            return cls("plugin_parameter", channel, -1, control & 0x7FFF)
        kind = _CHANNEL_CONTROLS.get(control)
        return cls(kind, channel) if kind is not None else None


_MIXER_EVENT_BASE = 0x70000000
_MIXER_VOLUME, _MIXER_PAN = 0x1FC0, 0x1FC1
_CHANNEL_CONTROLS = {0: "channel_volume", 1: "channel_pan", 4: "channel_pitch"}


@dataclass(frozen=True)
class AutomationPointSpec:
    """One envelope point: ``time_beats`` from the clip start, ``value`` 0..1, ``tension`` -1..1.

    Tension belongs to the segment that ENDS at this point (from the previous point to here) and
    describes its shape, not its direction. Positive tension moves fast first and eases into this
    point (``TENSION_EASE_OUT``; a compressor-style recovery: +0.3 covered 89 % of the travel in the
    first half of the time). Negative tension starts slowly and accelerates into this point
    (``TENSION_EASE_IN``; a swell or filter opening that lands on a downbeat). Zero is a straight
    line. Live evidence 2026-09-14, Pro-Q 4 High Cut over eight bars: +0.3 read 5698 Hz at the
    half-way bar, -0.3 read 2282 Hz there and 3004 Hz two bars later. ``pump()`` uses +0.5 on its
    recovery segments, so they recover quickly and settle.
    """

    time_beats: float
    value: float
    tension: float = 0
    curve: int = 0

    def __post_init__(self) -> None:
        for value in (self.time_beats, self.value, self.tension):
            if isinstance(value, bool) or not isinstance(value, (int, float)) or not math.isfinite(value):
                raise ValueError("Automation point coordinates must be finite numbers.")
        if self.time_beats < 0 or not 0 <= self.value <= 1 or not -1 <= self.tension <= 1:
            raise ValueError("Automation requires time>=0, value 0..1, and tension -1..1.")
        if type(self.curve) is not int or self.curve != 0:
            raise ValueError("Only linear automation points (curve=0) are supported.")


@dataclass(frozen=True)
class AutomationClipResult:
    channel: int
    clip_index: int


@dataclass(frozen=True)
class PumpResult:
    """A placed sidechain-style curve: the created clip and how many envelope points it holds."""

    clip: AutomationClipResult
    point_count: int


@dataclass(frozen=True)
class AutomationChannelInfo:
    """One automation clip channel as ``Automation.list()`` reports it.

    ``targets_text`` is the host's description of the links ("Insert 3 volume" when FL names the
    event, otherwise "event 0x71008030"); ``event_ids`` are the ids that appear in that text and
    ``targets`` their decoded ``AutomationTarget`` (None when an id is outside the decodable
    forms). ``point_count`` is the envelope size and ``clips`` every playlist clip that plays
    this channel (``ClipInfo`` records with absolute ticks). The clip match uses the channel's
    source index, which equals its rack index unless channels were reordered or deleted.
    """

    channel: int
    name: str
    targets_text: str
    event_ids: tuple[int, ...]
    targets: tuple["AutomationTarget | None", ...]
    point_count: int
    clips: tuple[ClipInfo, ...]

    @property
    def target(self) -> "AutomationTarget | None":
        """The first decoded target, for the common single-link clip."""
        return next((target for target in self.targets if target is not None), None)
