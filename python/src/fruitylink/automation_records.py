"""Typed automation targets and linear envelopes; no raw native event identifiers."""

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
