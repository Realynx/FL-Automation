"""Typed automation targets and linear envelopes; native event ids are decoded, never required."""

import math
from dataclasses import dataclass


@dataclass(frozen=True)
class AutomationTarget:
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
    def plugin_parameter(cls, channel_or_track: int, parameter: int, *, slot: int = -1) -> "AutomationTarget":
        return cls("plugin_parameter", channel_or_track, slot, parameter)

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
